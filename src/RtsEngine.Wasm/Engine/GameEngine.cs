using Silk.NET.Maths;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Platform-agnostic game engine.  Depends only on IRenderBackend and
/// Silk.NET.Maths — no JS, no browser APIs, no platform-specific code.
///
/// On desktop, wire this up with a Silk.NET.OpenGL backend.
/// On WASM, wire it up with WebGLRenderBackend.
/// The engine doesn't know or care which one it's talking to.
/// </summary>
public class GameEngine
{
    private readonly IRenderBackend _renderer;

    // Rotation angles (radians)
    private float _rotationX;
    private float _rotationY;

    // Angular velocity (radians per second)
    private float _velocityX = 0.5f;
    private float _velocityY = 0.8f;

    private const float Damping = 0.995f;
    private const float DragSensitivity = 0.01f;
    private const float ScrollSensitivity = 0.002f;
    private const float TapBoost = 2.0f;
    private const float MaxVelocity = 20.0f;

    private DateTime _lastFrameTime = DateTime.UtcNow;

    public float VelocityX => _velocityX;
    public float VelocityY => _velocityY;
    public float SpeedMagnitude => MathF.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

    /// <summary>Fired after each frame is rendered. Used by UI to refresh HUD.</summary>
    public event Action? OnFrameRendered;

    public GameEngine(IRenderBackend renderer)
    {
        _renderer = renderer;

        // Subscribe to platform-agnostic input events
        _renderer.PointerDrag += OnDrag;
        _renderer.ScrollWheel += OnScroll;
        _renderer.TapStart += OnTap;
        _renderer.ResetRequested += OnReset;
    }

    private bool _running;

    /// <summary>Start the render loop. Backend calls Tick() each frame.</summary>
    public void Run()
    {
        if (_running) return;
        _running = true;
        _lastFrameTime = DateTime.UtcNow;
        _renderer.StartLoop(Tick);
    }

    private Task Tick()
    {
        Update();
        var mvp = BuildMvp(_renderer.AspectRatio);
        _renderer.Render(mvp);
        OnFrameRendered?.Invoke();
        return Task.CompletedTask;
    }

    private void Update()
    {
        var now = DateTime.UtcNow;
        var dt = MathF.Min((float)(now - _lastFrameTime).TotalSeconds, 0.1f);
        _lastFrameTime = now;

        _rotationX += _velocityX * dt;
        _rotationY += _velocityY * dt;

        _velocityX *= Damping;
        _velocityY *= Damping;

        if (MathF.Abs(_velocityX) < 0.001f) _velocityX = 0;
        if (MathF.Abs(_velocityY) < 0.001f) _velocityY = 0;
    }

    // ── MVP construction ──────────────────────────────────────────────
    //
    // Silk.NET.Maths mirrors System.Numerics: row-major storage,
    // row-vector convention  (v_row * M).
    //
    // Composition order (row-major, applied left → right):
    //     combined = Model  *  View  *  Projection
    //
    // WebGL's uniformMatrix4fv(loc, false, data) expects column-major.
    // Transposing row-major → column-major automatically converts
    // the row-vector MVP into the column-vector form the GLSL shader
    // needs:   gl_Position = uMVP * vec4(pos, 1.0);
    //
    // Projection caveat:
    //   System.Numerics maps z to [0, 1]  (Direct3D / Vulkan).
    //   WebGL / OpenGL ES need z in [-1, 1].
    //   We build the projection matrix manually with OpenGL conventions.
    // ──────────────────────────────────────────────────────────────────

    private float[] BuildMvp(float aspectRatio)
    {
        var rotX = Matrix4X4.CreateRotationX(_rotationX);
        var rotY = Matrix4X4.CreateRotationY(_rotationY);
        var model = Matrix4X4.Multiply(rotX, rotY);

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(0, 0, 5),   // eye
            new Vector3D<float>(0, 0, 0),   // target
            new Vector3D<float>(0, 1, 0));  // up

        var fov = Scalar.DegreesToRadians(45.0f);
        var projection = PerspectiveOpenGL(fov, aspectRatio, 0.1f, 100.0f);

        var mvp = Matrix4X4.Multiply(Matrix4X4.Multiply(model, view), projection);
        return ToColumnMajor(mvp);
    }

    /// <summary>
    /// Right-handed perspective matrix with OpenGL clip-space z: [-1, 1].
    ///
    /// Contrast with System.Numerics / Silk.NET which produce z: [0, 1]:
    ///   M33 = far / (near - far)            →  -(far+near) / (far-near)
    ///   M34 = near*far / (near - far)       →  -2*far*near / (far-near)
    /// </summary>
    private static Matrix4X4<float> PerspectiveOpenGL(
        float fovRadians, float aspect, float near, float far)
    {
        float f = 1.0f / MathF.Tan(fovRadians * 0.5f);
        float nf = 1.0f / (near - far);

        return new Matrix4X4<float>(
            f / aspect, 0,  0,                       0,
            0,          f,  0,                       0,
            0,          0,  (far + near) * nf,       2.0f * far * near * nf,
            0,          0, -1,                       0
        );
    }

    private static float[] ToColumnMajor(Matrix4X4<float> m) => new[]
    {
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44,
    };

    // ── Input handlers (platform-agnostic) ───────────────────────────

    private void OnDrag(float dx, float dy)
    {
        _velocityY += dx * DragSensitivity;
        _velocityX += dy * DragSensitivity;
        Clamp();
    }

    private void OnScroll(float delta)
    {
        var factor = 1.0f + delta * ScrollSensitivity;
        _velocityX *= factor;
        _velocityY *= factor;
        Clamp();
    }

    private void OnTap()
    {
        if (SpeedMagnitude < 0.1f)
        {
            _velocityX = TapBoost;
            _velocityY = TapBoost;
        }
        else
        {
            var s = TapBoost / SpeedMagnitude;
            _velocityX += _velocityX * s;
            _velocityY += _velocityY * s;
        }
        Clamp();
    }

    private void OnReset()
    {
        _rotationX = 0;
        _rotationY = 0;
        _velocityX = 0.5f;
        _velocityY = 0.8f;
        _lastFrameTime = DateTime.UtcNow;
    }

    private void Clamp()
    {
        _velocityX = Math.Clamp(_velocityX, -MaxVelocity, MaxVelocity);
        _velocityY = Math.Clamp(_velocityY, -MaxVelocity, MaxVelocity);
    }
}
