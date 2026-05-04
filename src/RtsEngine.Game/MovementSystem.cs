using System.Collections.Generic;
using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// Per-tick advance for spawned units, with ORCA-based local avoidance.
///
/// Pipeline each tick:
///   1. Compute each unit's <i>preferred velocity</i> — the velocity it
///      would pick if no other units existed (toward the next path
///      waypoint at unit speed, or zero if pathless / arrived).
///   2. For each unit, project nearby neighbors into its tangent plane,
///      build ORCA half-plane constraints (one per neighbor), and solve
///      the 2D LP for a velocity closest to preferred that is collision-
///      free under the reciprocal assumption.
///   3. Integrate the chosen velocity over dt, snap back to the surface,
///      and check for waypoint arrival.
///
/// ORCA (van den Berg / Guy / Lin / Manocha 2011) replaces the older
/// hand-rolled separation+slowdown pass — it's deadlock-free under mutual
/// adoption, smooth, and bounded in cost. Reference impl: RVO2 library.
///
/// Held as a static "system" so the data layout stays plain — when entity
/// counts grow this is what an ECS migration would lift unchanged.
/// </summary>
public static class MovementSystem
{
    /// <summary>Padding multiplier on combined unit half-widths. &lt;1 means
    /// ORCA's "personal space" is smaller than the visible silhouette, so
    /// units can clip into each other a touch when forced to squeeze past
    /// — that's deliberate. With strict (≥1) padding, dense formations and
    /// "go to the same cell" group orders make ORCA produce infeasible
    /// constraints and units stall against an invisible wall. 0.7 keeps
    /// the avoidance present and effective without the hard-blocking.</summary>
    private const float AvoidancePadding = 0.70f;

    /// <summary>How far ahead in time (seconds) the avoidance considers
    /// future collisions. Larger = earlier, smoother evasion; smaller =
    /// tighter formations but later, jerkier reactions. RVO2's default for
    /// pedestrian sims is 5–10s; ours is on the small end because units
    /// are small relative to the planet and react quickly.</summary>
    private const float TimeHorizon = 2.0f;

    /// <summary>Distance from this unit beyond which a neighbor is ignored
    /// for ORCA. Sized to comfortably cover the look-ahead band: any pair
    /// further than this can't reach each other inside the time horizon
    /// even at the fastest unit's speed.</summary>
    private const float NeighborSearchRadius = 0.20f;

    /// <summary>Per-tick scratch buffers reused across calls. Sized up
    /// once on first need and never shrunk.</summary>
    private static Vector3[] _scratchPrefVel = System.Array.Empty<Vector3>();
    private static Vector3[] _scratchChosenVel = System.Array.Empty<Vector3>();
    private static List<Orca.Line> _scratchLines = new();

    public static void Tick(RtsState state, PlanetMesh mesh, RtsConfig config,
        EngineConfig engineConfig, float dt)
    {
        if (dt <= 0f) return;

        var units = state.Units;
        int n = units.Count;
        if (_scratchPrefVel.Length < n) _scratchPrefVel = new Vector3[n];
        if (_scratchChosenVel.Length < n) _scratchChosenVel = new Vector3[n];
        var prefVel = _scratchPrefVel;
        var chosenVel = _scratchChosenVel;

        // Pass 1: compute preferred velocity for each unit (where it wants
        // to go this tick, ignoring everyone else). Stored in prefVel[i] in
        // world coordinates, magnitude = unit's max speed (or 0 if no path).
        for (int i = 0; i < n; i++)
        {
            prefVel[i] = ComputePreferredVelocity(units[i], mesh, config);
        }

        // Pass 2: ORCA per unit. Builds half-planes from each in-range
        // neighbor and solves the 2D LP in u's tangent plane for a velocity
        // closest to prefVel that is collision-free.
        for (int i = 0; i < n; i++)
        {
            chosenVel[i] = SolveOrcaForUnit(i, units, mesh, config, dt, prefVel);
        }

        // Pass 3: integrate. Apply each unit's chosen velocity, track which
        // cell the unit is now over, chase that cell's surface height for
        // smooth slope traversal, and advance the path index when a
        // waypoint is reached.
        for (int i = 0; i < n; i++)
        {
            IntegrateUnit(units[i], chosenVel[i], mesh, config, engineConfig, dt);
        }
    }

