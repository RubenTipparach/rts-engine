// Solar system shader — Lambert-lit spheres, sun at origin.
// Per-vertex: pos(3f) + normal(3f) + color(3f) + brightness(1f) = 10 floats, stride 40.

struct Uniforms { mvp: mat4x4f, }
@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) color: vec3f,
    @location(3) brightness: f32,
}

@vertex
fn vs_main(
    @location(0) pos: vec3f,
    @location(1) normal: vec3f,
    @location(2) color: vec3f,
    @location(3) brightness: f32,
) -> VSOutput {
    var out: VSOutput;
    out.position = u.mvp * vec4f(pos, 1.0);
    out.worldPos = pos;
    out.normal = normal;
    out.color = color;
    out.brightness = brightness;
    return out;
}

@fragment
fn fs_main(
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) color: vec3f,
    @location(3) brightness: f32,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(-worldPos);
    let NdotL = max(dot(N, L), 0.0);
    let lit = color * brightness * (0.15 + NdotL * 0.85);
    return vec4f(lit, 1.0);
}
