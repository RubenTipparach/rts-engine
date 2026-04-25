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
    private readonly IGPU _gpu;
    private EngineUI? _ui;

    private float _azimuth, _elevation = 0.4f, _distance = 3.0f;
    private bool _dragging;
    private int _hoveredCell = -1;
    private DateTime _startTime = DateTime.UtcNow;

    private const float PixelsToRadians = 0.005f;

    // Camera transition
    private bool _transitioning;
    private float _transitionStart;
    private float _transitionDuration = 1.2f;
    private EditorMode _transitionTarget;

    private bool _running;
    public EditorMode Mode { get; set; } = EditorMode.SolarSystem;
    public string? SelectedPlanetConfig { get; private set; }
    public event Action? OnFrameRendered;

    public void SetPlanetRenderer(PlanetRenderer p) => _planet = p;

    public void SwitchToPlanetEdit()
    {
        // Start zoom-in transition
        _transitioning = true;
        _transitionStart = Elapsed();
        _transitionTarget = EditorMode.PlanetEdit;
        _hoveredCell = -1;
    }

    public void SwitchToSolarSystem()
    {
        Mode = EditorMode.SolarSystem;
        SelectedPlanetConfig = null;
        _transitioning = false;
    }

    public GameEngine(IRenderBackend app, IGPU gpu, PlanetRenderer planet,
        StarMapRenderer? starMap = null, SolarSystemRenderer? solarSystem = null)
    {
        _app = app;
        _gpu = gpu;
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
            if (!_dragging || _transitioning) return;
            if (Mode == EditorMode.PlanetEdit)
            {
                _azimuth += dx * PixelsToRadians;
                _elevation += dy * PixelsToRadians;
                _elevation = Math.Clamp(_elevation, -1.4f, 1.4f);
            }
            else if (Mode == EditorMode.StarMap)
                _starMap?.Orbit(dx, dy);
            else if (Mode == EditorMode.SolarSystem)
                _solarSystem?.Orbit(dx, dy);
        };

        _app.Scroll += delta =>
        {
            if (_transitioning) return;
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

    public async Task SetupUI(string uiShaderCode)
    {
        _ui = new EngineUI(_gpu);
        await _ui.Setup(uiShaderCode);

        var btn = _ui.AddButton("back_solar", "Solar System", 10, 60, 150, 40);
        btn.HasArrow = true;
        btn.BgColor = new Vector4(0.05f, 0.15f, 0.25f, 0.9f);
        btn.FgColor = new Vector4(0f, 1f, 1f, 1f);
        btn.Visible = false;
    }

    private void OnClick(float cx, float cy, int button)
    {
        if (_transitioning) return;

        // UI buttons consume clicks first
        if (_ui != null && button == 0)
        {
            var hit = _ui.HitTest(cx, cy);
            if (hit == "back_solar") { SwitchToSolarSystem(); return; }
        }

        if (Mode == EditorMode.PlanetEdit)
        {
            var cell = PickCell(cx, cy);
            if (cell == null) return;
            if (button == 0) _planet.Mesh.ChangeLevel(cell.Value, +1);
            else if (button == 2) _planet.Mesh.ChangeLevel(cell.Value, -1);
            _planet.MarkDirty(cell.Value);
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
                SelectedPlanetConfig = planet;
        }
    }

    private void OnMove(float cx, float cy)
    {
        if (Mode == EditorMode.PlanetEdit && !_transitioning)
            _hoveredCell = PickCell(cx, cy) ?? -1;
    }

    private void OnKey(string key)
    {
        if (_transitioning) return;
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

    private float Elapsed() => (float)(DateTime.UtcNow - _startTime).TotalSeconds;

    private async Task Tick()
    {
        float elapsed = Elapsed();

        // Handle zoom transition
        if (_transitioning)
        {
            float t = Math.Clamp((elapsed - _transitionStart) / _transitionDuration, 0f, 1f);
            float smooth = t * t * (3f - 2f * t); // smoothstep

            if (_transitionTarget == EditorMode.PlanetEdit)
            {
                // Lerp camera distance from current to planet-edit distance
                _distance = 3.0f; // target planet distance
                float scale = 1f - smooth;

                // Render planet with a pull-back during transition
                var cam = CameraPosition();
                float transitionDist = 3.0f + (80f - 3.0f) * (1f - smooth);
                var transPos = Vector3.Normalize(cam) * transitionDist;
                _planet.SetCameraPosition(transPos.X, transPos.Y, transPos.Z);
                _planet.SetTime(elapsed);
                _planet.Draw(BuildPlanetMvpAt(transPos, _app.AspectRatio));
            }

            if (t >= 1f)
            {
                _transitioning = false;
                Mode = _transitionTarget;
            }

            OnFrameRendered?.Invoke();
            return;
        }

        // Normal tick
        if (Mode == EditorMode.PlanetEdit)
        {
            await _planet.RebuildDirtyPatches();
            var cam = CameraPosition();
            _planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
            _planet.SetTime(elapsed);
            _planet.SetHighlightCell(_hoveredCell);
            await _planet.SyncOutline();
            _planet.Draw(BuildPlanetMvp(_app.AspectRatio));

            // UI buttons
            _ui?.SetButtonVisible("back_solar", true);
            _ui?.SetCanvasSize(_app.CanvasWidth, _app.CanvasHeight);
            if (_ui != null) await _ui.SyncBuffers();
            _ui?.Draw();
        }
        else if (Mode == EditorMode.SolarSystem && _solarSystem != null)
        {
            _ui?.SetButtonVisible("back_solar", false);
            var mvp = _solarSystem.BuildMvpFloats(_app.AspectRatio);
            _solarSystem.Draw(mvp);
        }
        else if (Mode == EditorMode.StarMap && _starMap != null)
        {
            _ui?.SetButtonVisible("back_solar", false);
            var mvp = _starMap.BuildMvpFloats(_app.AspectRatio);
            _starMap.Draw(mvp);
        }

        OnFrameRendered?.Invoke();
    }

    // ── Camera ───────────────────────────────────────────────────────

    private Vector3 CameraPosition()
    {
        float cx = _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _distance * MathF.Sin(_elevation);
        float cz = _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);
        return new Vector3(cx, cy, cz);
    }

    private float[] BuildPlanetMvp(float aspectRatio)
        => BuildPlanetMvpAt(CameraPosition(), aspectRatio);

    private float[] BuildPlanetMvpAt(Vector3 camPos, float aspectRatio)
    {
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(camPos.X, camPos.Y, camPos.Z),
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
