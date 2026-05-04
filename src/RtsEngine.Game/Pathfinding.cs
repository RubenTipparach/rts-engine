using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// A* pathfinder over the planet's hex/pent cell graph. Cell-to-cell
/// traversal is governed by elevation: same-level cells connect freely,
/// slope cells bridge their two endpoints, and units with the
/// <see cref="UnitDef.CanHop"/> flag may step a single level either way.
///
/// Heuristic is great-circle distance scaled by the planet radius, in the
/// same units as movement cost — admissible because every step's actual
/// cost is at least its straight-line distance.
/// </summary>
public static class Pathfinding
{
    /// <summary>Returns a cell-by-cell path from <paramref name="start"/>
    /// to <paramref name="goal"/>, including both endpoints. <c>null</c>
    /// if no path exists for these traversal rules.</summary>
    public static List<int>? FindPath(PlanetMesh mesh, int start, int goal, bool canHop,
        bool slopesTraversable)
    {
        if (start == goal) return new List<int> { start };
        if (start < 0 || goal < 0) return null;

        int n = mesh.CellCount;
        var cameFrom = new int[n];
        var gScore = new float[n];
        var fScore = new float[n];
        var closed = new bool[n];
        for (int i = 0; i < n; i++) { cameFrom[i] = -1; gScore[i] = float.PositiveInfinity; fScore[i] = float.PositiveInfinity; }

        gScore[start] = 0f;
        fScore[start] = Heuristic(mesh, start, goal);

        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, fScore[start]);

