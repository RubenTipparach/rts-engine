// Water shader — Catlike Coding "Looking Through Water" port:
//   https://catlikecoding.com/unity/tutorials/flow/looking-through-water/
//
// The legacy water shader's "underwater" colour was a smoothstep on a
// path-length proxy (`oceanDepth / cos(view-angle)`). We replace just that
// term with the tutorial's depth-difference fog over a refracted snapshot
// of the framebuffer: the rest of the surface treatment (Fresnel mix with
// sky reflection, Lambert lighting, sun specular) stays so the water still
// looks like water, only now what's *underneath* shows through correctly.
//
//   throughWater = mix(fogColor, sceneColor[refractedUV], exp2(-density * d))
//
// where d = bgEyeDepth - waterSurfaceEyeDepth, both linearised from the
// same log-depth projection terrain uses. The screen-space refraction
// offset is clamped by saturate(d) so shallow shores don't bleed
// foreground geometry sideways into the water.

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,
    cameraPos: vec4f,
    // .x = time
    // .y = waterFogDensity      (per-world-unit absorption)
    // .z = refractionStrength   (max screen-UV offset)
    // .w = (reserved)
    params: vec4f,
    // .rgb = waterFogColor (the colour deep water absorbs to)
    fogColor: vec4f,
    // .x = viewportW (px),  .y = viewportH (px)
    // .z = 1/viewportW,     .w = 1/viewportH
    viewport: vec4f,
}

@binding(0) @group(0) var<uniform> u: Uniforms;
@binding(1) @group(0) var dudvSamp: sampler;
@binding(2) @group(0) var waterDuDv: texture_2d<f32>;
@binding(3) @group(0) var sceneSamp: sampler;
@binding(4) @group(0) var sceneColor: texture_2d<f32>;
@binding(5) @group(0) var sceneDepth: texture_depth_2d;

// Logarithmic depth — must match terrain.wgsl exactly so the depth attached
// at sceneDepth (written by terrain) uses the same encoding the water
// shader expects.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}
// Inverse of applyLogDepth: ndc-Z in [0,1] → linear view-space distance.
//   ndcZ = log2(1+w) / log2(1+FAR)   ⇒   w = (1+FAR)^ndcZ - 1
fn linearizeLogDepth(ndcZ: f32) -> f32 {
    return pow(1.0 + LOG_DEPTH_FAR, ndcZ) - 1.0;
}

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
}

@vertex
fn vs_main(
    @location(0) pos: vec3f,
    @location(1) normal: vec3f,
) -> VSOutput {
    var out: VSOutput;
    out.position = applyLogDepth(u.mvp * vec4f(pos, 1.0));
    out.worldPos = pos;
    out.normal = normalize(normal);
    return out;
}

