using System.Numerics;
using RtsEngine.Core;
using RtsEngine.Game;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace RtsEngine.Desktop;

public static class Program
{
    public static void Main()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Vector2D<int>(1280, 720);
        opts.Title = "RTS Engine - Silk.NET Desktop";
        opts.API = new GraphicsAPI(ContextAPI.OpenGL, new APIVersion(3, 3));

        var window = Window.Create(opts);
        OpenGLGPU? gpu = null;
        PlanetRenderer? renderer = null;
        DesktopAppBackend? backend = null;
        GameEngine? engine = null;

        window.Load += async () =>
        {
            var gl = window.CreateOpenGL();

            gpu = new OpenGLGPU(gl);
            var mesh = new PlanetMesh(gridResolution: 12, radius: 1.0f, stepHeight: 0.04f, initialLevel: 1);
            SeedDemoHeightmap(mesh);
            renderer = new PlanetRenderer(gpu, mesh);
            await renderer.Setup(OpenGLGPU.TerrainShaderGLSL);

            backend = new DesktopAppBackend(window);
            engine = new GameEngine(backend, renderer);
            engine.Run();
        };

        window.Render += _ => backend?.Tick();
        window.Closing += () => gpu?.Dispose();
        window.Run();
    }

    private static void SeedDemoHeightmap(PlanetMesh mesh)
    {
        int N = mesh.GridResolution;
        for (int f = 0; f < 6; f++)
        {
            var face = (CubeFace)f;
            for (int r = N / 3; r < N - N / 3; r++)
                for (int c = N / 3; c < N - N / 3; c++)
                    mesh.SetLevel(face, r, c, 2);
            mesh.SetLevel(face, N / 2, N / 2, 3);
            mesh.SetLevel(face, 0, 0, 0);
            mesh.SetLevel(face, 0, N - 1, 0);
            mesh.SetLevel(face, N - 1, 0, 0);
            mesh.SetLevel(face, N - 1, N - 1, 0);
        }
    }
}

/// <summary>
/// OpenGL implementation of IGPU — same interface as WebGPU, backed by Silk.NET.
/// PlanetRenderer in Game calls IGPU methods; this translates them to GL calls.
/// </summary>
internal sealed class OpenGLGPU : IGPU, IDisposable
{
    private readonly GL _gl;

    // Handle tables (mirroring the JS proxy pattern)
    private readonly List<uint> _shaders = new() { 0 };
    private readonly List<uint> _buffers = new() { 0 };
    private readonly List<uint> _programs = new() { 0 };
    private readonly List<int> _uniforms = new() { 0 };
    private readonly List<uint> _vaos = new() { 0 };

    // Track pipeline → (program, vao) mapping
    private readonly Dictionary<int, (int programId, int vaoId)> _pipelines = new();
    // Track bind group → uniform buffer mapping
    private readonly Dictionary<int, List<(int binding, int bufferId)>> _bindGroups = new();

    private int _nextPipelineId = 1;
    private int _nextBindGroupId = 1;

