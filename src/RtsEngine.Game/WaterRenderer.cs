using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Renders the planet's water surface as a separate alpha-blended sphere
/// drawn after the opaque terrain pass. Owns its own pipeline (alpha blend,
/// depth-test on, depth-write off) and its own shader so neither the
/// terrain pass nor the atmosphere pass have to know about water.
///
/// Currently the water shader uses a fake-depth proxy (slab-thickness from
/// view angle and a static water-column-thickness uniform) for its colour
/// gradient and shore-foam mask. Adding TRUE refraction + depth requires:
///
///   1. A render-to-texture API on IGPU (CreateRenderTexture + a render
///      pass that targets it instead of the swap chain).
///   2. The terrain pass renders to that offscreen colour + depth target.
///   3. WaterRenderer's bind group gains two new bindings: sceneColor and
///      sceneDepth, sampled by the fragment shader.
///   4. water.wgsl's fragment shader samples sceneColor at a DuDv-offset
///      UV (for refraction) and reads sceneDepth at the current pixel
///      (for true water-column thickness — `sceneDepth - waterDepth`),
///      replacing the static `oceanDepth` proxy.
///
/// The class is structured so steps 3 and 4 are additive — the pipeline
/// + bind group setup already separates UBO/sampler/textures cleanly, so
/// adding two more texture bindings only needs WaterRenderer.Setup and
/// the shader to learn about them; nothing else changes.
/// </summary>
public sealed class WaterRenderer : IDisposable
{
    // mvp(64) + sunDir(16) + cameraPos(16) + params(16) = 112 bytes / 28 floats
    public const int UniformSize = 112;
    private const int UniFloats = 28;
    private const int TimeIdx = 24;
    private const int OceanDepthIdx = 26;

    private readonly IGPU _gpu;
    private readonly PlanetMesh _mesh;

    private int _pipeline;
    private int _ubo;
    private int _sampler;
    private int _dudvTexId;
    private int _bindGroup;

    private int _vbo;
    private int _ibo;
    private int _indexCount;

    private bool _ready;
    private readonly float[] _uni = new float[UniFloats];

    /// <summary>Toggle for whether to render the water at all. Defaults to
    /// true; the HUD's 🌊 Water button flips this. EngineBootstrap can
    /// also set it false at startup for non-Earth planets.</summary>
    public bool Visible { get; set; } = true;

    public WaterRenderer(IGPU gpu, PlanetMesh mesh)
    {
        _gpu = gpu;
        _mesh = mesh;
        // Default sun direction so the very first frame has plausible
        // lighting before GameEngine pushes the real value.
        _uni[16] = 0.5f; _uni[17] = 0.7f; _uni[18] = 0.5f; _uni[19] = 0f;
    }

    /// <summary>Set the water-column thickness in world units. PlanetRenderer
    /// computes this as 0.75 * StepHeight (= the geometric distance between
    /// the seabed at Radius and the water surface at LevelH(0)).</summary>
    public void SetOceanDepth(float worldUnits) => _uni[OceanDepthIdx] = worldUnits;

    public void SetTime(float seconds) => _uni[TimeIdx] = seconds;

    public void SetCameraPosition(float x, float y, float z)
    {
        _uni[20] = x; _uni[21] = y; _uni[22] = z;
    }

    public void SetSunDirection(float x, float y, float z)
    {
        _uni[16] = x; _uni[17] = y; _uni[18] = z;
    }

    public async Task Setup(string waterShaderCode, string dudvUrl, string normalUrl)
    {
        var module = await _gpu.CreateShaderModule(waterShaderCode);
        _ubo = await _gpu.CreateUniformBuffer(UniformSize);
        _sampler = await _gpu.CreateSampler("linear", "repeat");
        _dudvTexId = await _gpu.CreateTextureFromUrl(dudvUrl);
        // normalUrl ignored for now — see water.wgsl note about Dawn's
        // auto-layout pruning that binding regardless of how directly its
        // sample reaches the output. Argument kept so the call sites stay
        // unchanged when we re-add it (probably with an explicit pipeline
        // layout instead of `layout: 'auto'`).
        _ = normalUrl;

        // Vertex layout matches the water sphere mesh (pos3 + normal3),
        // 24-byte stride. Distinct from the terrain mesh's 28-byte stride
        // (pos3 + normal3 + level1) — water doesn't carry a level since
        // every fragment runs the same shader.
        // Marker pipeline (alpha blend + depth test + no depth write +
        // cullMode 'none').
        _pipeline = await _gpu.CreateRenderPipelineMarker(module, new object[]
        {
            new {
                arrayStride = 24,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x3", offset = 12, shaderLocation = 1 },
                }
            }
        });

        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
            new { binding = 1, samplerId = _sampler },
            new { binding = 2, textureViewId = _dudvTexId },
        });

        // Build the water sphere: an icosphere subdivided to 4 (2562 verts,
        // 5120 tris, 15360 ushort indices) at the planet's water-surface
        // radius LevelH(0). Pos + normal only — no level attribute.
        var (verts, idx) = BuildWaterSphereMesh();
        _vbo = await _gpu.CreateVertexBuffer(verts);
        _ibo = await _gpu.CreateIndexBuffer(idx);
        _indexCount = idx.Length;

        _ready = true;
    }

    /// <summary>Draw the water sphere. Caller is responsible for the
    /// preceding opaque pass (terrain) — this draws on top with alpha blend
    /// so the terrain underneath shows through at shallow regions.</summary>
    public void Draw(float[] mvpRawFloats)
    {
        if (!_ready || !Visible) return;
        Array.Copy(mvpRawFloats, 0, _uni, 0, 16);
        _gpu.WriteBuffer(_ubo, _uni);
        _gpu.RenderAdditional(_pipeline, _vbo, _ibo, _bindGroup, _indexCount);
    }

    /// <summary>Build a full-planet water sphere mesh at LevelH(0). Pos +
    /// normal interleaved (24-byte stride, 6 floats per vertex). Subdivisions
    /// = 4 → 2562 vertices, 5120 tris, 15360 ushort indices.</summary>
    private (float[] verts, ushort[] idx) BuildWaterSphereMesh()
    {
        // Use PlanetMesh's icosphere generator as the source of truth so
        // the water sphere matches the same kind of vertex distribution
        // the planet mesh uses. The 7-float layout PlanetMesh returns
        // (pos + normal + level) is repacked here to 6 floats (drop the
        // unused level).
        var (sevenF, sIdx) = _mesh.BuildWaterSphereMesh();
        int vCount = sevenF.Length / 7;
        var packed = new float[vCount * 6];
        for (int i = 0; i < vCount; i++)
        {
            int s = i * 7;
            int d = i * 6;
            packed[d + 0] = sevenF[s + 0];
            packed[d + 1] = sevenF[s + 1];
            packed[d + 2] = sevenF[s + 2];
            packed[d + 3] = sevenF[s + 3];
            packed[d + 4] = sevenF[s + 4];
            packed[d + 5] = sevenF[s + 5];
        }
        return (packed, sIdx);
    }

    public void Dispose()
    {
        if (_vbo > 0) _gpu.DestroyBuffer(_vbo);
        if (_ibo > 0) _gpu.DestroyBuffer(_ibo);
        if (_ubo > 0) _gpu.DestroyBuffer(_ubo);
    }
}
