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
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Earth", ConfigFile = "planets/earth.yaml",
            OrbitRadius = 25f, OrbitSpeed = 0.2f, Phase = 0f,
            DisplayRadius = 1.0f, Color = new(0.3f, 0.6f, 0.9f), NoiseSeed = 42,
            Moons =
            {
                new OrbitalBody
                {
                    Name = "Moon", ConfigFile = "planets/moon.yaml",
                    OrbitRadius = 3f, OrbitSpeed = 1.2f, Phase = 0f,
                    DisplayRadius = 0.3f, Color = new(0.7f, 0.7f, 0.7f), NoiseSeed = 77,
                }
            }
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Mars", ConfigFile = "planets/mars.yaml",
            OrbitRadius = 38f, OrbitSpeed = 0.15f, Phase = 2.1f,
            DisplayRadius = 0.6f, Color = new(0.85f, 0.4f, 0.2f), NoiseSeed = 1337,
        });
        sys.Planets.Add(new OrbitalBody
        {
            Name = "Glacius", ConfigFile = "planets/ice.yaml",
            OrbitRadius = 55f, OrbitSpeed = 0.08f, Phase = 4.2f,
            DisplayRadius = 0.7f, Color = new(0.7f, 0.85f, 0.95f), NoiseSeed = 5555,
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
    public List<OrbitalBody> Moons { get; } = new();

    public Vector3 GetPosition(float time)
    {
        float angle = Phase + time * OrbitSpeed;
        return new Vector3(
            MathF.Cos(angle) * OrbitRadius,
            0,
            MathF.Sin(angle) * OrbitRadius);
    }
}
