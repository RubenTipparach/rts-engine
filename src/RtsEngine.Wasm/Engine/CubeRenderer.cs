using RtsEngine.Core;
using RtsEngine.Game;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WebGPU cube renderer — implements IRenderer from Core.
/// Uses GPU.* proxy calls + CubeMesh from Core for shared vertex data.
/// </summary>
public class CubeRenderer : IRenderer
{
    private readonly HttpClient _http;

    private int _pipeline;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _uniformBuffer;
    private int _bindGroup;

    public CubeRenderer(HttpClient http)
    {
        _http = http;
    }

    public async Task Setup()
    {
        var shaderCode = await _http.GetStringAsync("shaders/cube.wgsl");
        var shaderModule = await GPU.CreateShaderModule(shaderCode);

        _vertexBuffer = await GPU.CreateVertexBuffer(CubeMesh.Vertices);
        _indexBuffer = await GPU.CreateIndexBuffer(CubeMesh.Indices);
        _uniformBuffer = await GPU.CreateUniformBuffer(64);

        var vertexLayouts = new object[]
        {
            new
            {
                arrayStride = CubeMesh.VertexStride,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x3", offset = 12, shaderLocation = 1 },
                }
            }
        };

        _pipeline = await GPU.CreateRenderPipeline(shaderModule, vertexLayouts);

        var entries = new object[]
        {
            new { binding = 0, bufferId = _uniformBuffer }
        };
        _bindGroup = await GPU.CreateBindGroup(_pipeline, 0, entries);
    }

    public void Draw(float[] mvpRawFloats)
    {
        GPU.WriteBuffer(_uniformBuffer, mvpRawFloats);
        GPU.Render(_pipeline, _vertexBuffer, _indexBuffer, _bindGroup, CubeMesh.IndexCount);
    }

    public void Dispose()
    {
        GPU.DestroyBuffer(_vertexBuffer);
        GPU.DestroyBuffer(_indexBuffer);
        GPU.DestroyBuffer(_uniformBuffer);
    }
}
