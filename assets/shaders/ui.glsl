// Screen-space UI shader (GLSL port of ui.wgsl). No uniforms — vertices in NDC.

#ifdef VERTEX
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec4 aColor;
out vec4 vColor;

void main() {
    gl_Position = vec4(aPos, 0.0, 1.0);
    vColor = aColor;
}
#endif

#ifdef FRAGMENT
in vec4 vColor;
out vec4 FragColor;
void main() {
    FragColor = vColor;
}
#endif
