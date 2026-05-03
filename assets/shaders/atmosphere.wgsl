// Atmospheric scattering — simplified Nishita single-scatter model.
// Based on GPU Gems 2, Chapter 16. Rendered on a transparent shell sphere
// larger than the planet surface, alpha-blended on top.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,      // xyz toward sun, normalized
    cameraPos: vec4f,   // xyz world space
    params: vec4f,      // x = planetRadius, y = atmosphereRadius, z = sunIntensity
}

@binding(0) @group(0) var<uniform> u: Uniforms;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
}

// Logarithmic depth (see sun.wgsl). Atmosphere participates in the depth test
// even though it doesn't write depth, so its NDC z must use the same curve as
// terrain/sun/distant planets to interact correctly with them.
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
    return out;
}

const PI: f32 = 3.14159265;
const NUM_SAMPLES: i32 = 8;
const NUM_LIGHT_SAMPLES: i32 = 4;
const SCALE_HEIGHT: f32 = 0.5;
// (1/wavelength)^4 for 680nm, 550nm, 440nm — Rayleigh scattering wavelength dependence
const WAVE_INV4: vec3f = vec3f(5.602, 9.473, 19.644);

fn rayleighPhase(ct: f32) -> f32 { return 0.75 * (1.0 + ct * ct); }

fn miePhase(ct: f32, g: f32) -> f32 {
    let g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * ct, 1.5));
}

fn raySphere(ro: vec3f, rd: vec3f, r: f32) -> vec2f {
    let b = dot(ro, rd);
    let c = dot(ro, ro) - r * r;
    let d = b * b - c;
    if d < 0.0 { return vec2f(-1.0); }
    let sq = sqrt(d);
    return vec2f(-b - sq, -b + sq);
}

fn density(alt: f32) -> f32 { return exp(-alt / SCALE_HEIGHT); }

fn lightOpticalDepth(ro: vec3f, rd: vec3f, len: f32, pR: f32, aR: f32) -> f32 {
    let step = len / f32(NUM_LIGHT_SAMPLES);
    var d = 0.0;
    for (var i = 0; i < NUM_LIGHT_SAMPLES; i++) {
        let p = ro + rd * (step * (f32(i) + 0.5));
        let alt = clamp((length(p) - pR) / (aR - pR), 0.0, 1.0);
        d += density(alt) * step;
    }
    return d;
}

@fragment
fn fs_main(@location(0) worldPos: vec3f) -> @location(0) vec4f {
    let pR = u.params.x;
    let aR = u.params.y;
    let sun = u.params.z;
    let L = normalize(u.sunDir.xyz);
    let ro = u.cameraPos.xyz;
    let rd = normalize(worldPos - ro);

    let aHit = raySphere(ro, rd, aR);
    if aHit.y < 0.0 { discard; }

    var tStart = max(0.0, aHit.x);
    var tEnd = aHit.y;

    let pHit = raySphere(ro, rd, pR);
    if pHit.x > 0.0 { tEnd = min(tEnd, pHit.x); }
    if tStart >= tEnd { discard; }

    let step = (tEnd - tStart) / f32(NUM_SAMPLES);
    let rScale = 0.005;
    let mScale = 0.003;
    let mG = 0.76;

    var rSum = vec3f(0.0);
    var mSum = vec3f(0.0);
    var odR = 0.0;
    var odM = 0.0;

    for (var i = 0; i < NUM_SAMPLES; i++) {
        let sp = ro + rd * (tStart + step * (f32(i) + 0.5));
        let h = length(sp);
        if h < pR { continue; }

        let alt = clamp((h - pR) / (aR - pR), 0.0, 1.0);
        let ld = density(alt);
        odR += ld * step;
        odM += ld * step;

        let spHit = raySphere(sp, L, pR);
        if spHit.x > 0.0 { continue; }

        let saHit = raySphere(sp, L, aR);
        let sod = lightOpticalDepth(sp, L, max(0.0, saHit.y), pR, aR);

        let tR = rScale * WAVE_INV4 * (odR + sod);
        let tM = vec3f(mScale * (odM + sod));
        let att = exp(-(tR + tM));

        rSum += ld * att * step;
        mSum += ld * att * step;
    }

    rSum *= rScale * WAVE_INV4;
    mSum *= mScale;

    let ct = dot(rd, L);
    var col = sun * (rSum * rayleighPhase(ct) + mSum * miePhase(ct, mG));
    col = 1.0 - exp(-col);

    let a = clamp(length(col) * 2.0, 0.0, 0.9);
    return vec4f(col, a);
}
