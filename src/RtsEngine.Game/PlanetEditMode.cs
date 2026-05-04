using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// The actual RTS gameplay scene — terrain editing + unit/building placement
/// + selection + movement orders. Owns its hover state, queued context-menu
/// order, and frame timing for the zoom lerp + movement tick. Subscribes to
/// HUD + backend events that are PlanetEdit-specific (box select, context
/// menu, build/produce/cancel/context-pick, edit toggle) and gates them on
/// <see cref="_isActive"/> so they're only acted on while this mode is in
/// front.
/// </summary>
public sealed class PlanetEditMode : IEditorMode
{
    private readonly IRenderBackend _app;
    private readonly PlanetCamera _camera;
    private readonly PlanetPicker _picker;
    private readonly EngineHud _hud;
    private readonly RtsState _state;
    private readonly RtsConfig? _rtsConfig;
    private readonly RtsRenderer? _rts;
    private readonly SolarSystemRenderer? _solarSystem;
    private readonly EngineConfig _engineConfig;
    private readonly Func<PlanetRenderer> _planetProvider;
    private readonly Func<string?> _selectedPlanetConfigProvider;
    private readonly Func<bool> _isActive;

    /// <summary>Fires when the player asks to leave PlanetEdit (ESC / Tab /
    /// Backspace, or auto-zoom past the threshold). GameEngine handles the
    /// transition.</summary>
    public event Action? ExitRequested;

    /// <summary>Fires when the player taps another planet through the
    /// faded-in backdrop. GameEngine queues the chained planet switch and
    /// kicks off a zoom-out.</summary>
    public event Action<string>? PlanetSwitchRequested;

    private int _hoveredCell = -1;
    private string? _pendingUnitOrder;     // "move", "attack", or null
    private bool _boxSelectActive;
    private float _lastTickElapsed;
    /// <summary>Last canvas-space cursor position seen via OnMove. Cached
    /// so the per-frame tick can re-pick the cell under the cursor when
    /// the camera moves on its own (scroll-zoom lerp, transition handoff,
    /// programmatic orbit) — pointer events only fire when the cursor
    /// pixel changes, but the cell under it can slide without that.</summary>
    private float _lastPointerX = -1f, _lastPointerY = -1f;

    public bool BoxSelectActive => _boxSelectActive;

    public PlanetEditMode(IRenderBackend app, PlanetCamera camera, PlanetPicker picker,
        EngineHud hud, RtsState state, RtsConfig? rtsConfig, RtsRenderer? rts,
        SolarSystemRenderer? solarSystem, EngineConfig engineConfig,
        Func<PlanetRenderer> planetProvider, Func<string?> selectedPlanetConfigProvider,
        Func<bool> isActive)
    {
        _app = app;
        _camera = camera;
        _picker = picker;
        _hud = hud;
        _state = state;
        _rtsConfig = rtsConfig;
        _rts = rts;
        _solarSystem = solarSystem;
        _engineConfig = engineConfig;
        _planetProvider = planetProvider;
        _selectedPlanetConfigProvider = selectedPlanetConfigProvider;
        _isActive = isActive;

        // HUD events that are gameplay-specific
        _hud.EditToggled += () => _hoveredCell = -1;
        _hud.PlacementCancelClicked += () =>
        {
            if (!_isActive()) return;
            _state.PlacementBuildingId = null;
            _hud.RefreshRtsButtons();
        };
        _hud.BuildPlacementClicked += typeId =>
        {
            if (!_isActive()) return;
            // Toggle: clicking the same build button cancels placement.
            _state.PlacementBuildingId = _state.PlacementBuildingId == typeId ? null : typeId;
            _state.SelectedBuildingInstanceId = -1;
            _hud.RefreshRtsButtons();
        };
        _hud.ProduceUnitClicked += unitId =>
        {
            if (!_isActive()) return;
            ProduceUnit(unitId);
        };
        _hud.ContextMenuPicked += idx =>
        {
            if (!_isActive()) return;
            OnContextMenuPick(idx);
        };

        // Backend events that are PlanetEdit-specific
        _app.BoxSelectUpdate += (x0, y0, x1, y1) =>
        {
            if (!_isActive()) return;
            _boxSelectActive = true;
            _hud.ShowBoxSelectOverlay(x0, y0, x1, y1);
        };
        _app.BoxSelectComplete += (x0, y0, x1, y1) =>
        {
            _boxSelectActive = false;
            _hud.HideBoxSelectOverlay();
            if (!_isActive()) return;
            BoxSelectUnits(x0, y0, x1, y1);
        };
        _app.ContextMenuRequested += (x, y) =>
        {
            if (!_isActive()) return;
            if (_state.SelectedUnitInstanceIds.Count == 0) { _hud.HideContextMenu(); return; }
            _hud.ShowContextMenu(x, y);
        };
    }

