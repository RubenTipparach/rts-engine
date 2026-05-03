using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Engine orchestrator — wires subsystems (camera, picker, hud, transitions),
/// owns the three editor modes, and drives the per-frame tick. No gameplay
/// logic lives here: input dispatches to the active mode, the transition
/// controller takes priority over normal mode rendering, and the HUD is
/// synced once per frame at the end. Game-side logic (RTS placement, unit
/// orders, terrain editing) lives in <see cref="PlanetEditMode"/>.
/// </summary>
public class GameEngine
{
    // Kernel deps
    private readonly IRenderBackend _app;
    private PlanetRenderer _planet;
    private readonly StarMapRenderer? _starMap;
    private readonly SolarSystemRenderer? _solarSystem;
    private readonly RtsRenderer? _rts;
    private readonly RtsConfig? _rtsConfig;
    private readonly RtsState _state = new();
    private readonly IGPU _gpu;
    private readonly EngineConfig _config;

    // Subsystems
    private readonly PlanetCamera _camera;
    private readonly PlanetPicker _picker;
    private readonly EngineHud _hud;
    private readonly ModeTransition _transition;

    // Modes
    private readonly PlanetEditMode _planetEditMode;
    private readonly SolarSystemMode? _solarSystemMode;
    private readonly StarMapMode? _starMapMode;

    // Engine-wide state
    private bool _running;
    private bool _dragging;
    private DateTime _startTime = DateTime.UtcNow;
    /// <summary>If the player taps another planet from the planet edit
    /// backdrop, we queue it here, kick off a zoom-out, and chain a zoom-in
    /// to the new planet once the zoom-out completes.</summary>
    private string? _pendingPlanetSwitch;
    private long _tickCount;
    private float _lastLoggedPitch = 90f;

    public EditorMode Mode { get; set; } = EditorMode.SolarSystem;
    public string? SelectedPlanetConfig { get; private set; }
    public event Action? OnFrameRendered;

    public GameEngine(IRenderBackend app, IGPU gpu, PlanetRenderer planet,
        StarMapRenderer? starMap = null, SolarSystemRenderer? solarSystem = null,
        EngineConfig? config = null,
        RtsRenderer? rts = null, RtsConfig? rtsConfig = null)
    {
        _app = app;
        _gpu = gpu;
        _planet = planet;
        _starMap = starMap;
        _solarSystem = solarSystem;
        _rts = rts;
        _rtsConfig = rtsConfig;
        _config = config ?? new EngineConfig();

        _camera = new PlanetCamera(_config, () => _planet.Mesh.Radius);
        _picker = new PlanetPicker(_app, _camera, () => _planet.Mesh, () => _state.Units);
        _transition = new ModeTransition(_camera, _app, _solarSystem, _config);
        _hud = new EngineHud(_app, _camera, _state, _rtsConfig,
            () => _planet.Mesh.Radius, () => Mode, () => _transition.IsActive);

        _planetEditMode = new PlanetEditMode(_app, _camera, _picker, _hud,
            _state, _rtsConfig, _rts, _solarSystem,
            () => _planet, () => SelectedPlanetConfig,
            () => Mode == EditorMode.PlanetEdit && !_transition.IsActive);
        if (_solarSystem != null) _solarSystemMode = new SolarSystemMode(_app, _solarSystem);
        if (_starMap != null) _starMapMode = new StarMapMode(_app, _starMap);

        WireHud();
        WireModes();
        WireBackend();
    }

    private void WireHud()
    {
        // Cross-cutting click — back button always means "leave PlanetEdit".
        // Per-mode HUD events (build/produce/cancel/context/edit) are handled
        // inside the mode that uses them.
        _hud.BackSolarClicked += SwitchToSolarSystem;
    }

    private void WireModes()
    {
        if (_solarSystemMode != null)
        {
            _solarSystemMode.PlanetPicked += (cfg, pos) =>
            {
                SelectedPlanetConfig = cfg;
                _transition.BeginZoomIn(pos, Elapsed());
            };
            _solarSystemMode.TabPressed += () => Mode = EditorMode.StarMap;
        }
        if (_starMapMode != null)
        {
            _starMapMode.ExitRequested += SwitchToSolarSystem;
        }
        _planetEditMode.ExitRequested += SwitchToSolarSystem;
        _planetEditMode.PlanetSwitchRequested += cfg =>
        {
            _pendingPlanetSwitch = cfg;
            SwitchToSolarSystem();
        };
    }

