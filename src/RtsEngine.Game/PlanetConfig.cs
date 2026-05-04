using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtsEngine.Game;

/// <summary>
/// Data-driven planet definition loaded from YAML. Everything the engine
/// needs to instantiate a planet lives here — geometry, textures, atmosphere,
/// terrain generation parameters. New planets = new YAML file, no code changes.
/// </summary>
public sealed class PlanetConfig
{
    public string Name { get; set; } = "Planet";
    public float Radius { get; set; } = 1.0f;
    public int Subdivisions { get; set; } = 4;
    public float StepHeight { get; set; } = 0.04f;

    public TerrainConfig Terrain { get; set; } = new();
    public WaterConfig Water { get; set; } = new();
    public GenerationConfig Generation { get; set; } = new();
    public AtmosphereConfig Atmosphere { get; set; } = new();
    public CameraConfig Camera { get; set; } = new();

    public static PlanetConfig FromYaml(string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<PlanetConfig>(yamlText) ?? new PlanetConfig();
    }
}

public sealed class TerrainConfig
{
    public string AtlasUrl { get; set; } = "textures/terrain_atlas.png";
    public List<LevelConfig> Levels { get; set; } = new();

    /// <summary>When true, level-0 cells render through the wave-water
    /// shader (Fresnel + DuDv distortion + specular). When false, level 0
    /// just samples its atlas tile like any other terrain — used for
    /// planets whose lowest tier is solid ground (Mars canyon, Venus
    /// lowland, Moon crater_floor) or frozen ocean (Glacius). Only Earth
    /// flips this on by default.</summary>
    public bool OceanLevel0 { get; set; } = false;
}

public sealed class LevelConfig
{
    public string Name { get; set; } = "";
    public List<float> Color { get; set; } = new();
}

public sealed class WaterConfig
{
    public string DuDvUrl { get; set; } = "textures/water_dudv.png";
    public string NormalUrl { get; set; } = "textures/water_normal.png";
}

public sealed class GenerationConfig
{
    public int Seed { get; set; } = 42;
    public float Frequency { get; set; } = 2.5f;
    // Six levels → five thresholds separating them, applied to noise ∈ [0,1].
    // Default palette: water, sand, grass×2, rock, snow. Earth-tuned (high
    // t0 keeps roughly half the surface ocean).
    public List<float> Thresholds { get; set; } = new()
    {
        0.45f, 0.52f, 0.65f, 0.78f, 0.88f
    };
}

public sealed class AtmosphereConfig
{
    public float InnerRadiusMul { get; set; } = 0.92f;
    public float OuterRadiusMul { get; set; } = 1.5f;
    public float SunIntensity { get; set; } = 30.0f;
    public List<float> SunDirection { get; set; } = new() { 0.5f, 0.7f, 0.5f };
}

public sealed class CameraConfig
{
    public float ZoomMin { get; set; } = 2.0f;
    public float ZoomMax { get; set; } = 8.0f;
    public float DefaultDistance { get; set; } = 3.0f;
}
