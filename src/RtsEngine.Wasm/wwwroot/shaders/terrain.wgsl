// Terrain shader: triplanar atlas + Lambert lighting + custom water + rim atmosphere.
// Atlas: 5-tile horizontal strip (water | sand | grass | rock | snow).

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

// ── Noise helpers ──────────────────────────────────────────────────

fn hash2(p: vec2f) -> f32 {
    return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453);
}

fn noise2d(p: vec2f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(hash2(i), hash2(i + vec2f(1.0, 0.0)), u.x),
        mix(hash2(i + vec2f(0.0, 1.0)), hash2(i + vec2f(1.0, 1.0)), u.x),
        u.y);
}

fn fbm2(p: vec2f) -> f32 {
    var v = 0.0; var a = 0.5; var q = p;
    for (var i = 0; i < 4; i++) {
        v += a * noise2d(q);
        q *= 2.0; a *= 0.5;
    }
    return v;
}

// ── Atlas sampling ─────────────────────────────────────────────────

fn sampleTile(uv: vec2f, level: f32) -> vec3f {
    let tileW = 1.0 / 5.0;
    let au = level * tileW + fract(uv.x) * tileW;
    return textureSample(terrainAtlas, terrainSampler, vec2f(au, fract(uv.y))).rgb;
}

fn triplanarTile(wp: vec3f, N: vec3f, level: f32) -> vec3f {
    let s = 4.0;
    let b = normalize(max(abs(N), vec3f(0.001)));
    let w = b / (b.x + b.y + b.z);
    return sampleTile(wp.zy * s, level) * w.x
         + sampleTile(wp.xz * s, level) * w.y
         + sampleTile(wp.xy * s, level) * w.z;
}

// ── Custom water ───────────────────────────────────────────────────

fn waterShader(wp: vec3f, N: vec3f, V: vec3f, L: vec3f) -> vec3f {
    let t = u.time;

    // Animated wave normals — perturb the sphere normal with two noise layers
    let waveScale = 12.0;
    let n1 = fbm2(wp.xz * waveScale + vec2f(t * 0.8, t * 0.5));
    let n2 = fbm2(wp.zy * waveScale * 0.7 + vec2f(-t * 0.6, t * 0.9));
    let perturbX = (n1 - 0.5) * 0.15;
    let perturbZ = (n2 - 0.5) * 0.15;

    // Compute tangent and bitangent from the sphere normal
    var tangent = normalize(cross(N, vec3f(0.0, 1.0, 0.0)));
    if length(tangent) < 0.1 { tangent = normalize(cross(N, vec3f(1.0, 0.0, 0.0))); }
    let bitangent = normalize(cross(N, tangent));
    let waveN = normalize(N + tangent * perturbX + bitangent * perturbZ);

    // Fresnel — more reflection at grazing angles
    let fresnel = pow(1.0 - max(dot(waveN, V), 0.0), 4.0);

    // Depth-scatter color: simulate light penetrating water at steep view angles
    let viewDepth = max(dot(N, V), 0.0); // 1=looking straight down, 0=grazing
    let shallowColor = vec3f(0.25, 0.55, 0.60); // turquoise
    let deepColor = vec3f(0.03, 0.08, 0.18);     // dark navy
    let scatterColor = mix(deepColor, shallowColor, pow(viewDepth, 0.6));

    // Distortion: warp the triplanar UV for the water tile
    let distortUV = wp + vec3f(perturbX, 0.0, perturbZ) * 0.05;
    let texColor = triplanarTile(distortUV, N, 0.0);

    // Blend scatter with distorted texture
    let waterBase = mix(scatterColor, texColor, 0.3 + viewDepth * 0.3);

    // Sky reflection (approximated gradient)
    let R = reflect(-V, waveN);
    let skyGrad = R.y * 0.5 + 0.5;
    let skyColor = mix(vec3f(0.35, 0.45, 0.55), vec3f(0.55, 0.70, 0.90), skyGrad);
    let reflected = mix(waterBase, skyColor, fresnel * 0.6);

    // Specular sun highlight on wavy surface
    let H = normalize(L + V);
    let spec = pow(max(dot(waveN, H), 0.0), 64.0);
    let sunSpec = vec3f(1.0, 0.95, 0.85) * spec * 1.2 * max(dot(N, L), 0.0);

    // Sub-surface scattering — backlit glow when sun is behind the water
    let sss = pow(max(dot(V, -L), 0.0), 3.0) * 0.15;
    let sssColor = vec3f(0.1, 0.35, 0.25) * sss;

    // Caustic shimmer — projected light patterns
    let caustic = fbm2(wp.xz * 20.0 + t * 0.3) * 0.1 * max(dot(N, L), 0.0);

    // Lambert (gentle — water is mostly reflective not diffuse)
    let NdotL = max(dot(N, L), 0.0);
    let ambient = 0.15;
    var lit = reflected * (ambient + NdotL * 0.5) + sunSpec + sssColor + vec3f(caustic);

    return lit;
}

// ── Fragment entry ─────────────────────────────────────────────────

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
    if level < 0.5 {
        // Water
        lit = waterShader(worldPos, N, V, L);
    } else {
        // Terrain
        let base = triplanarTile(worldPos, N, level);
        let NdotL = max(dot(N, L), 0.0);
        lit = base * (0.25 + NdotL * 0.9);
    }

    // Rim atmosphere glow
    let rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    let dayFactor = smoothstep(-0.1, 0.3, max(dot(N, L), 0.0));
    lit = lit + vec3f(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
