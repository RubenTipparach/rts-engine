using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtsEngine.Game;

public sealed class EngineConfig
{
    public EngineCameraConfig Camera { get; set; } = new();
    public LodConfig Lod { get; set; } = new();
    public LightingConfig Lighting { get; set; } = new();
    public SolarSystemViewConfig SolarSystemView { get; set; } = new();
    public PlanetEditViewConfig PlanetEditView { get; set; } = new();
    public RtsCameraConfig RtsCamera { get; set; } = new();
    public SlopeConfig Slopes { get; set; } = new();
    public ChamferConfig Terrain { get; set; } = new();
    public UnitArrivalConfig UnitArrival { get; set; } = new();
    public UnitMovementConfig UnitMovement { get; set; } = new();
    public DebugConfig Debug { get; set; } = new();

    public static EngineConfig FromYaml(string yaml)
    {
        var d = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        return d.Deserialize<EngineConfig>(yaml) ?? new();
    }
}

public sealed class EngineCameraConfig
{
    public float TransitionDuration { get; set; } = 1.5f;
    public float DefaultElevation { get; set; } = 0.4f;
    public float PixelsToRadians { get; set; } = 0.005f;
}

public sealed class LodConfig
{
    public float OutlineMaxDist { get; set; } = 5f;
    public float AtmosphereMaxDist { get; set; } = 20f;
    public float PlanetMaxDist { get; set; } = 50f;
    public float TransitionBlendStart { get; set; } = 0.1f;
    public float TransitionBlendEnd { get; set; } = 0.8f;
}

public sealed class LightingConfig
{
    public List<float> SunDirection { get; set; } = new() { 0.5f, 0.7f, 0.5f };
    public float AmbientIntensity { get; set; } = 0.15f;
    public float DiffuseIntensity { get; set; } = 0.85f;
}

public sealed class SolarSystemViewConfig
{
    public float DefaultDistance { get; set; } = 80f;
    public float MinDistance { get; set; } = 10f;
    public float MaxDistance { get; set; } = 200f;
    public int SphereSegmentsPlanet { get; set; } = 40;
    public int SphereSegmentsSun { get; set; } = 48;
    public int SphereSegmentsMoon { get; set; } = 24;
    public int OrbitRingSegments { get; set; } = 64;
    public int MoonOrbitSegments { get; set; } = 32;
    public float PickRadiusMultiplier { get; set; } = 3f;
}

public sealed class PlanetEditViewConfig
{
    public float DefaultDistance { get; set; } = 3f;
    public float MinDistance { get; set; } = 2f;
    public float MaxDistance { get; set; } = 200f;
    /// <summary>Distance above which the planet camera auto-glides back to
    /// solar-system view. Sits a touch above the solar-system camera's
    /// default distance so the player has to actively zoom past the comfort
    /// zone to leave.</summary>
    public float AutoZoomOutThreshold { get; set; } = 100f;
}

/// <summary>
/// Geometric chamfer applied at every land cell's perimeter. Creates a
/// small 45° bevel where cliff tops meet flat ground; concave edges (cliff
/// bases) get a faint inverse bevel as a side-effect of uniform application,
/// which reads as soft erosion rather than a hard step at typical RTS
/// camera angles. Disabled for water cells (level 0) so the sea surface
/// stays flat.
/// </summary>
public sealed class ChamferConfig
{
    /// <summary>Fraction of the way each polygon vertex is pulled toward
    /// the cell center to form the top fan's "inner ring". 0 disables
    /// the chamfer entirely. Typical: 0.10–0.20.</summary>
    public float ChamferInset { get; set; } = 0.15f;

    /// <summary>Drop in radius units that the perimeter polygon vertex
    /// sits below the cell's nominal level height — this is the height of
    /// the chamfer bevel. 0 disables the chamfer. Typical: ~0.3 × stepHeight.</summary>
    public float ChamferDrop { get; set; } = 0.012f;
}

/// <summary>
/// Procedural slope placement. Slopes ramp between adjacent cells of
/// different levels and are the primary way ground units traverse cliffs.
/// </summary>
public sealed class SlopeConfig
{
    /// <summary>Fraction of cells sitting between a strictly-lower and
    /// strictly-higher neighbor that get converted into slope ramps. The
    /// rest stay as flat-topped cliffs.</summary>
    public float Density { get; set; } = 0.45f;

    /// <summary>Independent seed offset so slope placement is stable per
    /// planet but not coupled to terrain noise. Combined with the planet's
    /// own noise seed at generation time.</summary>
    public int SeedOffset { get; set; } = 1337;
}

