using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

public class GameEngine
{
    private readonly IRenderBackend _app;
    private PlanetRenderer _planet;
    private readonly StarMapRenderer? _starMap;
    private readonly SolarSystemRenderer? _solarSystem;

    // Planet edit camera
    private float _azimuth, _elevation = 0.4f, _distance = 3.0f;
    private bool _dragging;
    private int _hoveredCell = -1;
    private DateTime _startTime = DateTime.UtcNow;

    private const float PixelsToRadians = 0.005f;

    private bool _running;
    public EditorMode Mode { get; set; } = EditorMode.SolarSystem;
    public string? SelectedPlanetConfig { get; private set; }
    public event Action? OnFrameRendered;

    public void SetPlanetRenderer(PlanetRenderer p) => _planet = p;

    public void SwitchToPlanetEdit()
    {
        Mode = EditorMode.PlanetEdit;
        _hoveredCell = -1;
    }

    public void SwitchToSolarSystem()
    {
        Mode = EditorMode.SolarSystem;
        SelectedPlanetConfig = null;
    }

    public GameEngine(IRenderBackend app, PlanetRenderer planet,
        StarMapRenderer? starMap = null, SolarSystemRenderer? solarSystem = null)
    {
        _app = app;
        _planet = planet;
        _starMap = starMap;
        _solarSystem = solarSystem;

        _app.PointerDown += () =>
        {
            _dragging = true;
            if (Mode == EditorMode.StarMap) _starMap?.SetDragging(true);
            if (Mode == EditorMode.SolarSystem) _solarSystem?.SetDragging(true);
        };
        _app.PointerUp += () =>
        {
            _dragging = false;
            if (Mode == EditorMode.StarMap) _starMap?.SetDragging(false);
            if (Mode == EditorMode.SolarSystem) _solarSystem?.SetDragging(false);
        };

        _app.PointerDrag += (dx, dy) =>
        {
            if (!_dragging) return;
            if (Mode == EditorMode.PlanetEdit)
            {
                _azimuth += dx * PixelsToRadians;
                _elevation += dy * PixelsToRadians;
                _elevation = Math.Clamp(_elevation, -1.4f, 1.4f);
            }
            else if (Mode == EditorMode.StarMap)
            {
                _starMap?.Orbit(dx, dy);
            }
            else if (Mode == EditorMode.SolarSystem)
            {
                _solarSystem?.Orbit(dx, dy);
            }
        };

        _app.Scroll += delta =>
        {
            if (Mode == EditorMode.PlanetEdit)
            {
                _distance -= delta * 0.002f;
                _distance = Math.Clamp(_distance, 2.0f, 8.0f);
            }
            else if (Mode == EditorMode.StarMap)
                _starMap?.Zoom(delta);
            else if (Mode == EditorMode.SolarSystem)
                _solarSystem?.Zoom(delta);
        };

        _app.PointerClick += OnClick;
        _app.PointerMove += OnMove;
        _app.KeyDown += OnKey;
    }

    private void OnClick(float cx, float cy, int button)
    {
        if (Mode == EditorMode.PlanetEdit)
        {
            var hit = PickCell(cx, cy);
            if (hit == null) return;
            if (button == 0) _planet.Mesh.ChangeLevel(hit.Value, +1);
            else if (button == 2) _planet.Mesh.ChangeLevel(hit.Value, -1);
            _planet.MarkDirty(hit.Value);
        }
        else if (Mode == EditorMode.StarMap && _starMap != null && button == 0)
        {
            int child = _starMap.PickChild(cx, cy, _app.CanvasWidth, _app.CanvasHeight);
            if (child >= 0)
            {
                _starMap.DrillDown(child);
                _ = _starMap.RebuildMesh();
            }
        }
        else if (Mode == EditorMode.SolarSystem && _solarSystem != null && button == 0)
        {
            var planet = _solarSystem.PickPlanet(cx, cy, _app.CanvasWidth, _app.CanvasHeight);
            if (planet != null)
            {
                // Don't switch mode yet — stay in solar system while planet loads.
                // Home.razor detects SelectedPlanetConfig change, loads the planet,
                // then calls SwitchToPlanetEdit() when ready.
                SelectedPlanetConfig = planet;
            }
        }
    }

