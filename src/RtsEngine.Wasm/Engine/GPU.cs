using Microsoft.JSInterop;
using RtsEngine.Core;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WebGPU implementation of IGPU — routes calls through gpu-proxy.js.
/// </summary>
public sealed class WebGPU : IGPU
{
    private readonly IJSRuntime _js;

    public WebGPU(IJSRuntime js) => _js = js;

    public async Task<bool> Init(string canvasId)
        => await _js.InvokeAsync<bool>("GPUProxy.init", canvasId);

    public async Task<int> CreateShaderModule(string shaderCode)
        => await _js.InvokeAsync<int>("GPUProxy.createShaderModule", shaderCode);

    public async Task<int> CreateVertexBuffer(float[] data)
        => await _js.InvokeAsync<int>("GPUProxy.createVertexBuffer", data);

    public async Task<int> CreateIndexBuffer(ushort[] data)
        => await _js.InvokeAsync<int>("GPUProxy.createIndexBuffer", data);

    public async Task<int> CreateIndexBuffer32(uint[] data)
        => await _js.InvokeAsync<int>("GPUProxy.createIndexBuffer32", data);

    public async Task<int> CreateUniformBuffer(int sizeBytes)
        => await _js.InvokeAsync<int>("GPUProxy.createUniformBuffer", sizeBytes);

    public void WriteBuffer(int bufferId, float[] data)
        => _js.InvokeVoidAsync("GPUProxy.writeBuffer", bufferId, data);

    public async Task<int> CreateRenderPipeline(int shaderModuleId, object[] vertexBufferLayouts)
        => await _js.InvokeAsync<int>("GPUProxy.createRenderPipeline", shaderModuleId, vertexBufferLayouts);

    public async Task<int> CreateBindGroup(int pipelineId, int groupIndex, object[] entries)
        => await _js.InvokeAsync<int>("GPUProxy.createBindGroup", pipelineId, groupIndex, entries);

    public void Render(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => _js.InvokeVoidAsync("GPUProxy.render", pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount);

    public void RenderAdditional(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => _js.InvokeVoidAsync("GPUProxy.renderAdditional", pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount);

    public void RenderOverlay(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => _js.InvokeVoidAsync("GPUProxy.renderOverlay", pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount);

    public void DestroyBuffer(int bufferId)
        => _js.InvokeVoidAsync("GPUProxy.destroyBuffer", bufferId);

    public async Task<int> CreateTextureFromUrl(string url)
        => await _js.InvokeAsync<int>("GPUProxy.createTextureFromUrl", url);

    public async Task<int> CreateSampler(string filter = "linear", string wrap = "repeat")
        => await _js.InvokeAsync<int>("GPUProxy.createSampler", filter, wrap);

    public async Task<int> CreateRenderPipelineAlphaBlend(int shaderModuleId, object[] vertexBufferLayouts)
        => await _js.InvokeAsync<int>("GPUProxy.createRenderPipelineAlphaBlend", shaderModuleId, vertexBufferLayouts);

    public async Task<int> CreateRenderPipelineUI(int shaderModuleId, object[] vertexBufferLayouts)
        => await _js.InvokeAsync<int>("GPUProxy.createRenderPipelineUI", shaderModuleId, vertexBufferLayouts);

    public async Task<int> CreateRenderPipelineLines(int shaderModuleId, object[] vertexBufferLayouts)
        => await _js.InvokeAsync<int>("GPUProxy.createRenderPipelineLines", shaderModuleId, vertexBufferLayouts);

    public void RenderNoBind(int pipelineId, int vertexBufferId, int indexBufferId, int indexCount)
        => _js.InvokeVoidAsync("GPUProxy.renderNoBind", pipelineId, vertexBufferId, indexBufferId, indexCount);
}
