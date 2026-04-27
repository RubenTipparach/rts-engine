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
    // Five levels → four thresholds separating them
    public List<float> Thresholds { get; set; } = new() { 0.30f, 0.45f, 0.65f, 0.82f };
}

public sealed class AtmosphereConfig
{
    public float InnerRadiusMul { get; set; } = 0.92f;
    public float OuterRadiusMul { get; set; } = 1.5f;
    public float SunIntensity { get; set; } = 30.0f;
    public List<float> SunDirection { get; set; } = new() { 0.5f, 0.7f, 0.5f };
}
