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

    /// <summary>
    /// Upload only the first <paramref name="floatCount"/> floats of
    /// <paramref name="data"/> into the buffer at offset 0. Use this when a
    /// long-lived scratch array has only a small populated prefix (e.g. the
    /// per-frame path-line / HP-bar streaming buffers): otherwise the full
    /// scratch (tens of thousands of floats) is marshalled across the
    /// Blazor↔JS boundary every frame on WASM, which dominates frame time
    /// once any selection turns those buffers on. Default impl slices into
    /// a fresh array; backends should override when they can avoid the copy.
    /// </summary>
    void WriteBuffer(int bufferId, float[] data, int floatCount)
    {
        if (floatCount >= data.Length) { WriteBuffer(bufferId, data); return; }
        var slice = new float[floatCount];
        Array.Copy(data, slice, floatCount);
        WriteBuffer(bufferId, slice);
    }
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

    // ── Offscreen scene rendering (Catlike-Coding "Looking Through Water"
    //    style depth fog + refraction). ────────────────────────────────────
    //
    // Inside the BeginSceneFrame / EndSceneFrame bracket, every Render*()
    // call targets an offscreen color+depth RT the same size as the canvas
    // instead of the swap chain. The depth is sampleable, and a separate
    // grab-color texture can be snapshotted at any point so a later pass
    // (water) can sample the pre-pass scene as a refraction background.
    //
    // GrabSceneColor copies the current scene-color into the grab texture.
    // EndSceneFrame composites the scene RT onto the swap chain.
    //
    // SceneDepthView and GrabColorView are stable texture-view handles
    // (registered with the proxy's view table) suitable for passing to
    // CreateBindGroup. They survive the lifetime of the proxy — backend
    // owns the underlying texture.

    /// <summary>Allocate the scene RT + grab snapshot up front so
    /// <see cref="SceneDepthView"/> / <see cref="GrabColorView"/> are usable
    /// in <see cref="CreateBindGroup"/> at setup time. Idempotent.</summary>
    Task PrepareSceneTargets();

    /// <summary>Begin offscreen scene rendering. Subsequent Render*() calls
    /// target the scene RT until <see cref="EndSceneFrame"/> is called.</summary>
    void BeginSceneFrame();

    /// <summary>Snapshot scene-color into the grab texture so subsequent
    /// passes (water) can sample the pre-water framebuffer as a refraction
    /// background. Must be inside a Begin/End scene-frame bracket.</summary>
    void GrabSceneColor();

    /// <summary>Composite the scene RT onto the swap chain and end
    /// scene-frame mode. Subsequent Render*() calls target the swap chain
    /// again (e.g. EngineUI's HUD pass).</summary>
    void EndSceneFrame();

    /// <summary>Variant of <see cref="RenderAdditional"/> for the water
    /// pass: scene depth attached read-only so the bound depth texture can
    /// also be sampled in the bind group (the shader needs both depth-test
    /// against terrain depth AND depth-difference math). The pipeline must
    /// have depth-write disabled.</summary>
    void RenderWaterPass(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount);

    /// <summary>Pipeline variant for the water sphere: opaque (no blend),
    /// depth-test on, depth-write OFF, cull-back. The shader composites the
    /// refracted background via the fog mix, so the output is opaque; the
    /// depth attachment stays at terrain depth so subsequent passes still
    /// sort against actual terrain.</summary>
    Task<int> CreateRenderPipelineWater(int shaderModuleId, object[] vertexBufferLayouts);

    /// <summary>Stable texture-view handle for the grab snapshot
    /// (refraction background). Suitable for <see cref="CreateBindGroup"/>;
    /// returns -1 before <see cref="PrepareSceneTargets"/> has been called.</summary>
    int GrabColorView { get; }

    /// <summary>Stable texture-view handle for the live scene-depth texture
    /// (depth-difference fog). Suitable for <see cref="CreateBindGroup"/>;
    /// returns -1 before <see cref="PrepareSceneTargets"/> has been called.</summary>
    int SceneDepthView { get; }

    /// <summary>
    /// Open a per-frame command recording session. On WebGPU this creates a
    /// single command encoder that all subsequent <c>Render*</c> calls share —
    /// reusing one render pass per (color-loadOp, depth-loadOp) phase instead
    /// of starting + submitting one command buffer per draw. Cuts JS interop
    /// hops and (more importantly) <c>queue.submit</c> sync points from N×draws
    /// to 1×frame. Idempotent / no-op on backends that don't need it (OpenGL).
    /// </summary>
    void BeginFrame() { }

    /// <summary>
    /// Close the per-frame command recording session opened by
    /// <see cref="BeginFrame"/> and submit it to the GPU. Must be called once
    /// per frame after all draws. No-op on backends that don't batch.
    /// </summary>
    void EndFrame() { }
}
