using System.Numerics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtsEngine.Game;

public sealed class SolarSystemData
{
    public string Name { get; set; } = "Sol";
    public Vector3 SunColor { get; set; } = new(1.0f, 0.95f, 0.8f);
    public float SunRadius { get; set; } = 3.0f;
    public List<OrbitalBody> Planets { get; } = new();

    /// <summary>
    /// Parse a solarsystem.yaml file into a populated <see cref="SolarSystemData"/>.
    /// All orbital structure (orbits, display radii, sun size, noise params)
    /// comes from the YAML; per-planet terrain LevelColors come from each
    /// planet's own configFile via <paramref name="lookupPlanetYaml"/> when
    /// provided, falling back to <see cref="PlanetMesh.LevelColors"/>
    /// (Earth-like) if the lookup is null or returns null.
    /// </summary>
    public static SolarSystemData FromYaml(string yaml, Func<string, string?>? lookupPlanetYaml = null)
    {
        var d = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        var raw = d.Deserialize<YamlRoot>(yaml);
        if (raw == null) return CreateDefault();

        var sys = new SolarSystemData
        {
            Name = raw.Sun?.Name ?? "Sol",
            SunColor = ToVec3(raw.Sun?.Color, new Vector3(1f, 0.95f, 0.8f)),
            SunRadius = raw.Sun?.Radius ?? 3f,
        };
        if (raw.Planets != null)
            foreach (var p in raw.Planets)
                sys.Planets.Add(BuildBody(p, lookupPlanetYaml));
        return sys;
    }

    private static OrbitalBody BuildBody(YamlPlanet p, Func<string, string?>? lookupPlanetYaml)
    {
        var body = new OrbitalBody
        {
            Name = p.Name ?? "",
            ConfigFile = p.ConfigFile ?? "",
            OrbitRadius = p.OrbitRadius,
            OrbitSpeed = p.OrbitSpeed,
            Phase = p.Phase,
            DisplayRadius = p.DisplayRadius,
            Color = ToVec3(p.Color, Vector3.One),
            NoiseSeed = p.NoiseSeed,
            NoiseFrequency = p.NoiseFrequency,
            NoiseThresholds = p.NoiseThresholds?.ToArray()
                ?? new[] { 0.45f, 0.52f, 0.65f, 0.78f, 0.88f },
        };

        // Resolve LevelColors from the planet's own config file if we have a
        // lookup. Lets Glacius get ice colors and Mars get red rock without
        // duplicating them inside solarsystem.yaml.
        if (lookupPlanetYaml != null && !string.IsNullOrEmpty(p.ConfigFile))
        {
            var planetYaml = lookupPlanetYaml(p.ConfigFile);
            if (planetYaml != null)
            {
                var levels = ExtractLevelColors(planetYaml);
                if (levels != null) body.LevelColors = levels;
            }
        }

        if (p.Moons != null)
            foreach (var m in p.Moons)
                body.Moons.Add(BuildBody(m, lookupPlanetYaml));
        return body;
    }

    private static Vector3[]? ExtractLevelColors(string planetYaml)
    {
        // Reuse PlanetConfig.FromYaml — it already pulls terrain.levels.
        try
        {
            var cfg = PlanetConfig.FromYaml(planetYaml);
            if (cfg.Terrain?.Levels == null || cfg.Terrain.Levels.Count == 0) return null;
            var arr = new Vector3[cfg.Terrain.Levels.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                var c = cfg.Terrain.Levels[i].Color;
                arr[i] = c != null && c.Count >= 3 ? new Vector3(c[0], c[1], c[2]) : Vector3.One;
            }
            return arr;
        }
        catch { return null; }
    }

    private static Vector3 ToVec3(List<float>? c, Vector3 fallback)
        => c != null && c.Count >= 3 ? new Vector3(c[0], c[1], c[2]) : fallback;

    private sealed class YamlRoot
    {
        public YamlSun? Sun { get; set; }
        public List<YamlPlanet>? Planets { get; set; }
    }
    private sealed class YamlSun
    {
        public string? Name { get; set; }
        public List<float>? Color { get; set; }
        public float Radius { get; set; } = 3f;
    }
    private sealed class YamlPlanet
    {
        public string? Name { get; set; }
        public string? ConfigFile { get; set; }
        public float OrbitRadius { get; set; }
        public float OrbitSpeed { get; set; }
        public float Phase { get; set; }
        public float DisplayRadius { get; set; } = 1f;
        public List<float>? Color { get; set; }
        public int NoiseSeed { get; set; } = 42;
        public float NoiseFrequency { get; set; } = 2.5f;
        public List<float>? NoiseThresholds { get; set; }
        public List<YamlPlanet>? Moons { get; set; }
    }

