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
    public static List<int>? FindPath(PlanetMesh mesh, int start, int goal, bool canHop)
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
                if (!CanTraverse(mesh, current, nbr, canHop)) continue;

                float step = StepCost(mesh, current, nbr);
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
    /// way, hopping ignores small level differences in both directions.</summary>
    public static bool CanTraverse(PlanetMesh mesh, int from, int to, bool canHop)
    {
        var sf = mesh.GetSlope(from);
        var st = mesh.GetSlope(to);

        // Slope ↔ its declared low/high terminus is always traversable. The
        // slope cell's surface meets the neighbor's surface exactly there.
        if (sf != null && (sf.LowNeighbor == to || sf.HighNeighbor == to)) return true;
        if (st != null && (st.LowNeighbor == from || st.HighNeighbor == from)) return true;

        int lf = mesh.GetLevel(from);
        int lt = mesh.GetLevel(to);

        // Same level → flat terrain → walk freely.
        if (lf == lt) return true;

        // One step difference is hop-able for infantry. Larger drops still
        // need a slope.
        if (canHop && Math.Abs(lf - lt) <= 1) return true;

        return false;
    }

    private static float StepCost(PlanetMesh mesh, int from, int to)
    {
        // Base cost = great-circle arc length between cell centers.
        float dist = Distance(mesh, from, to);

        // Slope traversal carries a small penalty so paths prefer flat
        // terrain when possible. Hop traversal (level mismatch with no
        // slope on either end) pays a steeper penalty so hop-capable
        // units still favor the slope route when one exists.
        var sf = mesh.GetSlope(from);
        var st = mesh.GetSlope(to);
        bool slopeStep = (sf != null && (sf.LowNeighbor == to || sf.HighNeighbor == to))
                      || (st != null && (st.LowNeighbor == from || st.HighNeighbor == from));
        int dl = Math.Abs(mesh.GetLevel(from) - mesh.GetLevel(to));

        if (slopeStep) return dist * 1.4f;
        if (dl > 0)    return dist * 2.5f;  // hop
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
}
