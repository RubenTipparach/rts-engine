// Screen-space UI shader with bitmap font support. Same vertex format as the
// pure-color UI shader plus a per-vertex UV that picks a cell out of the
// 8x16 font atlas. Background quads sample the reserved white texel at
// cell (0,0); glyph quads sample their character cell. Final color is
//   vec4(vColor.rgb, vColor.a * atlas.a)
// so transparent atlas pixels disappear via alpha blend, opaque white texels
// pass the unmodulated vColor through (good for backgrounds + tints).

@binding(0) @group(0) var samp: sampler;
@binding(1) @group(0) var atlas: texture_2d<f32>;

struct VSOut {
    @builtin(position) pos: vec4f,
    @location(0) uv: vec2f,
    @location(1) color: vec4f,
}

@vertex
fn vs_main(@location(0) pos: vec2f, @location(1) uv: vec2f, @location(2) color: vec4f) -> VSOut {
    var out: VSOut;
    out.pos = vec4f(pos, 0.0, 1.0);
    out.uv = uv;
    out.color = color;
    return out;
}

@fragment
fn fs_main(@location(0) uv: vec2f, @location(1) color: vec4f) -> @location(0) vec4f {
    let tex = textureSampleLevel(atlas, samp, uv, 0.0);
    return vec4f(color.rgb, color.a * tex.a);
}
