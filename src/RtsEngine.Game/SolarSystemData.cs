using System.Numerics;

namespace RtsEngine.Game;

public sealed class SolarSystemData
{
    public string Name { get; set; } = "Sol";
    public Vector3 SunColor { get; set; } = new(1.0f, 0.95f, 0.8f);
    public float SunRadius { get; set; } = 3.0f;
    public List<OrbitalBody> Planets { get; } = new();

    public static SolarSystemData CreateDefault()
    {
        var sys = new SolarSystemData { Name = "Sol" };
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Venus", ConfigFile = "planets/venus.yaml",
            OrbitRadius = 15f, OrbitSpeed = 0.3f, Phase = 0.8f,
            DisplayRadius = 0.8f, Color = new(0.85f, 0.70f, 0.35f), NoiseSeed = 999,
            NoiseFrequency = 2.2f, NoiseThresholds = new[]{ 0.15f, 0.38f, 0.62f, 0.85f },
            LevelColors = new[] { new Vector3(0.42f,0.30f,0.12f), new Vector3(0.78f,0.56f,0.22f), new Vector3(0.82f,0.66f,0.32f), new Vector3(0.38f,0.24f,0.10f), new Vector3(0.92f,0.86f,0.50f) },
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Earth", ConfigFile = "planets/earth.yaml",
            OrbitRadius = 25f, OrbitSpeed = 0.2f, Phase = 0f,
            DisplayRadius = 1.0f, Color = new(0.3f, 0.6f, 0.9f), NoiseSeed = 42,
            NoiseFrequency = 2.5f, NoiseThresholds = new[]{ 0.30f, 0.45f, 0.65f, 0.82f },
            LevelColors = new[] { new Vector3(0.15f,0.35f,0.75f), new Vector3(0.90f,0.80f,0.55f), new Vector3(0.30f,0.65f,0.25f), new Vector3(0.55f,0.55f,0.55f), new Vector3(0.95f,0.97f,1.00f) },
            Moons =
            {
                new OrbitalBody
                {
                    Name = "Moon", ConfigFile = "planets/moon.yaml",
                    OrbitRadius = 3f, OrbitSpeed = 1.2f, Phase = 0f,
                    DisplayRadius = 0.3f, Color = new(0.7f, 0.7f, 0.7f), NoiseSeed = 77,
                    NoiseFrequency = 3.0f, NoiseThresholds = new[]{ 0.25f, 0.45f, 0.65f, 0.85f },
                    LevelColors = new[] { new Vector3(0.18f,0.18f,0.18f), new Vector3(0.36f,0.35f,0.34f), new Vector3(0.52f,0.52f,0.52f), new Vector3(0.62f,0.62f,0.62f), new Vector3(0.78f,0.78f,0.78f) },
                }
            }
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Mars", ConfigFile = "planets/mars.yaml",
            OrbitRadius = 38f, OrbitSpeed = 0.15f, Phase = 2.1f,
            DisplayRadius = 0.6f, Color = new(0.85f, 0.4f, 0.2f), NoiseSeed = 1337,
            NoiseFrequency = 2.8f, NoiseThresholds = new[]{ 0.20f, 0.42f, 0.68f, 0.88f },
            LevelColors = new[] { new Vector3(0.36f,0.15f,0.10f), new Vector3(0.72f,0.36f,0.18f), new Vector3(0.78f,0.48f,0.28f), new Vector3(0.32f,0.25f,0.22f), new Vector3(0.90f,0.92f,0.96f) },
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Glacius", ConfigFile = "planets/ice.yaml",
            OrbitRadius = 55f, OrbitSpeed = 0.08f, Phase = 4.2f,
            DisplayRadius = 0.7f, Color = new(0.7f, 0.85f, 0.95f), NoiseSeed = 5555,
            NoiseFrequency = 2.0f, NoiseThresholds = new[]{ 0.35f, 0.50f, 0.68f, 0.85f },
            LevelColors = new[] { new Vector3(0.17f,0.24f,0.39f), new Vector3(0.72f,0.78f,0.88f), new Vector3(0.42f,0.46f,0.40f), new Vector3(0.44f,0.48f,0.58f), new Vector3(0.90f,0.94f,0.98f) },
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
    public float[] NoiseThresholds { get; set; } = { 0.30f, 0.45f, 0.65f, 0.82f };
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
