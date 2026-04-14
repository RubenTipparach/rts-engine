namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Platform-agnostic rendering backend.
///
/// Desktop: implemented via Silk.NET.OpenGL (native GL calls, zero JS).
/// WASM:    implemented via JS interop → WebGL (browser's only GPU API).
///
/// The game engine codes against this interface, never against a specific
/// graphics API. Shaders, buffers, and draw calls are all behind this wall.
/// </summary>
public interface IRenderBackend : IDisposable
{
    bool Initialize();
    void Render(float[] mvpColumnMajor);
    void StartLoop(Func<Task> onTick);
    void StopLoop();
    float CanvasWidth { get; }
    float CanvasHeight { get; }
    float AspectRatio => CanvasHeight > 0 ? CanvasWidth / CanvasHeight : 16f / 9f;

    event Action<float, float>? PointerDrag;
    event Action<float>? ScrollWheel;
    event Action? TapStart;
    event Action? ResetRequested;
}