    // ── Preferred velocity ────────────────────────────────────────────

    /// <summary>The velocity a unit would pick if it had the world to
    /// itself. For a pathing unit it's the unit-speed velocity toward the
    /// next waypoint, projected to the tangent plane so it slides along
    /// the surface; for a parked unit it's zero.</summary>
    private static Vector3 ComputePreferredVelocity(SpawnedUnit u, PlanetMesh mesh, RtsConfig config)
    {
        var path = u.Path;
        if (path == null || u.PathIndex >= path.Count) return Vector3.Zero;

        var def = config.GetUnit(u.TypeId);
        if (def == null) return Vector3.Zero;

        int targetCell = path[u.PathIndex];
        // Last waypoint with a sub-cell anchor: aim at the anchor exactly so
        // multi-unit orders pack into one hex without piling onto the same
        // point. Earlier waypoints always use the cell center.
        Vector3 targetPos;
        if (u.PathIndex == path.Count - 1 && u.FinalArrivalPos.HasValue)
        {
            targetPos = u.FinalArrivalPos.Value;
        }
        else
        {
            Vector3 targetCenter = mesh.GetCellCenter(targetCell);
            float targetH = mesh.GetCenterHeight(targetCell) + 0.002f;
            targetPos = targetCenter * targetH;
        }

        Vector3 toTarget = targetPos - u.SurfacePoint;
        float dist = toTarget.Length();
        if (dist < 1e-6f) return Vector3.Zero;

        // Project to u's tangent plane so the velocity slides along the
        // surface instead of pointing slightly inward as we cross between
        // cells at different elevations.
        var dir = toTarget / dist;
        dir = ProjectTangent(dir, u.SurfaceUp);
        if (dir.LengthSquared() < 1e-10f) return Vector3.Zero;
        dir = Vector3.Normalize(dir);

        float speed = def.Speed * mesh.Radius;
        return dir * speed;
    }

    // ── ORCA per agent ────────────────────────────────────────────────