    /// <summary>Called by GameEngine when the planet renderer is swapped
    /// (loading a new planet). Cell indices and selections are planet-
    /// specific so we wipe everything to a clean slate.</summary>
    public void OnPlanetSwapped()
    {
        _state.Clear();
        _hoveredCell = -1;
        _pendingUnitOrder = null;
        _hud.RefreshRtsButtons();
    }

    /// <summary>Called by GameEngine when entering PlanetEdit (post-zoom-in
    /// transition) so the hover highlight starts clean.</summary>
    public void ResetHover() => _hoveredCell = -1;

    public void OnClick(float canvasX, float canvasY, int button)
    {
        // If a "Move" / "Attack" context menu order is pending, the next
        // left-click on the world consumes it as the order's target.
        if (button == 0 && _pendingUnitOrder != null && !_hud.EditMode)
        {
            HandleMoveCommand(canvasX, canvasY);  // both move and attack route through here for now
            _pendingUnitOrder = null;
            return;
        }

        // Once orbit rings are visible, the backdrop bodies (sun + other
        // planets) are pickable too — tapping one chains a zoom-out then
        // zoom-in into that body's edit view.
        if (button == 0 && _solarSystem != null)
        {
            float ringAlpha = Smoothstep(ModeTransition.RingFadeNear, ModeTransition.RingFadeFar, _camera.Distance);
            if (ringAlpha > 0.1f)
            {
                var selectedCfg = _selectedPlanetConfigProvider();
                var selfPos = _solarSystem.GetBodyWorldPosition(selectedCfg);
                var planetMvp = _camera.BuildMvp(_app.AspectRatio);
                var (config, _) = _solarSystem.PickBackdrop(
                    canvasX, canvasY, _app.CanvasWidth, _app.CanvasHeight,
                    planetMvp, selfPos, selectedCfg, _camera.FocalY);
                if (config != null && config != selectedCfg)
                {
                    PlanetSwitchRequested?.Invoke(config);
                    return;
                }
            }
        }

        // RTS interactions take priority over terrain editing when edit
        // mode is OFF — left-click places a queued building, picks a
        // unit or building, or deselects. Right-click on a selected unit
        // issues a move command via the pathfinder.
        if (!_hud.EditMode && _rts != null)
        {
            if (button == 2)
            {
                HandleMoveCommand(canvasX, canvasY);
                return;
            }

            if (button != 0) return;

            // Picking priority while not placing: units first (they sit on
            // top of cells and are smaller, so a direct hit on a unit should
            // select the unit not its cell), then buildings via their own
            // generous pixel hitbox (cell-exact picking gets noisy under
            // heavy camera tilt — building selection used to silently fail
            // there), then a plain cell click.
            if (_state.PlacementBuildingId == null)
            {
                int unitId = _picker.PickUnit(canvasX, canvasY);
                if (unitId >= 0)
                {
                    _state.SelectedUnitInstanceIds.Clear();
                    _state.SelectedUnitInstanceIds.Add(unitId);
                    _state.SelectedBuildingInstanceId = -1;
                    _hud.RefreshRtsButtons();
                    return;
                }

                int bldgId = _picker.PickBuilding(canvasX, canvasY);
                if (bldgId >= 0)
                {
                    _state.SelectedBuildingInstanceId = bldgId;
                    _state.SelectedUnitInstanceIds.Clear();
                    _hud.RefreshRtsButtons();
                    return;
                }
            }

            var cell = _picker.PickCell(canvasX, canvasY);
            if (cell == null)
            {
                if (_state.SelectedBuildingInstanceId != -1
                    || _state.SelectedUnitInstanceIds.Count > 0
                    || _state.PlacementBuildingId != null)
                {
                    _state.SelectedBuildingInstanceId = -1;
                    _state.SelectedUnitInstanceIds.Clear();
                    _state.PlacementBuildingId = null;
                    _hud.RefreshRtsButtons();
                }
                return;
            }

            if (_state.PlacementBuildingId != null)
            {
                if (_state.BuildingAtCell(cell.Value) == null)
                    _state.PlaceBuilding(_state.PlacementBuildingId, cell.Value);
                _state.PlacementBuildingId = null;
                _hud.RefreshRtsButtons();
                return;
            }

            if (_state.SelectedBuildingInstanceId != -1 || _state.SelectedUnitInstanceIds.Count > 0)
            {
                _state.SelectedBuildingInstanceId = -1;
                _state.SelectedUnitInstanceIds.Clear();
                _hud.RefreshRtsButtons();
            }
            return;
        }

        // Cells only respond to taps when the player has turned editing on.
        if (!_hud.EditMode) return;
        var editCell = _picker.PickCell(canvasX, canvasY);
        if (editCell == null) return;
        var planet = _planetProvider();
        if (button == 0) planet.Mesh.ChangeLevel(editCell.Value, +1);
        else if (button == 2) planet.Mesh.ChangeLevel(editCell.Value, -1);
        planet.MarkDirty(editCell.Value);
    }

