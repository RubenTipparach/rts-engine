using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Mode-switch animation between solar-system and planet-edit views. Owns
/// the transition state (animation progress, focused planet position, planet-
/// ready flag) and renders the in-progress frame each tick. GameEngine kicks
/// off transitions via <see cref="BeginZoomIn"/> / <see cref="BeginZoomOut"/>,
/// then calls <see cref="RenderAndAdvance"/> each frame while
/// <see cref="IsActive"/>; when that returns true the transition just
/// completed and GameEngine handles the mode change + chain logic.
/// </summary>
public sealed class ModeTransition
{
    private readonly PlanetCamera _camera;
    private readonly IRenderBackend _app;
    private readonly SolarSystemRenderer? _solarSystem;
    private readonly EngineConfig _config;

    /// <summary>Visible-orbit-ring fade band. Below Near alpha = 0; above Far
    /// alpha = 1. Used here for the zoom-out backdrop fade and mirrored in
    /// GameEngine's normal-tick backdrop draw — kept identical so the ring
    /// alpha is continuous through trigger → transition → handoff.</summary>
    public const float RingFadeNear = 20f;
    public const float RingFadeFar = 90f;

    private const float TransitionDuration = 1.5f;

    public bool IsActive { get; private set; }
    public EditorMode Target { get; private set; }
    public Vector3 PlanetWorldPos { get; private set; }

    /// <summary>Set true by GameEngine once the planet renderer is ready (mesh
    /// built + textures loaded). Zoom-in completion waits on this so we don't
    /// snap to PlanetEdit before the planet can actually draw.</summary>
    public bool PlanetReady { get; set; }

    private float _start;
    private float _zoomOutStartDist;

    public ModeTransition(PlanetCamera camera, IRenderBackend app,
        SolarSystemRenderer? solarSystem, EngineConfig config)
    {
        _camera = camera;
        _app = app;
        _solarSystem = solarSystem;
        _config = config;
    }

    public void BeginZoomIn(Vector3 planetPos, float elapsed)
    {
        IsActive = true;
        Target = EditorMode.PlanetEdit;
        _start = elapsed;
        PlanetWorldPos = planetPos;
        PlanetReady = false;
    }

    public void BeginZoomOut(Vector3 planetPos, float elapsed)
    {
        IsActive = true;
        Target = EditorMode.SolarSystem;
        _start = elapsed;
        _zoomOutStartDist = _camera.Distance;
        PlanetWorldPos = planetPos;
        PlanetReady = false;
    }

    /// <summary>Render this frame's transition state and advance the
    /// animation. Returns true when the transition just completed this frame
    /// (caller is then responsible for the mode change + any chain logic).</summary>
    public bool RenderAndAdvance(PlanetRenderer planet, string? selectedPlanetConfig,
        float aspectRatio, float elapsed)
    {
        if (_solarSystem == null) return false;

        float t = Math.Clamp((elapsed - _start) / TransitionDuration, 0f, 1f);
        float smooth = t * t * (3f - 2f * t);

        // Track the focused planet's *live* orbital position so the static
        // mesh stays glued to where the dynamic mesh actually is. Without
        // this the static and dynamic copies drift apart during the ~1.5s
        // transition (the body keeps orbiting) and the planet visibly pops.
        _solarSystem.SetTime(elapsed);
        PlanetWorldPos = _solarSystem.GetBodyWorldPosition(selectedPlanetConfig);

        // Hide the focused body's dynamic mesh while the static one is
        // standing in for it — otherwise both would overlap at the same
        // world position once we sync them.
        _solarSystem.HidePlanet(selectedPlanetConfig);

        // Lighting matches the planet's current orbital position so the
        // detailed mesh is correctly lit from the moment it appears, not
        // only after the transition completes.
        var sunDir = PlanetWorldPos.LengthSquared() > 1e-6f
            ? -Vector3.Normalize(PlanetWorldPos)
            : new Vector3(0.5f, 0.7f, 0.5f);
        planet.SetSunDirection(sunDir.X, sunDir.Y, sunDir.Z);

        if (Target == EditorMode.PlanetEdit)
            return RenderZoomIn(planet, t, smooth, aspectRatio, elapsed);
        return RenderZoomOut(planet, selectedPlanetConfig, t, smooth, aspectRatio, elapsed);
    }