    public const string TerrainShaderGLSL = @"
#version 330 core
// -- VERTEX --
#ifdef VERTEX
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aCol;
uniform mat4 uMVP;
out vec3 vColor;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vColor = aCol;
}
#endif
// -- FRAGMENT --
#ifdef FRAGMENT
in vec3 vColor;
out vec4 FragColor;
void main() {
    FragColor = vec4(vColor, 1.0);
}
#endif
";

    public OpenGLGPU(GL gl)
    {
        _gl = gl;
        _gl.Enable(EnableCap.DepthTest);
        _gl.ClearColor(0.05f, 0.05f, 0.12f, 1.0f);
    }

    public Task<int> CreateShaderModule(string shaderCode)
    {
        // Compile vertex + fragment from combined source
        var vs = Compile(ShaderType.VertexShader, "#version 330 core\n#define VERTEX\n" + StripVersionAndBlocks(shaderCode));
        var fs = Compile(ShaderType.FragmentShader, "#version 330 core\n#define FRAGMENT\n" + StripVersionAndBlocks(shaderCode));

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        var id = _programs.Count;
        _programs.Add(program);
        return Task.FromResult(id);
    }

    public unsafe Task<int> CreateVertexBuffer(float[] data)
    {
        var buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        var id = _buffers.Count;
        _buffers.Add(buf);
        return Task.FromResult(id);
    }

    public unsafe Task<int> CreateIndexBuffer(ushort[] data)
    {
        var buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, buf);
        fixed (ushort* p = data)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(data.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        var id = _buffers.Count;
        _buffers.Add(buf);
        return Task.FromResult(id);
    }

    public Task<int> CreateUniformBuffer(int sizeBytes)
    {
        // OpenGL uniforms don't need a buffer — we use glUniformMatrix4fv directly.
        // Return a dummy handle; the actual uniform location is resolved at render time.
        var id = _buffers.Count;
        _buffers.Add(0);
        return Task.FromResult(id);
    }

    public unsafe void WriteBuffer(int bufferId, float[] data)
    {
        // Store MVP data for next render call
        _pendingMvp = data;
    }

    private float[]? _pendingMvp;

    public unsafe Task<int> CreateRenderPipeline(int shaderModuleId, object[] vertexBufferLayouts)
    {
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        // Set up vertex attributes from layout description
        // Layout: pos(float32x3) + color(float32x3), stride 24
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);

        var vaoId = _vaos.Count;
        _vaos.Add(vao);

        var pipelineId = _nextPipelineId++;
        _pipelines[pipelineId] = (shaderModuleId, vaoId);
        return Task.FromResult(pipelineId);
    }

    public Task<int> CreateBindGroup(int pipelineId, int groupIndex, object[] entries)
    {
        var bgId = _nextBindGroupId++;
        // Not needed for basic GL uniforms, but track for API compatibility
        _bindGroups[bgId] = new();
        return Task.FromResult(bgId);
    }

    public unsafe void Render(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var p)) return;

        var program = _programs[p.programId];
        var vao = _vaos[p.vaoId];

        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(program);
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _buffers[vertexBufferId]);

        // Re-set vertex attribs (needed after binding new VBO)
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _buffers[indexBufferId]);

        if (_pendingMvp != null)
        {
            var loc = _gl.GetUniformLocation(program, "uMVP");
            fixed (float* ptr = _pendingMvp)
                _gl.UniformMatrix4(loc, 1, false, ptr);
        }

        _gl.DrawElements(PrimitiveType.Triangles, (uint)indexCount, DrawElementsType.UnsignedShort, null);
    }

    public void DestroyBuffer(int bufferId)
    {
        if (bufferId > 0 && bufferId < _buffers.Count && _buffers[bufferId] != 0)
        {
            _gl.DeleteBuffer(_buffers[bufferId]);
            _buffers[bufferId] = 0;
        }
    }

    public void Dispose()
    {
        for (int i = 1; i < _buffers.Count; i++)
            if (_buffers[i] != 0) _gl.DeleteBuffer(_buffers[i]);
        for (int i = 1; i < _programs.Count; i++)
            if (_programs[i] != 0) _gl.DeleteProgram(_programs[i]);
        for (int i = 1; i < _vaos.Count; i++)
            if (_vaos[i] != 0) _gl.DeleteVertexArray(_vaos[i]);
    }

    private uint Compile(ShaderType type, string source)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        var log = _gl.GetShaderInfoLog(s);
        if (!string.IsNullOrEmpty(log)) Console.Error.WriteLine($"Shader ({type}): {log}");
        return s;
    }

    private static string StripVersionAndBlocks(string src)
    {
        // Remove any existing #version line and #ifdef blocks from the combined source
        var lines = src.Split('\n');
        var result = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#version")) continue;
            if (trimmed.StartsWith("#ifdef") || trimmed.StartsWith("#endif")) continue;
            if (trimmed.StartsWith("// --")) continue;
            result.AppendLine(line);
        }
        return result.ToString();
    }
}

/// <summary>
/// Desktop IRenderBackend — Silk.NET.Windowing + Input.
/// </summary>
internal sealed class DesktopAppBackend : IRenderBackend
{
    private readonly IWindow _window;
    private Func<Task>? _onTick;

    public float CanvasWidth => _window.Size.X;
    public float CanvasHeight => _window.Size.Y;

    public event Action? PointerDown;
    public event Action<float, float>? PointerDrag;
    public event Action? PointerUp;
    public event Action<float, float, int>? PointerClick;
    public event Action<float>? Scroll;

    private bool _dragging;
    private Vector2 _lastMouse;
    private Vector2 _downMouse;
    private float _totalDragDist;
    private const float ClickThreshold = 5f;

    public DesktopAppBackend(IWindow window)
    {
        _window = window;
        var input = window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, btn) =>
            {
                _dragging = true;
                _lastMouse = mouse.Position;
                _downMouse = mouse.Position;
                _totalDragDist = 0;
                PointerDown?.Invoke();
            };
            mouse.MouseUp += (m, btn) =>
            {
                if (!_dragging) return;
                _dragging = false;
                PointerUp?.Invoke();
                if (_totalDragDist < ClickThreshold)
                {
                    int button = btn == MouseButton.Left ? 0 : btn == MouseButton.Right ? 2 : 1;
                    PointerClick?.Invoke(mouse.Position.X, mouse.Position.Y, button);
                }
            };
            mouse.MouseMove += (m, pos) =>
            {
                if (!_dragging) return;
                var dx = pos.X - _lastMouse.X;
                var dy = pos.Y - _lastMouse.Y;
                _totalDragDist += MathF.Abs(dx) + MathF.Abs(dy);
                PointerDrag?.Invoke(dx, dy);
                _lastMouse = pos;
            };
            mouse.Scroll += (m, wheel) =>
            {
                Scroll?.Invoke(wheel.Y * 120f);
            };
        }
    }

    public void StartLoop(Func<Task> onTick) => _onTick = onTick;
    public void StopLoop() => _onTick = null;
    public void Tick() => _onTick?.Invoke();
    public void Dispose() { }
}
