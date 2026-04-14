using Silk.NET.Maths;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Platform-agnostic game engine.
///
/// Rendering: calls GL.* directly (like sokol_gfx — C# OpenGL API).
/// Windowing: uses IRenderBackend for canvas/loop/input (like sokol_app).
/// Math:      Silk.NET.Maths for all matrix/vector ops.
///
/// Zero JS. Zero platform-specific code.
/// </summary>
public class GameEngine
{
    private readonly IRenderBackend _app;
    private readonly CubeRenderer _cube;

    private float _rotationX;
    private float _rotationY;
    private float _velocityX = 0.5f;
    private float _velocityY = 0.8f;

    private const float Damping = 0.995f;
    private const float DragSensitivity = 0.01f;
    private const float ScrollSensitivity = 0.002f;
    private const float TapBoost = 2.0f;
    private const float MaxVelocity = 20.0f;

    private DateTime _lastFrameTime = DateTime.UtcNow;
    private bool _running;

    public float VelocityX => _velocityX;
    public float VelocityY => _velocityY;
    public float SpeedMagnitude => MathF.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

    public event Action? OnFrameRendered;

    public GameEngine(IRenderBackend app, CubeRenderer cube)
    {
        _app = app;
        _cube = cube;

        _app.PointerDrag += (dx, dy) => { _velocityY += dx * DragSensitivity; _velocityX += dy * DragSensitivity; Clamp(); };
        _app.ScrollWheel += d => { var f = 1f + d * ScrollSensitivity; _velocityX *= f; _velocityY *= f; Clamp(); };
        _app.TapStart += () => { if (SpeedMagnitude < 0.1f) { _velocityX = TapBoost; _velocityY = TapBoost; } else { var s = TapBoost / SpeedMagnitude; _velocityX += _velocityX * s; _velocityY += _velocityY * s; } Clamp(); };
        _app.ResetRequested += () => { _rotationX = 0; _rotationY = 0; _velocityX = 0.5f; _velocityY = 0.8f; _lastFrameTime = DateTime.UtcNow; };
    }

    public void Run()
    {
        if (_running) return;
        _running = true;
        _lastFrameTime = DateTime.UtcNow;
        _app.StartLoop(Tick);
    }

    private Task Tick()
    {
        // Update physics
        var now = DateTime.UtcNow;
        var dt = MathF.Min((float)(now - _lastFrameTime).TotalSeconds, 0.1f);
        _lastFrameTime = now;

        _rotationX += _velocityX * dt;
        _rotationY += _velocityY * dt;
        _velocityX *= Damping;
        _velocityY *= Damping;
        if (MathF.Abs(_velocityX) < 0.001f) _velocityX = 0;
        if (MathF.Abs(_velocityY) < 0.001f) _velocityY = 0;

        // Build MVP and draw — all through GL.* (no platform code)
        var mvp = BuildMvp(_app.AspectRatio);
        _cube.Draw(mvp);

        OnFrameRendered?.Invoke();
        return Task.CompletedTask;
    }

    // ── MVP ───────────────────────────────────────────────────────
    //
    // Silk.NET.Maths: row-major, row-vector convention.
    // Composition: Model * View * Projection (left to right).
    // Transpose to column-major for GL uniform upload.
    //
    // Projection: OpenGL z:[-1,1], NOT System.Numerics z:[0,1].

    private float[] BuildMvp(float aspectRatio)
    {
        var model = Matrix4X4.Multiply(
            Matrix4X4.CreateRotationX(_rotationX),
            Matrix4X4.CreateRotationY(_rotationY));

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(0, 0, 5),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));

        var fov = Scalar.DegreesToRadians(45.0f);
        var proj = PerspectiveGL(fov, aspectRatio, 0.1f, 100f);

        var mvp = Matrix4X4.Multiply(Matrix4X4.Multiply(model, view), proj);
        return ToColumnMajor(mvp);
    }

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

    private static float[] ToColumnMajor(Matrix4X4<float> m) => new[]
    {
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44,
    };

    private void Clamp()
    {
        _velocityX = Math.Clamp(_velocityX, -MaxVelocity, MaxVelocity);
        _velocityY = Math.Clamp(_velocityY, -MaxVelocity, MaxVelocity);
    }
}
