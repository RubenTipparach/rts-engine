namespace RtsEngine.Core;

/// GPU abstraction — what renderers code against.
/// WASM implements this via JS interop → WebGPU.
/// Desktop implements this via Silk.NET → OpenGL.
/// Game code never knows which.
public interface IGPU
{
    Task<int> CreateShaderModule(string shaderCode);
    Task<int> CreateVertexBuffer(float[] data);
    Task<int> CreateIndexBuffer(ushort[] data);
    Task<int> CreateUniformBuffer(int sizeBytes);
    void WriteBuffer(int bufferId, float[] data);
    Task<int> CreateRenderPipeline(int shaderModuleId, object[] vertexBufferLayouts);
    Task<int> CreateBindGroup(int pipelineId, int groupIndex, object[] entries);
    void Render(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount);
    void DestroyBuffer(int bufferId);

    /// <summary>
    /// Load a PNG/JPEG from a URL (relative to site root) and upload to GPU
    /// as a sampled 2D texture. Returns a texture handle.
    /// </summary>
    Task<int> CreateTextureFromUrl(string url);

    /// <summary>
    /// Create a texture sampler. filter: "linear" or "nearest". wrap: "repeat" or "clamp".
    /// </summary>
    Task<int> CreateSampler(string filter = "linear", string wrap = "repeat");
}
