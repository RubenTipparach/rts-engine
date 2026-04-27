// Star map shader — flat-colored vertices, no lighting.
// Per-vertex: pos(3f) + color(3f) + brightness(1f) = 7 floats, stride 28.

struct Uniforms { mvp: mat4x4f, }
@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) color: vec3f,
    @location(1) brightness: f32,
}

@vertex
fn vs_main(
    @location(0) pos: vec3f,
    @location(1) color: vec3f,
    @location(2) brightness: f32,
) -> VSOutput {
    var out: VSOutput;
    out.position = u.mvp * vec4f(pos, 1.0);
    out.color = color;
    out.brightness = brightness;
    return out;
}

@fragment
fn fs_main(
    @location(0) color: vec3f,
    @location(1) brightness: f32,
) -> @location(0) vec4f {
    return vec4f(color * brightness, 1.0);
}
