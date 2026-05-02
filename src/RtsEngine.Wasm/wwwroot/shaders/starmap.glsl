// Star map shader (GLSL port of starmap.wgsl). Flat-colored vertices.
// Per-vertex: pos(3f) + color(3f) + brightness(1f), stride 28.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
} u;

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aBrightness;
out vec3 vColor;
out float vBrightness;

void main() {
    gl_Position = u.mvp * vec4(aPos, 1.0);
    vColor = aColor;
    vBrightness = aBrightness;
}
#endif

#ifdef FRAGMENT
in vec3 vColor;
in float vBrightness;
out vec4 FragColor;

void main() {
    FragColor = vec4(vColor * vBrightness, 1.0);
}
#endif