    public void OnMove(float canvasX, float canvasY)
    {
        // Just record where the cursor is. The actual cell + unit + building
        // pick runs every frame in RenderTick so it tracks the surface
        // correctly when the camera moves under a stationary pointer (scroll
        // zoom, smooth zoom lerp, transitions).
        _lastPointerX = canvasX;
        _lastPointerY = canvasY;
    }

    public void OnKey(string key)
    {
        // Tab / Backspace = leave the planet, back to the solar system view.
        // Escape = cancel the current in-progress action (placement, pending
        // unit order, selection). It only ever exits to the solar system if
        // there's nothing left to cancel — and even then, not at all here:
        // exiting on plain Esc is too easy to do accidentally mid-build.
        if (key == "Tab" || key == "Backspace")
        {
            ExitRequested?.Invoke();
            return;
        }
        if (key == "Escape")
        {
            if (_state.PlacementBuildingId != null)
            {
                _state.PlacementBuildingId = null;
                _hud.RefreshRtsButtons();
            }
            else if (_pendingUnitOrder != null)
            {
                _pendingUnitOrder = null;
            }
            else if (_state.SelectedBuildingInstanceId != -1 || _state.SelectedUnitInstanceIds.Count > 0)
            {
                _state.SelectedBuildingInstanceId = -1;
                _state.SelectedUnitInstanceIds.Clear();
                _hud.RefreshRtsButtons();
            }
        }
    }

