using Microsoft.JSInterop;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WebGPU-style static API — the sokol pattern for C#.
///
/// Engine code calls GPU.CreateVertexBuffer(), GPU.Render() etc.
/// Plain C# calls, zero platform awareness.
///
/// WASM:    routes through gpu-proxy.js (generic WebGPU bridge)
/// Desktop: would route to native Vulkan/D3D12/Metal via Silk.NET
/// </summary>
public static class GPU
{
    private static IJSRuntime _js = null!;

    public static async Task<bool> Init(IJSRuntime js, string canvasId)
    {
        _js = js;
        return await _js.InvokeAsync<bool>("GPUProxy.init", canvasId);
    }

    public static void ResizeCanvas()
        => _js.InvokeVoidAsync("GPUProxy.resizeCanvas");

    // ── Shader Modules ───────────────────────────────────────────

    public static ValueTask<int> CreateShaderModule(string wgslCode)
        => _js.InvokeAsync<int>("GPUProxy.createShaderModule", wgslCode);

    // ── Buffers ──────────────────────────────────────────────────

    public static ValueTask<int> CreateVertexBuffer(float[] data)
        => _js.InvokeAsync<int>("GPUProxy.createVertexBuffer", data);

    public static ValueTask<int> CreateIndexBuffer(ushort[] data)
        => _js.InvokeAsync<int>("GPUProxy.createIndexBuffer", data);

    public static ValueTask<int> CreateUniformBuffer(int sizeBytes)
        => _js.InvokeAsync<int>("GPUProxy.createUniformBuffer", sizeBytes);

    public static void WriteBuffer(int bufferId, float[] data)
        => _js.InvokeVoidAsync("GPUProxy.writeBuffer", bufferId, data);

    // ── Pipeline ─────────────────────────────────────────────────

    public static ValueTask<int> CreateRenderPipeline(int shaderModuleId, object[] vertexBufferLayouts)
        => _js.InvokeAsync<int>("GPUProxy.createRenderPipeline", shaderModuleId, vertexBufferLayouts);

    // ── Bind Groups ──────────────────────────────────────────────

    public static ValueTask<int> CreateBindGroup(int pipelineId, int groupIndex, object[] entries)
        => _js.InvokeAsync<int>("GPUProxy.createBindGroup", pipelineId, groupIndex, entries);

    // ── Render ───────────────────────────────────────────────────

    public static void Render(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => _js.InvokeVoidAsync("GPUProxy.render", pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount);

    // ── Cleanup ──────────────────────────────────────────────────

    public static void DestroyBuffer(int bufferId)
        => _js.InvokeVoidAsync("GPUProxy.destroyBuffer", bufferId);
}