@fragment
fn fs_main(
    @builtin(position) fragCoord: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let V = normalize(u.cameraPos.xyz - worldPos);
    let t = u.params.x;
    let fogDensity = u.params.y;
    let refractionStrength = u.params.z;

    // ── Triplanar UV for the wave pattern ─────────────────────────────
    let absN = max(abs(N), vec3f(0.001));
    let total = absN.x + absN.y + absN.z;
    let wx = absN.x / total;
    let wy = absN.y / total;
    let wz = absN.z / total;
    let tiling = 6.0;
    var waterUV: vec2f;
    if (wy > wx && wy > wz) { waterUV = worldPos.xz * tiling; }
    else if (wx > wz)       { waterUV = worldPos.zy * tiling; }
    else                     { waterUV = worldPos.xy * tiling; }

    // Animated DuDv distortion → tangent-space (x, y) offset.
    let moveSpeed = 0.03;
    let moveFactor = t * moveSpeed;
    let dudvUV1 = vec2f(waterUV.x + moveFactor, waterUV.y);
    let dudv1 = textureSampleLevel(waterDuDv, dudvSamp, dudvUV1, 0.0).rg * 0.1;
    let dudvUV2 = waterUV + vec2f(dudv1.x, dudv1.y + moveFactor);
    let dudv2 = textureSampleLevel(waterDuDv, dudvSamp, dudvUV2, 0.0).rg * 2.0 - vec2f(1.0);

    var tang = cross(N, vec3f(0.0, 1.0, 0.0));
    if (dot(tang, tang) < 0.01) { tang = cross(N, vec3f(1.0, 0.0, 0.0)); }
    tang = normalize(tang);
    let bitang = normalize(cross(N, tang));
    let waveN = normalize(N + tang * dudv2.x * 0.3 + bitang * dudv2.y * 0.3);

    // ── Screen-space depth difference (tutorial §1.2) ─────────────────
    // Depth is read with textureLoad (no sampler) so the colour binding
    // can keep its filtering linear sampler — WebGPU's auto-layout marks
    // any sampler used with a depth texture as NonFiltering, which would
    // otherwise collide with the colour binding's filtering needs.
    let invViewport = u.viewport.zw;
    let viewportSize = vec2<i32>(i32(u.viewport.x), i32(u.viewport.y));
    let viewportMax = viewportSize - vec2<i32>(1);
    let pixelCoord0 = clamp(vec2<i32>(fragCoord.xy), vec2<i32>(0), viewportMax);
    let screenUV0 = fragCoord.xy * invViewport;

    let surfaceEye = linearizeLogDepth(fragCoord.z);
    let bgNdcZ0 = textureLoad(sceneDepth, pixelCoord0, 0);
    let bgEye0  = linearizeLogDepth(bgNdcZ0);
    let depthDiff0 = max(0.0, bgEye0 - surfaceEye);

    // ── Refraction (tutorial §2) ──────────────────────────────────────
    // saturate(depthDiff) gates the offset so shores stay sharp.
    // aspect compensation makes the screen-space pixel offset roughly
    // isotropic regardless of canvas aspect ratio.
    let aspect = u.viewport.x * invViewport.y;
    var uvOffset = vec2f(dudv2.x, dudv2.y) * refractionStrength;
    uvOffset.y = uvOffset.y * aspect;
    uvOffset = uvOffset * saturate(depthDiff0);
    var refractedUV = clamp(screenUV0 + uvOffset, vec2f(0.0), vec2f(1.0));

    // Re-sample depth at the refracted UV. If that pixel is in front of
    // the water surface (we sampled sideways into a foreground occluder),
    // discard the refraction and fall back to the straight UV.
    let pixelCoordR = clamp(vec2<i32>(refractedUV * u.viewport.xy), vec2<i32>(0), viewportMax);
    let bgNdcZ = textureLoad(sceneDepth, pixelCoordR, 0);
    let bgEye  = linearizeLogDepth(bgNdcZ);
    var diff = bgEye - surfaceEye;
    var bgUV = refractedUV;
    if (diff < 0.0) {
        bgUV = screenUV0;
        diff = depthDiff0;
    }

    // Refracted background colour (filtered linear via sceneSamp).
    let bgColor = textureSampleLevel(sceneColor, sceneSamp, bgUV, 0.0).rgb;

    // ── Underwater colour (tutorial §1.4) ─────────────────────────────
    let fogFactor    = exp2(-fogDensity * max(diff, 0.0));
    let throughWater = mix(u.fogColor.rgb, bgColor, fogFactor);

    // ── Surface highlights (kept from the legacy shader so the water
    //    surface still has the existing aesthetic) ──────────────────────
    let R = reflect(-V, waveN);
    let skyGrad = R.y * 0.5 + 0.5;
    let reflectColor = mix(vec3f(0.30, 0.40, 0.50), vec3f(0.50, 0.65, 0.85), skyGrad);

    let refractiveFactor = pow(max(dot(V, waveN), 0.0), 0.5);
    let waterBase = mix(reflectColor, throughWater, refractiveFactor);

    let reflectedLight = reflect(-L, waveN);
    let spec = pow(max(dot(reflectedLight, V), 0.0), 64.0);
    let specHighlight = vec3f(1.0, 0.95, 0.85) * spec * 0.5;

    let NdotL = max(dot(N, L), 0.0);
    let lit = waterBase * (0.4 + NdotL * 0.6) + specHighlight;

    // Opaque output. The fog mix has already composited the refracted
    // background into the result, so we don't blend.
    return vec4f(lit, 1.0);
}
