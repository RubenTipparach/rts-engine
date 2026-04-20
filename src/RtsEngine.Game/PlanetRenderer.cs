using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Renders a Goldberg-sphere planet with triplanar-textured terrain + Lambert lighting.
/// Uniform layout: mat4 mvp (64 bytes) + vec4 sunDir (16 bytes) = 80 bytes.
/// </summary>
public sealed class PlanetRenderer : IRenderer, IDisposable
{
    // Uniform layout (112 bytes):
    //   [ 0..15] mat4 mvp         (64 bytes)
    //   [16..19] vec4 sunDir      (16 bytes, xyz + pad)
    //   [20..23] vec4 cameraPos   (16 bytes, xyz + pad)
    //   [24..27] float time + pad (16 bytes)
    public const int UniformSize = 112;
    private const int UniformFloats = 28;

    private static readonly float[] DefaultSunDir = { 0.5f, 0.7f, 0.5f, 0f };

    private readonly IGPU _gpu;

    private int _shaderModule;
    private int _pipeline;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _uniformBuffer;
    private int _bindGroup;
    private int _samplerId;
    private int _atlasTextureId;
    private int _indexCount;

    private readonly float[] _uniformData = new float[UniformFloats];

    public PlanetMesh Mesh { get; }

    public PlanetRenderer(IGPU gpu, PlanetMesh mesh)
    {
        _gpu = gpu;
        Mesh = mesh;
        Array.Copy(DefaultSunDir, 0, _uniformData, 16, 4);
    }

    public void SetTime(float seconds) => _uniformData[24] = seconds;

    public void SetCameraPosition(float x, float y, float z)
    {
        _uniformData[20] = x;
        _uniformData[21] = y;
        _uniformData[22] = z;
    }

    public async Task Setup(string shaderCode, string atlasUrl = "textures/terrain_atlas.png")
    {
        _shaderModule = await _gpu.CreateShaderModule(shaderCode);
        _uniformBuffer = await _gpu.CreateUniformBuffer(UniformSize);

        _atlasTextureId = await _gpu.CreateTextureFromUrl(atlasUrl);
        _samplerId = await _gpu.CreateSampler("linear", "repeat");

        var (verts, indices) = Mesh.BuildMesh();
        _vertexBuffer = await _gpu.CreateVertexBuffer(verts);
        _indexBuffer = await _gpu.CreateIndexBuffer(indices);
        _indexCount = indices.Length;

        var vertexLayouts = new object[]
        {
            new
            {
                arrayStride = PlanetMesh.VertexStrideBytes,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 }, // pos
                    new { format = "float32x3", offset = 12, shaderLocation = 1 }, // normal
                    new { format = "float32",   offset = 24, shaderLocation = 2 }, // level
                }
            }
        };
        _pipeline = await _gpu.CreateRenderPipeline(_shaderModule, vertexLayouts);

        var entries = new object[]
        {
            new { binding = 0, bufferId = _uniformBuffer },
            new { binding = 1, samplerId = _samplerId },
            new { binding = 2, textureViewId = _atlasTextureId },
        };
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, entries);
    }

    /// <summary>
    /// Rebuild vertex + index buffers from the current PlanetMesh state.
    /// Bind group and texture are preserved.
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
        Array.Copy(mvpRawFloats, 0, _uniformData, 0, 16);
        _gpu.WriteBuffer(_uniformBuffer, _uniformData);
        _gpu.Render(_pipeline, _vertexBuffer, _indexBuffer, _bindGroup, _indexCount);
    }

    public void Dispose()
    {
        _gpu.DestroyBuffer(_vertexBuffer);
        _gpu.DestroyBuffer(_indexBuffer);
        _gpu.DestroyBuffer(_uniformBuffer);
    }
}