/// <summary>
/// Ground-level RTS camera behavior. As the player zooms toward the surface
/// the look-at target slides from the planet center to a point ahead on the
/// ground, producing a tilted, traditional-RTS view.
/// </summary>
public sealed class RtsCameraConfig
{
    /// <summary>Camera floor altitude above the surface, in radius units. The
    /// minimum orbit distance becomes radius * (1 + GroundClearance).</summary>
    public float GroundClearance { get; set; } = 0.15f;

    /// <summary>Camera tilt at full RTS view, measured as the angle from
    /// straight-down (looking at planet center) toward the horizon.
    /// 0° = always looking at planet center (no RTS tilt). 90° = looking
    /// horizontally along the surface. The default 59° corresponds to the
    /// previous design's "~30° below horizon" RTS pose
    /// (90° − 31° = 59°).</summary>
    public float MaxTiltDegrees { get; set; } = 59f;

    /// <summary>Zoom percentage (log-altitude space, 0 = max zoom out, 1 =
    /// max zoom in) at which the RTS tilt starts engaging. Below this the
    /// camera looks at planet center; above this it begins tilting to a
    /// horizon-ahead RTS view.</summary>
    public float TiltStartZoomPercent { get; set; } = 0.70f;

    /// <summary>Zoom percentage at which the RTS tilt is fully engaged.
    /// Between TiltStart and TiltFull the tilt smoothstep-lerps in.</summary>
    public float TiltFullZoomPercent { get; set; } = 1.0f;

    /// <summary>Per-scroll-delta-unit altitude change as a fraction of the
    /// current altitude. Each tick changes altitude by `delta × altitude ×
    /// scrollIncrement`, so the subjective zoom speed is uniform regardless
    /// of how close the camera is.</summary>
    public float ScrollIncrement { get; set; } = 0.002f;

    /// <summary>How fast the displayed zoom catches up to the target zoom
    /// (per second). Higher = snappier. The exponential decay gives smooth
    /// motion without explicit easing curves; rate 12 = ~98% of the way in
    /// 0.3 seconds.</summary>
    public float ZoomLerpRate { get; set; } = 12.0f;
}

/// <summary>
/// Move-order packing. When several units are ordered to the same destination
/// cell we pack up to <see cref="PerCellCapacity"/> of them into that cell
/// using sub-slot offsets in the cell's tangent plane, then spill the rest
/// into BFS-adjacent cells. Keeps groups tight instead of spreading every
/// unit onto its own hex.
/// </summary>
public sealed class UnitArrivalConfig
{
    /// <summary>Max units that share one destination cell before BFS moves
    /// to the next cell. 4 fits comfortably for current unit/cell sizing
    /// (units ~ ¼ of a cell across).</summary>
    public int PerCellCapacity { get; set; } = 4;

    /// <summary>Distance from cell center to each sub-slot anchor, expressed
    /// in unit-halfwidth multiples. 1.5 leaves enough breathing room that
    /// arrived units don't immediately re-collide under ORCA jitter.</summary>
    public float SlotSpacingMultiplier { get; set; } = 1.5f;
}

/// <summary>
/// Per-tick unit movement tuning. The pathfinder picks which cells a unit
/// crosses; this controls how its position resolves against the terrain
/// underneath as it moves through them.
/// </summary>
public sealed class UnitMovementConfig
{
    /// <summary>Per-second exponential rate at which a unit's altitude
    /// chases the surface height of the cell it's currently over. Higher =
    /// snappier altitude response (units stick to the ground tightly across
    /// slopes); lower = floatier (units take longer to settle to a new
    /// elevation). 20 ≈ ~95% convergence in 0.15 s, which reads as "walking
    /// up a ramp" rather than "snapping to each step."</summary>
    public float AltitudeLerpRate { get; set; } = 20f;

    /// <summary>Whether ground units may walk up slope cells to reach a
    /// higher elevation band. Off pins every unit to its starting band
    /// unless it's canHop-capable (infantry can still step a single level).
    /// Off by default while slope geometry/AI is being reworked; flip on
    /// in engine.yaml once slopes are ready to ship.</summary>
    public bool SlopesTraversable { get; set; } = false;
}

/// <summary>
/// Visualization toggles. Off in shipped builds; flip on in engine.yaml when
/// you need to see what the gameplay systems are doing under the hood.
/// </summary>
public sealed class DebugConfig
{
    /// <summary>Render each unit's queued path as a line strip from its
    /// current cell along the remaining waypoints to the destination, plus a
    /// small marker on the destination cell. Useful for debugging A* output
    /// and movement-system bugs.</summary>
    public bool ShowUnitPaths { get; set; } = false;
}
