using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// In-memory RTS state for a single planet — what's been placed and what's
/// currently selected. No resource model yet; clicking a build button puts
/// the engine in placement mode, clicking a built cell selects, clicking a
/// produce button spawns a unit. Persistence is out of scope for now.
/// </summary>
public sealed class RtsState
{
    private int _nextBuildingId = 1;
    private int _nextUnitId = 1;

    public List<PlacedBuilding> Buildings { get; } = new();
    public List<SpawnedUnit> Units { get; } = new();

    /// <summary>Building id queued for placement on next cell click. null = no
    /// active build mode.</summary>
    public string? PlacementBuildingId { get; set; }

    /// <summary>Currently selected building, or -1.</summary>
    public int SelectedBuildingInstanceId { get; set; } = -1;

    /// <summary>Currently selected unit instance ids. Mutually exclusive with
    /// the building selection — picking one clears the other. A single-click
    /// fills this with one id; a box select fills it with all units inside
    /// the rect.</summary>
    public HashSet<int> SelectedUnitInstanceIds { get; } = new();

    /// <summary>Entity currently under the mouse cursor, or -1. Drives the
    /// on-hover HP bar overlay alongside selected entities — set by
    /// PlanetEditMode.OnMove via the picker.</summary>
    public int HoveredUnitInstanceId { get; set; } = -1;
    public int HoveredBuildingInstanceId { get; set; } = -1;

    /// <summary>Convenience: first selected unit id, or -1.</summary>
    public int SelectedUnitInstanceId =>
        SelectedUnitInstanceIds.Count > 0 ? SelectedUnitInstanceIds.First() : -1;

    public PlacedBuilding? SelectedBuilding =>
        Buildings.FirstOrDefault(b => b.InstanceId == SelectedBuildingInstanceId);

    public SpawnedUnit? SelectedUnit =>
        SelectedUnitInstanceIds.Count > 0
            ? Units.FirstOrDefault(u => u.InstanceId == SelectedUnitInstanceIds.First())
            : null;

    public IEnumerable<SpawnedUnit> SelectedUnits =>
        Units.Where(u => SelectedUnitInstanceIds.Contains(u.InstanceId));

    public PlacedBuilding? BuildingAtCell(int cellIndex) =>
        Buildings.FirstOrDefault(b => b.CellIndex == cellIndex);

    public PlacedBuilding PlaceBuilding(string typeId, int cellIndex)
    {
        var b = new PlacedBuilding
        {
            InstanceId = _nextBuildingId++,
            TypeId = typeId,
            CellIndex = cellIndex,
        };
        Buildings.Add(b);
        return b;
    }

    public SpawnedUnit SpawnUnit(string typeId, Vector3 surfacePoint, Vector3 surfaceUp)
    {
        var u = new SpawnedUnit
        {
            InstanceId = _nextUnitId++,
            TypeId = typeId,
            SurfacePoint = surfacePoint,
            SurfaceUp = surfaceUp,
        };
        Units.Add(u);
        return u;
    }

    public void Clear()
    {
        Buildings.Clear();
        Units.Clear();
        PlacementBuildingId = null;
        SelectedBuildingInstanceId = -1;
        SelectedUnitInstanceIds.Clear();
    }
}

public sealed class PlacedBuilding
{
    public int InstanceId { get; set; }
    public string TypeId { get; set; } = "";
    public int CellIndex { get; set; }
    /// <summary>Lifetime counter for units this building has spawned. Drives
    /// the ring-offset pattern so consecutive spawns don't stack on top of
    /// each other.</summary>
    public int UnitsSpawned { get; set; }
    /// <summary>Team ownership — 0 = player, 1+ = enemies. Drives livery
    /// (texture team-color swap) and friendly-vs-hostile selection rules.</summary>
    public int Team { get; set; } = 0;
    /// <summary>Hit points. Buildings start at MaxHp and don't regenerate.</summary>
    public float MaxHp { get; set; } = 200f;
    public float Hp { get; set; } = 200f;
}

public sealed class SpawnedUnit
{
    public int InstanceId { get; set; }
    public string TypeId { get; set; } = "";
    /// <summary>Position on (or just above) the planet surface, in
    /// planet-local space (planet center = origin).</summary>
    public Vector3 SurfacePoint { get; set; }
    /// <summary>Outward radial unit vector at the unit's position. Used by
    /// the renderer to orient the unit so its base sits flat on the ground.</summary>
    public Vector3 SurfaceUp { get; set; }
    /// <summary>The cell the unit currently occupies. Updated by the
    /// movement system as the unit advances along its path.</summary>
    public int CellIndex { get; set; }

    /// <summary>Pathfinder result. Null/empty when stationary; otherwise a
    /// list of cell indices ending at the goal. <see cref="PathIndex"/> is
    /// the next cell to step toward.</summary>
    public List<int>? Path { get; set; }
    public int PathIndex { get; set; }

    /// <summary>Optional sub-cell anchor for the final waypoint, in world
    /// coordinates. When set, the unit aims at this exact point on its last
    /// path step instead of the destination cell's geometric center —
    /// that's how multi-unit move orders pack several units into one hex
    /// (each gets a different anchor inside the same cell). Null = use the
    /// cell center as before.</summary>
    public Vector3? FinalArrivalPos { get; set; }
    /// <summary>Heading vector along the surface (tangent), used by the
    /// renderer to face the unit's model toward its next step.</summary>
    public Vector3 Heading { get; set; }

    /// <summary>Current velocity in world coordinates (radius-units per
    /// second). Maintained by <see cref="MovementSystem"/>: at the start of
    /// each tick the desired path-following velocity is computed, ORCA picks
    /// a collision-free velocity closest to it, and that velocity is both
    /// integrated into <see cref="SurfacePoint"/> and stored here so other
    /// agents can see this unit's motion when they run their own ORCA pass.</summary>
    public Vector3 Velocity { get; set; }

    /// <summary>Team ownership — 0 = player, 1+ = enemies.</summary>
    public int Team { get; set; } = 0;
    public float MaxHp { get; set; } = 50f;
    public float Hp { get; set; } = 50f;
}