    public async Task RenderTick(float elapsed)
    {
        // Pulled too far out → glide back to the solar system view.
        // Picked a touch above the planet view's solar-system distance (80)
        // so the user has to actively zoom past the comfort zone to leave.
        if (_camera.Distance > _camera.AutoZoomOutThreshold)
        {
            ExitRequested?.Invoke();
            return;
        }

        var planet = _planetProvider();
        await planet.RebuildDirtyPatches();

        // Smooth zoom: chase the scroll-set target with an exponential
        // decay. Rate is per-second, so dt-corrected — feels identical
        // at 30 fps, 60 fps, or a stutter-recovery 120 fps tick. A clamped
        // dt prevents huge frame gaps from snapping the camera.
        float zoomDt = MathF.Min(0.05f, elapsed - _lastTickElapsed);
        _camera.Update(zoomDt);

        var cam = _camera.Position();
        planet.SetCameraPosition(cam.X, cam.Y, cam.Z);
        planet.SetTime(elapsed);

        // Re-pick the cell under the (cached) cursor every frame, after the
        // camera has been advanced. This is the load-bearing fix for "ghost
        // sticks to the wrong cell" — pointer events only fire when the
        // cursor pixel changes, but the cell under a stationary pointer
        // slides whenever the camera moves (scroll-zoom lerp, drag-orbit,
        // transitions). Picking lives here so it always sees the current
        // MVP. PickUnit / BuildingAtCell ride along for the HP bar hover.
        if (_lastPointerX >= 0f && _lastPointerY >= 0f)
        {
            _hoveredCell = _picker.PickCell(_lastPointerX, _lastPointerY) ?? -1;
            _state.HoveredUnitInstanceId = _picker.PickUnit(_lastPointerX, _lastPointerY);
            // Use the same generous building hitbox for hover that we use
            // for click — falls back to BuildingAtCell only if the screen-
            // space pick misses, which keeps it consistent with selection
            // behaviour and lets the HP bar show on hover at high tilt.
            int hoveredBldg = _picker.PickBuilding(_lastPointerX, _lastPointerY);
            if (hoveredBldg < 0 && _hoveredCell >= 0)
                hoveredBldg = _state.BuildingAtCell(_hoveredCell)?.InstanceId ?? -1;
            _state.HoveredBuildingInstanceId = hoveredBldg;
        }

        // Hex outline highlight only shows up while terrain editing — keeps
        // the planet looking clean during normal RTS play.
        planet.SetHighlightCell(_hud.EditMode ? _hoveredCell : -1);
        await planet.SyncOutline();

        // Advance any units that have a path queued. Frame-bounded dt so
        // a stutter (long await above) doesn't teleport units forward.
        if (_rts != null && _rtsConfig != null)
        {
            if (zoomDt > 0f) MovementSystem.Tick(_state, planet.Mesh, _rtsConfig, _engineConfig, zoomDt);
        }
        _lastTickElapsed = elapsed;

        // Sync lighting to the planet's solar-system position: the sun is at
        // the solar-system origin, so the direction toward the sun (relative
        // to a planet at world position P) is -P/|P|.
        var selectedCfg = _selectedPlanetConfigProvider();
        Vector3 selfPos = Vector3.Zero;
        Vector3 cameraWorldPos = cam;
        if (_solarSystem != null)
        {
            _solarSystem.SetTime(elapsed);
            selfPos = _solarSystem.GetBodyWorldPosition(selectedCfg);
            if (selfPos.LengthSquared() > 1e-6f)
            {
                var sunDir = -Vector3.Normalize(selfPos);
                planet.SetSunDirection(sunDir.X, sunDir.Y, sunDir.Z);
            }
            // The planet view's camera lives at `cam` in planet-local space
            // (origin = planet center). In solar-system world coords that's
            // `selfPos + cam`. The backdrop shader needs world-space camera.
            cameraWorldPos = selfPos + cam;
        }

        var planetMvp = _camera.BuildMvp(_app.AspectRatio);

        // Starfield first — clears the framebuffer and lays down the
        // procedural sky behind the focused planet. Without solar system
        // (e.g. desktop build), the planet itself has to clear instead so
        // the previous frame doesn't bleed through.
        if (_solarSystem != null)
        {
            var fwd = -Vector3.Normalize(cam);
            var right = Vector3.Normalize(Vector3.Cross(fwd, new Vector3(0, 1, 0)));
            var up = Vector3.Cross(right, fwd);
            _solarSystem.DrawStarfield(fwd, right, up, _camera.FovYDegrees, _app.AspectRatio);
        }

        planet.Draw(planetMvp, _camera.Distance, clearFirst: _solarSystem == null);

        // Buildings and units sit on the planet surface, in the same local
        // space as the terrain mesh. Reuse the planet MVP so they line up
        // exactly with the cells they're placed on.
        if (_rts != null)
        {
            _rts.SetSunDirection(selfPos.LengthSquared() > 1e-6f
                ? -Vector3.Normalize(selfPos) : new Vector3(0.5f, 0.7f, 0.5f));
            _rts.Draw(_state, planet.Mesh, planetMvp, cam, _app.CanvasWidth, _app.CanvasHeight, _hoveredCell);
        }

        // Orbit rings start invisible up close, fade in as we zoom out so
        // the player can see the orbital structure before the auto-trigger
        // pulls them back to solar-system view. Reaches full alpha just
        // below the auto-trigger so the handoff doesn't pop.
        float ringAlpha = Smoothstep(ModeTransition.RingFadeNear, ModeTransition.RingFadeFar, _camera.Distance);

        // Render the rest of the solar system (sun + other planets) as a
        // backdrop, with the world shifted so the focused planet is at origin.
        _solarSystem?.DrawBackdrop(planetMvp, selectedCfg, selfPos, cameraWorldPos, ringAlpha);
    }

