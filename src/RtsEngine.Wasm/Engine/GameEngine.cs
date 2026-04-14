using Silk.NET.Maths;

namespace RtsEngine.Wasm.Engine;

public class GameEngine
{
    private readonly IRenderBackend _app;
    private readonly CubeRenderer _cube;

    private float _rotationX;
    private float _rotationY;
    private float _velocityX;
    private float _velocityY;
    private bool _dragging;

    // Damping per second — velocity multiplied by this^dt each frame
    private const float FreeDamping = 0.04f;  // decays ~96% per second when free
    private const float DragDamping = 0.001f;  // near-instant stop when pointer is down
    private const float PixelsToRadians = 0.005f;

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

        _app.PointerDown += () => _dragging = true;

        _app.PointerDrag += (dx, dy) =>
        {
            // Rotate cube directly with pointer motion
            _rotationY += dx * PixelsToRadians;
            _rotationX += dy * PixelsToRadians;
            // Track velocity so release inherits momentum
            _velocityY = dx * PixelsToRadians;
            _velocityX = dy * PixelsToRadians;
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
            // Coast with inherited velocity, damping to a stop
            _rotationX += _velocityX * dt * 60f; // scale to ~60fps feel
            _rotationY += _velocityY * dt * 60f;

            var damping = MathF.Pow(_dragging ? DragDamping : FreeDamping, dt);
            _velocityX *= damping;
            _velocityY *= damping;

            if (MathF.Abs(_velocityX) < 0.0001f) _velocityX = 0;
            if (MathF.Abs(_velocityY) < 0.0001f) _velocityY = 0;
        }

        _cube.Draw(BuildMvp(_app.AspectRatio));
        OnFrameRendered?.Invoke();
        return Task.CompletedTask;
    }

    private float[] BuildMvp(float aspectRatio)
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
        return ToRawFloats(mvp);
    }

    private static float[] ToRawFloats(Matrix4X4<float> m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };
}