    /// <summary>Build u's tangent-plane basis, project all neighbors within
    /// <see cref="NeighborSearchRadius"/> into 2D, build ORCA lines, solve
    /// the LP, rotate the chosen 2D velocity back to 3D world space.</summary>
    private static Vector3 SolveOrcaForUnit(int idx, IReadOnlyList<SpawnedUnit> units,
        PlanetMesh mesh, RtsConfig config, float dt, Vector3[] prefVel)
    {
        var u = units[idx];
        var defU = config.GetUnit(u.TypeId);
        if (defU == null) return prefVel[idx];

        // Tangent basis at u: 'fwd' is the preferred-velocity direction
        // (or any tangent if pref is zero), 'right' = cross(up, fwd) is
        // the orthogonal in-plane axis. The 2D coords of any point P near
        // u are then (P · right, P · fwd) relative to u.
        Vector3 up = u.SurfaceUp;
        Vector3 fwd;
        if (prefVel[idx].LengthSquared() > 1e-10f)
            fwd = Vector3.Normalize(ProjectTangent(prefVel[idx], up));
        else if (u.Heading.LengthSquared() > 1e-10f)
            fwd = Vector3.Normalize(ProjectTangent(u.Heading, up));
        else
            fwd = ArbitraryTangent(up);
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, fwd));

        Vector2 ToLocal(Vector3 worldOffset)
            => new(Vector3.Dot(worldOffset, right), Vector3.Dot(worldOffset, fwd));
        Vector3 ToWorld(Vector2 local) => right * local.X + fwd * local.Y;

        // u's own velocity (last frame) and preferred velocity in 2D.
        Vector2 uVel2D = ToLocal(u.Velocity);
        Vector2 uPref2D = ToLocal(prefVel[idx]);
        float uSpeed = defU.Speed * mesh.Radius;
        float uRadius = defU.HalfWidth * mesh.Radius * AvoidancePadding;

        var lines = _scratchLines;
        lines.Clear();

        float searchSq = NeighborSearchRadius * mesh.Radius;
        searchSq *= searchSq;

        for (int j = 0; j < units.Count; j++)
        {
            if (j == idx) continue;
            var v = units[j];
            var defV = config.GetUnit(v.TypeId);
            if (defV == null) continue;

            var delta3D = v.SurfacePoint - u.SurfacePoint;
            if (delta3D.LengthSquared() > searchSq) continue;

            float vRadius = defV.HalfWidth * mesh.Radius * AvoidancePadding;
            Vector2 relPos = ToLocal(delta3D);
            Vector2 relVel = uVel2D - ToLocal(v.Velocity);

            // If the neighbor is parked (no remaining path AND no measurable
            // velocity) we take the full avoidance burden ourselves rather
            // than splitting it 50/50 — otherwise the moving unit's half
            // plane assumes the parked one will yield half the way, the
            // parked one never does, and the moving unit ends up wedged
            // against an invisible wall just shy of its goal.
            bool neighborParked =
                (v.Path == null || v.PathIndex >= v.Path.Count)
                && v.Velocity.LengthSquared() < 1e-8f;
            float responsibility = neighborParked ? 1.0f : 0.5f;

            lines.Add(Orca.BuildLine(relPos, relVel, uRadius + vRadius,
                TimeHorizon, dt, uVel2D, responsibility));
        }

        Orca.Solve(lines, uSpeed, uPref2D, optimizeDirection: false, out var chosen2D);
        return ToWorld(chosen2D);
    }

    // ── Integrate ─────────────────────────────────────────────────────

    /// <summary>Apply the chosen velocity, snap back to surface, update
    /// heading/cellIndex/pathIndex, and stash the velocity on the unit so
    /// neighbors can read it next tick. Detects waypoint arrival via a
    /// distance threshold; the path is advanced but never cleared (debug
    /// viz keeps showing the route after arrival).</summary>
    private static void IntegrateUnit(SpawnedUnit unit, Vector3 vel,
        PlanetMesh mesh, RtsConfig config, EngineConfig engineConfig, float dt)
    {
        // Always store the velocity (even zero) so neighbors see fresh data.
        unit.Velocity = vel;

        if (vel.LengthSquared() > 1e-10f)
        {
            // Move along the chosen velocity, then re-snap to surface so the
            // tangent-plane integration doesn't drift the unit into the
            // sphere. Radius preserved across the step — altitude follows
            // the terrain in the dedicated pass below, not here.
            float r = unit.SurfacePoint.Length();
            var moved = unit.SurfacePoint + vel * dt;
            unit.SurfacePoint = Vector3.Normalize(moved) * r;
            unit.SurfaceUp = Vector3.Normalize(unit.SurfacePoint);

            // Heading follows velocity when we're actually moving — feeds
            // the renderer's yaw rotation.
            unit.Heading = Vector3.Normalize(vel);
        }

        // Track which cell the unit is currently over. Without this the
        // unit's CellIndex stays whatever the last waypoint set it to, so
        // altitude tracking (next step) and any-angle paths spanning many
        // cells would never see the intermediate terrain. Hill-climbing
        // from the previous CellIndex is cheap because per-tick motion is
        // small relative to cell size — the loop converges in ≤ 1-2 hops.
        unit.CellIndex = HillClimbToNearestCell(mesh, unit.CellIndex, unit.SurfacePoint);

        // Altitude follow: chase the current cell's surface height with an
        // exponential lerp. Slopes give a smooth ramp because GetCenterHeight
        // returns the slope's midpoint and the cell pointer transitions
        // through low-side → slope → high-side cells as the unit walks
        // across them. Non-slope level changes (canHop one-step jumps) ease
        // in the same way rather than snapping. Out-of-band cliffs aren't a
        // concern because pathfinding never routes over them.
        float targetR = mesh.GetCenterHeight(unit.CellIndex) + 0.002f;
        float currentR = unit.SurfacePoint.Length();
        if (MathF.Abs(targetR - currentR) > 1e-6f)
        {
            float alpha = 1f - MathF.Exp(-engineConfig.UnitMovement.AltitudeLerpRate * dt);
            float newR = currentR + (targetR - currentR) * alpha;
            unit.SurfacePoint = Vector3.Normalize(unit.SurfacePoint) * newR;
            unit.SurfaceUp = Vector3.Normalize(unit.SurfacePoint);
        }

        // Path arrival check. Use a generous epsilon so a unit slowed by
        // avoidance still registers arrival when it's "close enough" to
        // the waypoint center. Otherwise tight-clump destinations would
        // leave units oscillating around their goals.
        var path = unit.Path;
        if (path == null || unit.PathIndex >= path.Count) return;

        int targetCell = path[unit.PathIndex];
        // Mirror the targeting choice from ComputePreferredVelocity so the
        // arrival snap lands where the preferred-velocity step was steering.
        Vector3 targetPos;
        if (unit.PathIndex == path.Count - 1 && unit.FinalArrivalPos.HasValue)
        {
            targetPos = unit.FinalArrivalPos.Value;
        }
        else
        {
            Vector3 targetCenter = mesh.GetCellCenter(targetCell);
            float targetH = mesh.GetCenterHeight(targetCell) + 0.002f;
            targetPos = targetCenter * targetH;
        }

        var def = config.GetUnit(unit.TypeId);
        // Arrival threshold: half a unit halfWidth — once this close to the
        // waypoint center, snap and advance. Keeps the path resolution
        // independent of cell size now that we've subdivided the grid.
        float arriveDist = (def?.HalfWidth ?? 0.01f) * mesh.Radius * 0.5f + 0.001f;

        if (Vector3.DistanceSquared(unit.SurfacePoint, targetPos) <= arriveDist * arriveDist)
        {
            unit.SurfacePoint = targetPos;
            unit.SurfaceUp = Vector3.Normalize(targetPos);
            // CellIndex is already maintained by the live hill-climb pass
            // above; no need to reassert it here.
            unit.PathIndex++;
            // Reaching the final waypoint leaves Path on the unit so the
            // path-debug viz can still show the route. PathIndex == Count
            // is the "no more steps" signal the rest of the system reads.
        }
    }

    /// <summary>Walk the cell graph from <paramref name="start"/> to the cell
    /// whose center is closest to <paramref name="pos"/>, projected onto the
    /// unit sphere. Each iteration moves to a strictly-closer neighbor; the
    /// search terminates as soon as no neighbor improves on the current
    /// candidate. Per-tick motion is much smaller than a cell so this
    /// resolves in 0-1 hops in steady state.</summary>
    private static int HillClimbToNearestCell(PlanetMesh mesh, int start, Vector3 pos)
    {
        if (start < 0) return start;
        var posUnit = pos.LengthSquared() > 1e-12f ? Vector3.Normalize(pos) : pos;
        int current = start;
        float bestDist = Vector3.DistanceSquared(mesh.GetCellCenter(current), posUnit);
        // Loose safety bound — converges in O(cells crossed this tick).
        int safety = 16;
        while (safety-- > 0)
        {
            int better = -1;
            float betterDist = bestDist;
            foreach (int n in mesh.GetNeighbors(current))
            {
                float d = Vector3.DistanceSquared(mesh.GetCellCenter(n), posUnit);
                if (d < betterDist) { betterDist = d; better = n; }
            }
            if (better < 0) return current;
            current = better;
            bestDist = betterDist;
        }
        return current;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static Vector3 ProjectTangent(Vector3 v, Vector3 up)
        => v - up * Vector3.Dot(v, up);

    private static Vector3 ArbitraryTangent(Vector3 up)
    {
        var worldUp = new Vector3(0, 1, 0);
        var t = Vector3.Cross(worldUp, up);
        if (t.LengthSquared() < 1e-5f) t = Vector3.Cross(new Vector3(1, 0, 0), up);
        return Vector3.Normalize(t);
    }
}
