// Terrain shader: triplanar atlas sampling + Lambert lighting + animated water
// + fake rim atmospheric glow (Fresnel-style, blue at grazing angles).
// Atlas is a 5-tile horizontal strip (1280×256): water | sand | grass | rock | snow.

struct Uniforms {
    mvp: mat4x4f,       // offset 0, 64 bytes
    sunDir: vec4f,      // offset 64 — xyz toward-sun, normalized
    cameraPos: vec4f,   // offset 80 — xyz camera world position
    time: f32,          // offset 96
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

// Map local UV (tileable in [0,1]) to an atlas tile.
fn sampleTile(uv: vec2f, level: f32) -> vec3f {
    let tileW = 1.0 / 5.0;
    let atlasU = level * tileW + fract(uv.x) * tileW;
    return textureSample(terrainAtlas, terrainSampler, vec2f(atlasU, fract(uv.y))).rgb;
}

// Two-sample scrolled water for animated waves (tenebris-style).
fn sampleWater(uv: vec2f) -> vec3f {
    let tileW = 1.0 / 5.0;
    let s = u.time * 0.04;
    let uv1 = uv + vec2f(s, s * 0.66);
    let uv2 = uv * 1.2 + vec2f(-s * 0.83, s);
    let u1 = fract(uv1.x) * tileW;
    let u2 = fract(uv2.x) * tileW;
    let c1 = textureSample(terrainAtlas, terrainSampler, vec2f(u1, fract(uv1.y))).rgb;
    let c2 = textureSample(terrainAtlas, terrainSampler, vec2f(u2, fract(uv2.y))).rgb;
    return mix(c1, c2, 0.5);
}

fn triplanarSample(worldPos: vec3f, normal: vec3f, level: f32, isWater: bool) -> vec3f {
    let scale = 4.0;
    let blend = normalize(max(abs(normal), vec3f(0.001)));
    let total = blend.x + blend.y + blend.z;
    let w = blend / total;

    var cx: vec3f; var cy: vec3f; var cz: vec3f;
    if isWater {
        cx = sampleWater(worldPos.zy * scale);
        cy = sampleWater(worldPos.xz * scale);
        cz = sampleWater(worldPos.xy * scale);
    } else {
        cx = sampleTile(worldPos.zy * scale, level);
        cy = sampleTile(worldPos.xz * scale, level);
        cz = sampleTile(worldPos.xy * scale, level);
    }

    return cx * w.x + cy * w.y + cz * w.z;
}

@fragment
fn fs_main(
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let V = normalize(u.cameraPos.xyz - worldPos);

    let isWater = level < 0.5;
    let base = triplanarSample(worldPos, N, level, isWater);

    // Lambert diffuse + ambient
    let NdotL = max(dot(N, L), 0.0);
    let ambient = 0.25;
    let diffuse = NdotL * 0.9;
    var lit = base * (ambient + diffuse);

    // Water: add sun specular highlight for shimmer
    if isWater {
        let H = normalize(L + V);
        let spec = pow(max(dot(N, H), 0.0), 48.0) * NdotL;
        lit = lit + vec3f(1.0, 0.95, 0.85) * spec * 0.6;
    }

    // Fake atmospheric rim glow — Fresnel-style at grazing angles.
    // Proper Nishita scattering renders on a separate shell (future).
    let rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    let atmoTint = vec3f(0.35, 0.55, 0.95);
    let dayFactor = smoothstep(-0.1, 0.3, NdotL);
    lit = lit + atmoTint * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
