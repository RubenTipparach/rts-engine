// Procedural star + nebula background. Drawn first each frame as a
// fullscreen triangle at the far plane so everything else writes
// over it via standard log-depth comparison.
//
// Star generation is a 3D-Voronoi port of the Phantom-Nebula Starfield
// shader (https://github.com/RubenTipparach/Phantom-Nebula/.../Starfield.fs).
// Each layer scatters cell centres in 3D and the fragment finds the
// nearest cell across the 27 surrounding cells; stars are bright spots
// at the cell-centre, brightness keyed off the cell hash. Three density
// layers stack to give big sparse stars + medium + dense background fizz.

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
// cover the whole screen after clipping. NDC z is just shy of 1.0 — the
// pipeline uses depthCompare='less' against a buffer cleared to 1.0, so a
// strict 1.0 would be rejected and the starfield would never draw. 0.99999
// passes the test and stays comfortably behind everything else.
@vertex
fn vs_main(@location(0) pos: vec3f) -> VSOutput {
    var out: VSOutput;
    out.position = vec4f(pos.xy, 0.99999, 1.0);
    out.ndc = pos.xy;
    return out;
}

// ── Hash / Voronoi (ported from Phantom-Nebula) ─────────────────────────────

fn hash1(x: f32) -> f32 {
    return fract(x + 1.3215 * 1.8152);
}

fn hash3(a: vec3f) -> f32 {
    return fract((hash1(a.z * 42.8883) + hash1(a.y * 36.9125) + hash1(a.x * 65.4321)) * 291.1257);
}

fn rehash3(x: f32) -> vec3f {
    return vec3f(
        hash1(((x + 0.5283) * 59.3829) * 274.3487),
        hash1(((x + 0.8192) * 83.6621) * 345.3871),
        hash1(((x + 0.2157) * 36.6521) * 458.3971)
    );
}

struct VoronoiResult {
    dist: f32,
    cellId: f32,
}

fn voronoi3D(posIn: vec3f, density: f32) -> VoronoiResult {
    let pos = posIn * density;
    let basePos = floor(pos);
    var m = 9999.0;
    var w = 0.0;

    for (var ix = -1; ix < 2; ix = ix + 1) {
        for (var iy = -1; iy < 2; iy = iy + 1) {
            for (var iz = -1; iz < 2; iz = iz + 1) {
                let cell = basePos + vec3f(f32(ix), f32(iy), f32(iz));
                let h = hash3(cell);
                let pt = rehash3(h) + cell;
                let diff = pos - pt;
                let d = dot(diff, diff);
                if (d < m) {
                    m = d;
                    w = h;
                }
            }
        }
    }

    var r: VoronoiResult;
    r.dist = m;
    r.cellId = w;
    return r;
}

fn starfield3D(dir: vec3f, density: f32) -> f32 {
    let v = voronoi3D(dir, density);
    // Inverted smoothstep — close to the cell centre = bright.
    let star = 1.0 - smoothstep(0.0, 0.02, v.dist);
    let brightness = fract(v.cellId * 123.456);
    // Only the top ~30% of cells light up, so most pixels are blank space.
    let mask = step(0.7, brightness);
    return star * brightness * mask;
}

// ── Nebula (cheap fbm) ───────────────────────────────────────────────────────

fn vnoise(p: vec3f) -> f32 {
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
        v = v + a * vnoise(q);
        q = q * 2.07;
        a = a * 0.5;
    }
    return v;
}

@fragment
fn fs_main(@location(0) ndc: vec2f) -> @location(0) vec4f {
    // Reconstruct world-space ray direction from NDC + camera basis.
    let ray = normalize(
        u.camForward.xyz
        + ndc.x * u.params.y * u.params.x * u.camRight.xyz
        + ndc.y * u.params.x * u.camUp.xyz);

    // Three Voronoi star layers at different densities.
    var stars = 0.0;
    stars = stars + starfield3D(ray, 100.0) * 1.0;
    stars = stars + starfield3D(ray, 200.0) * 0.8;
    stars = stars + starfield3D(ray, 300.0) * 0.6;
    let starColor = vec3f(1.0, 0.98, 0.95) * stars;

    // Soft nebula — large-scale fbm shaded with two complementary tints.
    let drift = u.params.z * 0.005;
    let n = fbm(ray * 1.4 + vec3f(drift, 0.0, drift * 0.7));
    let n2 = fbm(ray * 3.1 + vec3f(7.3, 2.1, 9.9));
    let cloud = smoothstep(0.45, 0.78, n);
    let warm = vec3f(0.55, 0.18, 0.38);   // magenta-rose
    let cool = vec3f(0.10, 0.20, 0.50);   // deep blue
    let nebula = mix(cool, warm, smoothstep(0.3, 0.8, n2)) * cloud * 0.20;

    let space = vec3f(0.005, 0.007, 0.018); // deep-space tint
    return vec4f(space + nebula + starColor, 1.0);
}
