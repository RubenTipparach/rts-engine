// Terrain shader — triplanar atlas + Lambert + OpenGL-Water style water
// (DuDv distortion map + normal map + Fresnel + specular).
// Uses textureSampleLevel exclusively for non-uniform flow safety.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,
    cameraPos: vec4f,
    // params.x = time
    // params.y = oceanLevel0 (1.0 = level 0 uses wave-water shader, 0.0 = level 0
    //            samples atlas like any other tier; only Earth flips this on)
    // params.z = water column thickness in world units (= 3 * stepHeight). The
    //            seabed (rock tier) sits this far below the water surface, so
    //            this is the maximum vertical depth a water-surface fragment
    //            sees through the water before it hits rock.
    params: vec4f,
}

@binding(0) @group(0) var<uniform> u: Uniforms;
@binding(1) @group(0) var samp: sampler;
@binding(2) @group(0) var terrainAtlas: texture_2d<f32>;
@binding(3) @group(0) var waterDuDv: texture_2d<f32>;
@binding(4) @group(0) var waterNormal: texture_2d<f32>;

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
}

// Logarithmic depth (see sun.wgsl). FAR must match every other 3D shader so
// the depth buffer sorts terrain, atmosphere, sun, and distant planets with a
// single consistent curve.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}

@vertex
fn vs_main(
    @location(0) pos: vec3f,
    @location(1) normal: vec3f,
    @location(2) level: f32,
) -> VSOutput {
    var out: VSOutput;
    out.position = applyLogDepth(u.mvp * vec4f(pos, 1.0));
    out.worldPos = pos;
    out.normal = normalize(normal);
    out.level = level;
    return out;
}

// ── Fallback level colors ─────────────────────────────────────────

// 6-level Earth-themed fallback. Used only when atlas sampling returns
// pure black (texture failed to load). Per-planet palettes ride along
// in the atlas itself; this is just a "you can still tell elevation
// apart" backstop. Index = mesh level (0..5).
fn levelColor(level: f32) -> vec3f {
    let lvl = i32(level + 0.5);
    if (lvl <= 0) { return vec3f(0.15, 0.35, 0.75); }    // water
    if (lvl == 1) { return vec3f(0.90, 0.80, 0.55); }    // sand
    if (lvl == 2) { return vec3f(0.30, 0.65, 0.25); }    // grass meadow
    if (lvl == 3) { return vec3f(0.55, 0.65, 0.30); }    // grass dry / savanna
    if (lvl == 4) { return vec3f(0.55, 0.55, 0.55); }    // rock
    return vec3f(0.95, 0.97, 1.00);                      // snow
}

// ── Atlas sampling ────────────────────────────────────────────────

fn sampleTile(uv: vec2f, level: f32) -> vec3f {
    let tileW = 1.0 / 6.0;  // atlas has 6 horizontal tiles
    let au = level * tileW + fract(uv.x) * tileW;
    let tex = textureSampleLevel(terrainAtlas, samp, vec2f(au, fract(uv.y)), 0.0).rgb;
    let brightness = tex.r + tex.g + tex.b;
    if (brightness < 0.01) { return levelColor(level); }
    return tex;
}

fn triplanarTile(wp: vec3f, N: vec3f, level: f32) -> vec3f {
    let s = 4.0;
    let b = max(abs(N), vec3f(0.001, 0.001, 0.001));
    let total = b.x + b.y + b.z;
    return sampleTile(wp.zy * s, level) * (b.x / total)
         + sampleTile(wp.xz * s, level) * (b.y / total)
         + sampleTile(wp.xy * s, level) * (b.z / total);
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

    // Always sample terrain atlas unconditionally (uniform flow)
    let terrainBase = triplanarTile(worldPos, N, level);

    // Detect walls vs top fans by how aligned the surface normal is to
    // the radial direction. Top fans (and gentle slopes) have N close
    // to radial → wallness ≈ 0, full Lambert. Cliff walls have N
    // tangent to the sphere → wallness ≈ 1, a flatter curve so
    // visually-similar walls at different planet positions don't shade
    // wildly differently from each other (Lambert + tangent normals
    // amplify directional differences across the sphere; that's what
    // produced the silver-vs-black "same angle" disparity).
    let radial = normalize(worldPos);
    let radialAlign = abs(dot(radial, N));
    let wallness = 1.0 - smoothstep(0.4, 0.85, radialAlign);
    let topCurve = 0.25 + NdotL * 0.9;
    let wallCurve = 0.55 + NdotL * 0.25;
    let lambert = mix(topCurve, wallCurve, wallness);

    // Water is no longer rendered through this shader — it has its own
    // alpha-blended pipeline in WaterRenderer / shaders/water.wgsl. Level-0
    // cells in the terrain mesh now emit only the rocky seabed (CliffLevel
    // tile), so the level == 0 path here would only be hit by future
    // mesh changes; treat it like any other tier so it's not a silent bug.
    let lit_pre = terrainBase * lambert;
    var lit = lit_pre;

    // Rim atmosphere glow at the planet's actual silhouette, not on
    // any tangent-normal surface — gate on dot(radial, V) so cliffs
    // inside the visible disc stay clear of the bluish glow.
    let rim = pow(1.0 - max(dot(radial, V), 0.0), 3.5);
    let dayFactor = smoothstep(-0.1, 0.3, NdotL);
    lit = lit + vec3f(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
