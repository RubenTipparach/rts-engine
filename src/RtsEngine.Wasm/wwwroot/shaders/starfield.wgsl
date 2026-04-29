// Procedural star + nebula background. Drawn first each frame as a
// fullscreen triangle at the far depth (z=1) so everything else writes
// over it via standard log-depth comparison.
//
// Reconstructs a world-space view ray from the camera's basis vectors and
// the fragment's NDC position, samples a sparse star field (hash-based)
// and a soft fbm nebula, and adds both to a near-black space colour.

struct Uniforms {
    // xyz: world-space camera basis vectors. w unused.
    camRight:   vec4f,
    camUp:      vec4f,
    camForward: vec4f,
    // x = tan(fovY/2), y = aspect ratio, z = time (slow nebula drift), w unused.
    params:     vec4f,
}
@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) ndc: vec2f,
}

// Fullscreen triangle: VBO contains 3 verts at (-1,-1), (3,-1), (-1,3) which
// cover the whole screen after clipping. We force z = 1 so the geometry sits
// at the far plane and anything else rendered after writes over it.
@vertex
fn vs_main(@location(0) pos: vec3f) -> VSOutput {
    var out: VSOutput;
    out.position = vec4f(pos.xy, 1.0, 1.0);
    out.ndc = pos.xy;
    return out;
}

fn hash3(p: vec3f) -> f32 {
    return fract(sin(dot(p, vec3f(127.1, 311.7, 74.7))) * 43758.5453);
}

fn noise3(p: vec3f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let s = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(mix(hash3(i),                       hash3(i + vec3f(1,0,0)),   s.x),
            mix(hash3(i + vec3f(0,1,0)),        hash3(i + vec3f(1,1,0)),   s.x), s.y),
        mix(mix(hash3(i + vec3f(0,0,1)),        hash3(i + vec3f(1,0,1)),   s.x),
            mix(hash3(i + vec3f(0,1,1)),        hash3(i + vec3f(1,1,1)),   s.x), s.y),
        s.z);
}

fn fbm(p: vec3f) -> f32 {
    var v = 0.0;
    var a = 0.5;
    var q = p;
    for (var i = 0; i < 5; i = i + 1) {
        v = v + a * noise3(q);
        q = q * 2.07;
        a = a * 0.5;
    }
    return v;
}

fn starsAt(dir: vec3f, scale: f32, threshold: f32, brightness: f32) -> vec3f {
    let p = dir * scale;
    let cell = floor(p);
    let f = fract(p);
    let h = hash3(cell);
    if (h <= threshold) { return vec3f(0.0); }
    // Star sub-pixel position inside the cell.
    let center = vec2f(
        fract(h * 17.13),
        fract(h * 91.71));
    let d = length(f.xy - center);
    let glow = max(0.0, 1.0 - d * 12.0);
    let core = smoothstep(0.95, 1.0, 1.0 - d * 50.0);
    // Tint based on hash so stars vary slightly in colour.
    let tint = mix(vec3f(0.85, 0.92, 1.0), vec3f(1.0, 0.95, 0.8), fract(h * 7.31));
    return tint * (glow * 0.35 + core) * brightness;
}

@fragment
fn fs_main(@location(0) ndc: vec2f) -> @location(0) vec4f {
    // Reconstruct world-space ray direction from NDC + camera basis.
    let ray = normalize(
        u.camForward.xyz
        + ndc.x * u.params.y * u.params.x * u.camRight.xyz
        + ndc.y * u.params.x * u.camUp.xyz);

    // Two layers of stars at different scales for visual depth.
    var col = vec3f(0.005, 0.007, 0.018); // deep-space tint
    col = col + starsAt(ray, 220.0, 0.992, 1.0);
    col = col + starsAt(ray, 90.0,  0.997, 1.4);

    // Soft nebula — large-scale fbm shaded with two complementary tints.
    let drift = u.params.z * 0.005;
    let n = fbm(ray * 1.4 + vec3f(drift, 0.0, drift * 0.7));
    let n2 = fbm(ray * 3.1 + vec3f(7.3, 2.1, 9.9));
    let cloud = smoothstep(0.45, 0.78, n);
    let warm = vec3f(0.45, 0.18, 0.32);   // magenta-rose
    let cool = vec3f(0.10, 0.20, 0.45);   // deep blue
    let nebula = mix(cool, warm, smoothstep(0.3, 0.8, n2)) * cloud * 0.18;

    col = col + nebula;
    return vec4f(col, 1.0);
}
