// RTS entity shader — simple Lambert-lit solid color, used for buildings and
// units placed on the planet surface. Vertex format matches RtsRenderer:
// pos(3f) + normal(3f) = 6 floats, stride 24.

struct Uniforms {
    mvp: mat4x4f,
    color: vec4f,    // rgb + alpha
    sunDir: vec4f,   // xyz: direction *toward* the sun, world space
}
@binding(0) @group(0) var<uniform> u: Uniforms;

// Logarithmic depth — same curve as terrain/solar system shaders so RTS
// entities z-sort correctly against the planet and backdrop.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}

struct VSOut {
    @builtin(position) position: vec4f,
    @location(0) normal: vec3f,
}

@vertex
fn vs_main(@location(0) pos: vec3f, @location(1) normal: vec3f) -> VSOut {
    var out: VSOut;
    out.position = applyLogDepth(u.mvp * vec4f(pos, 1.0));
    // Normal is already in planet-local space, oriented by the model matrix
    // when building meshes are placed. Pass through unchanged.
    out.normal = normal;
    return out;
}

@fragment
fn fs_main(@location(0) normal: vec3f) -> @location(0) vec4f {
    let N = normalize(normal);
    let L = normalize(u.sunDir.xyz);
    let NdotL = max(dot(N, L), 0.0);
    let lit = u.color.rgb * (0.30 + NdotL * 0.85);
    return vec4f(lit, u.color.a);
}
