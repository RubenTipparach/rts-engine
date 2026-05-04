// Screen-space UI shader. No uniforms — vertices are already in NDC.

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
