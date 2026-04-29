// Simple solid-color line shader for cell outline highlight.
// Uses only MVP; color is hardcoded (yellow).

struct Uniforms {
    mvp: mat4x4f,
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
    return vec4f(1.0, 0.9, 0.2, 1.0);
}
