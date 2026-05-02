// Solid-color line shader (GLSL port of outline.wgsl). Used for cell outlines
// and orbit rings. Alpha in u.color is honoured by the alpha-blended line
// pipeline.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 color;
} u;

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
}

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
void main() {
    gl_Position = applyLogDepth(u.mvp * vec4(aPos, 1.0));
}
#endif

#ifdef FRAGMENT
out vec4 FragColor;
void main() {
    FragColor = u.color;
}
#endif
