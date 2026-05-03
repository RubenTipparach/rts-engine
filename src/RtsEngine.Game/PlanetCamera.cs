using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Orbit camera for the planet edit view. Owns azimuth/elevation/distance
/// state, the scroll-target lerp, the RTS tilt blend, the look-at math, and
/// MVP construction. GameEngine routes input deltas in (Orbit/Scroll/Update),
/// reads back Position / BuildMvp / diagnostics, and otherwise stays out of
/// the camera's business.
/// </summary>
public sealed class PlanetCamera
{
    private readonly EngineConfig _config;
    private readonly Func<float> _radiusProvider;

    private const float PixelsToRadians = 0.005f;
    private const float ElevationLimit = 1.4f;
    private const float FovYDegreesValue = 45f;
    private static readonly float FocalYValue =
        1f / MathF.Tan(FovYDegreesValue * MathF.PI / 360f);

    public float Azimuth { get; set; }
    public float Elevation { get; set; } = 0.4f;
    public float Distance { get; set; } = 3.0f;
    /// <summary>Scroll updates this; the per-tick zoom lerp chases it from
    /// <see cref="Distance"/> at <c>RtsCamera.ZoomLerpRate</c> per second.
    /// Decoupling the two gives smooth zoom without losing input snappiness.</summary>
    public float TargetDistance { get; set; } = 3.0f;

    public float FovYDegrees => FovYDegreesValue;
    public float FocalY => FocalYValue;
    public float MaxDistance => _config.PlanetEditView.MaxDistance;
    public float AutoZoomOutThreshold => _config.PlanetEditView.AutoZoomOutThreshold;
    public float DefaultDistance => _config.PlanetEditView.DefaultDistance;

    public PlanetCamera(EngineConfig config, Func<float> radiusProvider)
    {
        _config = config;
        _radiusProvider = radiusProvider;
    }

    public Vector3 Position()
    {
        float cx = Distance * MathF.Cos(Elevation) * MathF.Cos(Azimuth);
        float cy = Distance * MathF.Sin(Elevation);
        float cz = Distance * MathF.Cos(Elevation) * MathF.Sin(Azimuth);
        return new Vector3(cx, cy, cz);
    }

    public void Orbit(float dxPixels, float dyPixels)
    {
        Azimuth += dxPixels * PixelsToRadians;
        Elevation += dyPixels * PixelsToRadians;
        Elevation = Math.Clamp(Elevation, -ElevationLimit, ElevationLimit);
    }

    /// <summary>Logarithmic scroll: each tick changes altitude (above the
    /// surface) by a fixed percentage rather than absolute distance, so
    /// subjective zoom speed feels uniform regardless of altitude. Writes
    /// <see cref="TargetDistance"/>; <see cref="Update"/> chases it.</summary>
    public void Scroll(float delta)
    {
        float radius = _radiusProvider();
        float altitude = MathF.Max(1e-4f, TargetDistance - radius);
        altitude -= delta * altitude * _config.RtsCamera.ScrollIncrement;
        TargetDistance = radius + altitude;
        TargetDistance = Math.Clamp(TargetDistance, MinDistance(), MaxDistance);
    }

    /// <summary>dt-corrected exponential lerp toward <see cref="TargetDistance"/>.
    /// Caller passes a frame-bounded dt (clamped to avoid teleport on stutter).</summary>
    public void Update(float dt)
    {
        if (dt <= 0f) return;
        float a = 1f - MathF.Exp(-_config.RtsCamera.ZoomLerpRate * dt);
        Distance += (TargetDistance - Distance) * a;
    }

    /// <summary>Closest the camera may approach, in world units. Sits just
    /// above the highest possible terrain so ground-level RTS zoom never dips
    /// below the surface.</summary>
    public float MinDistance()
    {
        float radius = _radiusProvider();
        return radius * (1f + _config.RtsCamera.GroundClearance);
    }

    public float ZoomPercent() => ZoomPercent(Distance);

    /// <summary>Zoom level expressed as a fraction in log-altitude space —
    /// 0 = at the auto-zoom-out threshold (max zoom out), 1 = at the orbit
    /// floor (max zoom in). Same parameterization as the on-screen indicator
    /// bar so designers can tune <c>RtsCamera.TiltStartZoomPercent</c>
    /// against numbers they can see.</summary>
    public float ZoomPercent(float distance)
    {
        float radius = _radiusProvider();
        float altitude = MathF.Max(1e-4f, distance - radius);
        float minAlt = MathF.Max(1e-4f, MinDistance() - radius);
        float maxAlt = MathF.Max(minAlt + 1e-3f, AutoZoomOutThreshold - radius);
        float pct = 1f - (MathF.Log(altitude) - MathF.Log(minAlt))
                       / (MathF.Log(maxAlt) - MathF.Log(minAlt));
        return Math.Clamp(pct, 0f, 1f);
    }

    public float TiltBlend() => TiltBlend(Distance);

    /// <summary>RTS tilt blend driven by zoom percentage rather than raw
    /// altitude — gives designers two clean tunables (start% / full%) that
    /// don't change meaning when the planet radius does.</summary>
    public float TiltBlend(float distance)
    {
        return Smoothstep(_config.RtsCamera.TiltStartZoomPercent,
                          _config.RtsCamera.TiltFullZoomPercent,
                          ZoomPercent(distance));
    }

