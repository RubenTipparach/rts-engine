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
    /// <summary>Scroll updates this; the per-tick zoom lerp chases it from
    /// <see cref="_distance"/> at <c>RtsCamera.ZoomLerpRate</c> per second.
    /// Decoupling the two gives smooth zoom without losing input snappiness —
    /// the user's scroll wheel writes immediately into the target, the
    /// camera follows.</summary>
    private float _targetDistance = 3.0f;
    private bool _dragging;
    private int _hoveredCell = -1;
    private DateTime _startTime = DateTime.UtcNow;

    private const float PixelsToRadians = 0.005f;

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

    /// <summary>
    /// True while the user is dragging a box-select rectangle. Suppresses
    /// PointerDrag-driven orbit so plain left drag doesn't move the camera
    /// when the player is selecting units.
    /// </summary>
    private bool _boxSelectActive;

    /// <summary>Anchor cell of the click that opened the unit context menu.
    /// The "move" / "attack" context-menu actions then queue against the next
    /// world click.</summary>
    private string? _pendingUnitOrder;     // "move", "attack", or null
    private const float ContextMenuItemHeight = 32f;
    private static readonly string[] ContextMenuItems = { "Cancel order", "Guard", "Move", "Attack" };

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
            if (_transitioning) return;
            // While the player is pulling a box-select rectangle, plain-left
            // drag should not orbit the planet camera. Desktop fires both
            // BoxSelectUpdate and PointerDrag for the same gesture so other
            // modes (solar system / starmap, where there's nothing to box-
            // select) keep their existing left-drag-orbits behaviour.
            if (_boxSelectActive) return;
            if (!_dragging) return;
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

        // Explicit orbit gesture: middle drag or Alt+left drag on desktop.
        // Desktop fires this in addition to PointerDrag so we'd double-orbit
        // if both ran — gate this one to PlanetEdit, where plain PointerDrag
        // is suppressed by _boxSelectActive during box select.
        _app.OrbitDrag += (dx, dy) =>
        {
            if (_transitioning) return;
            if (Mode != EditorMode.PlanetEdit) return;
            _azimuth += dx * PixelsToRadians;
            _elevation += dy * PixelsToRadians;
            _elevation = Math.Clamp(_elevation, -1.4f, 1.4f);
        };

        _app.BoxSelectUpdate += (x0, y0, x1, y1) =>
        {
            if (_transitioning) return;
            if (Mode != EditorMode.PlanetEdit) return;
            _boxSelectActive = true;
            UpdateBoxSelectOverlay(x0, y0, x1, y1);
        };

        _app.BoxSelectComplete += (x0, y0, x1, y1) =>
        {
            _boxSelectActive = false;
            HideBoxSelectOverlay();
            if (_transitioning) return;
            if (Mode != EditorMode.PlanetEdit) return;
            BoxSelectUnits(x0, y0, x1, y1);
        };

        _app.ContextMenuRequested += (x, y) =>
        {
            if (_transitioning) return;
            if (Mode != EditorMode.PlanetEdit) return;
            ShowUnitContextMenu(x, y);
        };

        _app.Scroll += delta =>
        {
            if (_transitioning) return;
            if (Mode == EditorMode.PlanetEdit)
            {
                // Logarithmic zoom: each scroll tick changes *altitude*
                // (above the surface) by a fixed percentage, not distance
                // from the planet center. Subjective zoom speed feels
                // uniform regardless of altitude. Scroll writes the *target*
                // distance; the camera lerps to it in Tick (RtsCamera.ZoomLerpRate)
                // so the motion is smooth without losing input snappiness.
                float radius = _planet.Mesh.Radius;
                float altitude = MathF.Max(1e-4f, _targetDistance - radius);
                altitude -= delta * altitude * _config.RtsCamera.ScrollIncrement;
                _targetDistance = radius + altitude;
                _targetDistance = Math.Clamp(_targetDistance,
                    MinPlanetDistance(), _config.PlanetEditView.MaxDistance);
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
        else if (id.StartsWith("ctx_"))
        {
            if (int.TryParse(id.Substring("ctx_".Length), out var idx))
                OnContextMenuPick(idx);
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
    /// Heads-up zoom indicator on the left edge — vertical bar fills as we
    /// approach the surface, plus numerical distance / altitude / zoom% /
    /// tilt% readouts so designers can dial in camera tuning by eye. Hidden
    /// outside PlanetEdit. Recreates the button each frame — cheap because
    /// the JS side reuses the existing DOM element and only touches text +
    /// style properties.
    /// </summary>
    private void UpdateZoomIndicator()
    {
        bool show = Mode == EditorMode.PlanetEdit && !_transitioning;
        if (!show)
        {
            _app.ShowUIButton("zoom_indicator", false);
            return;
        }

        float altitude = _distance - _planet.Mesh.Radius;

        // Reuse the same helpers the camera does so the indicator is the
        // source of truth for tuning — what you read here is what the tilt
        // and the smooth-up basis are reacting to.
        float zoomPct = ZoomPercent(_distance);
        int zoomInt = (int)MathF.Round(zoomPct * 100f);
        float tiltBlend = TiltBlend(_distance);
        int tiltInt = (int)MathF.Round(tiltBlend * 100f);

        // Multiline text on a button with white-space:pre. \n becomes a real
        // newline thanks to that style.
        string label = $"ZOOM\nD {_distance:F2}\nA {altitude:F3}\nZ {zoomInt,3}%\nT {tiltInt,3}%";

        // Linear gradient with a hard color stop at zoomInt% gives a clean
        // bar look without needing two separate elements.
        string css = "{" +
            "\"left\":\"10px\",\"top\":\"80px\"," +
            "\"width\":\"80px\",\"height\":\"220px\"," +
            "\"padding\":\"8px 6px\"," +
            "\"background\":\"linear-gradient(to top, rgba(80,200,120,0.75) " + zoomInt + "%, rgba(20,40,30,0.85) " + zoomInt + "%)\"," +
            "\"color\":\"#dfe\",\"fontSize\":\"11px\"," +
            "\"border\":\"1px solid #4a8\",\"borderRadius\":\"6px\"," +
            "\"fontFamily\":\"monospace\"," +
            "\"textAlign\":\"left\"," +
            "\"whiteSpace\":\"pre\"," +
            "\"pointerEvents\":\"none\"," +
            "\"lineHeight\":\"1.45\"," +
            "\"display\":\"block\"" +
        "}";

        _app.CreateUIButton("zoom_indicator", label, css);
        _app.ShowUIButton("zoom_indicator", true);
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

        UpdateRtsButtonVisibility(); UpdateZoomIndicator();
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

        // Any click outside the context menu dismisses it. Clicks ON the
        // menu hit UIButtonClick → OnContextMenuPick which hides it itself.
        HideUnitContextMenu();

        // If a "Move" / "Attack" context menu order is pending, the next
        // left-click on the world consumes it as the order's target.
        if (button == 0 && _pendingUnitOrder != null && Mode == EditorMode.PlanetEdit && !_editMode)
        {
            HandleMoveCommand(cx, cy);  // both move and attack route through here for now
            _pendingUnitOrder = null;
            return;
        }

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
                        _state.SelectedUnitInstanceIds.Clear();
                        _state.SelectedUnitInstanceIds.Add(unitId);
                        _state.SelectedBuildingInstanceId = -1;
                        RefreshRtsButtons();
                        return;
                    }
                }

                var cell = PickCell(cx, cy);
                if (cell == null)
                {
                    if (_state.SelectedBuildingInstanceId != -1
                        || _state.SelectedUnitInstanceIds.Count > 0
                        || _state.PlacementBuildingId != null)
                    {
                        _state.SelectedBuildingInstanceId = -1;
                        _state.SelectedUnitInstanceIds.Clear();
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
                    _state.SelectedUnitInstanceIds.Clear();
                    RefreshRtsButtons();
                    return;
                }

                if (_state.SelectedBuildingInstanceId != -1 || _state.SelectedUnitInstanceIds.Count > 0)
                {
                    _state.SelectedBuildingInstanceId = -1;
                    _state.SelectedUnitInstanceIds.Clear();
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
            Console.Error.WriteLine($"[click] solar pick at ({cx},{cy}) → {config ?? "<none>"}");
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

    private long _tickCount;

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
            Console.Error.WriteLine($"[tick] {_tickCount} mode={Mode} canvas={_app.CanvasWidth}x{_app.CanvasHeight} dist={_distance:F1} planetReady={_planetReady}");
        _tickCount++;

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
                    Console.Error.WriteLine($"[transition] zoom-in complete; switching Mode → PlanetEdit");
                    _transitioning = false;
                    Mode = EditorMode.PlanetEdit;
                    _distance = _config.PlanetEditView.DefaultDistance;
                    _targetDistance = _distance;
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
            UpdateRtsButtonVisibility(); UpdateZoomIndicator();
            _app.RenderUI();
            OnFrameRendered?.Invoke();
            return;
        }

        // Normal tick
        if (Mode == EditorMode.PlanetEdit)
        {
            // Pulled too far out → glide back to the solar system view.
            // Picked a touch above the planet view's solar-system distance (80)
            // so the user has to actively zoom past the comfort zone to leave.
            if (_distance > _config.PlanetEditView.AutoZoomOutThreshold)
            {
                SwitchToSolarSystem();
                OnFrameRendered?.Invoke();
                return;
            }

            await _planet.RebuildDirtyPatches();

            // Smooth zoom: chase the scroll-set target with an exponential
            // decay. Rate is per-second, so dt-corrected — feels identical
            // at 30 fps, 60 fps, or a stutter-recovery 120 fps tick. A clamped
            // dt prevents huge frame gaps from snapping the camera.
            float zoomDt = MathF.Min(0.05f, elapsed - _lastTickElapsed);
            if (zoomDt > 0f)
            {
                float a = 1f - MathF.Exp(-_config.RtsCamera.ZoomLerpRate * zoomDt);
                _distance += (_targetDistance - _distance) * a;
            }

            var cam = CameraPosition();
            _planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
            _planet.SetTime(elapsed);
            _planet.SetHighlightCell(_hoveredCell);
            await _planet.SyncOutline();

            // Advance any units that have a path queued. Frame-bounded dt so
            // a stutter (long await above) doesn't teleport units forward.
            if (_rts != null && _rtsConfig != null)
            {
                if (zoomDt > 0f) MovementSystem.Tick(_state, _planet.Mesh, _rtsConfig, zoomDt);
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
            // procedural sky behind the focused planet. Without solar system
            // (e.g. desktop build), the planet itself has to clear instead so
            // the previous frame doesn't bleed through.
            if (_solarSystem != null)
            {
                var fwd = -Vector3.Normalize(cam);
                var right = Vector3.Normalize(Vector3.Cross(fwd, new Vector3(0, 1, 0)));
                var up = Vector3.Cross(right, fwd);
                _solarSystem.DrawStarfield(fwd, right, up, PlanetViewFovYDegrees, _app.AspectRatio);
            }

            _planet.Draw(planetMvp, _distance, clearFirst: _solarSystem == null);

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
            UpdateRtsButtonVisibility(); UpdateZoomIndicator();
        }
        else if (Mode == EditorMode.SolarSystem && _solarSystem != null)
        {
            _app.ShowUIButton("back_solar", false);
            _app.ShowUIButton("edit_toggle", false);
            UpdateRtsButtonVisibility(); UpdateZoomIndicator();
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
            UpdateRtsButtonVisibility(); UpdateZoomIndicator();
            var mvp = _starMap.BuildMvpFloats(_app.AspectRatio);
            _starMap.Draw(mvp);
        }

        // Platform UI overlay. WASM uses HTML buttons that draw themselves;
        // desktop rasterises an EngineUI quad mesh.
        _app.RenderUI();

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

    /// <summary>
    /// Zoom level expressed as a fraction in log-altitude space —
    /// 0 = at the auto-zoom-out threshold (max zoom out), 1 = at the
    /// orbit floor (max zoom in). Same parameterization as the on-screen
    /// indicator bar so designers can tune <c>RtsCamera.TiltStartZoomPercent</c>
    /// against numbers they can see.
    /// </summary>
    private float ZoomPercent(float distance)
    {
        float radius = _planet.Mesh.Radius;
        float altitude = MathF.Max(1e-4f, distance - radius);
        float minAlt = MathF.Max(1e-4f, MinPlanetDistance() - radius);
        float maxAlt = MathF.Max(minAlt + 1e-3f, _config.PlanetEditView.AutoZoomOutThreshold - radius);
        float pct = 1f - (MathF.Log(altitude) - MathF.Log(minAlt))
                       / (MathF.Log(maxAlt) - MathF.Log(minAlt));
        return Math.Clamp(pct, 0f, 1f);
    }

    /// <summary>RTS tilt blend driven by zoom percentage rather than raw
    /// altitude — gives designers two clean tunables (start% / full%) that
    /// don't change meaning when the planet radius does.</summary>
    private float TiltBlend(float distance)
    {
        return Smoothstep(_config.RtsCamera.TiltStartZoomPercent,
                          _config.RtsCamera.TiltFullZoomPercent,
                          ZoomPercent(distance));
    }

    /// <summary>Tilted look-at target for RTS-style ground view. At high
    /// altitude returns the planet center; near the surface the target slides
    /// to a point ahead on the ground so the camera tilts forward rather than
    /// staring straight down.</summary>
    private Vector3 PlanetLookAtTarget(Vector3 camPos)
    {
        float blend = TiltBlend(camPos.Length());
        if (blend <= 0f) return Vector3.Zero;

        // Tangent at the camera pointing toward decreasing elevation — i.e.
        // "forward along the ground" for an orbit camera. Drives the look-at
        // offset so dragging tilts the view across the surface, not around it.
        float radius = _planet.Mesh.Radius;
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

        // World-up that's stable across the full tilt sweep without quaternion
        // bookkeeping. Two candidate up axes — global Y (good for orbital view
        // looking at planet center) and radial outward (good for ground-level
        // RTS view) — are weighted by their perpendicularity to forward and
        // mixed.
        //
        // The naive lerp(worldY, camDir, tiltBlend) version flipped the camera
        // mid-tilt: at intermediate blends the lerped vector ended up nearly
        // anti-parallel to forward, cross(forward, up) collapsed, the basis
        // flipped, the camera rolled 180°. A perpendicularity-weighted mix
        // can never be parallel to forward unless both candidates are — which
        // only happens if camDir is parallel to worldY (camera at a pole, and
        // _elevation is clamped well short of that).
        var fwdRaw = new Vector3(lookAt.X, lookAt.Y, lookAt.Z) - camPos;
        var fwd = fwdRaw.LengthSquared() > 1e-8f
            ? Vector3.Normalize(fwdRaw) : new Vector3(0, 0, -1);
        var camDir = camPos.LengthSquared() > 1e-8f
            ? Vector3.Normalize(camPos) : new Vector3(0, 1, 0);
        var worldY = new Vector3(0, 1, 0);

        float wY = 1f - MathF.Abs(Vector3.Dot(fwd, worldY));
        float wR = 1f - MathF.Abs(Vector3.Dot(fwd, camDir));
        // Square the weights so a fully-perpendicular axis dominates strongly
        // over a near-parallel one — keeps worldUp comfortably away from the
        // forward axis at all tilt amounts.
        wY *= wY; wR *= wR;
        float total = wY + wR + 1e-6f;
        var worldUp = (worldY * wY + camDir * wR) / total;
        if (worldUp.LengthSquared() < 1e-6f) worldUp = camDir;
        worldUp = Vector3.Normalize(worldUp);

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(camPos.X, camPos.Y, camPos.Z),
            new Vector3D<float>(lookAt.X, lookAt.Y, lookAt.Z),
            new Vector3D<float>(worldUp.X, worldUp.Y, worldUp.Z));
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
    /// Right-click handler: queue A* paths to the clicked cell for every
    /// currently-selected unit. No-op if nothing's selected or the click
    /// missed the surface.
    /// </summary>
    private void HandleMoveCommand(float canvasX, float canvasY)
    {
        if (_rtsConfig == null) return;
        if (_state.SelectedUnitInstanceIds.Count == 0) return;

        var targetCell = PickCell(canvasX, canvasY);
        if (targetCell == null) return;

        foreach (var unit in _state.SelectedUnits)
        {
            var def = _rtsConfig.GetUnit(unit.TypeId);
            if (def == null) continue;

            var path = Pathfinding.FindPath(_planet.Mesh, unit.CellIndex, targetCell.Value, def.CanHop);
            if (path == null) continue;

            unit.Path = path;
            unit.PathIndex = path.Count > 0 && path[0] == unit.CellIndex ? 1 : 0;
        }
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

    // ── Box select + context menu ─────────────────────────────────────────

    /// <summary>
    /// Update / show the translucent rectangle that marks the in-progress
    /// box selection. Reuses the EngineUI button pipeline so we don't need a
    /// dedicated overlay path — the button gets a transparent fill, no label,
    /// and pointerEvents:none so clicks on it pass straight through.
    /// </summary>
    private void UpdateBoxSelectOverlay(float x0, float y0, float x1, float y1)
    {
        float left = MathF.Min(x0, x1);
        float top  = MathF.Min(y0, y1);
        float w = MathF.Abs(x1 - x0);
        float h = MathF.Abs(y1 - y0);
        var css = "{" +
            "\"left\":\"" + (int)left + "px\",\"top\":\"" + (int)top + "px\"," +
            "\"width\":\"" + (int)w + "px\",\"height\":\"" + (int)h + "px\"," +
            "\"background\":\"rgba(80,200,120,0.18)\"," +
            "\"color\":\"rgba(120,255,160,0.85)\"," +
            "\"pointerEvents\":\"none\",\"display\":\"block\"}";
        _app.CreateUIButton("box_select", "", css);
        _app.ShowUIButton("box_select", true);
    }

    private void HideBoxSelectOverlay() => _app.ShowUIButton("box_select", false);

    /// <summary>
    /// Project every spawned unit through the current planet MVP and add any
    /// whose screen-space position falls inside the rect to the multi-select
    /// set. Replaces (doesn't append to) the prior selection.
    /// </summary>
    private void BoxSelectUnits(float x0, float y0, float x1, float y1)
    {
        if (_rts == null || _rtsConfig == null) return;
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return;

        var mvp = FloatsToMatrix(BuildPlanetMvp(w / h));

        _state.SelectedUnitInstanceIds.Clear();
        _state.SelectedBuildingInstanceId = -1;

        foreach (var unit in _state.Units)
        {
            var clip = Vector4.Transform(new Vector4(unit.SurfacePoint, 1f), mvp);
            if (clip.W <= 0.001f) continue;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;

            // Drop units behind the planet from the camera's perspective —
            // their screen position would otherwise sweep through the rect
            // and grab them through solid surface.
            var camDir = Vector3.Normalize(CameraPosition());
            if (Vector3.Dot(unit.SurfaceUp, camDir) < -0.1f) continue;

            if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1)
                _state.SelectedUnitInstanceIds.Add(unit.InstanceId);
        }

        RefreshRtsButtons();
    }

    private static Matrix4x4 FloatsToMatrix(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /// <summary>
    /// Pop up a 4-item context menu near the cursor — Cancel order / Guard /
    /// Move / Attack. No-op if there are no selected units; the right-click
    /// move-order path stays the primary way to issue moves.
    /// </summary>
    private void ShowUnitContextMenu(float x, float y)
    {
        if (_state.SelectedUnitInstanceIds.Count == 0) { HideUnitContextMenu(); return; }
        for (int i = 0; i < ContextMenuItems.Length; i++)
        {
            float top = y + i * ContextMenuItemHeight;
            var css = "{" +
                "\"left\":\"" + (int)x + "px\",\"top\":\"" + (int)top + "px\"," +
                "\"width\":\"140px\",\"height\":\"" + (int)ContextMenuItemHeight + "px\"," +
                "\"background\":\"rgba(20,25,35,0.95)\"," +
                "\"color\":\"#cde\"," +
                "\"display\":\"block\"}";
            _app.CreateUIButton($"ctx_{i}", ContextMenuItems[i], css);
            _app.ShowUIButton($"ctx_{i}", true);
        }
    }

    private void HideUnitContextMenu()
    {
        for (int i = 0; i < ContextMenuItems.Length; i++)
            _app.ShowUIButton($"ctx_{i}", false);
    }

    /// <summary>Handle a click on a ctx_N button.</summary>
    private void OnContextMenuPick(int index)
    {
        switch (index)
        {
            case 0: // Cancel order
                foreach (var u in _state.SelectedUnits) { u.Path = null; u.PathIndex = 0; }
                _pendingUnitOrder = null;
                break;
            case 1: // Guard
                // Stub: no enemy AI yet. Clear paths so the unit stays put.
                foreach (var u in _state.SelectedUnits) { u.Path = null; u.PathIndex = 0; }
                _pendingUnitOrder = null;
                break;
            case 2: // Move
                _pendingUnitOrder = "move";
                break;
            case 3: // Attack
                _pendingUnitOrder = "attack";
                break;
        }
        HideUnitContextMenu();
    }
}
