namespace RtsEngine.Core;

/// <summary>
/// Abstraction over the draw call.
/// WASM: CubeRenderer calls GPU.* → WebGPU.
/// Desktop: DesktopRenderer calls Silk.NET.OpenGL.
/// </summary>
public interface IRenderer
{
    void Draw(float[] mvpRawFloats);
}
