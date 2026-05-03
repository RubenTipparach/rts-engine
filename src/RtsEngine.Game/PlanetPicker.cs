using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Screen→world hit-testing for the planet edit view. Pure projection: given
/// canvas-space coords, returns the unit / cell / set of units that the ray
/// hits. Owns no selection state — callers decide what to do with the result.
/// Depends on <see cref="PlanetCamera"/> for the MVP, the renderer for canvas
/// dims, and provider delegates for the live mesh + unit list (so it doesn't
/// couple to <c>PlanetRenderer</c> or <c>RtsState</c>).
/// </summary>
public sealed class PlanetPicker
{
    private readonly IRenderBackend _app;
    private readonly PlanetCamera _camera;
    private readonly Func<PlanetMesh> _meshProvider;
    private readonly Func<IReadOnlyList<SpawnedUnit>> _unitsProvider;

    private const float UnitPickRadiusPixels = 18f;
    private const float UnitFacingThreshold = -0.05f;
    private const float UnitFacingThresholdBoxSelect = -0.1f;
    private const float CellFacingThreshold = -0.05f;

    public PlanetPicker(IRenderBackend app, PlanetCamera camera,
        Func<PlanetMesh> meshProvider, Func<IReadOnlyList<SpawnedUnit>> unitsProvider)
    {
        _app = app;
        _camera = camera;
        _meshProvider = meshProvider;
        _unitsProvider = unitsProvider;
    }

    /// <summary>Project every spawned unit to screen, return the instance id
    /// of the nearest one within a generous pick radius (units are visually
    /// small, so we forgive imprecise clicks). -1 if nothing's close enough.</summary>
    public int PickUnit(float canvasX, float canvasY)
    {
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return -1;

        var mvp = FloatsToMatrix(_camera.BuildMvp(w / h));
        var camDir = Vector3.Normalize(_camera.Position());

        float bestDist = UnitPickRadiusPixels * UnitPickRadiusPixels;
        int best = -1;

        foreach (var unit in _unitsProvider())
        {
            // Cull units on the far side of the planet.
            if (Vector3.Dot(unit.SurfaceUp, camDir) < UnitFacingThreshold) continue;

            var clip = Vector4.Transform(new Vector4(unit.SurfacePoint, 1f), mvp);
            if (clip.W <= 0.001f) continue;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;
            float d = (sx - canvasX) * (sx - canvasX) + (sy - canvasY) * (sy - canvasY);
            if (d < bestDist) { bestDist = d; best = unit.InstanceId; }
        }
        return best;
    }

    /// <summary>Nearest mesh cell (by screen distance) to the given canvas
    /// coords, or null if the click landed off-mesh. Back-facing cells are
    /// culled so clicks don't tunnel through the planet.</summary>
    public int? PickCell(float canvasX, float canvasY)
    {
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return null;

        var mvp = FloatsToMatrix(_camera.BuildMvp(w / h));
        var camDir = Vector3.Normalize(_camera.Position());
        var mesh = _meshProvider();

        float bestDist = float.MaxValue;
        int bestCell = -1;

        for (int i = 0; i < mesh.CellCount; i++)
        {
            if (Vector3.Dot(mesh.GetCellCenter(i), camDir) < CellFacingThreshold) continue;

            float cellH = mesh.Radius + mesh.GetLevel(i) * mesh.StepHeight;
            var center = mesh.GetCellCenter(i) * cellH;
            var clip = Vector4.Transform(new Vector4(center, 1f), mvp);
            if (clip.W <= 0.001f) continue;

            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;
            float d = (sx - canvasX) * (sx - canvasX) + (sy - canvasY) * (sy - canvasY);
            if (d < bestDist) { bestDist = d; bestCell = i; }
        }

        return bestCell >= 0 ? bestCell : null;
    }

    /// <summary>Project every spawned unit through the current planet MVP and
    /// return the instance ids of those whose screen-space position falls
    /// inside the rect. Units behind the planet are dropped so the box-select
    /// can't grab them through solid surface.</summary>
    public List<int> PickUnitsInRect(float x0, float y0, float x1, float y1)
    {
        var hits = new List<int>();
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1 || h < 1) return hits;

        var mvp = FloatsToMatrix(_camera.BuildMvp(w / h));
        var camDir = Vector3.Normalize(_camera.Position());

        foreach (var unit in _unitsProvider())
        {
            if (Vector3.Dot(unit.SurfaceUp, camDir) < UnitFacingThresholdBoxSelect) continue;

            var clip = Vector4.Transform(new Vector4(unit.SurfacePoint, 1f), mvp);
            if (clip.W <= 0.001f) continue;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;

            if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1)
                hits.Add(unit.InstanceId);
        }
        return hits;
    }

    private static Matrix4x4 FloatsToMatrix(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
}
