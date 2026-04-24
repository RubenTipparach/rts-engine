namespace RtsEngine.Core;

/// <summary>
/// Platform abstraction for the application shell — equivalent to sokol_app.
/// Handles window/canvas lifecycle, the frame loop, and input forwarding.
/// </summary>
public interface IRenderBackend : IDisposable
{
    float CanvasWidth { get; }
    float CanvasHeight { get; }
    float AspectRatio => CanvasHeight > 0 ? CanvasWidth / CanvasHeight : 16f / 9f;

    void StartLoop(Func<Task> onTick);
    void StopLoop();

    event Action? PointerDown;
    event Action<float, float>? PointerDrag;
    event Action? PointerUp;

    /// <summary>Fired on click (mouseup with minimal movement). Args: (canvasX, canvasY, button). button: 0=left, 2=right.</summary>
    event Action<float, float, int>? PointerClick;

    /// <summary>Fired on scroll wheel. Args: (deltaY). Positive = scroll up / zoom in.</summary>
    event Action<float>? Scroll;

    /// <summary>Fired on pointer move. Args: (canvasX, canvasY) in device pixels.</summary>
    event Action<float, float>? PointerMove;

    /// <summary>Fired on key press. Args: key name (e.g. "Tab", "Backspace", "Escape").</summary>
    event Action<string>? KeyDown;
}