        while (open.TryDequeue(out int current, out _))
        {
            if (current == goal) return Reconstruct(cameFrom, current);
            if (closed[current]) continue;
            closed[current] = true;

            foreach (int nbr in mesh.GetNeighbors(current))
            {
                if (closed[nbr]) continue;
                if (!CanTraverse(mesh, current, nbr, canHop, slopesTraversable)) continue;

                float step = StepCost(mesh, current, nbr, slopesTraversable);
                float tentative = gScore[current] + step;
                if (tentative >= gScore[nbr]) continue;

                cameFrom[nbr] = current;
                gScore[nbr] = tentative;
                fScore[nbr] = tentative + Heuristic(mesh, nbr, goal);
                open.Enqueue(nbr, fScore[nbr]);
            }
        }
        return null;
    }

    /// <summary>Whether a unit can move directly from <paramref name="from"/>
    /// to <paramref name="to"/>. Bidirectional — slope endpoints work either
    /// way, hopping ignores small level differences in both directions.
    /// When <paramref name="slopesTraversable"/> is false, slope cells stop
    /// acting as bridges between elevation bands — only same-level steps
    /// and canHop one-level jumps remain.</summary>
    public static bool CanTraverse(PlanetMesh mesh, int from, int to, bool canHop,
        bool slopesTraversable)
    {
        if (slopesTraversable)
        {
            var sf = mesh.GetSlope(from);
            var st = mesh.GetSlope(to);

            // Slope ↔ its declared low/high terminus is traversable. The
            // slope cell's surface meets the neighbor's surface exactly there.
            if (sf != null && (sf.LowNeighbor == to || sf.HighNeighbor == to)) return true;
            if (st != null && (st.LowNeighbor == from || st.HighNeighbor == from)) return true;
        }

        int lf = mesh.GetLevel(from);
        int lt = mesh.GetLevel(to);

        // Same level → flat terrain → walk freely.
        if (lf == lt) return true;

        // One step difference is hop-able for infantry. Larger drops still
        // need a slope (which is gated above).
        if (canHop && Math.Abs(lf - lt) <= 1) return true;

        return false;
    }

    private static float StepCost(PlanetMesh mesh, int from, int to, bool slopesTraversable)
    {
        // Base cost = great-circle arc length between cell centers.
        float dist = Distance(mesh, from, to);

        // Slope traversal carries a small penalty so paths prefer flat
        // terrain when possible. Hop traversal (level mismatch with no
        // slope on either end) pays a steeper penalty so hop-capable
        // units still favor the slope route when one exists.
        if (slopesTraversable)
        {
            var sf = mesh.GetSlope(from);
            var st = mesh.GetSlope(to);
            bool slopeStep = (sf != null && (sf.LowNeighbor == to || sf.HighNeighbor == to))
                          || (st != null && (st.LowNeighbor == from || st.HighNeighbor == from));
            if (slopeStep) return dist * 1.4f;
        }

        int dl = Math.Abs(mesh.GetLevel(from) - mesh.GetLevel(to));
        if (dl > 0) return dist * 2.5f;  // hop
        return dist;
    }

    private static float Heuristic(PlanetMesh mesh, int a, int b) => Distance(mesh, a, b);

    private static float Distance(PlanetMesh mesh, int a, int b)
    {
        Vector3 va = mesh.GetCellCenter(a);
        Vector3 vb = mesh.GetCellCenter(b);
        float dot = Math.Clamp(Vector3.Dot(va, vb), -1f, 1f);
        return MathF.Acos(dot) * mesh.Radius;
    }

    private static List<int> Reconstruct(int[] cameFrom, int end)
    {
        var path = new List<int>();
        for (int c = end; c != -1; c = cameFrom[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    // ── Any-angle smoothing ───────────────────────────────────────────

    /// <summary>
    /// Greedy great-circle line-of-sight test on the cell graph: walk from
    /// <paramref name="from"/> toward <paramref name="to"/>, at each step
    /// taking the neighbor whose direction best matches the current great-
    /// circle tangent. Returns true iff every step along that walk is
    /// traversable for the unit. Used by <see cref="SmoothPath"/> to drop
    /// intermediate waypoints whose direct arc is unobstructed, so units
    /// take straight-line motion in open ground instead of zigzagging
    /// cell-to-cell along the raw A* output.
    /// </summary>
    public static bool HasLineOfSight(PlanetMesh mesh, int from, int to, bool canHop,
        bool slopesTraversable)
    {
        if (from == to) return true;
        if (from < 0 || to < 0) return false;

        int current = from;
        Vector3 goalPos = mesh.GetCellCenter(to);
        // Loose safety bound — a great-circle walk on the cell graph is
        // monotone toward the goal, so it terminates well inside CellCount
        // steps. The bound is just to keep a malformed mesh from looping.
        int safety = mesh.CellCount;

        while (current != to && safety-- > 0)
        {
            Vector3 currentPos = mesh.GetCellCenter(current);

            // Tangent at 'current' pointing along the great-circle toward
            // 'goal': remove the radial component of (goal - current). On
            // unit-radius positions this is the geodesic direction.
            Vector3 toGoal = goalPos - currentPos;
            Vector3 tangent = toGoal - currentPos * Vector3.Dot(toGoal, currentPos);
            if (tangent.LengthSquared() < 1e-12f) return false;
            tangent = Vector3.Normalize(tangent);

            int bestNbr = -1;
            float bestDot = -2f;
            foreach (int n in mesh.GetNeighbors(current))
            {
                Vector3 dirN = mesh.GetCellCenter(n) - currentPos;
                Vector3 tN = dirN - currentPos * Vector3.Dot(dirN, currentPos);
                if (tN.LengthSquared() < 1e-12f) continue;
                tN = Vector3.Normalize(tN);
                float d = Vector3.Dot(tN, tangent);
                if (d > bestDot) { bestDot = d; bestNbr = n; }
            }
            if (bestNbr < 0) return false;

            // Progress guard: if the greedy pick isn't strictly closer to
            // the goal than current, the great-circle direction lies
            // between two neighbors and the walk would oscillate. Treat
            // that as "no clear line" and let the raw A* path handle it.
            float curDistSq = Vector3.DistanceSquared(currentPos, goalPos);
            float nextDistSq = Vector3.DistanceSquared(mesh.GetCellCenter(bestNbr), goalPos);
            if (nextDistSq >= curDistSq) return false;

            // The walk hit a non-traversable transition (cliff without
            // slope/hop) — line of sight blocked, raw A* must route around.
            if (!CanTraverse(mesh, current, bestNbr, canHop, slopesTraversable)) return false;

            current = bestNbr;
        }
        return current == to;
    }

    /// <summary>
    /// Any-angle string-pull: replace the raw cell-by-cell A* path with the
    /// minimal subsequence whose consecutive pairs all have line-of-sight.
    /// In open ground the result collapses to <c>[start, goal]</c> and the
    /// unit travels a single great-circle arc; near terrain corners the
    /// pulled-tight path keeps only the cells where the heading actually
    /// changes. Idempotent: smoothing an already-smoothed path returns it
    /// unchanged.
    /// </summary>
    public static List<int> SmoothPath(PlanetMesh mesh, List<int> path, bool canHop,
        bool slopesTraversable)
    {
        if (path.Count <= 2) return path;
        var result = new List<int> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            // Find the furthest j > i visible from path[i]. Walk back from
            // the end — typical RTS terrain has long visible stretches, so
            // this hits on the first try most of the time. Worst case is
            // O(n) LOS checks per anchor, which is fine at n ~ a few hundred.
            int j = path.Count - 1;
            while (j > i + 1 && !HasLineOfSight(mesh, path[i], path[j], canHop, slopesTraversable)) j--;
            result.Add(path[j]);
            i = j;
        }
        return result;
    }
}