    public static SolarSystemData CreateDefault()
    {
        var sys = new SolarSystemData { Name = "Sol" };
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Venus", ConfigFile = "planets/venus.yaml",
            OrbitRadius = 15f, OrbitSpeed = 0.3f, Phase = 0.8f,
            DisplayRadius = 0.8f, Color = new(0.85f, 0.70f, 0.35f), NoiseSeed = 999,
            NoiseFrequency = 2.2f,
            NoiseThresholds = new[]{ 0.15f, 0.30f, 0.50f, 0.68f, 0.85f },
            LevelColors = new[]
            {
                new Vector3(0.42f,0.30f,0.12f),  // 0 lowland
                new Vector3(0.78f,0.56f,0.22f),  // 1 volcanic
                new Vector3(0.82f,0.66f,0.32f),  // 2 tessera
                new Vector3(0.68f,0.50f,0.20f),  // 3 plateau
                new Vector3(0.38f,0.24f,0.10f),  // 4 shield
                new Vector3(0.92f,0.86f,0.50f),  // 5 maxwell
            },
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Earth", ConfigFile = "planets/earth.yaml",
            OrbitRadius = 25f, OrbitSpeed = 0.2f, Phase = 0f,
            DisplayRadius = 1.0f, Color = new(0.3f, 0.6f, 0.9f), NoiseSeed = 42,
            NoiseFrequency = 2.5f,
            NoiseThresholds = new[]{ 0.45f, 0.52f, 0.65f, 0.78f, 0.88f },
            LevelColors = new[]
            {
                new Vector3(0.15f,0.35f,0.75f),  // 0 water
                new Vector3(0.90f,0.80f,0.55f),  // 1 sand
                new Vector3(0.30f,0.65f,0.25f),  // 2 grass (meadow)
                new Vector3(0.55f,0.65f,0.30f),  // 3 grass_dry (savanna)
                new Vector3(0.55f,0.55f,0.55f),  // 4 rock
                new Vector3(0.95f,0.97f,1.00f),  // 5 snow
            },
            Moons =
            {
                new OrbitalBody
                {
                    Name = "Moon", ConfigFile = "planets/moon.yaml",
                    OrbitRadius = 3f, OrbitSpeed = 1.2f, Phase = 0f,
                    DisplayRadius = 0.3f, Color = new(0.7f, 0.7f, 0.7f), NoiseSeed = 77,
                    NoiseFrequency = 3.0f,
                    NoiseThresholds = new[]{ 0.20f, 0.35f, 0.55f, 0.72f, 0.88f },
                    LevelColors = new[]
                    {
                        new Vector3(0.18f,0.18f,0.18f),  // 0 crater_floor
                        new Vector3(0.36f,0.35f,0.34f),  // 1 regolith
                        new Vector3(0.52f,0.52f,0.52f),  // 2 highland
                        new Vector3(0.45f,0.43f,0.40f),  // 3 ridge
                        new Vector3(0.62f,0.62f,0.62f),  // 4 mountain
                        new Vector3(0.78f,0.78f,0.78f),  // 5 peak
                    },
                }
            }
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Mars", ConfigFile = "planets/mars.yaml",
            OrbitRadius = 38f, OrbitSpeed = 0.15f, Phase = 2.1f,
            DisplayRadius = 0.6f, Color = new(0.85f, 0.4f, 0.2f), NoiseSeed = 1337,
            NoiseFrequency = 2.8f,
            NoiseThresholds = new[]{ 0.20f, 0.35f, 0.50f, 0.68f, 0.85f },
            LevelColors = new[]
            {
                new Vector3(0.36f,0.15f,0.10f),  // 0 canyon
                new Vector3(0.72f,0.36f,0.18f),  // 1 dust
                new Vector3(0.78f,0.48f,0.28f),  // 2 plains
                new Vector3(0.65f,0.32f,0.20f),  // 3 mesa
                new Vector3(0.32f,0.25f,0.22f),  // 4 basalt
                new Vector3(0.90f,0.92f,0.96f),  // 5 ice_cap
            },
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Glacius", ConfigFile = "planets/ice.yaml",
            OrbitRadius = 55f, OrbitSpeed = 0.08f, Phase = 4.2f,
            DisplayRadius = 0.7f, Color = new(0.7f, 0.85f, 0.95f), NoiseSeed = 5555,
            NoiseFrequency = 2.0f,
            NoiseThresholds = new[]{ 0.40f, 0.50f, 0.60f, 0.74f, 0.88f },
            LevelColors = new[]
            {
                new Vector3(0.17f,0.24f,0.39f),  // 0 frozen_ocean
                new Vector3(0.72f,0.78f,0.88f),  // 1 ice_shelf
                new Vector3(0.42f,0.46f,0.40f),  // 2 tundra
                new Vector3(0.65f,0.70f,0.72f),  // 3 snowfield
                new Vector3(0.44f,0.48f,0.58f),  // 4 frozen_rock
                new Vector3(0.90f,0.94f,0.98f),  // 5 glacier
            },
        });
        return sys;
    }
}

public sealed class OrbitalBody
{
    public string Name { get; set; } = "";
    public string ConfigFile { get; set; } = "";
    public float OrbitRadius { get; set; }
    public float OrbitSpeed { get; set; }
    public float Phase { get; set; }
    public float DisplayRadius { get; set; } = 1f;
    public Vector3 Color { get; set; } = Vector3.One;
    public int NoiseSeed { get; set; } = 42;
    public float NoiseFrequency { get; set; } = 2.5f;
    public float[] NoiseThresholds { get; set; } =
        { 0.45f, 0.52f, 0.65f, 0.78f, 0.88f };
    public Vector3[] LevelColors { get; set; } = PlanetMesh.LevelColors;
    public List<OrbitalBody> Moons { get; } = new();

    public Vector3 GetPosition(float time)
    {
        const float TimeScale = 0.1f;
        float angle = Phase + time * OrbitSpeed * TimeScale;
        return new Vector3(
            MathF.Cos(angle) * OrbitRadius,
            0,
            MathF.Sin(angle) * OrbitRadius);
    }
}
