// Solar system shader — Lambert-lit spheres, sun at origin.
// Per-vertex: pos(3f) + normal(3f) + color(3f) + brightness(1f) = 10 floats, stride 40.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,  // direction *toward* the sun, in world space; updated per body per frame
}
@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) normal: vec3f,
    @location(1) color: vec3f,
    @location(2) brightness: f32,
}

// Logarithmic depth (see sun.wgsl). Keep FAR in sync with the engine's
// projection far plane so all shaders sample the same depth curve.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}

@vertex
fn vs_main(
    @location(0) pos: vec3f,
    @location(1) normal: vec3f,
    @location(2) color: vec3f,
    @location(3) brightness: f32,
) -> VSOutput {
    var out: VSOutput;
    out.position = applyLogDepth(u.mvp * vec4f(pos, 1.0));
    out.normal = normal;
    out.color = color;
    out.brightness = brightness;
    return out;
}

@fragment
fn fs_main(
    @location(0) normal: vec3f,
    @location(1) color: vec3f,
    @location(2) brightness: f32,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let NdotL = max(dot(N, L), 0.0);
    // Match terrain.wgsl's Lambert coefficients so a planet at orbital position
    // P shades the same in solar system view as it does in planet detail view.
    let lit = color * brightness * (0.25 + NdotL * 0.9);
    return vec4f(lit, 1.0);
}
