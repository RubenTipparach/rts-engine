// Standalone water shader — used by WaterRenderer for the planet's water
// sphere. Separate from terrain.wgsl so the water can run on its own
// alpha-blended pipeline without affecting the opaque terrain pass.
//
// Renders a translucent water surface with: animated DuDv-driven wave
// distortion, normal-mapped specular + Fresnel reflection, depth-based
// colour gradient (shallow → bright teal, deep → near-navy), shore foam,
// and a depth-driven alpha so shallow shores fade out and the terrain
// underneath shows through. NO terrain texture is sampled inside this
// shader — the water material is its own colour.
//
// Refraction/depth-FX upgrade (TODO when IGPU gets render-target support):
// add bindings for sceneColor + sceneDepth textures rendered in the
// preceding opaque pass; sample sceneColor at a DuDv-offset UV for true
// refraction, and read sceneDepth to compute true water-column thickness
// (currently approximated as the static `oceanDepth` uniform).

struct Uniforms {
    mvp: mat4x4f,
    sunDir: vec4f,
    cameraPos: vec4f,
    // params.x = time
    // params.y = (reserved)
    // params.z = water column thickness in world units (= 3/4 stepHeight,
    //            i.e. the geometric distance from the water surface sphere
    //            down to the seabed level)
    // params.w = (reserved)
    params: vec4f,
}

@binding(0) @group(0) var<uniform> u: Uniforms;
@binding(1) @group(0) var samp: sampler;
@binding(2) @group(0) var waterDuDv: texture_2d<f32>;
// (waterNormal binding removed — Dawn's `layout: 'auto'` analyser kept
//  pruning it as unused regardless of how directly its sample fed the
//  fragment output. Could revisit when we move to an explicit pipeline
//  layout. For now the wave normal comes from the geometric N plus the
//  DuDv-derived perturbation only — visually less detailed than a true
//  normal-mapped surface but matches the auto-layout exactly.)

struct VSOutput {
    @builtin(position) position: vec4f,
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
}

// Logarithmic depth — must match terrain.wgsl exactly so the water surface
// sorts correctly against terrain, atmosphere, sun, distant planets.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
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
    @location(0) worldPos: vec3f,
    @location(1) normal: vec3f,
) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let V = normalize(u.cameraPos.xyz - worldPos);
    let t = u.params.x;
    let oceanDepth = u.params.z;

    // Triplanar UV for sphere — pick the axis the surface normal aligns
    // with most so the wave pattern doesn't stretch at any pole.
    let b = max(abs(N), vec3f(0.001, 0.001, 0.001));
    let total = b.x + b.y + b.z;
    let wx = b.x / total;
    let wy = b.y / total;
    let wz = b.z / total;
    let tiling = 6.0;
    var waterUV: vec2f;
    if (wy > wx && wy > wz) { waterUV = worldPos.xz * tiling; }
    else if (wx > wz)       { waterUV = worldPos.zy * tiling; }
    else                     { waterUV = worldPos.xy * tiling; }

    // Animated DuDv distortion — two scrolling layers drive the wave
    // perturbation, classic OpenGL-water approach. The DuDv samples form
    // a small XY offset on N for the Fresnel/specular calculations below;
    // we no longer sample a separate normal map for full tangent-space
    // wave normals (see binding comment above).
    let moveSpeed = 0.03;
    let moveFactor = t * moveSpeed;
    let dudvUV1 = vec2f(waterUV.x + moveFactor, waterUV.y);
    let dudv1 = textureSampleLevel(waterDuDv, samp, dudvUV1, 0.0).rg * 0.1;
    let dudvUV2 = waterUV + vec2f(dudv1.x, dudv1.y + moveFactor);
    let dudv2 = textureSampleLevel(waterDuDv, samp, dudvUV2, 0.0).rg * 2.0 - vec2f(1.0);

    // Wave normal: tilt the surface normal in tangent space by the DuDv
    // distortion. Cheap stand-in for a proper normal map — gives the
    // surface enough variation that the Fresnel and specular terms below
    // sparkle plausibly without needing a second texture.
    var tang = cross(N, vec3f(0.0, 1.0, 0.0));
    if (dot(tang, tang) < 0.01) { tang = cross(N, vec3f(1.0, 0.0, 0.0)); }
    tang = normalize(tang);
    let bitang = normalize(cross(N, tang));
    let waveN = normalize(N + tang * dudv2.x * 0.3 + bitang * dudv2.y * 0.3);

    // Slab-thickness path length: from this water-surface fragment, the
    // ray going inward hits the seabed sphere after (oceanDepth / cos(view
    // angle from N)) world units. Clamped cosine so grazing rays get a
    // long-but-finite path. This is fake depth — when sceneDepth is wired
    // up later, replace with `sceneDepth_at_this_pixel - water_surface_depth`.
    let viewCos = max(dot(N, V), 0.08);
    let pathLen = oceanDepth / viewCos;

    // Depth-based water colour. No terrain texture is sampled. The
    // smoothstep range = "fog depth": path length at which the colour
    // saturates from shallow to deep. Halved from the previous 4× so the
    // fog reads as denser — water turns opaque-navy at shorter visible
    // path lengths instead of staying mid-teal across most of the basin.
    let shallowColor = vec3f(0.18, 0.55, 0.65);
    let deepColor    = vec3f(0.02, 0.10, 0.22);
    let depth01      = smoothstep(0.0, oceanDepth * 2.0, pathLen);
    let throughWater = mix(shallowColor, deepColor, depth01);

    // Shore foam — splotchy noise modulated by depth proximity.
    let depthFoamMask = 1.0 - smoothstep(0.0, oceanDepth * 1.5, pathLen);
    let foamPattern = textureSampleLevel(waterDuDv, samp, dudvUV2 * 0.5, 0.0).g;
    let foamShape = smoothstep(0.35, 0.65, foamPattern + depthFoamMask * 0.5);
    let foam = foamShape * smoothstep(0.0, 1.0, depthFoamMask);

    // Sky reflection (approximated gradient on reflected ray's Y).
    let R = reflect(-V, waveN);
    let skyGrad = R.y * 0.5 + 0.5;
    let reflectColor = mix(vec3f(0.30, 0.40, 0.50), vec3f(0.50, 0.65, 0.85), skyGrad);

    // Fresnel — more reflection at grazing angle.
    let refractiveFactor = pow(max(dot(V, waveN), 0.0), 0.5);
    let waterBase = mix(reflectColor, throughWater, refractiveFactor);

    // Sun specular on the wave normal.
    let reflectedLight = reflect(-L, waveN);
    let spec = pow(max(dot(reflectedLight, V), 0.0), 64.0);
    let specHighlight = vec3f(1.0, 0.95, 0.85) * spec * 0.5;

    let NdotL = max(dot(N, L), 0.0);
    let lit = waterBase * (0.4 + NdotL * 0.6) + specHighlight;
    let withFoam = mix(lit, vec3f(0.95, 0.97, 1.0), foam);

    // Alpha — translucent at shallow, opaque at depth + foam. Shore foam
    // forces alpha back up so it reads cleanly over whatever's beneath.
    // When sceneDepth becomes available, use the real water-column depth
    // instead of the path-length proxy here for a more accurate "fade in
    // at the shoreline" effect.
    let alphaCore = mix(0.55, 0.95, depth01);
    let alpha = max(alphaCore, foam);

    return vec4f(withFoam, alpha);
}
