namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Platform abstraction for the application shell — equivalent to sokol_app.
/// Handles window/canvas lifecycle, the frame loop, and input forwarding.
/// Does NOT handle rendering — that goes through the GL proxy directly.
/// </summary>
public interface IRenderBackend : IDisposable
{
    float CanvasWidth { get; }
    float CanvasHeight { get; }
    float AspectRatio => CanvasHeight > 0 ? CanvasWidth / CanvasHeight : 16f / 9f;

    void StartLoop(Func<Task> onTick);
    void StopLoop();

    event Action<float, float>? PointerDrag;
    event Action<float>? ScrollWheel;
    event Action? TapStart;
    event Action? ResetRequested;
}
