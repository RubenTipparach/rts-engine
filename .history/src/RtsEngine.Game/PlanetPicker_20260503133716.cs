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

    /// <summary>Cell whose center is closest to the world-space point where
    /// the camera ray through the cursor pixel lands on the planet sphere.
    /// Returns null if the ray misses the sphere entirely (cursor over empty
    /// space at the silhouette / off the planet's disc).
    ///
    /// This used to be a screen-space minimum-distance scan, which silently
    /// picked behind-horizon cells whose projected centers happened to land
    /// near the cursor — fine at orbit altitude where the planet fills the
    /// screen, but it flickered badly under RTS tilt because perspective
    /// foreshortening crowds far cells near the horizon line. Casting an
    /// actual ray against the sphere is both more correct and more stable.
    /// </summary>
    public int? PickCell(float canvasX, float canvasY)
    {
        var mesh = _meshProvider();
        // if (!TryRaycastSurface(canvasX, canvasY, mesh.Radius, out var hitDir))
        //     return null;

        // Closest cell to the hit direction = max dot product against the
        // unit-length cell-center direction. O(N) over cells; ~5k cells is
        // a few µs, fine on a per-pointer-move budget.
        int best = -1;
        float bestDot = float.MinValue;
        for (int i = 0; i < mesh.CellCount; i++)
        {
            float d = Vector3.Dot(mesh.GetCellCenter(i), hitDir);
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best >= 0 ? best : null;
    }

    /// <summary>Cast a ray from the camera through the cursor pixel and
    /// intersect it with a sphere of <paramref name="radius"/> centered at
    /// the planet origin. Returns the surface direction (normalized hit
    /// point) on success. Used by both cell picking and any screen→world
    /// query that needs a stable surface point under camera tilt.</summary>
    private bool TryRaycastSurface(float canvasX, float canvasY, float radius, out Vector3 hitDir)
    {
        hitDir = Vector3.Zero;
        float w = _app.CanvasWidth, h = _app.CanvasHeight;
        if (w < 1f || h < 1f) return false;

        // Orthonormalize the camera basis from Forward+Up — Up() is a hint,
        // not guaranteed perpendicular to forward at nonzero tilt.
        var fwd = Vector3.Normalize(_camera.Forward());
        var right = Vector3.Normalize(Vector3.Cross(fwd, _camera.Up()));
        var up = Vector3.Cross(right, fwd);
        float tan = MathF.Tan(_camera.FovYDegrees * MathF.PI / 360f);

        var rayDir = Vector3.Normalize(fwd
            + (2f * canvasX / w - 1f) * tan * (w / h) * right
            + (1f - 2f * canvasY / h) * tan * up);

        // Ray-sphere: |camPos + t·rayDir|² = radius².
        var camPos = _camera.Position();
        float b = Vector3.Dot(camPos, rayDir);
        float disc = b * b - camPos.LengthSquared() + radius * radius;
        if (disc < 0f) return false;
        float t = -b - MathF.Sqrt(disc);
        if (t < 0f) return false;
        hitDir = Vector3.Normalize(camPos + rayDir * t);
        return true;
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
