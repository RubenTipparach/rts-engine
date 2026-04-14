using Microsoft.JSInterop;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WASM app shell — equivalent to sokol_app.
/// Manages canvas lifecycle, the requestAnimationFrame loop, and input.
/// Does NOT do any rendering — that goes through the GL proxy.
/// </summary>
public sealed class WebGLRenderBackend : IRenderBackend
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<WebGLRenderBackend>? _dotnetRef;

    public float CanvasWidth { get; private set; } = 800;
    public float CanvasHeight { get; private set; } = 600;

    public event Action<float, float>? PointerDrag;
    public event Action<float>? ScrollWheel;
    public event Action? TapStart;
    public event Action? ResetRequested;

    private Func<Task>? _onTick;

    public WebGLRenderBackend(IJSRuntime js)
    {
        _js = js;
    }

    public async Task InitializeAsync(string canvasId)
    {
        _dotnetRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("AppShell.init", canvasId, _dotnetRef);
    }

    public void StartLoop(Func<Task> onTick)
    {
        _onTick = onTick;
        _ = _js.InvokeVoidAsync("AppShell.startLoop");
    }

    public void StopLoop()
    {
        _ = _js.InvokeVoidAsync("AppShell.stopLoop");
    }

    // ── JS → C# callbacks ─────────────────────────────────────────

    [JSInvokable] public async Task GameLoopTick() { if (_onTick != null) await _onTick(); }
    [JSInvokable] public void OnCanvasResize(float w, float h) { CanvasWidth = w; CanvasHeight = h; }
    [JSInvokable] public void OnPointerDrag(float dx, float dy) => PointerDrag?.Invoke(dx, dy);
    [JSInvokable] public void OnScrollWheel(float delta) => ScrollWheel?.Invoke(delta);
    [JSInvokable] public void OnTapStart() => TapStart?.Invoke();
    [JSInvokable] public void OnReset() => ResetRequested?.Invoke();

    public void Dispose()
    {
        _ = _js.InvokeVoidAsync("AppShell.dispose");
        _dotnetRef?.Dispose();
    }
}
