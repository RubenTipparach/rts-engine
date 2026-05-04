// Solar system shader — Lambert-lit spheres, sun at origin.
// Per-vertex: pos(3f) + normal(3f) + color(3f) + brightness(1f) = 10 floats, stride 40.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,  // direction *toward* the sun, in world space; updated per body per frame
    viewDir: vec4f, // xyz: direction *toward* the camera (constant across the body); w: dither factor 0..1
}
@binding(0) @group(0) var<uniform> u: Uniforms;

// 4x4 Bayer matrix as a flat 16-element constant. Used for ordered-dither
// transparency on bodies that cross between camera and the focused planet.
fn bayer4(p: vec2u) -> f32 {
    let m = array<f32, 16>(
         0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
        12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
         3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
        15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0,
    );
    return m[(p.x & 3u) + (p.y & 3u) * 4u];
}

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
    @builtin(position) fragCoord: vec4f,
    @location(0) normal: vec3f,
    @location(1) color: vec3f,
    @location(2) brightness: f32,
) -> @location(0) vec4f {
    // Ordered-dither transparency for backdrop bodies passing in front of the
    // focused planet — discard pixels whose Bayer threshold is below the
    // dither factor. dither=0 keeps the body fully opaque.
    let dither = u.viewDir.w;
    if (dither > 0.001) {
        let p = vec2u(u32(fragCoord.x), u32(fragCoord.y));
        if (dither > bayer4(p)) {
            discard;
        }
    }

    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let V = normalize(u.viewDir.xyz);
    let NdotL = max(dot(N, L), 0.0);
    // Match terrain.wgsl's Lambert coefficients so a planet at orbital position
    // P shades the same in solar system view as it does in planet detail view.
    var lit = color * brightness * (0.25 + NdotL * 0.9);

    // Fresnel rim glow on the lit side. Cheap stand-in for the ray-marched
    // atmospheric scatter on the detailed planet — gives every solar-system
    // body a soft blue limb that fades out across the night side.
    let rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    let dayFactor = smoothstep(-0.1, 0.3, NdotL);
    lit = lit + vec3f(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
