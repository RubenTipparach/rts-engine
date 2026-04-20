// Terrain shader — defensive rewrite using textureSampleLevel to avoid
// any non-uniform control flow / derivative issues that can make the
// pipeline invalid on certain mobile GPUs.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,
    cameraPos: vec4f,
    time: f32,
}

@binding(0) @group(0) var<uniform> u: Uniforms;
@binding(1) @group(0) var terrainSampler: sampler;
@binding(2) @group(0) var terrainAtlas: texture_2d<f32>;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
}

@vertex
fn vs_main(
    @location(0) pos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
) -> VSOutput {
    var out: VSOutput;
    out.position = u.mvp * vec4f(pos, 1.0);
    out.worldPos = pos;
    out.normal = normalize(normal);
    out.level = level;
    return out;
}

// ── Fallback level colors (used when texture sample is black) ─────

fn levelColor(level: f32) -> vec3f {
    let lvl = i32(level + 0.5);
    if (lvl <= 0) { return vec3f(0.15, 0.35, 0.75); }
    if (lvl == 1) { return vec3f(0.90, 0.80, 0.55); }
    if (lvl == 2) { return vec3f(0.30, 0.65, 0.25); }
    if (lvl == 3) { return vec3f(0.55, 0.55, 0.55); }
    return vec3f(0.95, 0.97, 1.00);
}

// ── Atlas sampling ────────────────────────────────────────────────
// Uses textureSampleLevel (explicit mip) — no implicit derivatives,
// works in any control flow.

fn sampleTile(uv: vec2f, level: f32) -> vec3f {
    let tileW = 0.2;
    let au = level * tileW + fract(uv.x) * tileW;
    let av = fract(uv.y);
    let tex = textureSampleLevel(terrainAtlas, terrainSampler, vec2f(au, av), 0.0).rgb;
    // Shader-side fallback if texture failed to load
    let brightness = tex.r + tex.g + tex.b;
    if (brightness < 0.01) { return levelColor(level); }
    return tex;
}

fn triplanarTile(wp: vec3f, N: vec3f, level: f32) -> vec3f {
    let s = 4.0;
    let b = max(abs(N), vec3f(0.001, 0.001, 0.001));
    let total = b.x + b.y + b.z;
    let wx = b.x / total;
    let wy = b.y / total;
    let wz = b.z / total;
    return sampleTile(wp.zy * s, level) * wx
         + sampleTile(wp.xz * s, level) * wy
         + sampleTile(wp.xy * s, level) * wz;
}

// ── Small value noise (for wave perturbation) ─────────────────────

fn hash2(p: vec2f) -> f32 {
    return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453);
}

fn vnoise(p: vec2f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let smoothf = f * f * (3.0 - 2.0 * f);
    let a = hash2(i);
    let b = hash2(i + vec2f(1.0, 0.0));
    let c = hash2(i + vec2f(0.0, 1.0));
    let d = hash2(i + vec2f(1.0, 1.0));
    return mix(mix(a, b, smoothf.x), mix(c, d, smoothf.x), smoothf.y);
}

// ── Fragment ──────────────────────────────────────────────────────

@fragment
fn fs_main(
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let V = normalize(u.cameraPos.xyz - worldPos);
    let NdotL = max(dot(N, L), 0.0);

    // Always sample the atlas (uniform control flow)
    let base = triplanarTile(worldPos, N, level);

    var lit = base * (0.25 + NdotL * 0.9);

    // Water tier: add animated effects on top of base color
    if (level < 0.5) {
        let t = u.time;

        // Wave normal perturbation (simple, no texture sample)
        let n1 = vnoise(worldPos.xz * 12.0 + vec2f(t * 0.6, t * 0.4)) - 0.5;
        let n2 = vnoise(worldPos.zy * 9.0  + vec2f(t * -0.5, t * 0.7)) - 0.5;

        var tang = cross(N, vec3f(0.0, 1.0, 0.0));
        if (dot(tang, tang) < 0.01) { tang = cross(N, vec3f(1.0, 0.0, 0.0)); }
        tang = normalize(tang);
        let bitang = normalize(cross(N, tang));
        let waveN = normalize(N + tang * (n1 * 0.15) + bitang * (n2 * 0.15));

        // Fresnel
        let fresnel = pow(1.0 - max(dot(waveN, V), 0.0), 4.0);

        // Depth scatter tint
        let viewDepth = max(dot(N, V), 0.0);
        let scatter = mix(vec3f(0.03, 0.08, 0.18), vec3f(0.25, 0.55, 0.60), pow(viewDepth, 0.6));
        let waterBase = mix(scatter, base, 0.3 + viewDepth * 0.3);

        // Sky reflection
        let R = reflect(-V, waveN);
        let skyGrad = R.y * 0.5 + 0.5;
        let skyColor = mix(vec3f(0.35, 0.45, 0.55), vec3f(0.55, 0.70, 0.90), skyGrad);
        let reflected = mix(waterBase, skyColor, fresnel * 0.6);

        // Specular
        let H = normalize(L + V);
        let spec = pow(max(dot(waveN, H), 0.0), 64.0);
        let sunSpec = vec3f(1.0, 0.95, 0.85) * spec * 1.2 * NdotL;

        // SSS
        let sss = pow(max(dot(V, -L), 0.0), 3.0) * 0.15;
        let sssColor = vec3f(0.1, 0.35, 0.25) * sss;

        // Caustics
        let caustic = vnoise(worldPos.xz * 20.0 + vec2f(t * 0.3, t * 0.3)) * 0.1 * NdotL;

        lit = reflected * (0.15 + NdotL * 0.5) + sunSpec + sssColor + vec3f(caustic, caustic, caustic);
    }

    // Rim atmosphere glow
    let rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    let dayFactor = smoothstep(-0.1, 0.3, NdotL);
    lit = lit + vec3f(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
