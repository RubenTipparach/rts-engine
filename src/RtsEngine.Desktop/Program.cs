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
        DesktopRenderer? renderer = null;
        DesktopAppBackend? backend = null;
        GameEngine? engine = null;

        window.Load += () =>
        {
            var gl = window.CreateOpenGL();
            renderer = new DesktopRenderer(gl);
            backend = new DesktopAppBackend(window);
            engine = new GameEngine(backend, renderer);
            engine.Run();
        };

        window.Render += _ =>
        {
            backend?.Tick();
        };

        window.Closing += () =>
        {
            renderer?.Dispose();
        };

        window.Run();
    }
}

/// <summary>
/// Desktop IRenderer — Silk.NET.OpenGL, uses CubeMesh from Core.
/// </summary>
internal sealed class DesktopRenderer : IRenderer, IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ibo, _program;
    private readonly int _mvpLocation;

    private const string VS = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aCol;
uniform mat4 uMVP;
out vec3 vColor;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vColor = aCol;
}";

    private const string FS = @"
#version 330 core
in vec3 vColor;
out vec4 FragColor;
void main() {
    FragColor = vec4(vColor, 1.0);
}";

    public unsafe DesktopRenderer(GL gl)
    {
        _gl = gl;

        // Shaders
        var vs = Compile(ShaderType.VertexShader, VS);
        var fs = Compile(ShaderType.FragmentShader, FS);
        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        _gl.LinkProgram(_program);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        _gl.UseProgram(_program);
        _mvpLocation = _gl.GetUniformLocation(_program, "uMVP");

        // Buffers — vertex data from Core.CubeMesh
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = CubeMesh.Vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(CubeMesh.Vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _ibo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ibo);
        fixed (ushort* p = CubeMesh.Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(CubeMesh.Indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)CubeMesh.VertexStride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)CubeMesh.VertexStride, (void*)12);

        _gl.Enable(EnableCap.DepthTest);
        _gl.ClearColor(0.05f, 0.05f, 0.12f, 1.0f);
    }

    public unsafe void Draw(float[] mvpRawFloats)
    {
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
        fixed (float* p = mvpRawFloats)
            _gl.UniformMatrix4(_mvpLocation, 1, false, p);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)CubeMesh.IndexCount, DrawElementsType.UnsignedShort, null);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ibo);
        _gl.DeleteProgram(_program);
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

    private bool _dragging;
    private Vector2 _lastMouse;

    public DesktopAppBackend(IWindow window)
    {
        _window = window;
        var input = window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (_, btn) =>
            {
                if (btn != MouseButton.Left) return;
                _dragging = true;
                _lastMouse = mouse.Position;
                PointerDown?.Invoke();
            };
            mouse.MouseUp += (_, btn) =>
            {
                if (btn != MouseButton.Left) return;
                _dragging = false;
                PointerUp?.Invoke();
            };
            mouse.MouseMove += (_, pos) =>
            {
                if (!_dragging) return;
                var dx = pos.X - _lastMouse.X;
                var dy = pos.Y - _lastMouse.Y;
                _lastMouse = pos;
                PointerDrag?.Invoke(dx, dy);
            };
        }
    }

    public void StartLoop(Func<Task> onTick) => _onTick = onTick;
    public void StopLoop() => _onTick = null;
    public void Tick() => _onTick?.Invoke();
    public void Dispose() { }
}
