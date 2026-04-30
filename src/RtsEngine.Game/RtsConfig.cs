using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtsEngine.Game;

/// <summary>
/// Data-driven RTS gameplay definitions. Buildings live on hex cells and
/// produce units; units spawn next to their building. Sizes are in planet
/// radius units so they scale uniformly with the world. New buildings or
/// units = new entry here, no code changes.
/// </summary>
public sealed class RtsConfig
{
    public List<BuildingDef> Buildings { get; set; } = new();
    public List<UnitDef> Units { get; set; } = new();

    public static RtsConfig FromYaml(string yaml)
    {
        var d = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return d.Deserialize<RtsConfig>(yaml) ?? new RtsConfig();
    }

    public BuildingDef? GetBuilding(string id) =>
        Buildings.FirstOrDefault(b => b.Id == id);

    public UnitDef? GetUnit(string id) =>
        Units.FirstOrDefault(u => u.Id == id);
}

public sealed class BuildingDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<float> Color { get; set; } = new() { 0.7f, 0.7f, 0.7f };
    public float HalfWidth { get; set; } = 0.03f;
    public float Height { get; set; } = 0.05f;
    /// <summary>Unit ids this building can produce, in display order.</summary>
    public List<string> Produces { get; set; } = new();
}

public sealed class UnitDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<float> Color { get; set; } = new() { 0.5f, 0.7f, 0.5f };
    public float HalfWidth { get; set; } = 0.01f;
    public float Height { get; set; } = 0.018f;
    /// <summary>Surface speed in radius units per second.</summary>
    public float Speed { get; set; } = 0.04f;
    /// <summary>Infantry-style mobility — can hop a single elevation step
    /// between adjacent cells without needing a slope.</summary>
    public bool CanHop { get; set; } = false;
}