    private void WireBackend()
    {
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
            if (_transition.IsActive) return;
            // While the player is pulling a box-select rectangle, plain-left
            // drag should not orbit the planet camera. Desktop fires both
            // BoxSelectUpdate and PointerDrag for the same gesture so other
            // modes (solar system / starmap, where there's nothing to box-
            // select) keep their existing left-drag-orbits behaviour.
            if (_planetEditMode.BoxSelectActive) return;
            if (!_dragging) return;
            DispatchOrbit(dx, dy);
        };

        // Explicit orbit gesture: middle drag or Alt+left drag on desktop.
        // Routes orbit in every mode — middle/alt-left should rotate the
        // solar system / star map view too. The desktop backend doesn't
        // fire PointerDrag for these gestures, so there's no double-orbit
        // worry; in PlanetEdit, plain PointerDrag is suppressed by
        // BoxSelectActive during box select.
        _app.OrbitDrag += (dx, dy) =>
        {
            if (_transition.IsActive) return;
            DispatchOrbit(dx, dy);
        };

        _app.Scroll += delta =>
        {
            if (_transition.IsActive) return;
            if (Mode == EditorMode.PlanetEdit) _camera.Scroll(delta);
            else if (Mode == EditorMode.StarMap) _starMap?.Zoom(delta);
            else if (Mode == EditorMode.SolarSystem) _solarSystem?.Zoom(delta);
        };

