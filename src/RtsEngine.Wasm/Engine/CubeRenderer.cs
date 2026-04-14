using Silk.NET.Maths;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Sets up and draws a colored cube using the GPU (WebGPU) proxy.
/// All GPU resource creation and draw calls are plain C# GPU.* calls.
/// No JS, no platform awareness.
///
/// Matrix convention:
///   Silk.NET stores row-major (M_rc = row r, col c).
///   WGSL mat4x4f is column-major in memory.
///   Passing raw Silk.NET bytes → WGSL interprets rows as columns → automatic transpose.
///   This is correct: (v_row * M) == M^T * v_col, and the reinterpretation gives M^T.
///   So: NO manual transposing. Pass raw floats directly.
/// </summary>
public class CubeRenderer
{
    private readonly HttpClient _http;

    private int _pipeline;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _uniformBuffer;
    private int _bindGroup;
    private const int IndexCount = 36;

    public CubeRenderer(HttpClient http)
    {
        _http = http;
    }

    // Cube: 6 faces × 4 verts, each = pos(3f) + color(3f) = 24 bytes/vert
    private static readonly float[] Vertices =
    {
        // Front (red)
        -1, -1,  1,   1.0f, 0.2f, 0.2f,
         1, -1,  1,   1.0f, 0.2f, 0.2f,
         1,  1,  1,   1.0f, 0.4f, 0.4f,
        -1,  1,  1,   1.0f, 0.4f, 0.4f,
        // Back (green)
        -1, -1, -1,   0.2f, 1.0f, 0.2f,
        -1,  1, -1,   0.2f, 1.0f, 0.4f,
         1,  1, -1,   0.4f, 1.0f, 0.4f,
         1, -1, -1,   0.4f, 1.0f, 0.2f,
        // Top (blue)
        -1,  1, -1,   0.2f, 0.2f, 1.0f,
        -1,  1,  1,   0.2f, 0.4f, 1.0f,
         1,  1,  1,   0.4f, 0.4f, 1.0f,
         1,  1, -1,   0.4f, 0.2f, 1.0f,
        // Bottom (yellow)
        -1, -1, -1,   1.0f, 1.0f, 0.2f,
         1, -1, -1,   1.0f, 1.0f, 0.4f,
         1, -1,  1,   1.0f, 1.0f, 0.4f,
        -1, -1,  1,   1.0f, 1.0f, 0.2f,
        // Right (magenta)
         1, -1, -1,   1.0f, 0.2f, 1.0f,
         1,  1, -1,   1.0f, 0.4f, 1.0f,
         1,  1,  1,   1.0f, 0.4f, 1.0f,
         1, -1,  1,   1.0f, 0.2f, 1.0f,
        // Left (cyan)
        -1, -1, -1,   0.2f, 1.0f, 1.0f,
        -1, -1,  1,   0.2f, 1.0f, 1.0f,
        -1,  1,  1,   0.4f, 1.0f, 1.0f,
        -1,  1, -1,   0.4f, 1.0f, 1.0f,
    };

    private static readonly ushort[] Indices =
    {
         0,  1,  2,   0,  2,  3,
         4,  5,  6,   4,  6,  7,
         8,  9, 10,   8, 10, 11,
        12, 13, 14,  12, 14, 15,
        16, 17, 18,  16, 18, 19,
        20, 21, 22,  20, 22, 23,
    };

    public async Task Setup()
    {
        // Load shader from file — keeps WGSL as a standalone asset
        var shaderCode = await _http.GetStringAsync("shaders/cube.wgsl");
        var shaderModule = await GPU.CreateShaderModule(shaderCode);

        // Create buffers
        _vertexBuffer = await GPU.CreateVertexBuffer(Vertices);
        _indexBuffer = await GPU.CreateIndexBuffer(Indices);
        _uniformBuffer = await GPU.CreateUniformBuffer(64); // mat4x4f = 64 bytes

        // Vertex layout: pos(float32x3) + color(float32x3), stride = 24 bytes
        var vertexLayouts = new object[]
        {
            new
            {
                arrayStride = 24,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 }, // position
                    new { format = "float32x3", offset = 12, shaderLocation = 1 }, // color
                }
            }
        };

        // Create pipeline
        _pipeline = await GPU.CreateRenderPipeline(shaderModule, vertexLayouts);

        // Create bind group (uniform buffer at binding 0)
        var entries = new object[]
        {
            new { binding = 0, bufferId = _uniformBuffer }
        };
        _bindGroup = await GPU.CreateBindGroup(_pipeline, 0, entries);
    }

    public void Draw(float[] mvpRawBytes)
    {
        // Upload MVP matrix — raw Silk.NET row-major bytes.
        // WGSL column-major reinterpretation gives automatic transpose.
        // This is correct: shader does M^T * v = (v * M)^T.
        GPU.WriteBuffer(_uniformBuffer, mvpRawBytes);

        // Draw
        GPU.Render(_pipeline, _vertexBuffer, _indexBuffer, _bindGroup, IndexCount);
    }

    public void Dispose()
    {
        GPU.DestroyBuffer(_vertexBuffer);
        GPU.DestroyBuffer(_indexBuffer);
        GPU.DestroyBuffer(_uniformBuffer);
    }
}
