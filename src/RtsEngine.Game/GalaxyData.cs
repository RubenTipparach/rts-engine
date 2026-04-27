using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// Hierarchical galaxy data: Galaxy → Sector → Cluster → Group → Star.
/// Procedurally generated with seeded noise. Each level has a center
/// position and contains children offset from it.
/// </summary>
public sealed class GalaxyData
{
    public List<Sector> Sectors { get; } = new();
    public Vector3 Center { get; set; } = Vector3.Zero;

    public static GalaxyData Generate(int seed = 12345)
    {
        var rng = new Random(seed);
        var galaxy = new GalaxyData();

        int sectorCount = 4 + rng.Next(3); // 4-6 sectors
        for (int s = 0; s < sectorCount; s++)
        {
            var sectorPos = RandomDisk(rng, 80f) + new Vector3(0, (rng.NextSingle() - 0.5f) * 10f, 0);
            var sector = new Sector { Name = $"Sector {s + 1}", Center = sectorPos };

            int clusterCount = 3 + rng.Next(3); // 3-5 clusters per sector
            for (int c = 0; c < clusterCount; c++)
            {
                var clusterPos = sectorPos + RandomSphere(rng, 20f);
                var cluster = new StarCluster { Name = $"Cluster {s + 1}-{c + 1}", Center = clusterPos };

                int groupCount = 2 + rng.Next(3); // 2-4 groups per cluster
                for (int g = 0; g < groupCount; g++)
                {
                    var groupPos = clusterPos + RandomSphere(rng, 6f);
                    var group = new StarGroup { Name = $"Group {s + 1}-{c + 1}-{g + 1}", Center = groupPos };

                    int starCount = 3 + rng.Next(8); // 3-10 stars per group
                    for (int st = 0; st < starCount; st++)
                    {
                        var starPos = groupPos + RandomSphere(rng, 2f);
                        float temp = 3000f + rng.NextSingle() * 7000f; // 3000K-10000K
                        group.Stars.Add(new Star
                        {
                            Name = $"Star {s + 1}-{c + 1}-{g + 1}-{(char)('A' + st)}",
                            Position = starPos,
                            Temperature = temp,
                            Luminosity = 0.5f + rng.NextSingle() * 2f,
                        });
                    }
                    cluster.Groups.Add(group);
                }
                sector.Clusters.Add(cluster);
            }
            galaxy.Sectors.Add(sector);
        }
        return galaxy;
    }

    private static Vector3 RandomDisk(Random rng, float radius)
    {
        float angle = rng.NextSingle() * MathF.PI * 2f;
        float r = MathF.Sqrt(rng.NextSingle()) * radius;
        return new Vector3(MathF.Cos(angle) * r, 0, MathF.Sin(angle) * r);
    }

    private static Vector3 RandomSphere(Random rng, float radius)
    {
        float u = rng.NextSingle() * 2f - 1f;
        float theta = rng.NextSingle() * MathF.PI * 2f;
        float r = MathF.Cbrt(rng.NextSingle()) * radius;
        float s = MathF.Sqrt(1f - u * u);
        return new Vector3(s * MathF.Cos(theta), u, s * MathF.Sin(theta)) * r;
    }

    /// <summary>Color from blackbody temperature (simplified).</summary>
    public static Vector3 TempToColor(float kelvin)
    {
        float t = kelvin / 100f;
        float r, g, b;
        if (t <= 66f)
        {
            r = 1f;
            g = Math.Clamp(0.39f * MathF.Log(t) - 0.63f, 0f, 1f);
        }
        else
        {
            r = Math.Clamp(1.29f * MathF.Pow(t - 60f, -0.13f), 0f, 1f);
            g = Math.Clamp(1.13f * MathF.Pow(t - 60f, -0.076f), 0f, 1f);
        }
        if (t >= 66f) b = 1f;
        else if (t <= 19f) b = 0f;
        else b = Math.Clamp(0.54f * MathF.Log(t - 10f) - 1.19f, 0f, 1f);
        return new Vector3(r, g, b);
    }
}

public sealed class Sector
{
    public string Name { get; set; } = "";
    public Vector3 Center { get; set; }
    public List<StarCluster> Clusters { get; } = new();
}

public sealed class StarCluster
{
    public string Name { get; set; } = "";
    public Vector3 Center { get; set; }
    public List<StarGroup> Groups { get; } = new();
}

public sealed class StarGroup
{
    public string Name { get; set; } = "";
    public Vector3 Center { get; set; }
    public List<Star> Stars { get; } = new();
}

public sealed class Star
{
    public string Name { get; set; } = "";
    public Vector3 Position { get; set; }
    public float Temperature { get; set; } = 5800f; // Kelvin
    public float Luminosity { get; set; } = 1f;
}

/// <summary>View hierarchy levels for star map navigation.</summary>
public enum StarMapLevel { Galaxy, Sector, Cluster, Group }
