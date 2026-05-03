// Screen-space UI shader with bitmap font support (GLSL port of text.wgsl).
// Same vertex format as the pure-color UI shader plus a per-vertex UV into
// the 8x16 font atlas.

layout(binding = 1) uniform sampler2D atlas;

#ifdef VERTEX
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aColor;
out vec2 vUV;
out vec4 vColor;

void main() {
    gl_Position = vec4(aPos, 0.0, 1.0);
    vUV = aUV;
    vColor = aColor;
}
#endif

#ifdef FRAGMENT
in vec2 vUV;
in vec4 vColor;
out vec4 FragColor;

void main() {
    vec4 tex = textureLod(atlas, vUV, 0.0);
    FragColor = vec4(vColor.rgb, vColor.a * tex.a);
}
#endif
