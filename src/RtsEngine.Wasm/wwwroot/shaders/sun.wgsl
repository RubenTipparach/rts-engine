// Sun shader — procedural surface with glow, corona, and animated granulation.
// Rendered as a sphere; fragment uses world position for noise-based detail.

struct Uniforms {
    mvp: mat4x4f,
    params: vec4f,  // x = time, y = glowRadius/sunRadius
}

@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) localPos: vec3f,
}

// Logarithmic depth: maps view-space depth to NDC z = log2(1+w)/log2(1+FAR).
// FAR must match the projection's far plane (engine uses 10000). Keeping
// precision uniform across the very wide range needed when the sun and other
// planets share the scene with the close-up detailed planet.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}

@vertex
fn vs_main(@location(0) pos: vec3f) -> VSOutput {
    var out: VSOutput;
    out.position = applyLogDepth(u.mvp * vec4f(pos, 1.0));
    out.worldPos = pos;
    out.normal = normalize(pos);
    out.localPos = pos;
    return out;
}

fn hash3(p: vec3f) -> f32 {
    return fract(sin(dot(p, vec3f(127.1, 311.7, 74.7))) * 43758.5453);
}

fn noise3(p: vec3f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(mix(hash3(i), hash3(i + vec3f(1,0,0)), u.x),
            mix(hash3(i + vec3f(0,1,0)), hash3(i + vec3f(1,1,0)), u.x), u.y),
        mix(mix(hash3(i + vec3f(0,0,1)), hash3(i + vec3f(1,0,1)), u.x),
            mix(hash3(i + vec3f(0,1,1)), hash3(i + vec3f(1,1,1)), u.x), u.y),
        u.z);
}

fn fbm3(p: vec3f) -> f32 {
    var v = 0.0; var a = 0.5; var q = p;
    for (var i = 0; i < 4; i = i + 1) {
        v = v + a * noise3(q);
        q = q * 2.0; a = a * 0.5;
    }
    return v;
}

@fragment
fn fs_main(
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) localPos: vec3f,
) -> @location(0) vec4f {
    let t = u.params.x;
    let N = normalize(normal);
    let dir = normalize(localPos);

    // Animated surface granulation
    let n1 = fbm3(dir * 5.0 + vec3f(t * 0.05, t * 0.03, t * 0.04));
    let n2 = fbm3(dir * 10.0 + vec3f(-t * 0.08, t * 0.06, t * 0.02));
    let granule = n1 * 0.6 + n2 * 0.4;

    // Color: hot white center → orange → dark red at limb
    let hotColor = vec3f(1.0, 0.98, 0.9);
    let warmColor = vec3f(1.0, 0.7, 0.2);
    let coolColor = vec3f(0.8, 0.3, 0.05);

    let base = mix(warmColor, hotColor, granule);

    // Limb darkening — edges are darker/redder
    let viewDot = max(dot(N, normalize(-worldPos)), 0.0);
    let limb = pow(viewDot, 0.4);
    var col = mix(coolColor, base, limb);

    // Bright spots (solar flares)
    let flare = smoothstep(0.72, 0.78, n1) * 0.5;
    col = col + vec3f(flare, flare * 0.8, flare * 0.3);

    // Emissive — sun is always bright
    col = col * 1.8;

    return vec4f(col, 1.0);
}
