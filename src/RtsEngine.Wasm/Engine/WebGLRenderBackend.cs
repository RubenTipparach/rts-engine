using Microsoft.JSInterop;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WASM implementation of IRenderBackend.
/// Delegates GPU calls to webgl-engine.js via Blazor JS interop.
///
/// This is the ONLY file that knows about JavaScript.
/// On a desktop build, you'd swap this for SilkNetRenderBackend which
/// calls Silk.NET.OpenGL directly — same interface, zero JS.
/// </summary>
public sealed class WebGLRenderBackend : IRenderBackend
{
    private readonly IJSRuntime _js;
    private readonly string _canvasId;
    private DotNetObjectReference<WebGLRenderBackend>? _dotnetRef;

    public float CanvasWidth { get; private set; } = 800;
    public float CanvasHeight { get; private set; } = 600;

    public event Action<float, float>? PointerDrag;
    public event Action<float>? ScrollWheel;
    public event Action? TapStart;
    public event Action? ResetRequested;

    private Func<Task>? _onTick;

    public WebGLRenderBackend(IJSRuntime js, string canvasId = "glCanvas")
    {
        _js = js;
        _canvasId = canvasId;
    }

    public async Task<bool> InitializeAsync()
    {
        _dotnetRef = DotNetObjectReference.Create(this);
        return await _js.InvokeAsync<bool>("WebGLEngine.init", _canvasId, _dotnetRef);
    }

    // Sync Initialize for interface — callers should prefer InitializeAsync
    bool IRenderBackend.Initialize() => true;

    public void Render(float[] mvpColumnMajor)
    {
        _ = _js.InvokeVoidAsync("WebGLEngine.render", mvpColumnMajor);
    }

    public void StartLoop(Func<Task> onTick)
    {
        _onTick = onTick;
        _ = _js.InvokeVoidAsync("WebGLEngine.startLoop");
    }

    public void StopLoop()
    {
        _ = _js.InvokeVoidAsync("WebGLEngine.stopLoop");
    }

    // --- JS → C# callbacks (invoked by webgl-engine.js) ---

    [JSInvokable]
    public async Task GameLoopTick()
    {
        if (_onTick != null) await _onTick();
    }

    [JSInvokable]
    public void OnCanvasResize(float width, float height)
    {
        CanvasWidth = width;
        CanvasHeight = height;
    }

    [JSInvokable]
    public void OnPointerDrag(float dx, float dy) => PointerDrag?.Invoke(dx, dy);

    [JSInvokable]
    public void OnScrollWheel(float delta) => ScrollWheel?.Invoke(delta);

    [JSInvokable]
    public void OnTapStart() => TapStart?.Invoke();

    [JSInvokable]
    public void OnReset() => ResetRequested?.Invoke();

    public void Dispose()
    {
        _ = _js.InvokeVoidAsync("WebGLEngine.dispose");
        _dotnetRef?.Dispose();
    }
}
