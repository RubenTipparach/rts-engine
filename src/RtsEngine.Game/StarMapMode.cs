using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>Hierarchical galaxy navigation — galaxy → sector → cluster →
/// group → star. Click drills down into the picked child; Backspace zooms
/// back out; Tab/Escape fires <see cref="ExitRequested"/> to bounce back to
/// the solar system.</summary>
public sealed class StarMapMode : IEditorMode
{
    private readonly IRenderBackend _app;
    private readonly StarMapRenderer _starMap;

    public event Action? ExitRequested;

    public StarMapMode(IRenderBackend app, StarMapRenderer starMap)
    {
        _app = app;
        _starMap = starMap;
    }

    public void OnClick(float canvasX, float canvasY, int button)
    {
        if (button != 0) return;
        int child = _starMap.PickChild(canvasX, canvasY, _app.CanvasWidth, _app.CanvasHeight);
        if (child >= 0)
        {
            _starMap.DrillDown(child);
            _ = _starMap.RebuildMesh();
        }
    }

    public void OnMove(float canvasX, float canvasY) { }

    public void OnKey(string key)
    {
        if (key == "Tab") ExitRequested?.Invoke();
        else if (key == "Backspace")
        {
            _starMap.ZoomOut();
            _ = _starMap.RebuildMesh();
        }
    }

    public Task RenderTick(float elapsed)
    {
        var mvp = _starMap.BuildMvpFloats(_app.AspectRatio);
        _starMap.Draw(mvp);
        return Task.CompletedTask;
    }
}
