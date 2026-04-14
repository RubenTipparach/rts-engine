using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace RtsEngine.Desktop;

/// <summary>
/// Desktop spinning cube — same engine, Silk.NET native OpenGL.
/// No Blazor, no JS, no browser. Pure desktop.
/// </summary>
public static class Program
{
    private static IWindow _window = null!;
    private static GL _gl = null!;

    // GL handles
    private static uint _vao, _vbo, _ibo, _shaderProgram;
    private static int _mvpLocation;

    // Rotation state — same physics as WASM version
    private static float _rotationX, _rotationY;
    private static float _velocityX, _velocityY;
    private static bool _dragging;
    private static Vector2 _lastMouse;
    private static DateTime _lastFrameTime = DateTime.UtcNow;

    private const float FreeDamping = 0.04f;
    private const float DragDamping = 0.001f;
    private const float PixelsToRadians = 0.005f;

    private const string VertexShader = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aCol;
uniform mat4 uMVP;
out vec3 vColor;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vColor = aCol;
}";

    private const string FragmentShader = @"
#version 330 core
in vec3 vColor;
out vec4 FragColor;
void main() {
    FragColor = vec4(vColor, 1.0);
}";

    // Same cube data as WASM version
    private static readonly float[] Vertices =
    {
        -1,-1, 1,  1.0f,0.2f,0.2f,   1,-1, 1,  1.0f,0.2f,0.2f,   1, 1, 1,  1.0f,0.4f,0.4f,  -1, 1, 1,  1.0f,0.4f,0.4f,
        -1,-1,-1,  0.2f,1.0f,0.2f,  -1, 1,-1,  0.2f,1.0f,0.4f,   1, 1,-1,  0.4f,1.0f,0.4f,   1,-1,-1,  0.4f,1.0f,0.2f,
        -1, 1,-1,  0.2f,0.2f,1.0f,  -1, 1, 1,  0.2f,0.4f,1.0f,   1, 1, 1,  0.4f,0.4f,1.0f,   1, 1,-1,  0.4f,0.2f,1.0f,
        -1,-1,-1,  1.0f,1.0f,0.2f,   1,-1,-1,  1.0f,1.0f,0.4f,   1,-1, 1,  1.0f,1.0f,0.4f,  -1,-1, 1,  1.0f,1.0f,0.2f,
         1,-1,-1,  1.0f,0.2f,1.0f,   1, 1,-1,  1.0f,0.4f,1.0f,   1, 1, 1,  1.0f,0.4f,1.0f,   1,-1, 1,  1.0f,0.2f,1.0f,
        -1,-1,-1,  0.2f,1.0f,1.0f,  -1,-1, 1,  0.2f,1.0f,1.0f,  -1, 1, 1,  0.4f,1.0f,1.0f,  -1, 1,-1,  0.4f,1.0f,1.0f,
    };

    private static readonly ushort[] Indices =
    {
         0, 1, 2,  0, 2, 3,   4, 5, 6,  4, 6, 7,   8, 9,10,  8,10,11,
        12,13,14, 12,14,15,  16,17,18, 16,18,19,  20,21,22, 20,22,23,
    };

    public static void Main()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Vector2D<int>(1280, 720);
        opts.Title = "RTS Engine - Silk.NET Desktop";
        opts.API = new GraphicsAPI(ContextAPI.OpenGL, new APIVersion(3, 3));

        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Closing += OnClose;
        _window.Run();
    }

    private static unsafe void OnLoad()
    {
        _gl = _window.CreateOpenGL();

        // Input
        var input = _window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (_, btn) =>
            {
                if (btn == MouseButton.Left)
                {
                    _dragging = true;
                    _lastMouse = mouse.Position;
                }
            };
            mouse.MouseUp += (_, btn) =>
            {
                if (btn == MouseButton.Left) _dragging = false;
            };
            mouse.MouseMove += (_, pos) =>
            {
                if (!_dragging) return;
                var dx = pos.X - _lastMouse.X;
                var dy = pos.Y - _lastMouse.Y;
                _lastMouse = pos;
                _rotationY += dx * PixelsToRadians;
                _rotationX -= dy * PixelsToRadians;
                _velocityY = dx * PixelsToRadians;
                _velocityX = -dy * PixelsToRadians;
            };
        }

        // Compile shaders
        var vs = CompileShader(ShaderType.VertexShader, VertexShader);
        var fs = CompileShader(ShaderType.FragmentShader, FragmentShader);
        _shaderProgram = _gl.CreateProgram();
        _gl.AttachShader(_shaderProgram, vs);
        _gl.AttachShader(_shaderProgram, fs);
        _gl.LinkProgram(_shaderProgram);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        _gl.UseProgram(_shaderProgram);
        _mvpLocation = _gl.GetUniformLocation(_shaderProgram, "uMVP");

        // VAO + VBO + IBO
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* ptr = Vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Vertices.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);

        _ibo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ibo);
        fixed (ushort* ptr = Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Indices.Length * sizeof(ushort)), ptr, BufferUsageARB.StaticDraw);

        // Vertex layout: pos(3f) + color(3f), stride 24
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);

        _gl.Enable(EnableCap.DepthTest);
        _gl.ClearColor(0.05f, 0.05f, 0.12f, 1.0f);
    }

    private static unsafe void OnRender(double _)
    {
        var now = DateTime.UtcNow;
        var dt = MathF.Min((float)(now - _lastFrameTime).TotalSeconds, 0.1f);
        _lastFrameTime = now;

        if (!_dragging)
        {
            _rotationX += _velocityX * dt * 60f;
            _rotationY += _velocityY * dt * 60f;
            var damping = MathF.Pow(FreeDamping, dt);
            _velocityX *= damping;
            _velocityY *= damping;
            if (MathF.Abs(_velocityX) < 0.0001f) _velocityX = 0;
            if (MathF.Abs(_velocityY) < 0.0001f) _velocityY = 0;
        }

        var size = _window.Size;
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);

        var aspect = (float)size.X / size.Y;
        var mvp = BuildMvp(aspect);

        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(_shaderProgram);
        _gl.BindVertexArray(_vao);

        // Silk.NET row-major memory → GL column-major reinterpretation = correct
        fixed (float* ptr = mvp)
            _gl.UniformMatrix4(_mvpLocation, 1, false, ptr);

        _gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedShort, null);
    }

    private static void OnClose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ibo);
        _gl.DeleteProgram(_shaderProgram);
    }

    private static float[] BuildMvp(float aspectRatio)
    {
        var model = Matrix4X4.Multiply(
            Matrix4X4.CreateRotationX(_rotationX),
            Matrix4X4.CreateRotationY(_rotationY));

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(0, 0, 5),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));

        // Desktop OpenGL clips z to [-1, 1]. Silk.NET's built-in maps to [0, 1] (D3D).
        // Construct manually for OpenGL convention.
        var fov = Scalar.DegreesToRadians(45.0f);
        var proj = PerspectiveGL(fov, aspectRatio, 0.1f, 100f);

        var mvp = Matrix4X4.Multiply(Matrix4X4.Multiply(model, view), proj);

        return new[]
        {
            mvp.M11, mvp.M12, mvp.M13, mvp.M14,
            mvp.M21, mvp.M22, mvp.M23, mvp.M24,
            mvp.M31, mvp.M32, mvp.M33, mvp.M34,
            mvp.M41, mvp.M42, mvp.M43, mvp.M44,
        };
    }

    /// <summary>
    /// OpenGL perspective: z maps to [-1, 1].
    /// WebGPU/D3D use [0, 1] — Silk.NET's built-in targets that.
    /// Desktop GL needs this manual variant.
    /// </summary>
    private static Matrix4X4<float> PerspectiveGL(float fov, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fov * 0.5f);
        float nf = 1f / (near - far);
        return new Matrix4X4<float>(
            f / aspect, 0,  0,                     0,
            0,          f,  0,                     0,
            0,          0,  (far + near) * nf,     2f * far * near * nf,
            0,          0, -1,                     0);
    }

    private static uint CompileShader(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        var log = _gl.GetShaderInfoLog(shader);
        if (!string.IsNullOrEmpty(log))
            Console.Error.WriteLine($"Shader ({type}): {log}");
        return shader;
    }
}
