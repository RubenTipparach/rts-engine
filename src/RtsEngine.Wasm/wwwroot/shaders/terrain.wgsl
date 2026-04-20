struct Uniforms {
    mvp : mat4x4f,
}
@binding(0) @group(0) var<uniform> uniforms : Uniforms;

struct VSOutput {
    @builtin(position) position : vec4f,
    @location(0) color : vec3f,
}

@vertex
fn vs_main(
    @location(0) pos : vec3f,
    @location(1) col : vec3f
) -> VSOutput {
    var out : VSOutput;
    out.position = uniforms.mvp * vec4f(pos, 1.0);
    out.color = col;
    return out;
}

@fragment
fn fs_main(@location(0) color : vec3f) -> @location(0) vec4f {
    return vec4f(color, 1.0);
}
