using Microsoft.JSInterop;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// OpenGL-like static API — the sokol pattern for C#.
///
/// Engine code calls GL.bindBuffer(), GL.drawElements(), etc.
/// These are plain C# calls with zero awareness of the platform.
///
/// WASM:    routes through gl-proxy.js via IJSRuntime (generic bridge)
/// Desktop: would route through Silk.NET.OpenGL P/Invoke (native GL)
///
/// Like sokol_gfx, the developer never writes or sees JS.
/// gl-proxy.js is infrastructure — a 1:1 mapping of GL calls to WebGL,
/// written once, shared across all projects.
/// </summary>
public static class GL
{
    private static IJSRuntime _js = null!;

    // ── GL constants (subset matching WebGL / OpenGL ES 2.0) ──────
    public const int COLOR_BUFFER_BIT = 0x4000;
    public const int DEPTH_BUFFER_BIT = 0x0100;
    public const int DEPTH_TEST = 0x0B71;
    public const int CULL_FACE = 0x0B44;
    public const int ARRAY_BUFFER = 0x8892;
    public const int ELEMENT_ARRAY_BUFFER = 0x8893;
    public const int STATIC_DRAW = 0x88E4;
    public const int FLOAT = 0x1406;
    public const int UNSIGNED_SHORT = 0x1403;
    public const int TRIANGLES = 0x0004;
    public const int VERTEX_SHADER = 0x8B31;
    public const int FRAGMENT_SHADER = 0x8B30;
    public const int COMPILE_STATUS = 0x8B81;
    public const int LINK_STATUS = 0x8B82;

    /// <summary>
    /// Initialize the GL proxy. On WASM this sets the JS runtime;
    /// on desktop this would initialize the Silk.NET GL context.
    /// Call once at startup.
    /// </summary>
    public static async Task<bool> Init(IJSRuntime js, string canvasId)
    {
        _js = js;
        return await _js.InvokeAsync<bool>("GLProxy.init", canvasId);
    }

    // ── State ─────────────────────────────────────────────────────

    public static void Enable(int cap)
        => _js.InvokeVoidAsync("GLProxy.enable", cap);

    public static void ClearColor(float r, float g, float b, float a)
        => _js.InvokeVoidAsync("GLProxy.clearColor", r, g, b, a);

    public static void Clear(int mask)
        => _js.InvokeVoidAsync("GLProxy.clear", mask);

    public static void Viewport(int x, int y, int w, int h)
        => _js.InvokeVoidAsync("GLProxy.viewport", x, y, w, h);

    // ── Shaders ───────────────────────────────────────────────────

    public static ValueTask<int> CreateShader(int type)
        => _js.InvokeAsync<int>("GLProxy.createShader", type);

    public static void ShaderSource(int shader, string source)
        => _js.InvokeVoidAsync("GLProxy.shaderSource", shader, source);

    public static void CompileShader(int shader)
        => _js.InvokeVoidAsync("GLProxy.compileShader", shader);

    public static ValueTask<bool> GetShaderParameter(int shader, int pname)
        => _js.InvokeAsync<bool>("GLProxy.getShaderParameter", shader, pname);

    public static ValueTask<string> GetShaderInfoLog(int shader)
        => _js.InvokeAsync<string>("GLProxy.getShaderInfoLog", shader);

    public static ValueTask<int> CreateProgram()
        => _js.InvokeAsync<int>("GLProxy.createProgram");

    public static void AttachShader(int program, int shader)
        => _js.InvokeVoidAsync("GLProxy.attachShader", program, shader);

    public static void LinkProgram(int program)
        => _js.InvokeVoidAsync("GLProxy.linkProgram", program);

    public static ValueTask<bool> GetProgramParameter(int program, int pname)
        => _js.InvokeAsync<bool>("GLProxy.getProgramParameter", program, pname);

    public static ValueTask<string> GetProgramInfoLog(int program)
        => _js.InvokeAsync<string>("GLProxy.getProgramInfoLog", program);

    public static void UseProgram(int program)
        => _js.InvokeVoidAsync("GLProxy.useProgram", program);

    public static void DeleteShader(int shader)
        => _js.InvokeVoidAsync("GLProxy.deleteShader", shader);

    // ── Buffers ───────────────────────────────────────────────────

    public static ValueTask<int> CreateBuffer()
        => _js.InvokeAsync<int>("GLProxy.createBuffer");

    public static void BindBuffer(int target, int buffer)
        => _js.InvokeVoidAsync("GLProxy.bindBuffer", target, buffer);

    public static void BufferDataFloat(int target, float[] data, int usage)
        => _js.InvokeVoidAsync("GLProxy.bufferDataFloat", target, data, usage);

    public static void BufferDataUshort(int target, ushort[] data, int usage)
        => _js.InvokeVoidAsync("GLProxy.bufferDataUshort", target, data, usage);

    // ── Vertex attributes ─────────────────────────────────────────

    public static ValueTask<int> GetAttribLocation(int program, string name)
        => _js.InvokeAsync<int>("GLProxy.getAttribLocation", program, name);

    public static void EnableVertexAttribArray(int index)
        => _js.InvokeVoidAsync("GLProxy.enableVertexAttribArray", index);

    public static void VertexAttribPointer(int index, int size, int type, bool normalized, int stride, int offset)
        => _js.InvokeVoidAsync("GLProxy.vertexAttribPointer", index, size, type, normalized, stride, offset);

    // ── Uniforms ──────────────────────────────────────────────────

    public static ValueTask<int> GetUniformLocation(int program, string name)
        => _js.InvokeAsync<int>("GLProxy.getUniformLocation", program, name);

    public static void UniformMatrix4fv(int location, bool transpose, float[] value)
        => _js.InvokeVoidAsync("GLProxy.uniformMatrix4fv", location, transpose, value);

    // ── Draw ──────────────────────────────────────────────────────

    public static void DrawElements(int mode, int count, int type, int offset)
        => _js.InvokeVoidAsync("GLProxy.drawElements", mode, count, type, offset);

    // ── Cleanup ───────────────────────────────────────────────────

    public static void DeleteBuffer(int buffer)
        => _js.InvokeVoidAsync("GLProxy.deleteBuffer", buffer);

    public static void DeleteProgram(int program)
        => _js.InvokeVoidAsync("GLProxy.deleteProgram", program);
}
