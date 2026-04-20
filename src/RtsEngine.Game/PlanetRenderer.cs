using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Renders a cube-sphere planet with vertex-colored discrete heightmap.
/// Mesh is rebuilt from the PlanetMesh when cells are edited.
/// </summary>
public sealed class PlanetRenderer : IRenderer, IDisposable
{
    public const int VertexStride = 24; // pos3f + color3f
    public const int UniformSize = 64;  // one mat4

    private readonly IGPU _gpu;

    private int _shaderModule;
    private int _pipeline;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _uniformBuffer;
    private int _bindGroup;
    private int _indexCount;

    public PlanetMesh Mesh { get; }

    public PlanetRenderer(IGPU gpu, PlanetMesh mesh)
    {
        _gpu = gpu;
        Mesh = mesh;
    }

    public async Task Setup(string shaderCode)
    {
        _shaderModule = await _gpu.CreateShaderModule(shaderCode);
        _uniformBuffer = await _gpu.CreateUniformBuffer(UniformSize);

        var (verts, indices) = Mesh.BuildMesh();
        _vertexBuffer = await _gpu.CreateVertexBuffer(verts);
        _indexBuffer = await _gpu.CreateIndexBuffer(indices);
        _indexCount = indices.Length;

        var vertexLayouts = new object[]
        {
            new
            {
                arrayStride = VertexStride,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x3", offset = 12, shaderLocation = 1 },
                }
            }
        };
        _pipeline = await _gpu.CreateRenderPipeline(_shaderModule, vertexLayouts);

        var entries = new object[] { new { binding = 0, bufferId = _uniformBuffer } };
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, entries);
    }

    /// <summary>
    /// Rebuild the vertex + index buffers from the current PlanetMesh state.
    /// Call after editing cell levels.
    /// </summary>
    public async Task RebuildMesh()
    {
        _gpu.DestroyBuffer(_vertexBuffer);
        _gpu.DestroyBuffer(_indexBuffer);

        var (verts, indices) = Mesh.BuildMesh();
        _vertexBuffer = await _gpu.CreateVertexBuffer(verts);
        _indexBuffer = await _gpu.CreateIndexBuffer(indices);
        _indexCount = indices.Length;
    }

    public void Draw(float[] mvpRawFloats)
    {
        _gpu.WriteBuffer(_uniformBuffer, mvpRawFloats);
        _gpu.Render(_pipeline, _vertexBuffer, _indexBuffer, _bindGroup, _indexCount);
    }

    public void Dispose()
    {
        _gpu.DestroyBuffer(_vertexBuffer);
        _gpu.DestroyBuffer(_indexBuffer);
        _gpu.DestroyBuffer(_uniformBuffer);
    }
}
