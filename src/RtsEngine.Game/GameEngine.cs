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
    private readonly RtsRenderer? _rts;
    private readonly RtsConfig? _rtsConfig;
    private readonly RtsState _state = new();
    private readonly IGPU _gpu;
    private readonly EngineConfig _config;

    private float _azimuth, _elevation = 0.4f, _distance = 3.0f;
    private bool _dragging;
    private int _hoveredCell = -1;
    private DateTime _startTime = DateTime.UtcNow;

    private const float PixelsToRadians = 0.005f;

    /// <summary>
    /// Once the planet-view camera distance crosses this, we glide back to
    /// solar-system view. Sits just above the solar-system camera's default
    /// distance (80) so the user has to actively zoom out to leave.
    /// </summary>
    private const float AutoZoomOutThreshold = 100f;
    private const float RingFadeNear = 20f;   // start fading rings in here
    private const float RingFadeFar = 90f;    // fully visible by here (just below auto-trigger)
    private const float PlanetViewFovYDegrees = 45f;
    private static readonly float PlanetViewFocalY = 1f / MathF.Tan(PlanetViewFovYDegrees * MathF.PI / 360f);

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Camera transition (solar system ↔ planet)
    private bool _transitioning;
    private float _transitionStart;
    private const float TransitionDuration = 1.5f;
    private EditorMode _transitionTarget;
    private Vector3 _transitionPlanetPos;
    private float _transitionDisplayRadius = 1f;
    private float _zoomOutStartDist = 3f;       // captured _distance when zoom-out begins
    private bool _planetReady;
    /// <summary>
    /// If the player taps another planet from the planet edit backdrop, we
    /// queue it here, kick off a zoom-out, and chain a zoom-in to the new
    /// planet once the zoom-out completes.
    /// </summary>
    private string? _pendingPlanetSwitch;

    private bool _running;
    private float _lastTickElapsed;
    public EditorMode Mode { get; set; } = EditorMode.SolarSystem;
    public string? SelectedPlanetConfig { get; private set; }
    public event Action? OnFrameRendered;

    /// <summary>
    /// Edit mode is OFF by default. Clicks only raise/lower terrain when the
    /// player has explicitly toggled it on via the Edit button.
    /// </summary>
    private bool _editMode;

    public void SetPlanetRenderer(PlanetRenderer p)
    {
        _planet = p;
        // Cell indices are planet-specific — buildings built on the previous
        // planet would map to nonsensical cells on the new one. Wipe state.
        _state.Clear();
        RefreshRtsButtons();
    }

    /// <summary>Called by Home.razor after the planet renderer is ready.</summary>
    public void SwitchToPlanetEdit()
    {
        _planetReady = true;
        if (!_transitioning)
        {
            Mode = EditorMode.PlanetEdit;
            _hoveredCell = -1;
        }
    }

    public void SwitchToSolarSystem()
    {
        if (_solarSystem == null) { Mode = EditorMode.SolarSystem; return; }

        // Snapshot the planet's current orbital position so the static mesh
        // starts the zoom-out aligned with the live dynamic mesh. The transition
        // tick keeps it updated each frame.
        _solarSystem.SetTime(Elapsed());
        _transitionPlanetPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);

        // Capture where the planet view's camera is right now so the zoom-out
        // animation starts from the user's current distance, not a forced 3.0.
        _zoomOutStartDist = _distance;

        _transitioning = true;
        _transitionStart = Elapsed();
        _transitionTarget = EditorMode.SolarSystem;
        _planetReady = false;
        // Keep SelectedPlanetConfig set during the transition so we can keep
        // hiding the dynamic mesh and tracking its orbital position. Cleared
        // when the transition finishes.
        Mode = EditorMode.PlanetEdit; // stays in edit mode visually until transition completes
    }

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
                // Multiplicative scale so far-out scrolls don't crawl. Range
                // goes wide enough to see the sun and other planets behind the
                // detailed mesh, and tight enough to skim the surface for an
                // RTS-style ground view.
                _distance -= delta * _distance * 0.001f;
                _distance = Math.Clamp(_distance, MinPlanetDistance(), 200f);
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

    public void SetupUI()
    {
        var css = @"{
            ""top"":""10px"",""left"":""10px"",
            ""padding"":""10px 20px"",""fontSize"":""16px"",
            ""background"":""rgba(10,40,60,0.9)"",""color"":""#0ff"",
            ""border"":""1px solid #0ff"",""borderRadius"":""6px"",
            ""cursor"":""pointer"",""display"":""none"",
            ""fontFamily"":""monospace""
        }";
        var editCss = @"{
            ""top"":""10px"",""left"":""200px"",
            ""padding"":""10px 20px"",""fontSize"":""16px"",
            ""background"":""rgba(40,30,10,0.9)"",""color"":""#fc6"",
            ""border"":""1px solid #fc6"",""borderRadius"":""6px"",
            ""cursor"":""pointer"",""display"":""none"",
            ""fontFamily"":""monospace""
        }";
        _app.CreateUIButton("back_solar", "⬅ Solar System", css);
        _app.CreateUIButton("edit_toggle", "✏ Edit", editCss);

        // RTS build bar — one button per building type, laid out along the
        // bottom edge. Produce buttons (per selected building) and the
        // cancel-placement button are created lazily on demand.
        if (_rtsConfig != null)
        {
            for (int i = 0; i < _rtsConfig.Buildings.Count; i++)
            {
                var b = _rtsConfig.Buildings[i];
                _app.CreateUIButton($"build_{b.Id}", $"🏗 {b.Name}", BuildButtonCss(i, active: false));
                _app.ShowUIButton($"build_{b.Id}", false);
            }
            _app.CreateUIButton("cancel_placement", "✕ Cancel Placement", CancelPlacementCss());
            _app.ShowUIButton("cancel_placement", false);

            // Pre-create produce buttons too so we can just toggle visibility.
            // Worst case = max(produces) buttons; create one per unit type and
            // re-label as needed. Simpler: one button per unique unit id.
            var unitIds = _rtsConfig.Units.Select(u => u.Id).Distinct().ToList();
            for (int i = 0; i < unitIds.Count; i++)
            {
                _app.CreateUIButton($"produce_{unitIds[i]}", "", ProduceButtonCss(i));
                _app.ShowUIButton($"produce_{unitIds[i]}", false);
            }
        }

        _app.UIButtonClick += OnUIButton;
    }

    private static string BuildButtonCss(int slot, bool active)
    {
        int left = 10 + slot * 180;
        var bg = active ? "rgba(80,40,10,0.95)" : "rgba(20,40,30,0.9)";
        var col = active ? "#ffd080" : "#9fc";
        return @"{""bottom"":""10px"",""left"":""" + left + @"px""," +
               @"""padding"":""10px 16px"",""fontSize"":""14px""," +
               @"""background"":""" + bg + @"""," +
               @"""color"":""" + col + @"""," +
               @"""border"":""1px solid #4a8"",""borderRadius"":""6px""," +
               @"""cursor"":""pointer"",""display"":""none""," +
               @"""fontFamily"":""monospace""}";
    }

    private static string ProduceButtonCss(int slot)
    {
        int top = 60 + slot * 50;
        return @"{""top"":""" + top + @"px"",""right"":""10px""," +
               @"""padding"":""8px 14px"",""fontSize"":""13px""," +
               @"""background"":""rgba(40,20,40,0.9)"",""color"":""#fac""," +
               @"""border"":""1px solid #c6a"",""borderRadius"":""6px""," +
               @"""cursor"":""pointer"",""display"":""none""," +
               @"""fontFamily"":""monospace""}";
    }

    private static string CancelPlacementCss()
    {
        return @"{""bottom"":""60px"",""left"":""10px""," +
               @"""padding"":""8px 14px"",""fontSize"":""13px""," +
               @"""background"":""rgba(60,20,20,0.9)"",""color"":""#fcc""," +
               @"""border"":""1px solid #c66"",""borderRadius"":""6px""," +
               @"""cursor"":""pointer"",""display"":""none""," +
               @"""fontFamily"":""monospace""}";
    }

    private void OnUIButton(string id)
    {
        if (id == "back_solar") SwitchToSolarSystem();
        else if (id == "edit_toggle")
        {
            _editMode = !_editMode;
            _hoveredCell = -1;
            // Reuse CreateUIButton to update the label — JS overlay swaps the
            // textContent on the existing element.
            var editCss = @"{
                ""top"":""10px"",""left"":""200px"",
                ""padding"":""10px 20px"",""fontSize"":""16px"",""cursor"":""pointer"",
                ""border"":""1px solid #fc6"",""borderRadius"":""6px"",""fontFamily"":""monospace"",
                ""display"":""block"",
                ""background"":""" + (_editMode ? "rgba(80,40,10,0.95)" : "rgba(40,30,10,0.9)") + @""",
                ""color"":""" + (_editMode ? "#ffd080" : "#fc6") + @"""
            }";
            _app.CreateUIButton("edit_toggle", _editMode ? "✓ Editing" : "✏ Edit", editCss);
        }
        else if (id == "cancel_placement")
        {
            _state.PlacementBuildingId = null;
            RefreshRtsButtons();
        }
        else if (id.StartsWith("build_") && _rtsConfig != null)
        {
            var typeId = id.Substring("build_".Length);
            // Toggle: clicking the same build button cancels placement.
            _state.PlacementBuildingId = _state.PlacementBuildingId == typeId ? null : typeId;
            _state.SelectedBuildingInstanceId = -1;
            RefreshRtsButtons();
        }
        else if (id.StartsWith("produce_") && _rtsConfig != null)
        {
            var unitId = id.Substring("produce_".Length);
            ProduceUnit(unitId);
        }
    }

    /// <summary>
    /// Cheap per-frame visibility sync — only toggles ShowUIButton, no
    /// label/CSS rewrites. Call every frame in PlanetEdit; call once on
    /// mode change to hide everything when leaving.
    /// </summary>
    private void UpdateRtsButtonVisibility()
    {
        if (_rtsConfig == null) return;
        bool inPlanet = Mode == EditorMode.PlanetEdit && !_transitioning;

        foreach (var b in _rtsConfig.Buildings)
            _app.ShowUIButton($"build_{b.Id}", inPlanet);
        _app.ShowUIButton("cancel_placement", inPlanet && _state.PlacementBuildingId != null);

        var selected = _state.SelectedBuilding;
        var selectedDef = selected != null ? _rtsConfig.GetBuilding(selected.TypeId) : null;
        var enabled = inPlanet && selectedDef != null ? selectedDef.Produces : new List<string>();
        foreach (var u in _rtsConfig.Units)
            _app.ShowUIButton($"produce_{u.Id}", enabled.Contains(u.Id));
    }

    /// <summary>
    /// Full refresh — rewrites button labels + CSS to reflect placement
    /// highlights and the produce-bar slot order. Called from event
    /// handlers (build/produce/cancel clicks, building selection).
    /// </summary>
    private void RefreshRtsButtons()
    {
        if (_rtsConfig == null) return;

        for (int i = 0; i < _rtsConfig.Buildings.Count; i++)
        {
            var b = _rtsConfig.Buildings[i];
            bool active = _state.PlacementBuildingId == b.Id;
            _app.CreateUIButton($"build_{b.Id}", (active ? "▶ " : "🏗 ") + b.Name,
                BuildButtonCss(i, active));
        }

        var selected = _state.SelectedBuilding;
        var selectedDef = selected != null ? _rtsConfig.GetBuilding(selected.TypeId) : null;
        if (selectedDef != null)
        {
            for (int slot = 0; slot < selectedDef.Produces.Count; slot++)
            {
                var uid = selectedDef.Produces[slot];
                var unitDef = _rtsConfig.GetUnit(uid);
                _app.CreateUIButton($"produce_{uid}", $"⚙ {unitDef?.Name ?? uid}",
                    ProduceButtonCss(slot));
            }
        }

        UpdateRtsButtonVisibility();
    }

    private void ProduceUnit(string unitId)
    {
        if (_rtsConfig == null) return;
        var b = _state.SelectedBuilding;
        if (b == null) return;
        var def = _rtsConfig.GetBuilding(b.TypeId);
        if (def == null || !def.Produces.Contains(unitId)) return;

        var mesh = _planet.Mesh;
        var up = mesh.GetCellCenter(b.CellIndex);
        float surfaceR = mesh.Radius + mesh.GetLevel(b.CellIndex) * mesh.StepHeight;

        // Spawn units in a ring around the building so successive units don't
        // stack on the same point. Tangent basis comes from the cell's normal.
        var worldUp = new Vector3(0, 1, 0);
        var tangentA = Vector3.Cross(worldUp, up);
        if (tangentA.LengthSquared() < 1e-5f) tangentA = Vector3.Cross(new Vector3(1, 0, 0), up);
        tangentA = Vector3.Normalize(tangentA);
        var tangentB = Vector3.Normalize(Vector3.Cross(up, tangentA));

        int slot = b.UnitsSpawned;
        b.UnitsSpawned++;
        float angle = slot * 0.7f;
        float ring = 0.06f * mesh.Radius;
        var offset = tangentA * (MathF.Cos(angle) * ring) + tangentB * (MathF.Sin(angle) * ring);
        var pos = Vector3.Normalize(up * surfaceR + offset) * (surfaceR + 0.002f);

        var spawned = _state.SpawnUnit(unitId, pos, Vector3.Normalize(pos));
        // Anchor the unit on the building's cell so move commands have a
        // valid starting point for pathfinding from the moment it spawns.
        spawned.CellIndex = b.CellIndex;
        spawned.Heading = tangentA;
    }

    private void OnClick(float cx, float cy, int button)
    {
        if (_transitioning) return;

        // UI buttons consume clicks first
        if (Mode == EditorMode.PlanetEdit)
        {
            // Once orbit rings are visible, the backdrop bodies (sun + other
            // planets) are pickable too — tapping one chains a zoom-out then
            // zoom-in into that body's edit view.
            if (button == 0 && _solarSystem != null)
            {
                float ringAlpha = Smoothstep(RingFadeNear, RingFadeFar, _distance);
                if (ringAlpha > 0.1f)
                {
                    var selfPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);
                    var planetMvp = BuildPlanetMvp(_app.AspectRatio);
                    var (config, _) = _solarSystem.PickBackdrop(
                        cx, cy, _app.CanvasWidth, _app.CanvasHeight,
                        planetMvp, selfPos, SelectedPlanetConfig, PlanetViewFocalY);
                    if (config != null && config != SelectedPlanetConfig)
                    {
                        _pendingPlanetSwitch = config;
                        SwitchToSolarSystem();
                        return;
                    }
                }
            }

            // RTS interactions take priority over terrain editing when edit
            // mode is OFF — left-click places a queued building, picks a
            // unit or building, or deselects. Right-click on a selected unit
            // issues a move command via the pathfinder.
            if (!_editMode && _rts != null)
            {
                if (button == 2)
                {
                    HandleMoveCommand(cx, cy);
                    return;
                }

                if (button != 0) return;

                // Picking priority while not placing: units first (they sit on
                // top of cells and are smaller, so the player would expect a
                // direct hit to select the unit not its cell), then buildings,
                // then a plain cell click.
                if (_state.PlacementBuildingId == null)
                {
                    int unitId = PickUnit(cx, cy);
                    if (unitId >= 0)
                    {
                        _state.SelectedUnitInstanceId = unitId;
                        _state.SelectedBuildingInstanceId = -1;
                        RefreshRtsButtons();
                        return;
                    }
                }

                var cell = PickCell(cx, cy);
                if (cell == null)
                {
                    if (_state.SelectedBuildingInstanceId != -1
                        || _state.SelectedUnitInstanceId != -1
                        || _state.PlacementBuildingId != null)
                    {
                        _state.SelectedBuildingInstanceId = -1;
                        _state.SelectedUnitInstanceId = -1;
                        _state.PlacementBuildingId = null;
                        RefreshRtsButtons();
                    }
                    return;
                }

                if (_state.PlacementBuildingId != null)
                {
                    if (_state.BuildingAtCell(cell.Value) == null)
                        _state.PlaceBuilding(_state.PlacementBuildingId, cell.Value);
                    _state.PlacementBuildingId = null;
                    RefreshRtsButtons();
                    return;
                }

                var existing = _state.BuildingAtCell(cell.Value);
                if (existing != null)
                {
                    _state.SelectedBuildingInstanceId = existing.InstanceId;
                    _state.SelectedUnitInstanceId = -1;
                    RefreshRtsButtons();
                    return;
                }

                if (_state.SelectedBuildingInstanceId != -1 || _state.SelectedUnitInstanceId != -1)
                {
                    _state.SelectedBuildingInstanceId = -1;
                    _state.SelectedUnitInstanceId = -1;
                    RefreshRtsButtons();
                }
                return;
            }

            // Cells only respond to taps when the player has turned editing on.
            if (!_editMode) return;
            var editCell = PickCell(cx, cy);
            if (editCell == null) return;
            if (button == 0) _planet.Mesh.ChangeLevel(editCell.Value, +1);
            else if (button == 2) _planet.Mesh.ChangeLevel(editCell.Value, -1);
            _planet.MarkDirty(editCell.Value);
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
            var (config, pos, dispR) = _solarSystem.PickPlanet(cx, cy, _app.CanvasWidth, _app.CanvasHeight);
            if (config != null)
            {
                SelectedPlanetConfig = config;
                _transitionPlanetPos = pos;
                _transitionDisplayRadius = dispR;
                _planetReady = false;
                _transitioning = true;
                _transitionStart = Elapsed();
                _transitionTarget = EditorMode.PlanetEdit;
            }
        }
    }

    private void OnMove(float cx, float cy)
    {
        // Only show the hex hover highlight while editing, so the planet looks
        // clean by default.
        if (Mode == EditorMode.PlanetEdit && !_transitioning && _editMode)
            _hoveredCell = PickCell(cx, cy) ?? -1;
        else if (!_editMode && _hoveredCell != -1)
            _hoveredCell = -1;
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

        // Handle zoom transition: renders actual planet during zoom for seamless switch
        if (_transitioning && _solarSystem != null)
        {
            float t = Math.Clamp((elapsed - _transitionStart) / TransitionDuration, 0f, 1f);
            float smooth = t * t * (3f - 2f * t);

            // Track the focused planet's *live* orbital position so the static
            // mesh stays glued to where the dynamic mesh actually is. Without
            // this the static and dynamic copies drift apart during the ~1.5s
            // transition (the body keeps orbiting) and the planet visibly pops.
            _solarSystem.SetTime(elapsed);
            _transitionPlanetPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);

            // Hide the focused body's dynamic mesh while the static one is
            // standing in for it — otherwise both would overlap at the same
            // world position once we sync them.
            _solarSystem.HidePlanet(SelectedPlanetConfig);

            // Lighting matches the planet's current orbital position so the
            // detailed mesh is correctly lit from the moment it appears, not
            // only after the transition completes.
            var sunDir = _transitionPlanetPos.LengthSquared() > 1e-6f
                ? -Vector3.Normalize(_transitionPlanetPos)
                : new Vector3(0.5f, 0.7f, 0.5f);
            _planet.SetSunDirection(sunDir.X, sunDir.Y, sunDir.Z);

            if (_transitionTarget == EditorMode.PlanetEdit)
            {
                // Solar system camera zooms toward planet
                float ssDist = 80f * (1f - smooth) + 3f * smooth;
                _solarSystem.Distance = ssDist;
                _solarSystem.SetFocusTarget(_transitionPlanetPos * smooth);

                // Starfield first (clears framebuffer), then solar system
                // background which is already additive.
                var (sFwd, sRight, sUp) = _solarSystem.GetCameraBasis();
                _solarSystem.DrawStarfield(sFwd, sRight, sUp, _solarSystem.FovYDegreesPublic, _app.AspectRatio);

                var ssMvp = _solarSystem.BuildMvpFloats(_app.AspectRatio);
                _solarSystem.Draw(ssMvp);

                // Render textured planet on top, aligned exactly with the noise sphere.
                // Derive planet MVP from solar system MVP + translation so FOV/near/far match.
                if (_planetReady && smooth > 0.1f)
                {
                    // planetMVP = translate(planetPos) * solarSystemMVP
                    // This transforms planet-at-origin to the same clip position as
                    // the solar system transforms planetPos. Exact alignment, no FOV mismatch.
                    var pp = _transitionPlanetPos;
                    var trans = Matrix4X4.CreateTranslation(
                        new Vector3D<float>(pp.X, pp.Y, pp.Z));
                    var ssMvpMat = RawToSilkMat(ssMvp);
                    var planetMvpMat = Matrix4X4.Multiply(trans, ssMvpMat);
                    var planetMvp = MatrixHelper.ToRawFloats(planetMvpMat);

                    // Camera distance for LOD
                    var camDir = new Vector3(
                        MathF.Cos(_solarSystem.Elevation) * MathF.Cos(_solarSystem.Azimuth),
                        MathF.Sin(_solarSystem.Elevation),
                        MathF.Cos(_solarSystem.Elevation) * MathF.Sin(_solarSystem.Azimuth));
                    var ssCamPos = _transitionPlanetPos * smooth + camDir * ssDist;
                    float planetDist = (ssCamPos - _transitionPlanetPos).Length();

                    _planet.SetCameraPosition(ssCamPos.X - pp.X, ssCamPos.Y - pp.Y, ssCamPos.Z - pp.Z);
                    _planet.SetTime(elapsed);
                    _planet.Draw(planetMvp, planetDist, clearFirst: false);
                }

                // Switch when animation done AND planet ready
                if (t >= 1f && _planetReady)
                {
                    _transitioning = false;
                    Mode = EditorMode.PlanetEdit;
                    _distance = 3f;
                    _azimuth = _solarSystem.Azimuth;
                    _elevation = _solarSystem.Elevation;
                    _hoveredCell = -1;
                    _solarSystem.Distance = 80f;
                    _solarSystem.SetFocusTarget(Vector3.Zero);
                    _solarSystem.HidePlanet(null);
                }
            }
            else // Zoom OUT — focused planet stays at origin, world moves around it
            {
                // The detailed mesh stays at origin (in camera space). Camera
                // pulls back; the rest of the solar system (sun + other planets)
                // is rendered shifted by -planetPos so it visibly orbits past
                // while we zoom away. This matches planet-edit mode rendering,
                // which is also "planet at origin, backdrop shifted".
                float zoomDist = _zoomOutStartDist * (1f - smooth) + 80f * smooth;
                var camPos = new Vector3(
                    zoomDist * MathF.Cos(_elevation) * MathF.Cos(_azimuth),
                    zoomDist * MathF.Sin(_elevation),
                    zoomDist * MathF.Cos(_elevation) * MathF.Sin(_azimuth));
                var planetMvp = BuildPlanetMvpAt(camPos, _app.AspectRatio);

                // Starfield first (clears framebuffer); detailed mesh and
                // backdrop are then additive on top.
                var fwdOut = -Vector3.Normalize(camPos);
                var rightOut = Vector3.Normalize(Vector3.Cross(fwdOut, new Vector3(0, 1, 0)));
                var upOut = Vector3.Cross(rightOut, fwdOut);
                _solarSystem.DrawStarfield(fwdOut, rightOut, upOut, PlanetViewFovYDegrees, _app.AspectRatio);

                // Detailed mesh at origin.
                _planet.SetCameraPosition(camPos.X, camPos.Y, camPos.Z);
                _planet.SetTime(elapsed);
                _planet.Draw(planetMvp, zoomDist, clearFirst: false);

                // Backdrop: sun + other bodies, world translated by -planetPos.
                // Same fade curve as planet-edit normal tick so the ring alpha
                // is continuous through trigger → transition → handoff.
                var cameraWorldPosOut = _transitionPlanetPos + camPos;
                float ringAlphaOut = Smoothstep(RingFadeNear, RingFadeFar, zoomDist);
                _solarSystem.DrawBackdrop(planetMvp, SelectedPlanetConfig, _transitionPlanetPos,
                    cameraWorldPosOut, ringAlphaOut);

                if (t >= 1f)
                {
                    _transitioning = false;
                    Mode = EditorMode.SolarSystem;
                    // The shifted-frame camera (origin = planet, distance = 80
                    // along _azimuth/_elevation) is the same world position as
                    // a solar-system camera at distance 80 focused on planetPos
                    // along the same angles. Carry those angles over so the
                    // viewing direction doesn't snap.
                    _solarSystem.Distance = 80f;
                    _solarSystem.Azimuth = _azimuth;
                    _solarSystem.Elevation = _elevation;
                    _solarSystem.SetFocusTarget(_transitionPlanetPos);
                    _solarSystem.HidePlanet(null);
                    SelectedPlanetConfig = null;

                    // If the player picked another planet from the backdrop,
                    // immediately chain a zoom-in to it.
                    if (_pendingPlanetSwitch != null)
                    {
                        SelectedPlanetConfig = _pendingPlanetSwitch;
                        _pendingPlanetSwitch = null;
                        _transitionPlanetPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);
                        _planetReady = false;
                        _transitioning = true;
                        _transitionStart = Elapsed();
                        _transitionTarget = EditorMode.PlanetEdit;
                    }
                }
            }

            _app.ShowUIButton("back_solar", false);
            _app.ShowUIButton("edit_toggle", false);
            UpdateRtsButtonVisibility();
            OnFrameRendered?.Invoke();
            return;
        }

        // Normal tick
        if (Mode == EditorMode.PlanetEdit)
        {
            // Pulled too far out → glide back to the solar system view.
            // Picked a touch above the planet view's solar-system distance (80)
            // so the user has to actively zoom past the comfort zone to leave.
            if (_distance > AutoZoomOutThreshold)
            {
                SwitchToSolarSystem();
                OnFrameRendered?.Invoke();
                return;
            }

            await _planet.RebuildDirtyPatches();
            var cam = CameraPosition();
            _planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
            _planet.SetTime(elapsed);
            _planet.SetHighlightCell(_hoveredCell);
            await _planet.SyncOutline();

            // Advance any units that have a path queued. Frame-bounded dt so
            // a stutter (long await above) doesn't teleport units forward.
            if (_rts != null && _rtsConfig != null)
            {
                float dt = MathF.Min(0.05f, elapsed - _lastTickElapsed);
                if (dt > 0f) MovementSystem.Tick(_state, _planet.Mesh, _rtsConfig, dt);
            }
            _lastTickElapsed = elapsed;

            // Sync lighting to the planet's solar-system position: the sun is at
            // the solar-system origin, so the direction toward the sun (relative
            // to a planet at world position P) is -P/|P|.
            Vector3 selfPos = Vector3.Zero;
            Vector3 cameraWorldPos = cam;
            if (_solarSystem != null)
            {
                _solarSystem.SetTime(elapsed);
                selfPos = _solarSystem.GetBodyWorldPosition(SelectedPlanetConfig);
                if (selfPos.LengthSquared() > 1e-6f)
                {
                    var sunDir = -Vector3.Normalize(selfPos);
                    _planet.SetSunDirection(sunDir.X, sunDir.Y, sunDir.Z);
                }
                // The planet view's camera lives at `cam` in planet-local space
                // (origin = planet center). In solar-system world coords that's
                // `selfPos + cam`. The backdrop shader needs world-space camera.
                cameraWorldPos = selfPos + cam;
            }

            var planetMvp = BuildPlanetMvp(_app.AspectRatio);

            // Starfield first — clears the framebuffer and lays down the
            // procedural sky behind the focused planet.
            if (_solarSystem != null)
            {
                var fwd = -Vector3.Normalize(cam);
                var right = Vector3.Normalize(Vector3.Cross(fwd, new Vector3(0, 1, 0)));
                var up = Vector3.Cross(right, fwd);
                _solarSystem.DrawStarfield(fwd, right, up, PlanetViewFovYDegrees, _app.AspectRatio);
            }

            _planet.Draw(planetMvp, _distance, clearFirst: false);

            // Buildings and units sit on the planet surface, in the same local
            // space as the terrain mesh. Reuse the planet MVP so they line up
            // exactly with the cells they're placed on.
            if (_rts != null)
            {
                _rts.SetSunDirection(selfPos.LengthSquared() > 1e-6f
                    ? -Vector3.Normalize(selfPos) : new Vector3(0.5f, 0.7f, 0.5f));
                _rts.Draw(_state, _planet.Mesh, planetMvp);
            }

            // Orbit rings start invisible up close, fade in as we zoom out so
            // the player can see the orbital structure before the auto-trigger
            // pulls them back to solar-system view. Reaches full alpha just
            // below the auto-trigger so the handoff doesn't pop.
            float ringAlpha = Smoothstep(RingFadeNear, RingFadeFar, _distance);

            // Render the rest of the solar system (sun + other planets) as a
            // backdrop, with the world shifted so the focused planet is at origin.
            _solarSystem?.DrawBackdrop(planetMvp, SelectedPlanetConfig, selfPos, cameraWorldPos, ringAlpha);

            _app.ShowUIButton("back_solar", true);
            _app.ShowUIButton("edit_toggle", true);
            UpdateRtsButtonVisibility();
        }
        else if (Mode == EditorMode.SolarSystem && _solarSystem != null)
        {
            _app.ShowUIButton("back_solar", false);
            _app.ShowUIButton("edit_toggle", false);
            UpdateRtsButtonVisibility();
            _solarSystem.SetTime(elapsed);
            var mvp = _solarSystem.BuildMvpFloats(_app.AspectRatio);

            // Starfield first (clears), then the rest of the system stacks on top.
            var (fwd, right, up) = _solarSystem.GetCameraBasis();
            _solarSystem.DrawStarfield(fwd, right, up, _solarSystem.FovYDegreesPublic, _app.AspectRatio);

            _solarSystem.Draw(mvp);
        }
        else if (Mode == EditorMode.StarMap && _starMap != null)
        {
            _app.ShowUIButton("back_solar", false);
            _app.ShowUIButton("edit_toggle", false);
            UpdateRtsButtonVisibility();
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

    /// <summary>Closest the planet camera may approach, in world units. Sits
    /// just above the highest possible terrain so ground-level RTS zoom never
    /// dips below the surface.</summary>
    private float MinPlanetDistance()
    {
        float radius = _planet.Mesh.Radius;
        return radius * (1f + _config.RtsCamera.GroundClearance);
    }

    /// <summary>Tilted look-at target for RTS-style ground view. At high
    /// altitude returns the planet center; near the surface the target slides
    /// to a point ahead on the ground so the camera tilts forward rather than
    /// staring straight down.</summary>
    private Vector3 PlanetLookAtTarget(Vector3 camPos)
    {
        float radius = _planet.Mesh.Radius;
        float altitude = camPos.Length() - radius;
        float blend = 1f - Smoothstep(0f, _config.RtsCamera.TiltStartHeight, altitude);
        if (blend <= 0f) return Vector3.Zero;

        // Tangent at the camera pointing toward decreasing elevation — i.e.
        // "forward along the ground" for an orbit camera. Drives the look-at
        // offset so dragging tilts the view across the surface, not around it.
        float ce = MathF.Cos(_elevation), se = MathF.Sin(_elevation);
        float ca = MathF.Cos(_azimuth), sa = MathF.Sin(_azimuth);
        var southTangent = new Vector3(se * ca, -ce, se * sa);
        var camDir = Vector3.Normalize(camPos);
        var groundTarget = camDir * radius + southTangent * (radius * _config.RtsCamera.LookAhead);
        return groundTarget * blend;
    }

    private float[] BuildPlanetMvpAt(Vector3 camPos, float aspectRatio)
    {
        var lookAt = PlanetLookAtTarget(camPos);
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(camPos.X, camPos.Y, camPos.Z),
            new Vector3D<float>(lookAt.X, lookAt.Y, lookAt.Z),
            new Vector3D<float>(0, 1, 0));
        // Far plane pushed out to fit the rest of the solar system (sun + other
        // planets render as a backdrop). Logarithmic depth in the shaders keeps
        // precision uniform across the wide range. Near plane tightened so the
        // RTS ground camera doesn't clip into nearby terrain.
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(45.0f), aspectRatio, 0.01f, 10000.0f);
        return MatrixHelper.ToRawFloats(Matrix4X4.Multiply(view, proj));
    }

    public float[] BuildMvp(float aspectRatio) => BuildPlanetMvp(aspectRatio);

    private static Matrix4X4<float> RawToSilkMat(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    // ── Picking ─────────────────────────────────────────────────────

    /// <summary>
    /// Project every spawned unit to screen, return the instance id of the
    /// nearest one within a generous pick radius (units are visually small,
    /// so we forgive imprecise clicks). -1 if nothing's close enough.
    /// </summary>
    private int PickUnit(float canvasX, float canvasY)
    {
        if (_rts == null) return -1;
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return -1;

        var m = BuildPlanetMvp(w / h);
        var mvp = new Matrix4x4(
            m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

        const float pickPx = 18f;
        float bestDist = pickPx * pickPx;
        int best = -1;
        var camDir = Vector3.Normalize(CameraPosition());

        foreach (var unit in _state.Units)
        {
            // Cull units on the far side of the planet.
            if (Vector3.Dot(unit.SurfaceUp, camDir) < -0.05f) continue;

            var clip = Vector4.Transform(new Vector4(unit.SurfacePoint, 1f), mvp);
            if (clip.W <= 0.001f) continue;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;
            float d = (sx - canvasX) * (sx - canvasX) + (sy - canvasY) * (sy - canvasY);
            if (d < bestDist) { bestDist = d; best = unit.InstanceId; }
        }
        return best;
    }

    /// <summary>
    /// Right-click handler: if a unit is selected and the click landed on
    /// a cell, queue an A* path and let MovementSystem advance the unit
    /// next tick. No-op if there's no path or no selection.
    /// </summary>
    private void HandleMoveCommand(float canvasX, float canvasY)
    {
        var unit = _state.SelectedUnit;
        if (unit == null || _rtsConfig == null) return;
        var def = _rtsConfig.GetUnit(unit.TypeId);
        if (def == null) return;

        var targetCell = PickCell(canvasX, canvasY);
        if (targetCell == null) return;

        var path = Pathfinding.FindPath(_planet.Mesh, unit.CellIndex, targetCell.Value, def.CanHop);
        if (path == null) return;

        unit.Path = path;
        unit.PathIndex = path.Count > 0 && path[0] == unit.CellIndex ? 1 : 0;
    }

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
