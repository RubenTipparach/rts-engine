using Microsoft.JSInterop;
using RtsEngine.Core;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WASM app shell — equivalent to sokol_app.
/// Manages canvas lifecycle, the requestAnimationFrame loop, and input.
/// Does NOT do any rendering — that goes through the GPU proxy.
/// </summary>
public sealed class WebGLRenderBackend : IRenderBackend
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<WebGLRenderBackend>? _dotnetRef;

    public float CanvasWidth { get; private set; } = 800;
    public float CanvasHeight { get; private set; } = 600;

    public event Action? PointerDown;
    public event Action<float, float>? PointerDrag;
    public event Action? PointerUp;
    public event Action<float, float, int>? PointerClick;
    public event Action<float>? Scroll;
    public event Action<float, float>? PointerMove;
    public event Action<string>? KeyDown;
#pragma warning disable CS0067 // WASM doesn't yet expose RTS-style middle/right gestures; the DOM
    // overlay would need to track button state + alt and emit them — TODO when desktop parity matters.
    public event Action<float, float>? OrbitDrag;
    public event Action<float, float, float, float>? BoxSelectUpdate;
    public event Action<float, float, float, float>? BoxSelectComplete;
    public event Action<float, float>? ContextMenuRequested;
#pragma warning restore CS0067

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
    [JSInvokable] public void OnPointerDown() => PointerDown?.Invoke();
    [JSInvokable] public void OnPointerDrag(float dx, float dy) => PointerDrag?.Invoke(dx, dy);
    [JSInvokable] public void OnPointerUp() => PointerUp?.Invoke();
    [JSInvokable] public void OnPointerClick(float x, float y, int button) => PointerClick?.Invoke(x, y, button);
    [JSInvokable] public void OnScroll(float deltaY) => Scroll?.Invoke(deltaY);
    [JSInvokable] public void OnPointerMove(float x, float y) => PointerMove?.Invoke(x, y);
    [JSInvokable] public void OnKeyDown(string key) => KeyDown?.Invoke(key);
    [JSInvokable] public void OnUIButtonClick(string id) => UIButtonClick?.Invoke(id);

    public event Action<string>? UIButtonClick;

    public void CreateUIButton(string id, string text, string cssJson)
        => _js.InvokeVoidAsync("AppShell.createButton", id, text, cssJson);

    public void ShowUIButton(string id, bool visible)
        => _js.InvokeVoidAsync("AppShell.showButton", id, visible);

    public void Dispose()
    {
        _ = _js.InvokeVoidAsync("AppShell.dispose");
        _dotnetRef?.Dispose();
    }
}