        _app.PointerClick += (cx, cy, button) =>
        {
            if (_transition.IsActive) return;
            // Any click outside the context menu dismisses it. Clicks ON
            // the menu hit UIButtonClick → ContextMenuPicked which the
            // gameplay mode handles itself.
            _hud.HideContextMenu();
            CurrentMode()?.OnClick(cx, cy, button);
        };
        _app.PointerMove += (cx, cy) =>
        {
            if (_transition.IsActive) return;
            CurrentMode()?.OnMove(cx, cy);
        };
        _app.KeyDown += key =>
        {
            if (_transition.IsActive) return;
            CurrentMode()?.OnKey(key);
        };
    }

    private void DispatchOrbit(float dx, float dy)
    {
        if (Mode == EditorMode.PlanetEdit) _camera.Orbit(dx, dy);
        else if (Mode == EditorMode.StarMap) _starMap?.Orbit(dx, dy);
        else if (Mode == EditorMode.SolarSystem) _solarSystem?.Orbit(dx, dy);
    }

    private IEditorMode? CurrentMode() => Mode switch
    {
        EditorMode.PlanetEdit => _planetEditMode,
        EditorMode.SolarSystem => _solarSystemMode,
        EditorMode.StarMap => _starMapMode,
        _ => null,
    };

    public void SetPlanetRenderer(PlanetRenderer p)
    {
        _planet = p;
        // Cell indices and selections are planet-specific — wipe gameplay state.
        _planetEditMode.OnPlanetSwapped();
    }

    /// <summary>Called by Home.razor after the planet renderer is ready.</summary>
    public void SwitchToPlanetEdit()
    {
        _transition.PlanetReady = true;
        if (!_transition.IsActive)
        {
            Mode = EditorMode.PlanetEdit;
            _planetEditMode.ResetHover();
        }
    }

    public void SwitchToSolarSystem()
    {
        if (_solarSystem == null) { Mode = EditorMode.SolarSystem; return; }

        // Snapshot the planet's current orbital position so the static mesh
        // starts the zoom-out aligned with the live dynamic mesh. The
        // transition tick keeps it updated each frame.
        _solarSystem.SetTime(Elapsed());
        var planetPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);

        _transition.BeginZoomOut(planetPos, Elapsed());
        // Keep SelectedPlanetConfig set during the transition so we can keep
        // hiding the dynamic mesh and tracking its orbital position. Cleared
        // when the transition finishes.
        Mode = EditorMode.PlanetEdit; // stays in edit mode visually until transition completes
    }

    public void SetupUI() => _hud.Setup();
    public float[] BuildMvp(float aspectRatio) => _camera.BuildMvp(aspectRatio);

    public void Run()
    {
        if (_running) return;
        _running = true;
        _app.StartLoop(Tick);
    }

    private float Elapsed() => (float)(DateTime.UtcNow - _startTime).TotalSeconds;

    private async Task Tick()
    {
        try { await TickInner(); }
        catch (Exception e)
        {
            // Silk.NET swallows exceptions thrown from async render handlers
            // (the Task is fire-and-forget). Surface them here or we'd see a
            // frozen window with no diagnostic.
            Console.Error.WriteLine($"[tick] EXCEPTION: {e.GetType().Name}: {e.Message}");
            Console.Error.WriteLine(e.StackTrace);
        }
    }

    private async Task TickInner()
    {
        if (_tickCount < 3 || _tickCount == 60 || _tickCount == 300)
            Console.Error.WriteLine($"[tick] {_tickCount} mode={Mode} canvas={_app.CanvasWidth}x{_app.CanvasHeight} dist={_camera.Distance:F1} planetReady={_transition.PlanetReady}");
        // Camera pitch + basis — only meaningful in PlanetEdit. Logged every
        // 6 ticks (~10 Hz at 60fps), plus on any pitch sign-flip so we don't
        // miss a transient inversion between samples. Includes the tilt
        // smoothstep value (drives lookAt) and the dot of the camera up
        // vector with worldY (catches roll flips that would visually look
        // like pitch swings even when the view direction is smooth).
        if (Mode == EditorMode.PlanetEdit && !_transition.IsActive)
        {
            float pitch = _camera.PitchDegrees();
            bool flipped = MathF.Sign(pitch) != MathF.Sign(_lastLoggedPitch) && _tickCount > 0;
            if (_tickCount % 6 == 0 || flipped)
            {
                var (upDotY, upDotR) = _camera.UpDots();
                var up = _camera.Up();
                Console.Error.WriteLine(
                    $"[camera] pitch={pitch:F2}° dist={_camera.Distance:F2} " +
                    $"zoom={(int)MathF.Round(_camera.ZoomPercent()*100)}% " +
                    $"tilt={(_camera.TiltBlend()*100):F1}% " +
                    $"up=({up.X:+0.00;-0.00},{up.Y:+0.00;-0.00},{up.Z:+0.00;-0.00}) " +
                    $"up·Y={upDotY:F2} up·radial={upDotR:F2}");
                _lastLoggedPitch = pitch;
            }
        }
        _tickCount++;

        float elapsed = Elapsed();

        // Transition tick takes precedence over the normal per-mode render
        // — renders the in-between frame and advances the animation.
        if (_transition.IsActive && _solarSystem != null)
        {
            bool completed = _transition.RenderAndAdvance(_planet, SelectedPlanetConfig,
                _app.AspectRatio, elapsed);
            if (completed)
            {
                if (_transition.Target == EditorMode.PlanetEdit)
                {
                    Mode = EditorMode.PlanetEdit;
                    _planetEditMode.ResetHover();
                }
                else
                {
                    Mode = EditorMode.SolarSystem;
                    SelectedPlanetConfig = null;

                    // If the player picked another planet from the backdrop,
                    // immediately chain a zoom-in to it.
                    if (_pendingPlanetSwitch != null)
                    {
                        SelectedPlanetConfig = _pendingPlanetSwitch;
                        _pendingPlanetSwitch = null;
                        var nextPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);
                        _transition.BeginZoomIn(nextPos, Elapsed());
                    }
                }
            }

            _hud.Sync();
            _app.RenderUI();
            OnFrameRendered?.Invoke();
            return;
        }

        // Normal tick — dispatch to the active mode.
        var mode = CurrentMode();
        if (mode != null) await mode.RenderTick(elapsed);

        // Platform UI overlay. WASM uses HTML buttons that draw themselves;
        // desktop rasterises an EngineUI quad mesh.
        _hud.Sync();
        _app.RenderUI();
        OnFrameRendered?.Invoke();
    }
}
