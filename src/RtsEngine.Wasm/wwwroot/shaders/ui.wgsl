// Screen-space UI shader. Vertices are in NDC [-1,1].
// Per-vertex: pos2f + color4f.

struct Uniforms { mvp: mat4x4f, }
@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOut {
    @builtin(position) pos: vec4f,
    @location(0) color: vec4f,
}

@vertex
fn vs_main(@location(0) pos: vec2f, @location(1) color: vec4f) -> VSOut {
    var out: VSOut;
    out.pos = vec4f(pos, 0.0, 1.0);
    out.color = color;
    return out;
}

@fragment
fn fs_main(@location(0) color: vec4f) -> @location(0) vec4f {
    return color;
}
