// Terrain shader (GLSL port of terrain.wgsl). Triplanar atlas + Lambert +
// OpenGL-Water-style water (DuDv distortion + normal map + Fresnel + spec).
// Single source split by #ifdef VERTEX / #ifdef FRAGMENT in OpenGLGPU.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 sunDir;
    vec4 cameraPos;
    // params.x = time
    // params.y = oceanLevel0 (1.0 = level 0 uses wave-water shader, 0.0 = level 0
    //            samples atlas like any other tier; only Earth flips this on)
    // params.z = water column thickness in world units (= 3 * stepHeight). The
    //            seabed (rock tier) sits this far below the water surface, so
    //            this is the maximum vertical depth a water-surface fragment
    //            sees through the water before it hits rock.
    vec4 params;
} u;

// Binding slots match the WGSL order. WGSL slot 1 holds a standalone sampler;
// in GLSL the sampler is baked into each sampler2D, so slot 1 is unused on
// our side — we just bind the sampler to all the texture units. The C# bind
// group still writes slot 1 (sampler entry) and slots 2/3/4 (textures).
layout(binding = 2) uniform sampler2D terrainAtlas;
layout(binding = 3) uniform sampler2D waterDuDv;
layout(binding = 4) uniform sampler2D waterNormal;

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
}

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in float aLevel;
out vec3 vWorldPos;
out vec3 vNormal;
out float vLevel;

void main() {
    gl_Position = applyLogDepth(u.mvp * vec4(aPos, 1.0));
    vWorldPos = aPos;
    vNormal = normalize(aNormal);
    vLevel = aLevel;
}
#endif

#ifdef FRAGMENT
in vec3 vWorldPos;
in vec3 vNormal;
in float vLevel;
out vec4 FragColor;

// 6-level Earth-themed fallback. Used only when atlas sampling returns
// pure black (texture failed to load). Per-planet palettes ride along
// in the atlas itself; this is just a "you can still tell elevation
// apart" backstop. Index = mesh level (0..5).
vec3 levelColor(float level) {
    int lvl = int(level + 0.5);
    if (lvl <= 0) return vec3(0.15, 0.35, 0.75);   // water
    if (lvl == 1) return vec3(0.90, 0.80, 0.55);   // sand
    if (lvl == 2) return vec3(0.30, 0.65, 0.25);   // grass meadow
    if (lvl == 3) return vec3(0.55, 0.65, 0.30);   // grass dry / savanna
    if (lvl == 4) return vec3(0.55, 0.55, 0.55);   // rock
    return vec3(0.95, 0.97, 1.00);                  // snow
}

vec3 sampleTile(vec2 uv, float level) {
    float tileW = 1.0 / 6.0;  // atlas has 6 horizontal tiles
    float au = level * tileW + fract(uv.x) * tileW;
    vec3 tex = textureLod(terrainAtlas, vec2(au, fract(uv.y)), 0.0).rgb;
    float brightness = tex.r + tex.g + tex.b;
    if (brightness < 0.01) return levelColor(level);
    return tex;
}

vec3 triplanarTile(vec3 wp, vec3 N, float level) {
    float s = 4.0;
    vec3 b = max(abs(N), vec3(0.001));
    float total = b.x + b.y + b.z;
    return sampleTile(wp.zy * s, level) * (b.x / total)
         + sampleTile(wp.xz * s, level) * (b.y / total)
         + sampleTile(wp.xy * s, level) * (b.z / total);
}

// Water rendering moved to shaders/water.glsl + WaterRenderer.

void main() {
    vec3 N = normalize(vNormal);
    vec3 L = normalize(u.sunDir.xyz);
    vec3 V = normalize(u.cameraPos.xyz - vWorldPos);
    float NdotL = max(dot(N, L), 0.0);

    vec3 terrainBase = triplanarTile(vWorldPos, N, vLevel);

    // Water rendering moved to shaders/water.glsl + WaterRenderer. Level-0
    // cells in the terrain mesh emit only the rocky seabed, so this shader
    // never needs to branch on water any more.
    vec3 lit = terrainBase * (0.25 + NdotL * 0.9);

    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    float dayFactor = smoothstep(-0.1, 0.3, NdotL);
    lit = lit + vec3(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    FragColor = vec4(lit, 1.0);
}
#endif
