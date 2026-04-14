using Silk.NET.Maths;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Platform-agnostic game engine.
///
/// Rendering: calls GPU.* (WebGPU proxy — like sokol_gfx).
/// Windowing: IRenderBackend for canvas/loop/input (like sokol_app).
/// Math:      Silk.NET.Maths for all matrix/vector ops.
///
/// Zero JS. Zero platform-specific code.
///
/// MVP convention:
///   Silk.NET uses System.Numerics convention:
///     - Row-major storage, row-vector multiplication (v * M)
///     - CreatePerspectiveFieldOfView maps z to [0, 1] (matches WebGPU)
///     - CreateLookAt is right-handed
///     - Composition: MVP = Model * View * Projection (left to right)
///
///   Raw bytes passed to GPU uniform buffer (no transposing):
///     - WGSL mat4x4f interprets row-major data as column-major
///     - This automatically transposes: shader sees M^T
///     - Shader does M^T * v_col = (v_row * M)^T = correct result
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

        // Build MVP and draw
        var mvp = BuildMvp(_app.AspectRatio);
        _cube.Draw(mvp);

        OnFrameRendered?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Build Model-View-Projection matrix.
    ///
    /// Silk.NET conventions (mirrors System.Numerics):
    ///   - Row-major, row-vector: v_transformed = v * M
    ///   - Compose left to right: MVP = Model * View * Proj
    ///   - CreatePerspectiveFieldOfView: z maps to [0, 1] (WebGPU native)
    ///   - CreateLookAt: right-handed (camera looks down -Z)
    ///
    /// No custom projection matrix needed — Silk.NET's z:[0,1] matches WebGPU.
    /// No transposing needed — raw row-major bytes reinterpreted as column-major
    /// by WGSL gives the correct M^T for column-vector shader convention.
    /// </summary>
    private float[] BuildMvp(float aspectRatio)
    {
        var model = Matrix4X4.Multiply(
            Matrix4X4.CreateRotationX(_rotationX),
            Matrix4X4.CreateRotationY(_rotationY));

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(0, 0, 5),   // eye
            new Vector3D<float>(0, 0, 0),   // target
            new Vector3D<float>(0, 1, 0));  // up

        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f),
            aspectRatio,
            0.1f,
            100.0f);

        var mvp = Matrix4X4.Multiply(Matrix4X4.Multiply(model, view), proj);

        // Pass raw row-major floats — no transpose!
        return ToRawFloats(mvp);
    }

    /// <summary>
    /// Extract raw row-major floats from Matrix4X4.
    /// Memory layout: M11, M12, M13, M14, M21, M22, ...
    /// WGSL reinterprets as column-major → automatic transpose → correct.
    /// </summary>
    private static float[] ToRawFloats(Matrix4X4<float> m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };

    private void Clamp()
    {
        _velocityX = Math.Clamp(_velocityX, -MaxVelocity, MaxVelocity);
        _velocityY = Math.Clamp(_velocityY, -MaxVelocity, MaxVelocity);
    }
}
