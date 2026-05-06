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

    /// <summary>Fired on key press. Args: key name.</summary>
    event Action<string>? KeyDown;

    /// <summary>
    /// Camera orbit drag — fires for middle-mouse drags and Alt+left drags.
    /// Args: (dx, dy). Args mirror PointerDrag, but the engine routes this
    /// to the orbit camera while leaving plain PointerDrag free for box
    /// select. Backends without these gestures (e.g. WASM in its current
    /// shape) can fall back to firing OrbitDrag for plain PointerDrag too.
    /// </summary>
    event Action<float, float>? OrbitDrag;

    /// <summary>
    /// Fired continuously while a left-drag selection rectangle is being
    /// pulled. Args: (downX, downY, currentX, currentY) in canvas pixels.
    /// Engines render an overlay rectangle here so the player sees the
    /// region they're about to select.
    /// </summary>
    event Action<float, float, float, float>? BoxSelectUpdate;

    /// <summary>
    /// Fired once when a box-select drag releases. Args: (x0, y0, x1, y1)
    /// with x0 ≤ x1 and y0 ≤ y1. The engine multi-selects all units whose
    /// projected screen position falls inside.
    /// </summary>
    event Action<float, float, float, float>? BoxSelectComplete;

    /// <summary>
    /// Fired on a long right-press (held past a threshold). Args: (x, y) in
    /// canvas pixels. The engine pops up a unit context menu here. Short
    /// right clicks still fire PointerClick(button=2) so the move-order
    /// path is unchanged.
    /// </summary>
    event Action<float, float>? ContextMenuRequested;

    /// <summary>Fired when an engine-managed UI button is clicked. Args: button ID.</summary>
    event Action<string>? UIButtonClick;

    /// <summary>Create or update a platform-native UI button overlay.</summary>
    void CreateUIButton(string id, string text, string cssJson);
    void ShowUIButton(string id, bool visible);

    /// <summary>
    /// Display (or hide) a multi-line profiler text overlay. Implementations
    /// should pick whatever native presentation fits — WASM uses an HTML
    /// `<pre>` overlay with a Copy button; desktop dumps to stdout. Default
    /// is no-op so backends can opt in.
    /// </summary>
    void ShowProfilerOverlay(string text, bool visible) { }

    /// <summary>
    /// Copy the given string to the user's clipboard (WASM:
    /// navigator.clipboard.writeText; desktop falls back to stdout). No-op by
    /// default for backends that don't have clipboard access.
    /// </summary>
    void CopyTextToClipboard(string text) { }

    /// <summary>Fired when the user clicks the Copy button on the profiler
    /// overlay. The host responds by snapshotting current stats and routing
    /// them to <see cref="CopyTextToClipboard"/>.</summary>
    event Action? ProfilerCopyRequested;

    /// <summary>
    /// Render any platform-managed UI on top of the current frame. WASM uses
    /// HTML DOM buttons that draw themselves outside the canvas, so this is
    /// a no-op there. Desktop rasterises an EngineUI quad mesh.
    /// </summary>
    void RenderUI() { }

    /// <summary>
    /// Returns true if the click at (cx, cy) hit a platform-managed UI element
    /// (e.g. a desktop EngineUI button). When true, the engine should treat
    /// the click as consumed by the UI and not pass it to the world. The HTML
    /// overlay is its own DOM layer above the canvas, so WASM never sees those
    /// clicks reach the engine — it returns false here.
    /// </summary>
    bool ConsumesPointerClick(float cx, float cy) => false;
}
