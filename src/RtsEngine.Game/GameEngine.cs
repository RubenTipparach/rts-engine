using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

public class GameEngine
{
    private readonly IRenderBackend _app;
    private readonly IRenderer _renderer;
    private readonly PlanetRenderer? _planet;

    private float _azimuth;
    private float _elevation = 0.4f;
    private float _distance = 3.0f;
    private bool _dragging;

    private const float PixelsToRadians = 0.005f;
    private const float MinDist = 1.5f;
    private const float MaxDist = 8.0f;
    private const float MinElev = -1.4f;
    private const float MaxElev = 1.4f;

    private bool _running;
    private bool _meshDirty;
    private DateTime _startTime = DateTime.UtcNow;

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
            _azimuth += dx * PixelsToRadians;
            _elevation += dy * PixelsToRadians;
            _elevation = Math.Clamp(_elevation, MinElev, MaxElev);
        };

        _app.Scroll += delta =>
        {
            _distance -= delta * 0.002f;
            _distance = Math.Clamp(_distance, MinDist, MaxDist);
        };

        _app.PointerClick += OnClick;
    }

    private void OnClick(float cx, float cy, int button)
    {
        if (_planet == null) return;
        var hit = RayPick(cx, cy);
        if (hit == null) return;

        int cell = hit.Value;
        if (button == 0)
            _planet.Mesh.ChangeLevel(cell, +1); // raise, clamped
        else if (button == 2)
            _planet.Mesh.ChangeLevel(cell, -1); // lower, clamped
        _meshDirty = true;
    }

    public void Run()
    {
        if (_running) return;
        _running = true;
        _app.StartLoop(Tick);
    }

    private async Task Tick()
    {
        if (_meshDirty && _planet != null)
        {
            _meshDirty = false;
            await _planet.RebuildMesh();
        }

        if (_planet != null)
        {
            var cam = CameraPosition();
            _planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
            _planet.SetTime((float)(DateTime.UtcNow - _startTime).TotalSeconds);
        }

        _renderer.Draw(BuildMvp(_app.AspectRatio));
        OnFrameRendered?.Invoke();
    }

    // ── Camera ──────────────────────────────────────────────────────

    private Vector3 CameraPosition()
    {
        float cx = _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _distance * MathF.Sin(_elevation);
        float cz = _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);
        return new Vector3(cx, cy, cz);
    }

    public float[] BuildMvp(float aspectRatio)
    {
        var pos = CameraPosition();
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(pos.X, pos.Y, pos.Z),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f), aspectRatio, 0.1f, 100.0f);
        var mvp = Matrix4X4.Multiply(view, proj);
        return MatrixHelper.ToRawFloats(mvp);
    }

    // ── Ray picking (geometric — no matrix inversion needed) ────────

    private int? RayPick(float canvasX, float canvasY)
    {
        if (_planet == null) return null;
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return null;

        float ndcX = 2f * canvasX / w - 1f;
        float ndcY = 1f - 2f * canvasY / h;

        var camPos = CameraPosition();
        Vector3 fwd = Vector3.Normalize(-camPos);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, fwd);

        float fovRad = 45f * MathF.PI / 180f;
        float halfH = MathF.Tan(fovRad * 0.5f);
        float halfW = halfH * (w / h);

        Vector3 rayDir = Vector3.Normalize(fwd + right * (ndcX * halfW) + up * (ndcY * halfH));

        float r = _planet.Mesh.Radius + PlanetMesh.MaxLevel * _planet.Mesh.StepHeight + 0.01f;
        float? t = RaySphere(camPos, rayDir, r);
        if (t == null) return null;

        return _planet.Mesh.DirectionToCell(camPos + rayDir * t.Value);
    }

    private static float? RaySphere(Vector3 origin, Vector3 dir, float radius)
    {
        float b = 2f * Vector3.Dot(origin, dir);
        float c = Vector3.Dot(origin, origin) - radius * radius;
        float disc = b * b - 4f * c;
        if (disc < 0) return null;
        float sq = MathF.Sqrt(disc);
        float t0 = (-b - sq) * 0.5f;
        float t1 = (-b + sq) * 0.5f;
        if (t0 > 0) return t0;
        if (t1 > 0) return t1;
        return null;
    }
}
