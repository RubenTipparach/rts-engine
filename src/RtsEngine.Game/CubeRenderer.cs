using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Describes WHAT to render — a colored cube with an MVP uniform.
/// Codes against IGPU, never against a specific platform.
/// Lives in Game because it's game content, not engine framework.
/// </summary>
public class CubeRenderer : IRenderer
{
    private readonly IGPU _gpu;

    private int _pipeline;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _uniformBuffer;
    private int _bindGroup;

    public CubeRenderer(IGPU gpu)
    {
        _gpu = gpu;
    }

    public async Task Setup(string shaderCode)
    {
        var shaderModule = await _gpu.CreateShaderModule(shaderCode);

        _vertexBuffer = await _gpu.CreateVertexBuffer(CubeMesh.Vertices);
        _indexBuffer = await _gpu.CreateIndexBuffer(CubeMesh.Indices);
        _uniformBuffer = await _gpu.CreateUniformBuffer(64);

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

        _pipeline = await _gpu.CreateRenderPipeline(shaderModule, vertexLayouts);

        var entries = new object[]
        {
            new { binding = 0, bufferId = _uniformBuffer }
        };
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, entries);
    }

    public void Draw(float[] mvpRawFloats)
    {
        _gpu.WriteBuffer(_uniformBuffer, mvpRawFloats);
        _gpu.Render(_pipeline, _vertexBuffer, _indexBuffer, _bindGroup, CubeMesh.IndexCount);
    }

    public void Dispose()
    {
        _gpu.DestroyBuffer(_vertexBuffer);
        _gpu.DestroyBuffer(_indexBuffer);
        _gpu.DestroyBuffer(_uniformBuffer);
    }
}
