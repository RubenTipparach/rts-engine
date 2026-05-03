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
    /// <summary>Heading vector along the surface (tangent), used by the
    /// renderer to face the unit's model toward its next step.</summary>
    public Vector3 Heading { get; set; }

    /// <summary>Team ownership — 0 = player, 1+ = enemies.</summary>
    public int Team { get; set; } = 0;
    public float MaxHp { get; set; } = 50f;
    public float Hp { get; set; } = 50f;
}
