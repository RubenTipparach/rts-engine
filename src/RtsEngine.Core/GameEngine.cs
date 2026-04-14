using Silk.NET.Maths;

namespace RtsEngine.Core;

/// <summary>
/// Platform-agnostic game engine. Shared by WASM and Desktop.
///
/// Depends only on:
///   - IRenderBackend (app shell — input, loop)
///   - IRenderer (draw calls)
///   - Silk.NET.Maths (matrix math)
/// </summary>
public class GameEngine
{
    private readonly IRenderBackend _app;
    private readonly IRenderer _renderer;

    private float _rotationX;
    private float _rotationY;
    private float _velocityX;
    private float _velocityY;
    private bool _dragging;

    private const float FreeDamping = 0.04f;
    private const float PixelsToRadians = 0.005f;

    private DateTime _lastFrameTime = DateTime.UtcNow;
    private bool _running;

    public float VelocityX => _velocityX;
    public float VelocityY => _velocityY;
    public float SpeedMagnitude => MathF.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

    public event Action? OnFrameRendered;

    public GameEngine(IRenderBackend app, IRenderer renderer)
    {
        _app = app;
        _renderer = renderer;

        _app.PointerDown += () => _dragging = true;

        _app.PointerDrag += (dx, dy) =>
        {
            _rotationY += dx * PixelsToRadians;
            _rotationX -= dy * PixelsToRadians;
            _velocityY = dx * PixelsToRadians;
            _velocityX = -dy * PixelsToRadians;
        };

        _app.PointerUp += () => _dragging = false;
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

        _renderer.Draw(BuildMvp(_app.AspectRatio));
        OnFrameRendered?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Build MVP matrix. Uses Silk.NET's built-in CreatePerspectiveFieldOfView
    /// which maps z to [0,1] — correct for WebGPU and D3D.
    ///
    /// For desktop OpenGL (z:[-1,1]), the platform renderer can override
    /// the projection or use glClipControl. The MVP math itself is shared.
    ///
    /// Raw row-major floats passed to GPU — WGSL/GL column-major
    /// reinterpretation gives automatic transpose = correct.
    /// </summary>
    public float[] BuildMvp(float aspectRatio)
    {
        var model = Matrix4X4.Multiply(
            Matrix4X4.CreateRotationX(_rotationX),
            Matrix4X4.CreateRotationY(_rotationY));

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(0, 0, 5),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));

        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f),
            aspectRatio,
            0.1f,
            100.0f);

        var mvp = Matrix4X4.Multiply(Matrix4X4.Multiply(model, view), proj);
        return MatrixHelper.ToRawFloats(mvp);
    }
}