    private void ProduceUnit(string unitId)
    {
        if (_rtsConfig == null) return;
        var b = _state.SelectedBuilding;
        if (b == null) return;
        var def = _rtsConfig.GetBuilding(b.TypeId);
        if (def == null || !def.Produces.Contains(unitId)) return;

        var planet = _planetProvider();
        var mesh = planet.Mesh;
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

    /// <summary>Right-click handler: distribute selected units across arrival
    /// cells around the click target. Each unit type's PerCellCapacity sets
    /// how many fit per hex; the moving group packs at the most-restrictive
    /// member's capacity (so a mixed marine+tank order packs at vehicle
    /// density). Existing units already standing at a destination cell are
    /// counted, and the new arrivals start at the first un-taken sub-slot
    /// so reinforcements don't snap onto the same anchor as the units
    /// already there.</summary>
    private void HandleMoveCommand(float canvasX, float canvasY)
    {
        if (_rtsConfig == null) return;
        if (_state.SelectedUnitInstanceIds.Count == 0) return;

        var targetCell = _picker.PickCell(canvasX, canvasY);
        if (targetCell == null) return;

        var planet = _planetProvider();
        var movingUnits = _state.SelectedUnits
            .Where(u => _rtsConfig.GetUnit(u.TypeId) != null)
            .ToList();
        if (movingUnits.Count == 0) return;

        // Group capacity = MIN over the moving units' per-type caps. A pure
        // infantry order packs 8/cell; a mixed group with one tank drops
        // to vehicle density (4/cell) so the tank has room.
        int capacity = movingUnits.Min(u => _rtsConfig.GetUnit(u.TypeId)?.PerCellCapacity ?? 4);
        capacity = Math.Max(1, capacity);

        var movingIds = new HashSet<int>(movingUnits.Select(u => u.InstanceId));
        var slots = PickArrivalSlots(planet.Mesh, targetCell.Value,
            movingUnits.Count, capacity, movingIds);
        if (slots.Count == 0) return;

        // Greedy nearest-unit assignment per slot. The sub-slot index in
        // each (cell, subSlot) pair is already chosen by PickArrivalSlots
        // to skip occupied positions; ComputeArrivalAnchor turns it into a
        // world-space anchor on a ring sized by the group capacity.
        var pool = new List<SpawnedUnit>(movingUnits);
        foreach (var (cell, subSlot) in slots)
        {
            if (pool.Count == 0) break;

            var slotPos = planet.Mesh.GetCellCenter(cell);
            int bestIdx = 0;
            float bestDist = Vector3.DistanceSquared(pool[0].SurfacePoint, slotPos);
            for (int i = 1; i < pool.Count; i++)
            {
                float d = Vector3.DistanceSquared(pool[i].SurfacePoint, slotPos);
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
            var unit = pool[bestIdx];
            pool.RemoveAt(bestIdx);

            var def = _rtsConfig.GetUnit(unit.TypeId)!;
            bool slopes = _engineConfig.UnitMovement.SlopesTraversable;
            var rawPath = Pathfinding.FindPath(planet.Mesh, unit.CellIndex, cell, def.CanHop, slopes);
            if (rawPath == null) continue;
            // Any-angle string-pull on the cell-graph A* output. In open
            // ground this collapses to [start, goal], so units trace a
            // single great-circle arc instead of zigzagging hex-to-hex;
            // ORCA still resolves contention with other units, and the
            // raw cell sequence reappears only where terrain forces a
            // detour around an obstacle.
            var path = Pathfinding.SmoothPath(planet.Mesh, rawPath, def.CanHop, slopes);
            unit.Path = path;
            unit.PathIndex = path.Count > 0 && path[0] == unit.CellIndex ? 1 : 0;
            unit.FinalArrivalPos = ComputeArrivalAnchor(planet.Mesh, cell, subSlot, capacity, def.HalfWidth);
        }
    }

    /// <summary>BFS outward from <paramref name="target"/> producing
    /// (cell, sub-slot) arrival slots. Each cell can hold up to
    /// <paramref name="capacity"/> units total — pre-existing units that
    /// aren't part of the moving group occupy the low sub-slot indices,
    /// new arrivals fill the next contiguous indices, and BFS spills to
    /// neighbors once a cell is full. Cells with buildings on them are
    /// skipped (units can't share a hex with a structure) but their
    /// neighbors are still visited so the expansion routes around them.</summary>
    private List<(int Cell, int SubSlot)> PickArrivalSlots(PlanetMesh mesh, int target,
        int count, int capacity, HashSet<int> movingIds)
    {
        var slots = new List<(int, int)>(count);
        var visited = new HashSet<int> { target };
        var queue = new Queue<int>();
        queue.Enqueue(target);
        while (slots.Count < count && queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (_state.BuildingAtCell(c) == null)
            {
                int existing = CountResidentsAt(c, movingIds);
                int free = Math.Max(0, capacity - existing);
                int packs = Math.Min(free, count - slots.Count);
                for (int i = 0; i < packs; i++) slots.Add((c, existing + i));
            }
            foreach (var n in mesh.GetNeighbors(c))
                if (visited.Add(n)) queue.Enqueue(n);
        }
        return slots;
    }

    /// <summary>Count units presently sitting at <paramref name="cell"/>
    /// that are NOT part of the current move order. The result is treated
    /// as the number of low-index sub-slots already taken — an
    /// approximation, since we don't track each unit's actual sub-slot,
    /// but a useful one because new arrivals get the next contiguous
    /// indices and so end up at distinct ring angles from the residents.
    /// MovementSystem's live cell tracker keeps CellIndex accurate even
    /// when units have drifted under ORCA contention.</summary>
    private int CountResidentsAt(int cell, HashSet<int> movingIds)
    {
        int n = 0;
        foreach (var u in _state.Units)
            if (u.CellIndex == cell && !movingIds.Contains(u.InstanceId)) n++;
        return n;
    }

    /// <summary>World-space arrival anchor for a unit packing into a hex
    /// at <paramref name="subSlotIndex"/> on a ring sized for
    /// <paramref name="capacity"/> total units. The ring radius scales
    /// with capacity so adjacent anchors stay at least
    /// (halfwidth × spacingMultiplier) apart along the chord — capacity-4
    /// rings are tight, capacity-8 rings widen out to keep that minimum
    /// chord. A small angular phase keeps the pattern from aligning with
    /// the cell's hex edges. Tangent basis is an arbitrary stable choice;
    /// the basis only affects which screen direction is "slot 0," not the
    /// packing quality.</summary>
    private Vector3 ComputeArrivalAnchor(PlanetMesh mesh, int cell, int subSlotIndex,
        int capacity, float halfWidth)
    {
        Vector3 up = mesh.GetCellCenter(cell);
        Vector3 cellPos = up * (mesh.Radius + mesh.GetLevel(cell) * mesh.StepHeight + 0.002f);
        if (capacity <= 0 || subSlotIndex < 0) return cellPos;

        Vector3 worldUp = new(0, 1, 0);
        Vector3 right = Vector3.Cross(worldUp, up);
        if (right.LengthSquared() < 1e-5f) right = new Vector3(1, 0, 0);
        right = Vector3.Normalize(right);
        Vector3 fwd = Vector3.Normalize(Vector3.Cross(up, right));

        // chord = 2R sin(π/N); want chord ≥ spacing → R ≥ spacing/sin(π/N).
        float spacing = halfWidth * mesh.Radius * _engineConfig.UnitArrival.SlotSpacingMultiplier;
        float ringR = spacing / MathF.Max(1e-3f, MathF.Sin(MathF.PI / capacity));
        float angle = (subSlotIndex + 0.5f) * MathF.Tau / capacity;
        float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
        return cellPos + (right * dx + fwd * dy) * ringR;
    }

    /// <summary>Replace (don't append to) the multi-select set with every unit
    /// whose screen-space position falls inside the rect.</summary>
    private void BoxSelectUnits(float x0, float y0, float x1, float y1)
    {
        if (_rts == null || _rtsConfig == null) return;

        _state.SelectedUnitInstanceIds.Clear();
        _state.SelectedBuildingInstanceId = -1;
        foreach (var id in _picker.PickUnitsInRect(x0, y0, x1, y1))
            _state.SelectedUnitInstanceIds.Add(id);

        _hud.RefreshRtsButtons();
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
        _hud.HideContextMenu();
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
