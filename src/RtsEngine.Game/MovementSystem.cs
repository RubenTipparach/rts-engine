using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// Per-tick advance for units that have a path queued. Held as a static
/// "system" so the data layout stays plain — when entity counts grow this
/// is what an ECS migration would lift unchanged: data lives on the unit,
/// behavior lives in the tick.
///
/// Movement is great-circle interpolation between cell centers at their
/// surface heights. Step size = unit speed × dt; arrival at a cell snaps
/// the path index forward.
/// </summary>
public static class MovementSystem
{
    public static void Tick(RtsState state, PlanetMesh mesh, RtsConfig config, float dt)
    {
        if (dt <= 0f) return;
        const float arrivalEpsilon = 0.0015f;

        foreach (var unit in state.Units)
        {
            var path = unit.Path;
            if (path == null || unit.PathIndex >= path.Count) continue;

            var def = config.GetUnit(unit.TypeId);
            if (def == null) continue;

            // Step size in world units this tick. Speed is stored in
            // radius-units per second; multiply by mesh radius to convert
            // back to absolute distance the surface vector should advance.
            float stepLen = def.Speed * mesh.Radius * dt;
            int safety = 16;  // bound per-tick segment skips so a huge dt
                              // can't infinite-loop on a tiny path

            while (stepLen > 0f && unit.PathIndex < path.Count && safety-- > 0)
            {
                int targetCell = path[unit.PathIndex];
                Vector3 targetCenter = mesh.GetCellCenter(targetCell);
                float targetH = mesh.GetCenterHeight(targetCell) + 0.002f; // hover slightly
                Vector3 targetPos = targetCenter * targetH;

                Vector3 toTarget = targetPos - unit.SurfacePoint;
                float dist = toTarget.Length();

                if (dist <= stepLen + arrivalEpsilon)
                {
                    // Arrived at this waypoint — snap, advance, keep budget.
                    unit.SurfacePoint = targetPos;
                    unit.SurfaceUp = Vector3.Normalize(targetPos);
                    unit.CellIndex = targetCell;
                    if (toTarget.LengthSquared() > 1e-12f)
                        unit.Heading = Vector3.Normalize(toTarget);
                    unit.PathIndex++;
                    stepLen -= dist;
                    if (unit.PathIndex >= path.Count)
                    {
                        // Goal reached — leave Path on the unit so debug
                        // visualization can still show it. PathIndex >=
                        // Count is the "no more steps to take" signal the
                        // movement loop already checks against.
                        break;
                    }
                    continue;
                }

                Vector3 dir = toTarget / dist;
                unit.SurfacePoint += dir * stepLen;
                unit.SurfaceUp = Vector3.Normalize(unit.SurfacePoint);
                unit.Heading = dir;
                stepLen = 0f;
            }
        }
    }
}
