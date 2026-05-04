// RTS entity shader (GLSL port of rts.wgsl). Lambert-lit textured solid for
// buildings + units. Vertex format: pos(3f) + normal(3f), stride 24.
//
// Texture is triplanar-sampled from model-space position because .obj
// models don't carry UVs. Pure-magenta texels (1, 0, 1) -- the reserved
// livery slot in the palette -- recolor to the per-instance team color.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 color;
    vec4 sunDir;
    vec4 teamColor;
} u;

layout(binding = 2) uniform sampler2D tex;

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
}

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
out vec3 vNormal;
out vec3 vModelPos;

void main() {
    gl_Position = applyLogDepth(u.mvp * vec4(aPos, 1.0));
    vNormal = aNormal;
    vModelPos = aPos;
}
#endif

#ifdef FRAGMENT
in vec3 vNormal;
in vec3 vModelPos;
out vec4 FragColor;

const vec3 LIVERY = vec3(1.0, 0.0, 1.0);
const float TEXTURE_SCALE = 80.0;

vec3 sampleTri(vec2 uv) {
    return textureLod(tex, fract(uv), 0.0).rgb;
}

void main() {
    vec3 N = normalize(vNormal);

    vec3 p = vModelPos * TEXTURE_SCALE;
    vec3 cX = sampleTri(p.zy);
    vec3 cY = sampleTri(p.xz);
    vec3 cZ = sampleTri(p.xy);
    vec3 bw = abs(N) + vec3(1e-3);
    vec3 w = bw / (bw.x + bw.y + bw.z);

    // Per-axis livery substitution before blending -- keeps a clean team-
    // color edge even at the magenta region's boundary.
    vec3 mX = (length(cX - LIVERY) < 0.05) ? u.teamColor.rgb : cX;
    vec3 mY = (length(cY - LIVERY) < 0.05) ? u.teamColor.rgb : cY;
    vec3 mZ = (length(cZ - LIVERY) < 0.05) ? u.teamColor.rgb : cZ;
    vec3 texCol = mX * w.x + mY * w.y + mZ * w.z;

    vec3 surface = texCol * u.color.rgb;

    vec3 L = normalize(u.sunDir.xyz);
    float NdotL = max(dot(N, L), 0.0);
    // sunDir.w == 1.0 flags a flat-shaded marker (selection disc, HP bar)
    // -- same pipeline, no Lambert fold so the color reads as authored.
    vec3 lit = mix(surface * (0.30 + NdotL * 0.85), u.color.rgb, u.sunDir.w);
    FragColor = vec4(lit, u.color.a);
}
#endif
