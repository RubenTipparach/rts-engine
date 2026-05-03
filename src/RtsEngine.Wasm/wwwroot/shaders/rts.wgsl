// RTS entity shader — Lambert-lit textured solid for buildings + units.
// Vertex format: pos(3f) + normal(3f) = 6 floats, stride 24.
//
// Texture is triplanar-sampled from model-space position because the .obj
// models don't carry UVs. Livery: pixels exactly equal to (1, 0, 1) (the
// reserved magenta slot in the 64-color texture palette) get replaced with
// the per-instance team color, so paint a magenta band/stripe into the
// PNG and that region recolors to whichever team owns the unit.
//
// Markers (selection disc, HP bar) reuse the same pipeline with sunDir.w = 1
// to skip Lambert and use the color uniform directly.

struct Uniforms {
    mvp: mat4x4f,
    color: vec4f,        // rgb = base tint multiplied with the texture sample
    sunDir: vec4f,       // xyz dir toward sun (model space), w = flat-shading flag
    teamColor: vec4f,    // rgb = livery substitute, w unused
}
@binding(0) @group(0) var<uniform> u: Uniforms;
@binding(1) @group(0) var samp: sampler;
@binding(2) @group(0) var tex: texture_2d<f32>;

const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}

struct VSOut {
    @builtin(position) position: vec4f,
    @location(0) normal: vec3f,
    @location(1) modelPos: vec3f,
}

@vertex
fn vs_main(@location(0) pos: vec3f, @location(1) normal: vec3f) -> VSOut {
    var out: VSOut;
    out.position = applyLogDepth(u.mvp * vec4f(pos, 1.0));
    out.normal = normal;
    out.modelPos = pos;
    return out;
}

const LIVERY = vec3f(1.0, 0.0, 1.0);
const TEXTURE_SCALE = 80.0;  // larger = denser tiling on the model

fn sampleTri(uv: vec2f) -> vec3f {
    return textureSampleLevel(tex, samp, fract(uv), 0.0).rgb;
}

@fragment
fn fs_main(@location(0) normal: vec3f, @location(1) modelPos: vec3f) -> @location(0) vec4f {
    let N = normalize(normal);

    // Triplanar UV from model-space position. Each axis projects the model
    // onto a 2D plane; we blend by absolute normal components so faces
    // facing +Y get the XZ projection most strongly, etc.
    let p = modelPos * TEXTURE_SCALE;
    let cX = sampleTri(p.zy);
    let cY = sampleTri(p.xz);
    let cZ = sampleTri(p.xy);
    let bw = abs(N) + vec3f(1e-3);
    let total = bw.x + bw.y + bw.z;
    let w = bw / total;

    // Per-axis livery test, then blend. A pure-magenta texel on any plane
    // means "livery here" — substitute the team color before blending so
    // partial bleed at the magenta border still recolors to team.
    let mX = select(cX, u.teamColor.rgb, length(cX - LIVERY) < 0.05);
    let mY = select(cY, u.teamColor.rgb, length(cY - LIVERY) < 0.05);
    let mZ = select(cZ, u.teamColor.rgb, length(cZ - LIVERY) < 0.05);
    let texCol = mX * w.x + mY * w.y + mZ * w.z;

    // Per-instance tint multiplied with the texture sample so the YAML
    // base-color still shows through if the texture is mostly grayscale.
    let surface = texCol * u.color.rgb;

    let L = normalize(u.sunDir.xyz);
    let NdotL = max(dot(N, L), 0.0);
    // sunDir.w == 1.0 flags a flat-shaded marker (selection disc, HP bar) —
    // same pipeline, no Lambert fold so the color reads as authored.
    let lit = mix(surface * (0.30 + NdotL * 0.85), u.color.rgb, u.sunDir.w);
    return vec4f(lit, u.color.a);
}
