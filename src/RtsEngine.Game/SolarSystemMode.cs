using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>Solar-system view: sun + planets + orbit rings. Click a planet
/// to fire <see cref="PlanetPicked"/>; Tab fires <see cref="TabPressed"/>.
/// GameEngine subscribes to those to start a zoom-in transition or switch
/// to the star map.</summary>
public sealed class SolarSystemMode : IEditorMode
{
    private readonly IRenderBackend _app;
    private readonly SolarSystemRenderer _solarSystem;

    public event Action<string, Vector3>? PlanetPicked;
    public event Action? TabPressed;

    public SolarSystemMode(IRenderBackend app, SolarSystemRenderer solarSystem)
    {
        _app = app;
        _solarSystem = solarSystem;
    }

    public void OnClick(float canvasX, float canvasY, int button)
    {
        if (button != 0) return;
        var (config, pos, _) = _solarSystem.PickPlanet(canvasX, canvasY,
            _app.CanvasWidth, _app.CanvasHeight);
        Console.Error.WriteLine($"[click] solar pick at ({canvasX},{canvasY}) → {config ?? "<none>"}");
        if (config != null) PlanetPicked?.Invoke(config, pos);
    }

    public void OnMove(float canvasX, float canvasY) { }

    public void OnKey(string key)
    {
        if (key == "Tab") TabPressed?.Invoke();
    }

    public Task RenderTick(float elapsed)
    {
        _solarSystem.SetTime(elapsed);
        var mvp = _solarSystem.BuildMvpFloats(_app.AspectRatio);

        // Starfield first (clears), then the rest of the system stacks on top.
        var (fwd, right, up) = _solarSystem.GetCameraBasis();
        _solarSystem.DrawStarfield(fwd, right, up, _solarSystem.FovYDegreesPublic, _app.AspectRatio);

        _solarSystem.Draw(mvp);
        return Task.CompletedTask;
    }
}