    /// <summary>Tilted look-at target for RTS-style ground view. At high
    /// altitude returns the planet center; near the surface the target slides
    /// to a point ahead on the ground so the camera tilts forward rather than
    /// staring straight down.</summary>
    public Vector3 LookAtTarget(Vector3 camPos)
    {
        float blend = TiltBlend(camPos.Length());
        if (blend <= 0f) return Vector3.Zero;

        float radius = _radiusProvider();
        float ce = MathF.Cos(Elevation), se = MathF.Sin(Elevation);
        float ca = MathF.Cos(Azimuth), sa = MathF.Sin(Azimuth);
        var southTangent = new Vector3(se * ca, -ce, se * sa);
        var camDir = Vector3.Normalize(camPos);
        var groundTarget = camDir * radius + southTangent * (radius * _config.RtsCamera.LookAhead);
        return groundTarget * blend;
    }

    /// <summary>World-up reference for the LookAt basis. Slerp from worldY
    /// (orbital view: north stays at the top of the screen) to camDir (RTS
    /// surface view: surface normal is "up"). Driven by the same tilt blend
    /// the look-at uses, so worldUp changes in lockstep with the look-at
    /// swing — no snap, no roll-flip, no upside-down terrain at full tilt.</summary>
    public Vector3 ResolveUp(Vector3 camPos, Vector3 camDir)
    {
        var worldY = new Vector3(0, 1, 0);
        float blend = TiltBlend(camPos.Length());

        float cosOmega = Math.Clamp(Vector3.Dot(worldY, camDir), -0.9999f, 0.9999f);
        float omega = MathF.Acos(cosOmega);
        if (omega < 1e-3f) return camDir;

        float sinOmega = MathF.Sin(omega);
        float a = MathF.Sin((1f - blend) * omega) / sinOmega;
        float b = MathF.Sin(blend * omega) / sinOmega;
        var up = a * worldY + b * camDir;
        if (up.LengthSquared() < 1e-6f) return camDir;
        return Vector3.Normalize(up);
    }

    public float[] BuildMvp(float aspectRatio) => BuildMvpAt(Position(), aspectRatio);

    public float[] BuildMvpAt(Vector3 camPos, float aspectRatio)
    {
        var lookAt = LookAtTarget(camPos);
        var camDir = camPos.LengthSquared() > 1e-8f
            ? Vector3.Normalize(camPos) : new Vector3(0, 1, 0);
        var worldUp = ResolveUp(camPos, camDir);

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(camPos.X, camPos.Y, camPos.Z),
            new Vector3D<float>(lookAt.X, lookAt.Y, lookAt.Z),
            new Vector3D<float>(worldUp.X, worldUp.Y, worldUp.Z));
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(FovYDegreesValue), aspectRatio, 0.01f, 10000.0f);
        return MatrixHelper.ToRawFloats(Matrix4X4.Multiply(view, proj));
    }

    /// <summary>Diagnostic — camera pitch in degrees relative to the local
    /// horizon at the camera's surface point.
    ///   0°   = camera staring along the horizon
    ///   90°  = camera looking straight down
    ///  -90°  = camera looking straight up</summary>
    public float PitchDegrees()
    {
        var camPos = Position();
        var lookAt = LookAtTarget(camPos);
        var view = lookAt - camPos;
        if (view.LengthSquared() < 1e-8f) return 0f;
        view = Vector3.Normalize(view);
        var surfaceNormal = camPos.LengthSquared() > 1e-8f
            ? Vector3.Normalize(camPos) : new Vector3(0, 1, 0);
        float dot = Math.Clamp(Vector3.Dot(view, surfaceNormal), -1f, 1f);
        return -MathF.Asin(dot) * (180f / MathF.PI);
    }

    /// <summary>Diagnostic — dot of the resolved camera up axis with worldY +
    /// with the radial outward direction. Both values should evolve smoothly
    /// through the tilt sweep.</summary>
    public (float upDotY, float upDotR) UpDots()
    {
        var camPos = Position();
        var lookAt = LookAtTarget(camPos);
        var fwdRaw = lookAt - camPos;
        if (fwdRaw.LengthSquared() < 1e-8f) return (1f, 0f);
        var fwd = Vector3.Normalize(fwdRaw);
        var camDir = camPos.LengthSquared() > 1e-8f
            ? Vector3.Normalize(camPos) : new Vector3(0, 1, 0);
        var worldUp = ResolveUp(camPos, camDir);

        var right = Vector3.Cross(fwd, worldUp);
        if (right.LengthSquared() < 1e-6f)
            return (Vector3.Dot(worldUp, new Vector3(0, 1, 0)), Vector3.Dot(worldUp, camDir));
        right = Vector3.Normalize(right);
        var camUp = Vector3.Normalize(Vector3.Cross(right, fwd));
        return (Vector3.Dot(camUp, new Vector3(0, 1, 0)), Vector3.Dot(camUp, camDir));
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
