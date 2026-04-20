using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Planet editor engine. Orbit camera around a cube-sphere planet.
/// Left-click to raise a cell, right-click to lower.
/// Drag to orbit, scroll to zoom.
/// </summary>
public class GameEngine
{
    private readonly IRenderBackend _app;
    private readonly IRenderer _renderer;
    private readonly PlanetRenderer? _planet;

    // Camera state
    private float _azimuth;         // horizontal angle (radians)
    private float _elevation = 0.4f; // vertical angle (radians), clamped to ±~85°
    private float _distance = 3.0f;  // distance from origin
    private bool _dragging;

    private const float PixelsToRadians = 0.005f;
    private const float MinDist = 1.5f;
    private const float MaxDist = 8.0f;
    private const float MinElev = -1.4f; // ~-80°
    private const float MaxElev = 1.4f;  // ~+80°

    private DateTime _lastFrameTime = DateTime.UtcNow;
    private bool _running;
    private bool _meshDirty;

    public event Action? OnFrameRendered;

    public GameEngine(IRenderBackend app, IRenderer renderer)
    {
        _app = app;
        _renderer = renderer;
        _planet = renderer as PlanetRenderer;

        _app.PointerDown += () => _dragging = true;
        _app.PointerUp += () => _dragging = false;

        _app.PointerDrag += (dx, dy) =>
        {
            if (!_dragging) return;
            _azimuth -= dx * PixelsToRadians;
            _elevation += dy * PixelsToRadians;
            _elevation = Math.Clamp(_elevation, MinElev, MaxElev);
        };

        _app.Scroll += delta =>
        {
            _distance -= delta * 0.002f;
            _distance = Math.Clamp(_distance, MinDist, MaxDist);
        };

        _app.PointerClick += (cx, cy, button) =>
        {
            if (_planet == null) return;
            var hit = RayPick(cx, cy);
            if (hit == null) return;

            var (face, row, col) = hit.Value;
            if (button == 0) // left click — raise
                _planet.Mesh.CycleLevel(face, row, col, 1);
            else if (button == 2) // right click — lower
                _planet.Mesh.CycleLevel(face, row, col, -1);
            _meshDirty = true;
        };
    }

    public void Run()
    {
        if (_running) return;
        _running = true;
        _lastFrameTime = DateTime.UtcNow;
        _app.StartLoop(Tick);
    }

    private async Task Tick()
    {
        if (_meshDirty && _planet != null)
        {
            _meshDirty = false;
            await _planet.RebuildMesh();
        }

        _renderer.Draw(BuildMvp(_app.AspectRatio));
        OnFrameRendered?.Invoke();
    }

    /// <summary>
    /// Ray-pick: screen pixel → planet cell.
    /// Casts a ray from the camera through the pixel into the scene and
    /// intersects with the planet sphere (approximate — uses base radius).
    /// </summary>
    private (CubeFace face, int row, int col)? RayPick(float canvasX, float canvasY)
    {
        if (_planet == null) return null;

        float w = _app.CanvasWidth;
        float h = _app.CanvasHeight;
        if (w < 1 || h < 1) return null;

        // NDC
        float ndcX = 2f * canvasX / w - 1f;
        float ndcY = 1f - 2f * canvasY / h;

        // Camera matrices
        float aspect = w / h;
        var view = BuildViewMatrix();
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f), aspect, 0.1f, 100.0f);
        var vp = Matrix4X4.Multiply(view, proj);

        if (!Matrix4X4.Invert(vp, out var invVP))
            return null;

        // Unproject near and far points
        var nearClip = Unproject(invVP, ndcX, ndcY, 0f);
        var farClip = Unproject(invVP, ndcX, ndcY, 1f);

        var rayOrigin = ToNumerics(nearClip);
        var rayDir = Vector3.Normalize(ToNumerics(farClip) - rayOrigin);

        // Intersect with sphere of radius slightly above max possible (to catch top of cliff 3)
        float r = _planet.Mesh.Radius + PlanetMesh.MaxLevel * _planet.Mesh.StepHeight + 0.01f;
        float? t = RaySphereIntersect(rayOrigin, rayDir, r);
        if (t == null) return null;

        Vector3 hitPoint = rayOrigin + rayDir * t.Value;
        return _planet.Mesh.DirectionToCell(hitPoint);
    }

    private static float? RaySphereIntersect(Vector3 origin, Vector3 dir, float radius)
    {
        float b = 2f * Vector3.Dot(origin, dir);
        float c = Vector3.Dot(origin, origin) - radius * radius;
        float disc = b * b - 4f * c;
        if (disc < 0) return null;
        float sqrtDisc = MathF.Sqrt(disc);
        float t0 = (-b - sqrtDisc) * 0.5f;
        float t1 = (-b + sqrtDisc) * 0.5f;
        if (t0 > 0) return t0;
        if (t1 > 0) return t1;
        return null;
    }

    private static Vector3D<float> Unproject(Matrix4X4<float> invVP, float ndcX, float ndcY, float ndcZ)
    {
        var clip = new Vector4D<float>(ndcX, ndcY, ndcZ, 1f);
        var world = Vector4D.Transform(clip, invVP);
        if (MathF.Abs(world.W) < 1e-10f) return default;
        return new Vector3D<float>(world.X / world.W, world.Y / world.W, world.Z / world.W);
    }

    private static Vector3 ToNumerics(Vector3D<float> v) => new(v.X, v.Y, v.Z);

    private Matrix4X4<float> BuildViewMatrix()
    {
        float cx = _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _distance * MathF.Sin(_elevation);
        float cz = _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);

        return Matrix4X4.CreateLookAt(
            new Vector3D<float>(cx, cy, cz),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));
    }

    public float[] BuildMvp(float aspectRatio)
    {
        var view = BuildViewMatrix();

        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f),
            aspectRatio,
            0.1f,
            100.0f);

        var mvp = Matrix4X4.Multiply(view, proj);
        return MatrixHelper.ToRawFloats(mvp);
    }
}
