using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// Optimal Reciprocal Collision Avoidance (van den Berg / Guy / Lin /
/// Manocha, 2011). Picks a collision-free velocity for an agent given its
/// neighbors' positions and velocities, in 2D. Each neighbor produces a
/// half-plane constraint on the velocity space; the result is the velocity
/// closest to the agent's preferred velocity that satisfies every
/// constraint and stays inside the speed circle.
///
/// We run this in each unit's surface-tangent plane: positions and
/// velocities get projected to 2D via (right, fwd), the LP is solved, and
/// the result is rotated back to 3D in <see cref="MovementSystem"/>. On a
/// sphere of radius 1+ with cell-size neighborhoods, the curvature error
/// from treating tangent planes as identical is well below avoidance
/// noise floor — same approximation crowd sims on terrain make.
///
/// Naming follows the reference paper / RVO2 library.
/// </summary>
internal static class Orca
{
    /// <summary>A directed line in 2D velocity space. Permitted velocities
    /// lie on the line itself or to its <i>left</i> (i.e. on the side
    /// reached by rotating <see cref="Direction"/> +90°). The half-plane
    /// constraint is: <c>cross(direction, vel - point) ≥ 0</c>.</summary>
    public struct Line
    {
        public Vector2 Point;
        public Vector2 Direction;
    }

    /// <summary>
    /// Build an ORCA half-plane for the pair (this agent vs. a neighbor).
    /// </summary>
    /// <param name="relPos">neighbor.pos - this.pos in the tangent plane</param>
    /// <param name="relVel">this.vel - neighbor.vel</param>
    /// <param name="combinedRadius">this.radius + neighbor.radius</param>
    /// <param name="timeHorizon">how far in the future to consider collision (s)</param>
    /// <param name="dt">current frame time step (s) — used when already overlapping</param>
    /// <param name="thisVel">this agent's current velocity (in tangent plane)</param>
    /// <param name="responsibility">share of the avoidance this agent takes.
    /// 0.5 = standard reciprocal (both agents are running ORCA), 1.0 = this
    /// agent does all the work (the neighbor is a static obstacle that
    /// won't move out of the way), 0.0 = this agent does nothing (rare —
    /// only useful when the neighbor is doing all the work). Setting it
    /// to 1.0 against parked units is the load-bearing fix for "moving
    /// unit gets stuck behind a stopped one": the half-plane no longer
    /// assumes the other side will yield, so the moving agent's solution
    /// space stays feasible and it routes around.</param>
    public static Line BuildLine(Vector2 relPos, Vector2 relVel, float combinedRadius,
        float timeHorizon, float dt, Vector2 thisVel, float responsibility = 0.5f)
    {
        float distSq = relPos.LengthSquared();
        float rSq = combinedRadius * combinedRadius;

        Line line = default;
        Vector2 u;

        if (distSq > rSq)
        {
            // Not currently overlapping — build the standard truncated VO.
            // 'w' is the offset between current relative velocity and the
            // cutoff disc center (= relPos / timeHorizon).
            Vector2 w = relVel - relPos / timeHorizon;
            float wLenSq = w.LengthSquared();
            float dotW = Vector2.Dot(w, relPos);

            if (dotW < 0f && dotW * dotW > rSq * wLenSq)
            {
                // Project onto the small cutoff disc at the apex of the
                // VO cone — relevant when neighbors are far enough that
                // their truncation disc is the closest part of the VO.
                float wLen = MathF.Sqrt(wLenSq);
                Vector2 unitW = w / wLen;
                line.Direction = new Vector2(unitW.Y, -unitW.X);
                u = unitW * (combinedRadius / timeHorizon - wLen);
            }
            else
            {
                // Project onto one of the cone's legs (sides). 'leg' is the
                // distance from agent to neighbor along the cone leg.
                float leg = MathF.Sqrt(distSq - rSq);
                if (Cross(relPos, w) > 0f)
                {
                    line.Direction = new Vector2(
                        relPos.X * leg - relPos.Y * combinedRadius,
                        relPos.X * combinedRadius + relPos.Y * leg) / distSq;
                }
                else
                {
                    line.Direction = -new Vector2(
                        relPos.X * leg + relPos.Y * combinedRadius,
                        -relPos.X * combinedRadius + relPos.Y * leg) / distSq;
                }
                float dotV = Vector2.Dot(relVel, line.Direction);
                u = line.Direction * dotV - relVel;
            }
        }
        else
        {
            // Already overlapping — push apart this frame using dt as the
            // time horizon so the resolve is immediate, not gradual.
            float invDt = 1f / dt;
            Vector2 w = relVel - relPos * invDt;
            float wLen = w.Length();
            Vector2 unitW = wLen > 1e-9f ? w / wLen : Vector2.UnitX;
            line.Direction = new Vector2(unitW.Y, -unitW.X);
            u = unitW * (combinedRadius * invDt - wLen);
        }

        // Reciprocal split: see <paramref name="responsibility"/>. 0.5 is
        // the symmetric default that makes ORCA non-oscillating; >0.5 means
        // this agent takes more of the burden (e.g. against a static one).
        line.Point = thisVel + u * responsibility;
        return line;
    }

