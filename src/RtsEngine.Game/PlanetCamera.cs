using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Orbit camera for the planet edit view. Owns azimuth/elevation/distance
/// state, scroll-target lerp, RTS tilt angle, and MVP construction.
///
/// <b>Tilt model</b> — the RTS tilt rotates forward inside the meridian
/// plane (spanned by camDir = radial-out and southTangent = downhill-toward-
/// south). The signed tilt angle is <c>-TiltAngle(distance)</c>, i.e. tilt
/// goes <i>negative</i> as zoom increases:
///   θ = 0      →  forward = -camDir              (orbit, looking at center)
///   θ = -θmax  →  forward = -camDir·cos θ + southTangent·sin θ
///                 = -camDir·cos|θ| - southTangent·sin|θ|  (RTS pose,
///                 tilted toward the *north* tangent)
///
/// The reason it tilts north and not south: southTangent.Y = -cos(elev) is
/// negative across the usable elevation range, so tilting forward toward
/// +southTangent rotates it into the world-down hemisphere — incompatible
/// with a world-Y-up convention and an upside-down render across the
/// orbit ↔ RTS handoff. Negating θ flips the tilt into the world-up
/// hemisphere, matching the solar-system camera's orientation.
///
/// Up is constant: <c>up = -southTangent</c> (= world+Y at equator,
/// projecting toward the north pole elsewhere — same convention as
/// <see cref="SolarSystemRenderer"/>'s <c>(0,1,0)</c>). It doesn't need to
/// rotate with tilt because <c>CreateLookAt</c> orthonormalizes against
/// forward; passing the same north-tangent hint at every distance makes
/// the zoom-out transition continuous with the orbit camera.
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

    /// <summary>Forward direction at the given effective distance. The tilt
    /// angle is negated so forward rotates toward the north tangent
    /// (-southTangent) at full RTS zoom rather than the south tangent —
    /// see the class docstring for why that matters. The distance parameter
    /// lets transitions ask for the basis at a synthetic camera position
    /// instead of the camera's stored <see cref="Distance"/>.</summary>
    public Vector3 Forward(float distance)
    {
        float theta = -TiltAngle(distance);
        var (camDir, southTangent) = MeridianFrame();
        return -camDir * MathF.Cos(theta) + southTangent * MathF.Sin(theta);
    }
    public Vector3 Forward() => Forward(Distance);

    /// <summary>Camera screen-up. Always the north tangent (-southTangent),
    /// independent of distance and tilt — same convention as the solar-system
    /// camera, so the orbit ↔ RTS handoff is continuous.
    /// <c>CreateLookAt</c> orthonormalizes against forward, so the constant
    /// hint is sufficient even though forward rotates with tilt. The
    /// distance parameter is unused but kept for API symmetry with
    /// <see cref="Forward"/>.</summary>
    public Vector3 Up(float distance)
    {
        var (_, southTangent) = MeridianFrame();
        return -southTangent;
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
        => 90f + TiltAngle() * (180f / MathF.PI);

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