    private bool RenderZoomIn(PlanetRenderer planet, float t, float smooth,
        float aspectRatio, float elapsed)
    {
        // Solar system camera zooms toward the planet, ending at the same
        // distance the planet-edit camera will hand off to. Reading these
        // from config keeps the transition continuous — when defaultDistance
        // changed to scale with planet size, this used to be a hardcoded 3
        // and the camera visibly popped backward at the moment of handoff.
        float ssStart = _config.SolarSystemView.DefaultDistance;   // typically 80
        float ssEnd = _camera.DefaultDistance;                     // hands off to planet edit
        float ssDist = ssStart * (1f - smooth) + ssEnd * smooth;
        _solarSystem!.Distance = ssDist;
        _solarSystem.SetFocusTarget(PlanetWorldPos * smooth);

        // Starfield first (clears framebuffer), then solar system
        // background which is already additive.
        var (sFwd, sRight, sUp) = _solarSystem.GetCameraBasis();
        _solarSystem.DrawStarfield(sFwd, sRight, sUp, _solarSystem.FovYDegreesPublic, aspectRatio);

        var ssMvp = _solarSystem.BuildMvpFloats(aspectRatio);
        _solarSystem.Draw(ssMvp);

        // Render textured planet on top, aligned exactly with the noise sphere.
        // Derive planet MVP from solar system MVP + translation so FOV/near/far match.
        if (PlanetReady && smooth > 0.1f)
        {
            // planetMVP = translate(planetPos) * solarSystemMVP
            // This transforms planet-at-origin to the same clip position as
            // the solar system transforms planetPos. Exact alignment, no FOV mismatch.
            var pp = PlanetWorldPos;
            var trans = Matrix4X4.CreateTranslation(
                new Vector3D<float>(pp.X, pp.Y, pp.Z));
            var ssMvpMat = RawToSilkMat(ssMvp);
            var planetMvpMat = Matrix4X4.Multiply(trans, ssMvpMat);
            var planetMvp = MatrixHelper.ToRawFloats(planetMvpMat);

            // Camera distance for LOD
            var camDir = new Vector3(
                MathF.Cos(_solarSystem.Elevation) * MathF.Cos(_solarSystem.Azimuth),
                MathF.Sin(_solarSystem.Elevation),
                MathF.Cos(_solarSystem.Elevation) * MathF.Sin(_solarSystem.Azimuth));
            var ssCamPos = PlanetWorldPos * smooth + camDir * ssDist;
            float planetDist = (ssCamPos - PlanetWorldPos).Length();

            planet.SetCameraPosition(ssCamPos.X - pp.X, ssCamPos.Y - pp.Y, ssCamPos.Z - pp.Z);
            planet.SetTime(elapsed);
            planet.Draw(planetMvp, planetDist, clearFirst: false);
        }

        // Switch when animation done AND planet ready
        if (t >= 1f && PlanetReady)
        {
            Console.WriteLine($"[transition] zoom-in complete; switching Mode → PlanetEdit");
            IsActive = false;
            _camera.Distance = _camera.DefaultDistance;
            _camera.TargetDistance = _camera.Distance;
            _camera.Azimuth = _solarSystem.Azimuth;
            _camera.Elevation = _solarSystem.Elevation;
            _solarSystem.Distance = _config.SolarSystemView.DefaultDistance;
            _solarSystem.SetFocusTarget(Vector3.Zero);
            _solarSystem.HidePlanet(null);
            return true;
        }
        return false;
    }

    private bool RenderZoomOut(PlanetRenderer planet, string? selectedPlanetConfig,
        float t, float smooth, float aspectRatio, float elapsed)
    {
        // The detailed mesh stays at origin (in camera space). Camera
        // pulls back; the rest of the solar system (sun + other planets)
        // is rendered shifted by -planetPos so it visibly orbits past
        // while we zoom away. This matches planet-edit mode rendering,
        // which is also "planet at origin, backdrop shifted". Target
        // distance is the solar-system default so the handoff lands at
        // the same orbital distance the user started from.
        float zoomDist = _zoomOutStartDist * (1f - smooth)
                       + _config.SolarSystemView.DefaultDistance * smooth;
        var camPos = new Vector3(
            zoomDist * MathF.Cos(_camera.Elevation) * MathF.Cos(_camera.Azimuth),
            zoomDist * MathF.Sin(_camera.Elevation),
            zoomDist * MathF.Cos(_camera.Elevation) * MathF.Sin(_camera.Azimuth));
        var planetMvp = _camera.BuildMvpAt(camPos, aspectRatio);

        // Starfield first (clears framebuffer); detailed mesh and
        // backdrop are then additive on top.
        var fwdOut = -Vector3.Normalize(camPos);
        var rightOut = Vector3.Normalize(Vector3.Cross(fwdOut, new Vector3(0, 1, 0)));
        var upOut = Vector3.Cross(rightOut, fwdOut);
        _solarSystem!.DrawStarfield(fwdOut, rightOut, upOut, _camera.FovYDegrees, aspectRatio);

        // Detailed mesh at origin.
        planet.SetCameraPosition(camPos.X, camPos.Y, camPos.Z);
        planet.SetTime(elapsed);
        planet.Draw(planetMvp, zoomDist, clearFirst: false);

        // Backdrop: sun + other bodies, world translated by -planetPos.
        // Same fade curve as planet-edit normal tick so the ring alpha
        // is continuous through trigger → transition → handoff.
        var cameraWorldPosOut = PlanetWorldPos + camPos;
        float ringAlphaOut = Smoothstep(RingFadeNear, RingFadeFar, zoomDist);
        _solarSystem.DrawBackdrop(planetMvp, selectedPlanetConfig, PlanetWorldPos,
            cameraWorldPosOut, ringAlphaOut);

        if (t >= 1f)
        {
            IsActive = false;
            // The shifted-frame camera (origin = planet, distance =
            // SolarSystemView.DefaultDistance along _azimuth/_elevation) is
            // the same world position as a solar-system camera at the same
            // distance focused on planetPos along the same angles. Carry
            // those angles over so the viewing direction doesn't snap.
            _solarSystem.Distance = _config.SolarSystemView.DefaultDistance;
            _solarSystem.Azimuth = _camera.Azimuth;
            _solarSystem.Elevation = _camera.Elevation;
            _solarSystem.SetFocusTarget(PlanetWorldPos);
            _solarSystem.HidePlanet(null);
            return true;
        }
        return false;
    }

    private static Matrix4X4<float> RawToSilkMat(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
