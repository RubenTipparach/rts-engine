// Terrain shader: triplanar atlas + Lambert lighting + water + rim atmosphere.
// Stripped to bare essentials for pipeline validity, with water + effects added safely.

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

// ── Helpers ───────────────────────────────────────────────────────

fn levelColor(level: f32) -> vec3f {
    let lvl = i32(level + 0.5);
    if (lvl <= 0) { return vec3f(0.15, 0.35, 0.75); }
    if (lvl == 1) { return vec3f(0.90, 0.80, 0.55); }
    if (lvl == 2) { return vec3f(0.30, 0.65, 0.25); }
    if (lvl == 3) { return vec3f(0.55, 0.55, 0.55); }
    return vec3f(0.95, 0.97, 1.00);
}

fn hash2(p: vec2f) -> f32 {
    return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453);
}

fn noise2d(p: vec2f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let uu = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(hash2(i), hash2(i + vec2f(1.0, 0.0)), uu.x),
        mix(hash2(i + vec2f(0.0, 1.0)), hash2(i + vec2f(1.0, 1.0)), uu.x),
        uu.y);
}

fn fbm2(pin: vec2f) -> f32 {
    var v = 0.0;
    var a = 0.5;
    var q = pin;
    for (var i = 0; i < 4; i = i + 1) {
        v = v + a * noise2d(q);
        q = q * 2.0;
        a = a * 0.5;
    }
    return v;
}

// ── Atlas sampling with fallback ──────────────────────────────────

fn sampleTile(uv: vec2f, level: f32) -> vec3f {
    let tileW = 0.2;
    let au = level * tileW + fract(uv.x) * tileW;
    let tex = textureSample(terrainAtlas, terrainSampler, vec2f(au, fract(uv.y))).rgb;
    let fb = levelColor(level);
    let brightness = tex.r + tex.g + tex.b;
    if (brightness < 0.01) { return fb; }
    return tex;
}

fn triplanarTile(wp: vec3f, N: vec3f, level: f32) -> vec3f {
    let s = 4.0;
    let b = max(abs(N), vec3f(0.001));
    let w = b / (b.x + b.y + b.z);
    return sampleTile(wp.zy * s, level) * w.x
         + sampleTile(wp.xz * s, level) * w.y
         + sampleTile(wp.xy * s, level) * w.z;
}

// ── Water shader ──────────────────────────────────────────────────

fn waterShader(wp: vec3f, N: vec3f, V: vec3f, L: vec3f) -> vec3f {
    let t = u.time;

    // Wave normals
    let waveScale = 12.0;
    let n1 = fbm2(wp.xz * waveScale + vec2f(t * 0.8, t * 0.5));
    let n2 = fbm2(wp.zy * waveScale * 0.7 + vec2f(t * -0.6, t * 0.9));
    let px = (n1 - 0.5) * 0.15;
    let pz = (n2 - 0.5) * 0.15;

    // Tangent frame
    var tang = cross(N, vec3f(0.0, 1.0, 0.0));
    if (dot(tang, tang) < 0.01) {
        tang = cross(N, vec3f(1.0, 0.0, 0.0));
    }
    tang = normalize(tang);
    let bitang = normalize(cross(N, tang));
    let waveN = normalize(N + tang * px + bitang * pz);

    // Fresnel
    let fresnel = pow(1.0 - max(dot(waveN, V), 0.0), 4.0);

    // Depth scatter
    let viewDepth = max(dot(N, V), 0.0);
    let shallowColor = vec3f(0.25, 0.55, 0.60);
    let deepColor = vec3f(0.03, 0.08, 0.18);
    let scatterColor = mix(deepColor, shallowColor, pow(viewDepth, 0.6));

    // Distorted texture sample
    let distortWP = wp + vec3f(px, 0.0, pz) * 0.05;
    let texColor = triplanarTile(distortWP, N, 0.0);
    let waterBase = mix(scatterColor, texColor, 0.3 + viewDepth * 0.3);

    // Sky reflection
    let R = reflect(-V, waveN);
    let skyGrad = R.y * 0.5 + 0.5;
    let skyColor = mix(vec3f(0.35, 0.45, 0.55), vec3f(0.55, 0.70, 0.90), skyGrad);
    let reflected = mix(waterBase, skyColor, fresnel * 0.6);

    // Specular
    let H = normalize(L + V);
    let spec = pow(max(dot(waveN, H), 0.0), 64.0);
    let sunSpec = vec3f(1.0, 0.95, 0.85) * spec * 1.2 * max(dot(N, L), 0.0);

    // Sub-surface scattering
    let sss = pow(max(dot(V, -L), 0.0), 3.0) * 0.15;
    let sssColor = vec3f(0.1, 0.35, 0.25) * sss;

    // Caustics
    let caustic = fbm2(wp.xz * 20.0 + t * 0.3) * 0.1 * max(dot(N, L), 0.0);

    // Combine
    let NdotL = max(dot(N, L), 0.0);
    var lit = reflected * (0.15 + NdotL * 0.5) + sunSpec + sssColor + vec3f(caustic);
    return lit;
}

// ── Fragment entry ────────────────────────────────────────────────

@fragment
fn fs_main(
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let V = normalize(u.cameraPos.xyz - worldPos);

    var lit: vec3f;
    if (level < 0.5) {
        lit = waterShader(worldPos, N, V, L);
    } else {
        let base = triplanarTile(worldPos, N, level);
        let NdotL = max(dot(N, L), 0.0);
        lit = base * (0.25 + NdotL * 0.9);
    }

    // Rim atmosphere glow
    let rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    let NdotL2 = max(dot(N, L), 0.0);
    let dayFactor = smoothstep(-0.1, 0.3, NdotL2);
    lit = lit + vec3f(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
