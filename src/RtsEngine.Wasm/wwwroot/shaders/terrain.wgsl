// Terrain shader — triplanar atlas + Lambert + OpenGL-Water style water
// (DuDv distortion map + normal map + Fresnel + specular).
// Uses textureSampleLevel exclusively for non-uniform flow safety.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,
    cameraPos: vec4f,
    time: f32,
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

// ── Fallback level colors ─────────────────────────────────────────

fn levelColor(level: f32) -> vec3f {
    let lvl = i32(level + 0.5);
    if (lvl <= 0) { return vec3f(0.15, 0.35, 0.75); }
    if (lvl == 1) { return vec3f(0.90, 0.80, 0.55); }
    if (lvl == 2) { return vec3f(0.30, 0.65, 0.25); }
    if (lvl == 3) { return vec3f(0.55, 0.55, 0.55); }
    return vec3f(0.95, 0.97, 1.00);
}

// ── Atlas sampling ────────────────────────────────────────────────

fn sampleTile(uv: vec2f, level: f32) -> vec3f {
    let tileW = 0.2;
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

// ── Water (OpenGL-Water style: DuDv distortion + normal map) ──────

fn waterShader(wp: vec3f, N: vec3f, V: vec3f, L: vec3f) -> vec3f {
    let t = u.time;
    let tiling = 6.0;

    // Triplanar UV for sphere
    let b = max(abs(N), vec3f(0.001, 0.001, 0.001));
    let total = b.x + b.y + b.z;
    let wx = b.x / total;
    let wy = b.y / total;
    let wz = b.z / total;

    // Dominant UV plane
    var waterUV: vec2f;
    if (wy > wx && wy > wz) { waterUV = wp.xz * tiling; }
    else if (wx > wz)       { waterUV = wp.zy * tiling; }
    else                     { waterUV = wp.xy * tiling; }

    // Animated DuDv distortion (two scrolling layers like OpenGL-Water)
    let moveSpeed = 0.03;
    let moveFactor = t * moveSpeed;
    let dudvUV1 = vec2f(waterUV.x + moveFactor, waterUV.y);
    let dudv1 = textureSampleLevel(waterDuDv, samp, dudvUV1, 0.0).rg * 0.1;
    let dudvUV2 = waterUV + vec2f(dudv1.x, dudv1.y + moveFactor);
    let distortion = (textureSampleLevel(waterDuDv, samp, dudvUV2, 0.0).rg * 2.0 - 1.0) * 0.02;

    // Normal from normal map (perturbed by distortion)
    let nmSample = textureSampleLevel(waterNormal, samp, dudvUV2, 0.0).rgb;
    let mapNormal = vec3f(nmSample.r * 2.0 - 1.0, nmSample.b * 3.0, nmSample.g * 2.0 - 1.0);

    // Transform map normal from tangent space to world
    var tang = cross(N, vec3f(0.0, 1.0, 0.0));
    if (dot(tang, tang) < 0.01) { tang = cross(N, vec3f(1.0, 0.0, 0.0)); }
    tang = normalize(tang);
    let bitang = normalize(cross(N, tang));
    let waveN = normalize(tang * mapNormal.x + N * mapNormal.y + bitang * mapNormal.z);

    // Fresnel — more reflection at grazing angle
    let refractiveFactor = pow(max(dot(V, waveN), 0.0), 0.5);

    // Depth scatter: view-dependent color for seeing "into" the water
    let viewDepth = max(dot(N, V), 0.0);
    let shallowColor = vec3f(0.20, 0.50, 0.55);
    let deepColor = vec3f(0.02, 0.06, 0.15);
    let refractColor = mix(deepColor, shallowColor, pow(viewDepth, 0.6));

    // Reflection: approximated sky gradient
    let R = reflect(-V, waveN);
    let skyGrad = R.y * 0.5 + 0.5;
    let reflectColor = mix(vec3f(0.30, 0.40, 0.50), vec3f(0.50, 0.65, 0.85), skyGrad);

    // Blend refraction and reflection via Fresnel
    let waterBase = mix(reflectColor, refractColor, refractiveFactor);

    // Tint toward water blue
    let tinted = mix(waterBase, vec3f(0.0, 0.3, 0.5), 0.2);

    // Sun specular on wave surface
    let reflectedLight = reflect(-L, waveN);
    let spec = pow(max(dot(reflectedLight, V), 0.0), 64.0);
    let specHighlight = vec3f(1.0, 0.95, 0.85) * spec * 0.5;

    // Lambert (gentle)
    let NdotL = max(dot(N, L), 0.0);
    return tinted * (0.2 + NdotL * 0.4) + specHighlight;
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

    var lit: vec3f;
    if (level < 0.5) {
        lit = waterShader(worldPos, N, V, L);
    } else {
        lit = terrainBase * (0.25 + NdotL * 0.9);
    }

    // Rim atmosphere glow
    let rim = pow(1.0 - max(dot(N, V), 0.0), 3.5);
    let dayFactor = smoothstep(-0.1, 0.3, NdotL);
    lit = lit + vec3f(0.35, 0.55, 0.95) * rim * 0.35 * dayFactor;

    return vec4f(lit, 1.0);
}
