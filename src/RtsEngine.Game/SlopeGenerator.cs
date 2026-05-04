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

        // Reused per-cell scratch for marking which neighbors are one step
        // below; max 6 (hex), pentagons use only the first 5 entries.
        Span<bool> isLow = stackalloc bool[6];

        for (int cell = 0; cell < mesh.CellCount; cell++)
        {
            byte level = mesh.GetLevel(cell);
            // Slopes only on land — water cells stay flat.
            if (level == 0) continue;

            // Refuse to spawn next to an existing slope. Adjacent slopes
            // with mismatched tilt axes leave a thin sliver wall at the
            // shared edge (their tilted top heights disagree there), and
            // a slope whose low end opens onto another tilted ramp
            // doesn't actually deliver units to flat ground — the ramp
            // "goes nowhere". Iteration is in cell-index order, so the
            // first slope to claim a region wins and its neighbors stay
            // flat; this is deterministic per seed.
            var nbrs = mesh.GetNeighbors(cell);
            bool blockedByNeighbor = false;
            foreach (int nbr in nbrs)
            {
                if (mesh.HasSlope(nbr)) { blockedByNeighbor = true; break; }
            }
            if (blockedByNeighbor) continue;

            // Sand cells (level 1) can't be slopes: their low neighbor
            // would have to be water, which we already forbid.
            if (level < 2) continue;

            int n = nbrs.Count;
            int half = n / 2;

            // Mark every neighbor that sits exactly one step below this
            // cell — these form the cliff edge along the cell's perimeter.
            for (int i = 0; i < n; i++)
                isLow[i] = mesh.GetLevel(nbrs[i]) == level - 1;

            // Find the longest contiguous run of low neighbors around the
            // ring. The middle of this run is the "downhill" direction;
            // anchoring the slope axis there points the tilt straight out
            // of the cliff face instead of slanting across it. For runs
            // of 3, this picks the central hex (the user-asked behavior);
            // runs of 1 reduce to "the only choice"; runs that wrap the
            // index boundary are handled by always advancing from a true
            // run start (where the previous index is not low).
            int bestStart = -1, bestLen = 0;
            for (int start = 0; start < n; start++)
            {
                if (!isLow[start]) continue;
                if (isLow[(start - 1 + n) % n]) continue;
                int len = 0;
                while (len < n && isLow[(start + len) % n]) len++;
                if (len > bestLen) { bestLen = len; bestStart = start; }
            }
            if (bestLen == 0) continue;

            int lowIdx = (bestStart + bestLen / 2) % n;
            int highIdx = (lowIdx + half) % n;
            int low = nbrs[lowIdx];
            int high = nbrs[highIdx];

            // High anchor must be a same-level cell directly opposite the
            // chosen low; otherwise the tilt has no flat ground to meet
            // on its high edge and we'd be back to the steep "into the
            // void" case from before. la < 1 (water below sand) was
            // already excluded by the level >= 2 gate.
            if (mesh.GetLevel(high) != level) continue;

            // Independent roll per cell — density controls overall coverage.
            // Using NextSingle keeps the roll deterministic across runs.
            if (rng.NextSingle() >= density) continue;

            mesh.SetSlope(cell, low, high);
            placed++;
        }

        return placed;
    }
}
