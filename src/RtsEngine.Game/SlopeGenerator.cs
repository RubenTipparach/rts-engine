namespace RtsEngine.Game;

/// <summary>
/// Procedural slope placement. Walks every cell, considers each neighbor
/// pair where the cell sits between an adjacent cell at one level lower
/// and an adjacent cell at one level higher, and at a configurable density
/// converts the cell into a slope ramp between those two levels.
///
/// Generation is seeded so a given (planet seed, slope density) always
/// produces the same set of slopes — important for multiplayer determinism
/// and for letting designers iterate on a stable map.
/// </summary>
public static class SlopeGenerator
{
    /// <summary>
    /// Decorate <paramref name="mesh"/> with slope ramps. <paramref name="density"/>
    /// is the fraction of eligible cells (those with both a strictly-lower
    /// and strictly-higher neighbor) that get converted into slopes. 0 = no
    /// slopes, 1 = every eligible cell is a slope.
    /// </summary>
    public static int Generate(PlanetMesh mesh, int seed, float density)
    {
        mesh.ClearSlopes();
        var rng = new Random(seed);
        int placed = 0;

        for (int cell = 0; cell < mesh.CellCount; cell++)
        {
            byte level = mesh.GetLevel(cell);
            // Slopes only on land — water cells stay flat.
            if (level == 0) continue;

            int low = -1, high = -1;
            int lowDelta = 0, highDelta = 0;
            // Pick the steepest opposite-side pair of neighbors. Hexagons have
            // 3 such pairs (each opposite edge); pentagons don't have strict
            // opposites, so we relax to "any two neighbors not adjacent in the
            // ring" for those.
            var nbrs = mesh.GetNeighbors(cell);
            int n = nbrs.Count;
            int half = n / 2;

            for (int i = 0; i < n; i++)
            {
                int a = nbrs[i];
                int b = nbrs[(i + half) % n];
                if (a == b) continue;
                int la = mesh.GetLevel(a);
                int lb = mesh.GetLevel(b);
                int dLow = level - la;        // positive if a is lower than us
                int dHigh = lb - level;       // positive if b is higher than us
                if (dLow <= 0 || dHigh <= 0) continue;

                // Track the steepest (largest combined drop+rise) candidate.
                int score = dLow + dHigh;
                if (score > lowDelta + highDelta)
                {
                    low = a; high = b;
                    lowDelta = dLow; highDelta = dHigh;
                }
            }

            if (low < 0 || high < 0) continue;

            // Independent roll per cell — density controls overall coverage.
            // Using NextSingle keeps the roll deterministic across runs.
            if (rng.NextSingle() >= density) continue;

            mesh.SetSlope(cell, low, high);
            placed++;
        }

        return placed;
    }
}