    /// <summary>
    /// Solve the 2D LP: find the velocity inside a circle of radius
    /// <paramref name="maxSpeed"/> that satisfies every line's half-plane
    /// constraint and is closest to <paramref name="prefVel"/>.
    ///
    /// Uses incremental projection (the textbook algorithm): start from
    /// <c>prefVel</c> clamped to the speed circle. For each line in turn,
    /// if the running result violates it, project the result onto that
    /// line's boundary while still respecting all earlier lines and the
    /// speed circle. Runs in expected O(n) and worst-case O(n²) over
    /// neighbor count.
    /// </summary>
    /// <returns>
    /// The number of lines that couldn't be satisfied. 0 means the LP
    /// found a feasible velocity in <paramref name="result"/>; &gt;0 means
    /// the agent is in a hopeless squeeze and the caller should fall back
    /// (typically to a "minimize maximum penetration" 3D LP — for our scale
    /// the result-as-is is usually fine).
    /// </returns>
    public static int Solve(System.Collections.Generic.List<Line> lines, float maxSpeed,
        Vector2 prefVel, bool optimizeDirection, out Vector2 result)
    {
        // Clamp the initial guess to the speed circle.
        float prefLen = prefVel.Length();
        if (optimizeDirection)
        {
            // When we're solving the recovery LP (minimize max penetration),
            // we want the fastest possible velocity along prefVel's direction.
            result = prefLen > 1e-9f ? prefVel / prefLen * maxSpeed : Vector2.Zero;
        }
        else if (prefLen > maxSpeed)
        {
            result = prefVel / prefLen * maxSpeed;
        }
        else
        {
            result = prefVel;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            // Half-plane constraint: cross(dir, vel - point) >= 0.
            if (Cross(lines[i].Direction, lines[i].Point - result) > 0f)
            {
                // Current result violates line i — re-solve along that line,
                // intersected with all previous lines and the speed circle.
                Vector2 tempResult = result;
                if (!SolveAlongLine(lines, i, maxSpeed, prefVel, optimizeDirection, ref result))
                {
                    result = tempResult;
                    return i;  // infeasible at line i
                }
            }
        }
        return lines.Count;
    }

    /// <summary>
    /// Project onto line[lineNo] subject to all previous lines and the
    /// speed circle. Returns false if the constraints are infeasible (the
    /// caller falls back to its prior result).
    /// </summary>
    private static bool SolveAlongLine(System.Collections.Generic.List<Line> lines, int lineNo,
        float maxSpeed, Vector2 prefVel, bool optimizeDirection, ref Vector2 result)
    {
        var line = lines[lineNo];
        float dotPD = Vector2.Dot(line.Point, line.Direction);
        float disc = dotPD * dotPD + maxSpeed * maxSpeed - line.Point.LengthSquared();
        if (disc < 0f) return false;  // line entirely outside speed circle

        float sqDisc = MathF.Sqrt(disc);
        float tLeft = -dotPD - sqDisc;
        float tRight = -dotPD + sqDisc;

        for (int i = 0; i < lineNo; i++)
        {
            var prior = lines[i];
            float den = Cross(line.Direction, prior.Direction);
            float num = Cross(prior.Direction, line.Point - prior.Point);
            if (MathF.Abs(den) <= 1e-9f)
            {
                // Parallel: either always satisfied or never.
                if (num < 0f) return false;
                continue;
            }
            float t = num / den;
            if (den >= 0f)
                tRight = MathF.Min(tRight, t);
            else
                tLeft = MathF.Max(tLeft, t);
            if (tLeft > tRight) return false;
        }

        if (optimizeDirection)
        {
            // Maximize speed along prefVel direction. If prefVel matches
            // line direction, take tRight; else tLeft.
            float t = Vector2.Dot(prefVel, line.Direction) > 0f ? tRight : tLeft;
            result = line.Point + line.Direction * t;
        }
        else
        {
            // Minimize distance to prefVel: project prefVel onto the line,
            // clamp t to [tLeft, tRight].
            float t = Vector2.Dot(line.Direction, prefVel - line.Point);
            t = MathF.Min(tRight, MathF.Max(tLeft, t));
            result = line.Point + line.Direction * t;
        }
        return true;
    }

    /// <summary>2D cross product — z-component of the 3D cross.</summary>
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
