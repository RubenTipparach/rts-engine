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
    public float MaxDistance { get; set; } = 8f;
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

    /// <summary>Altitude (above surface, radius units) at which the tilt
    /// blend starts fading in. Above this the camera looks at planet center.</summary>
    public float TiltStartHeight { get; set; } = 0.6f;

    /// <summary>How far ahead (along the surface, radius units) the look-at
    /// target sits when fully tilted. Tuned so that altitude/lookAhead gives
    /// roughly a 30° downward tilt at the ground floor.</summary>
    public float LookAhead { get; set; } = 0.25f;
}
