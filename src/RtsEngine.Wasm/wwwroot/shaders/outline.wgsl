// Simple solid-color line shader for cell outline highlight.
// Uses only MVP; color is hardcoded (yellow).

struct Uniforms {
    mvp: mat4x4f,
}

@binding(0) @group(0) var<uniform> u: Uniforms;

@vertex
fn vs_main(@location(0) pos: vec3f) -> @builtin(position) vec4f {
    return u.mvp * vec4f(pos, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4f {
    return vec4f(1.0, 0.9, 0.2, 1.0);
}
