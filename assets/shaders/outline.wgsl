// Solid-color line shader for cell outlines and orbit rings. The `color`
// uniform's alpha is honoured by the alpha-blended line pipeline so callers
// can fade rings in/out without rebuilding geometry.

struct Uniforms {
    mvp: mat4x4f,
    color: vec4f, // rgb + alpha
}

@binding(0) @group(0) var<uniform> u: Uniforms;

// Logarithmic depth (see sun.wgsl). Cell outline + orbit rings live in the
// same scene as terrain / sun / distant planets, so they need the matching
// depth curve for correct sorting.
const LOG_DEPTH_FAR = 10000.0;
fn applyLogDepth(p: vec4f) -> vec4f {
    let logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4f(p.x, p.y, logZ * p.w, p.w);
}

@vertex
fn vs_main(@location(0) pos: vec3f) -> @builtin(position) vec4f {
    return applyLogDepth(u.mvp * vec4f(pos, 1.0));
}

@fragment
fn fs_main() -> @location(0) vec4f {
    return u.color;
}
