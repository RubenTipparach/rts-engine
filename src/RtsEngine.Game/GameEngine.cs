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
    private DateTime _startTime = DateTime.UtcNow;

    private int _hoveredCell = -1;

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
        var hit = PickCell(cx, cy);
        if (hit == null) return;

        if (button == 0)
            _planet.Mesh.ChangeLevel(hit.Value, +1);
        else if (button == 2)
            _planet.Mesh.ChangeLevel(hit.Value, -1);
        _planet.MarkDirty(hit.Value);
    }

    private void OnMove(float cx, float cy)
    {
        if (_planet == null) return;
        _hoveredCell = PickCell(cx, cy) ?? -1;
    }

    public void Run()
    {
        if (_running) return;
        _running = true;
        _app.StartLoop(Tick);
    }

    private async Task Tick()
    {
        if (_planet != null)
        {
            await _planet.RebuildDirtyPatches();
        }

        if (_planet != null)
        {
            var cam = CameraPosition();
            _planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
            _planet.SetTime((float)(DateTime.UtcNow - _startTime).TotalSeconds);
            _planet.SetHighlightCell(_hoveredCell);
            await _planet.SyncOutline();
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

    // ── Picking — project every cell center to screen, find closest ──
    // Exact: uses the same MVP as rendering, no geometric approximation.

    private int? PickCell(float canvasX, float canvasY)
    {
        if (_planet == null) return null;
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return null;

        var m = BuildMvp(w / h);
        var mvp = new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);

        var mesh = _planet.Mesh;
        float bestDist = float.MaxValue;
        int bestCell = -1;

        for (int i = 0; i < mesh.CellCount; i++)
        {
            float cellH = mesh.Radius + mesh.GetLevel(i) * mesh.StepHeight;
            var center = mesh.GetCellCenter(i) * cellH;

            var clip = Vector4.Transform(new Vector4(center, 1f), mvp);
            if (clip.W <= 0.001f) continue;

            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;

            float dx = sx - canvasX;
            float dy = sy - canvasY;
            float dist = dx * dx + dy * dy;

            if (dist < bestDist) { bestDist = dist; bestCell = i; }
        }

        if (bestDist > 40f * 40f) return null;
        return bestCell;
    }
}
