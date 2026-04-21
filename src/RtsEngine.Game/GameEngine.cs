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
    private const float MinDist = 2.0f;
    private const float MaxDist = 8.0f;
    private const float MinElev = -1.4f;
    private const float MaxElev = 1.4f;

    private bool _running;
    private bool _meshDirty;
    private DateTime _startTime = DateTime.UtcNow;

    private int _hoveredCell = -1;
    private float _lastMvpAspect;
    private float[]? _lastMvp;

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
        _app.PointerMove += OnMove;
    }

    private void OnClick(float cx, float cy, int button)
    {
        if (_planet == null) return;
        var hit = RayPick(cx, cy);
        if (hit == null) return;

        if (button == 0)
            _planet.Mesh.ChangeLevel(hit.Value, +1);
        else if (button == 2)
            _planet.Mesh.ChangeLevel(hit.Value, -1);
        _meshDirty = true;
    }

    private void OnMove(float cx, float cy)
    {
        if (_planet == null) return;
        _hoveredCell = RayPick(cx, cy) ?? -1;
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
            _planet.SetHighlightCell(_hoveredCell);
            await _planet.SyncOutline();
        }

        _lastMvp = BuildMvp(_app.AspectRatio);
        _renderer.Draw(_lastMvp);
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

    // ── Ray picking — uses actual MVP matrix for exact match ────────

    private int? RayPick(float canvasX, float canvasY)
    {
        if (_planet == null) return null;
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return null;

        float ndcX = 2f * canvasX / w - 1f;
        float ndcY = 1f - 2f * canvasY / h;

        // Build the same MVP used for rendering, convert to System.Numerics for inversion
        var m = BuildMvp(w / h);
        var mvp = new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);

        if (!Matrix4x4.Invert(mvp, out var inv))
            return null;

        var nearPt = Unproj(inv, ndcX, ndcY, 0f);
        var farPt  = Unproj(inv, ndcX, ndcY, 1f);
        var rayDir = Vector3.Normalize(farPt - nearPt);

        float r = _planet.Mesh.Radius + PlanetMesh.MaxLevel * _planet.Mesh.StepHeight + 0.01f;
        float? t = RaySphere(nearPt, rayDir, r);
        if (t == null) return null;

        return _planet.Mesh.DirectionToCell(nearPt + rayDir * t.Value);
    }

    private static Vector3 Unproj(Matrix4x4 inv, float nx, float ny, float nz)
    {
        var c = Vector4.Transform(new Vector4(nx, ny, nz, 1f), inv);
        if (MathF.Abs(c.W) < 1e-10f) return default;
        return new Vector3(c.X / c.W, c.Y / c.W, c.Z / c.W);
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
