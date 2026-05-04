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
    Task<int> CreateIndexBuffer32(uint[] data);
    Task<int> CreateUniformBuffer(int sizeBytes);
    void WriteBuffer(int bufferId, float[] data);
    Task<int> CreateRenderPipeline(int shaderModuleId, object[] vertexBufferLayouts);
    Task<int> CreateBindGroup(int pipelineId, int groupIndex, object[] entries);
    void Render(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount);

    /// <summary>Same as Render but preserves previous content (loadOp: load). For multi-pass.</summary>
    void RenderAdditional(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount);

    /// <summary>Load color, clear depth. For overlaying 3D content on existing background.</summary>
    void RenderOverlay(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount);

    void DestroyBuffer(int bufferId);

    Task<int> CreateTextureFromUrl(string url);
    Task<int> CreateSampler(string filter = "linear", string wrap = "repeat");

    /// <summary>Same as CreateRenderPipeline but with alpha blend + depth write off. For transparent overlays.</summary>
    Task<int> CreateRenderPipelineAlphaBlend(int shaderModuleId, object[] vertexBufferLayouts);

    /// <summary>World-space markers (HP bars, selection discs, path lines, build ghosts):
    /// alpha blend, depth test on but no depth write, cull-none so flat quads render
    /// regardless of facing. Distinct from <see cref="CreateRenderPipelineAlphaBlend"/>
    /// which culls front faces for inside-out shells (atmosphere).</summary>
    Task<int> CreateRenderPipelineMarker(int shaderModuleId, object[] vertexBufferLayouts);

    /// <summary>Screen-space UI: alpha blend, no depth test, no culling.</summary>
    Task<int> CreateRenderPipelineUI(int shaderModuleId, object[] vertexBufferLayouts);

    /// <summary>Draw without bind group (for shaders with no uniforms/textures).</summary>
    void RenderNoBind(int pipelineId, int vertexBufferId, int indexBufferId, int indexCount);

    /// <summary>Creates a line-list pipeline. For wireframe overlays (cell outlines, debug lines).</summary>
    Task<int> CreateRenderPipelineLines(int shaderModuleId, object[] vertexBufferLayouts);
}
