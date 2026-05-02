// RTS entity shader (GLSL port of rts.wgsl). Lambert-lit solid color for
// buildings + units. Vertex format: pos(3f) + normal(3f), stride 24.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 color;   // rgb + alpha
    vec4 sunDir;
} u;

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
}

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
out vec3 vNormal;

void main() {
    gl_Position = applyLogDepth(u.mvp * vec4(aPos, 1.0));
    vNormal = aNormal;
}
#endif

#ifdef FRAGMENT
in vec3 vNormal;
out vec4 FragColor;

void main() {
    vec3 N = normalize(vNormal);
    vec3 L = normalize(u.sunDir.xyz);
    float NdotL = max(dot(N, L), 0.0);
    vec3 lit = u.color.rgb * (0.30 + NdotL * 0.85);
    FragColor = vec4(lit, u.color.a);
}
#endif
