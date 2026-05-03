using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Orbit camera for the planet edit view. Owns azimuth/elevation/distance
/// state, scroll-target lerp, RTS tilt angle, and MVP construction.
///
/// <b>Tilt model</b> — the RTS tilt is parameterized as an explicit angle
/// θ from straight-down, in the camera's meridian plane (the plane spanned
/// by the radial-up direction camDir and the south-tangent direction).
///   θ = 0     →  forward = -camDir          (looking at planet center)
///   θ = θmax  →  forward tilted toward southTangent (RTS view)
///
/// At any θ, forward and screen-up are co-planar in the meridian plane and
/// rotate together as a rigid frame:
///   forward(θ) = -camDir·cos θ + southTangent·sin θ
///   up(θ)      = -southTangent·cos θ - camDir·sin θ
/// They're perpendicular by construction (forward·up = 0 for all θ — the
/// sign on the camDir term matters: with +camDir·sin θ the pair becomes
/// near-antiparallel at moderate tilt, which silently broke CreateLookAt's
/// basis and flipped the view upside-down). At θ=0 up = -southTangent,
/// which equals world+Y at the equator and projects toward the north pole
/// elsewhere — the same convention the solar-system camera uses.
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

    /// <summary>RTS tilt blend, in [0, 1]. Smoothstep-driven by zoom percent.
    /// Multiplied by <c>MaxTiltDegrees</c> to give the actual tilt angle.</summary>
    public float TiltBlend(float distance)
    {
        return Smoothstep(_config.RtsCamera.TiltStartZoomPercent,
                          _config.RtsCamera.TiltFullZoomPercent,
                          ZoomPercent(distance));
    }

    /// <summary>Tilt angle in radians. 0 = looking straight at planet center;
    /// <c>MaxTiltDegrees</c> at full zoom for the RTS pose.</summary>
    public float TiltAngle(float distance) =>
        TiltBlend(distance) * _config.RtsCamera.MaxTiltDegrees * (MathF.PI / 180f);
    public float TiltAngle() => TiltAngle(Distance);

    /// <summary>Local orthonormal frame at the camera's azimuth/elevation:
    /// camDir = radial outward, southTangent = downhill in latitude (toward
    /// south pole), east = orbit-azimuthal tangent. <c>camDir × southTangent
    /// = -east</c>, so (camDir, southTangent, -east) is a right-handed
    /// basis.</summary>
    private (Vector3 camDir, Vector3 southTangent) MeridianFrame()
    {
        float ce = MathF.Cos(Elevation), se = MathF.Sin(Elevation);
        float ca = MathF.Cos(Azimuth), sa = MathF.Sin(Azimuth);
        return (
            new Vector3(ce * ca, se, ce * sa),
            new Vector3(se * ca, -ce, se * sa));
    }

    /// <summary>Forward direction at the given effective distance (used for
    /// transitions where the renderer wants the basis at a synthetic
    /// camera position, not the camera's current Distance).</summary>
    public Vector3 Forward(float distance)
    {
        float theta = TiltAngle(distance);
        var (camDir, southTangent) = MeridianFrame();
        return -camDir * MathF.Cos(theta) + southTangent * MathF.Sin(theta);
    }
    public Vector3 Forward() => Forward(Distance);

    /// <summary>Camera screen-up at the given effective distance. Rotates in
    /// lockstep with <see cref="Forward"/> in the meridian plane, so the
    /// pair stays perpendicular without any reference-vector gymnastics.</summary>
    public Vector3 Up(float distance)
    {
        float theta = TiltAngle(distance);
        var (camDir, southTangent) = MeridianFrame();
        return southTangent * MathF.Cos(theta) - camDir * MathF.Sin(theta);
    }
    
    public Vector3 Up() => Up(Distance);

    public float[] BuildMvp(float aspectRatio) => BuildMvpAt(Position(), aspectRatio);

    public float[] BuildMvpAt(Vector3 camPos, float aspectRatio)
    {
        // Use camPos magnitude as effective distance so transition frames
        // (which pass synthetic camera positions during zoom-out) get the
        // tilt that matches their distance, not the camera's stored one.
        float effectiveDist = camPos.Length();
        var forward = Forward(effectiveDist);
        var up = Up(effectiveDist);
        var lookAt = camPos + forward;

        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(camPos.X, camPos.Y, camPos.Z),
            new Vector3D<float>(lookAt.X, lookAt.Y, lookAt.Z),
            new Vector3D<float>(up.X, up.Y, up.Z));
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(FovYDegreesValue), aspectRatio, 0.01f, 10000.0f);
        return MatrixHelper.ToRawFloats(Matrix4X4.Multiply(view, proj));
    }

    /// <summary>Diagnostic — pitch in degrees below the local horizon.
    ///   90° = looking straight down at the planet center (no tilt)
    ///   0°  = looking horizontally along the surface (max tilt of 90°)
    /// Default config caps at 59° tilt → 31° below horizon.</summary>
    public float PitchDegrees()
        => 90f - TiltAngle() * (180f / MathF.PI);

    /// <summary>Diagnostic — dot of the camera up axis with worldY + with
    /// the radial outward direction. Used to be a roll-flip detector when
    /// the basis was computed via reference-vector slerps; now it's just a
    /// readout since <see cref="Up"/> can't flip by construction.</summary>
    public (float upDotY, float upDotR) UpDots()
    {
        var (camDir, _) = MeridianFrame();
        var up = Up();
        return (Vector3.Dot(up, new Vector3(0, 1, 0)), Vector3.Dot(up, camDir));
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