    private void OnMove(float cx, float cy)
    {
        if (Mode == EditorMode.PlanetEdit)
            _hoveredCell = PickCell(cx, cy) ?? -1;
    }

    private void OnKey(string key)
    {
        if (key == "Tab")
        {
            if (Mode == EditorMode.PlanetEdit) SwitchToSolarSystem();
            else if (Mode == EditorMode.SolarSystem) Mode = EditorMode.StarMap;
            else SwitchToSolarSystem();
        }
        else if (key == "Backspace")
        {
            if (Mode == EditorMode.PlanetEdit) SwitchToSolarSystem();
            else if (Mode == EditorMode.StarMap && _starMap != null)
            {
                _starMap.ZoomOut();
                _ = _starMap.RebuildMesh();
            }
        }
        else if (key == "Escape")
        {
            if (Mode == EditorMode.PlanetEdit) SwitchToSolarSystem();
        }
    }

    public void Run()
    {
        if (_running) return;
        _running = true;
        _app.StartLoop(Tick);
    }

    private async Task Tick()
    {
        float elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;

        if (Mode == EditorMode.PlanetEdit)
        {
            await _planet.RebuildDirtyPatches();
            var cam = CameraPosition();
            _planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
            _planet.SetTime(elapsed);
            _planet.SetHighlightCell(_hoveredCell);
            await _planet.SyncOutline();
            _planet.Draw(BuildPlanetMvp(_app.AspectRatio));
        }
        else if (Mode == EditorMode.SolarSystem && _solarSystem != null)
        {
            var mvp = _solarSystem.BuildMvpFloats(_app.AspectRatio);
            _solarSystem.Draw(mvp);
        }
        else if (Mode == EditorMode.StarMap && _starMap != null)
        {
            var mvp = _starMap.BuildMvpFloats(_app.AspectRatio);
            _starMap.Draw(mvp);
        }

        OnFrameRendered?.Invoke();
    }

    // ── Planet camera ───────────────────────────────────────────────

    private Vector3 CameraPosition()
    {
        float cx = _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _distance * MathF.Sin(_elevation);
        float cz = _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);
        return new Vector3(cx, cy, cz);
    }

    private float[] BuildPlanetMvp(float aspectRatio)
    {
        var pos = CameraPosition();
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(pos.X, pos.Y, pos.Z),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f), aspectRatio, 0.1f, 100.0f);
        return MatrixHelper.ToRawFloats(Matrix4X4.Multiply(view, proj));
    }

    public float[] BuildMvp(float aspectRatio) => BuildPlanetMvp(aspectRatio);

    // ── Picking ─────────────────────────────────────────────────────

    private int? PickCell(float canvasX, float canvasY)
    {
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return null;

        var m = BuildPlanetMvp(w / h);
        var mvp = new Matrix4x4(
            m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

        var mesh = _planet.Mesh;
        float bestDist = float.MaxValue;
        int bestCell = -1;
        var camDir = Vector3.Normalize(CameraPosition());

        for (int i = 0; i < mesh.CellCount; i++)
        {
            if (Vector3.Dot(mesh.GetCellCenter(i), camDir) < -0.05f) continue;

            float cellH = mesh.Radius + mesh.GetLevel(i) * mesh.StepHeight;
            var center = mesh.GetCellCenter(i) * cellH;
            var clip = Vector4.Transform(new Vector4(center, 1f), mvp);
            if (clip.W <= 0.001f) continue;

            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;
            float d = (sx - canvasX) * (sx - canvasX) + (sy - canvasY) * (sy - canvasY);
            if (d < bestDist) { bestDist = d; bestCell = i; }
        }

        return bestCell >= 0 ? bestCell : null;
    }
}
