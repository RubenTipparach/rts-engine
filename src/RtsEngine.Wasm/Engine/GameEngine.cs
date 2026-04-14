using Silk.NET.Maths;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Core game engine managing the spinning cube state.
/// Uses Silk.NET.Maths for all matrix/vector operations.
/// </summary>
public class GameEngine
{
    // Rotation angles (radians)
    private float _rotationX;
    private float _rotationY;

    // Angular velocity (radians per second)
    private float _velocityX = 0.5f;
    private float _velocityY = 0.8f;

    // Damping factor (slight friction so cube slows down naturally)
    private const float Damping = 0.995f;
    private const float DragSensitivity = 0.01f;
    private const float ScrollSensitivity = 0.002f;
    private const float TapBoost = 2.0f;
    private const float MaxVelocity = 20.0f;

    private DateTime _lastFrameTime = DateTime.UtcNow;

    public float VelocityX => _velocityX;
    public float VelocityY => _velocityY;
    public float RotationX => _rotationX;
    public float RotationY => _rotationY;

    public float SpeedMagnitude => MathF.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

    public void Update()
    {
        var now = DateTime.UtcNow;
        var dt = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        // Clamp delta to avoid huge jumps on tab switch
        dt = Math.Min(dt, 0.1f);

        // Apply velocity
        _rotationX += _velocityX * dt;
        _rotationY += _velocityY * dt;

        // Apply damping
        _velocityX *= Damping;
        _velocityY *= Damping;

        // Stop tiny velocities
        if (MathF.Abs(_velocityX) < 0.001f) _velocityX = 0;
        if (MathF.Abs(_velocityY) < 0.001f) _velocityY = 0;
    }

    public float[] GetMvpMatrix(float aspectRatio)
    {
        // Model: rotate around X and Y axes
        var rotX = Matrix4X4.CreateRotationX(_rotationX);
        var rotY = Matrix4X4.CreateRotationY(_rotationY);
        var model = Matrix4X4.Multiply(rotX, rotY);

        // View: camera looking at origin from z=5
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(0, 0, 5),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0)
        );

        // Projection: perspective
        var projection = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f),
            aspectRatio,
            0.1f,
            100.0f
        );

        // MVP = Model * View * Projection
        var mvp = Matrix4X4.Multiply(Matrix4X4.Multiply(model, view), projection);

        // Flatten to column-major float array for WebGL
        return ToColumnMajorArray(mvp);
    }

    public void ApplyDragVelocity(float dx, float dy)
    {
        _velocityY += dx * DragSensitivity;
        _velocityX += dy * DragSensitivity;
        ClampVelocity();
    }

    public void ApplyScrollBoost(float delta)
    {
        var factor = 1.0f + delta * ScrollSensitivity;
        _velocityX *= factor;
        _velocityY *= factor;
        ClampVelocity();
    }

    public void ApplyTapBoost()
    {
        // Add a burst of speed in the current direction, or start spinning if stopped
        if (SpeedMagnitude < 0.1f)
        {
            _velocityX = TapBoost;
            _velocityY = TapBoost;
        }
        else
        {
            var scale = TapBoost / SpeedMagnitude;
            _velocityX += _velocityX * scale;
            _velocityY += _velocityY * scale;
        }
        ClampVelocity();
    }

    public void Reset()
    {
        _rotationX = 0;
        _rotationY = 0;
        _velocityX = 0.5f;
        _velocityY = 0.8f;
        _lastFrameTime = DateTime.UtcNow;
    }

    private void ClampVelocity()
    {
        _velocityX = Math.Clamp(_velocityX, -MaxVelocity, MaxVelocity);
        _velocityY = Math.Clamp(_velocityY, -MaxVelocity, MaxVelocity);
    }

    private static float[] ToColumnMajorArray(Matrix4X4<float> m)
    {
        return new[]
        {
            m.M11, m.M21, m.M31, m.M41,
            m.M12, m.M22, m.M32, m.M42,
            m.M13, m.M23, m.M33, m.M43,
            m.M14, m.M24, m.M34, m.M44,
        };
    }
}
