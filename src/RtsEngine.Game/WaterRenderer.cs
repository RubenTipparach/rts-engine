using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Renders the planet's water surface as an opaque sphere drawn after the
/// terrain pass. Implements the Catlike Coding "Looking Through Water"
/// technique: the underwater term is depth-difference exponential fog over
/// a refracted snapshot of the framebuffer (sceneColor at refractedUV);
/// the legacy surface treatment (Fresnel + sky reflection + sun specular +
/// Lambert) stays so the water still has its existing aesthetic.
///
/// Bind group (six entries):
///   0 = uniform (mvp, sun, camera, params, fogColor, viewport)
///   1 = DuDv sampler (linear, repeat)
///   2 = water DuDv map
///   3 = scene sampler  (linear, clamp)
///   4 = sceneColor — grab snapshot taken between terrain and water
///   5 = sceneDepth — live depth attachment, read-only in this pass
///
/// Output is opaque (alpha = 1) — the fog mix already composited the
/// refracted background. The pipeline keeps depth-test on but depth-write
/// off so depth at this pixel stays as the terrain depth, which keeps the
/// fog math behind the fragment correct and the atmosphere/outline passes
/// sorting against actual geometry.
/// </summary>
public sealed class WaterRenderer : IDisposable
{
    // mvp(64) + sunDir(16) + cameraPos(16) + params(16)
    //         + fogColor(16) + viewport(16) = 144 bytes / 36 floats.
    public const int UniformSize = 144;
    private const int UniFloats = 36;
    private const int TimeIdx              = 24; // params.x
    private const int FogDensityIdx        = 25; // params.y
    private const int RefractionStrengthIdx= 26; // params.z
    private const int FogColorIdx          = 28; // fogColor.rgb @ 28..30
    private const int ViewportIdx          = 32; // viewport.xyzw @ 32..35

    private readonly IGPU _gpu;
    private readonly PlanetMesh _mesh;

    private int _pipeline;
    private int _ubo;
    private int _dudvSampler;
    private int _sceneSampler;
    private int _dudvTexId;
    private int _bindGroup;

    private int _vbo;
    private int _ibo;
    private int _indexCount;

    private bool _ready;
    private readonly float[] _uni = new float[UniFloats];

    /// <summary>Toggle for whether to render the water at all. Defaults true;
    /// the HUD's 🌊 Water button flips this. Mars/Moon/etc. start it off
    /// via PlanetRenderer.ApplyConfig (no liquid water).</summary>
    public bool Visible { get; set; } = true;

    public WaterRenderer(IGPU gpu, PlanetMesh mesh)
    {
        _gpu = gpu;
        _mesh = mesh;
        // Default sun direction so the very first frame has plausible
        // lighting before GameEngine pushes the real value.
        _uni[16] = 0.5f; _uni[17] = 0.7f; _uni[18] = 0.5f; _uni[19] = 0f;
        // Tutorial defaults; ApplyConfig overrides from YAML.
        _uni[FogDensityIdx]         = 8.0f;
        _uni[RefractionStrengthIdx] = 0.04f;
        _uni[FogColorIdx + 0] = 0.04f;
        _uni[FogColorIdx + 1] = 0.18f;
        _uni[FogColorIdx + 2] = 0.30f;
        _uni[FogColorIdx + 3] = 0f;
    }

    public void SetTime(float seconds) => _uni[TimeIdx] = seconds;

    public void SetCameraPosition(float x, float y, float z)
    {
        _uni[20] = x; _uni[21] = y; _uni[22] = z;
    }

    public void SetSunDirection(float x, float y, float z)
    {
        _uni[16] = x; _uni[17] = y; _uni[18] = z;
    }

    /// <summary>Push the current canvas size into the uniform so the water
    /// shader can convert <c>fragCoord.xy</c> into screen-space UV for
    /// sampling sceneColor / sceneDepth. Set every frame so the value is
    /// fresh after a window/canvas resize.</summary>
    public void SetViewport(float w, float h)
    {
        _uni[ViewportIdx + 0] = w;
        _uni[ViewportIdx + 1] = h;
        _uni[ViewportIdx + 2] = w > 0 ? 1f / w : 0f;
        _uni[ViewportIdx + 3] = h > 0 ? 1f / h : 0f;
    }

    /// <summary>Tutorial tuning knobs. <paramref name="fogDensity"/> is the
    /// per-world-unit absorption rate (the exponent in <c>exp2(-density*d)</c>);
    /// <paramref name="refractionStrength"/> is the maximum screen-UV offset
    /// from wave-normal-driven refraction (~0.05 reads well at planet scale).</summary>
    public void SetFogParams(float r, float g, float b, float fogDensity, float refractionStrength)
    {
        _uni[FogColorIdx + 0]       = r;
        _uni[FogColorIdx + 1]       = g;
        _uni[FogColorIdx + 2]       = b;
        _uni[FogDensityIdx]         = fogDensity;
        _uni[RefractionStrengthIdx] = refractionStrength;
    }

    public async Task Setup(string waterShaderCode, string dudvUrl)
    {
        var module = await _gpu.CreateShaderModule(waterShaderCode);
        _ubo          = await _gpu.CreateUniformBuffer(UniformSize);
        _dudvSampler  = await _gpu.CreateSampler("linear", "repeat");
        _sceneSampler = await _gpu.CreateSampler("linear", "clamp");
        _dudvTexId    = await _gpu.CreateTextureFromUrl(dudvUrl);

        // Allocate the offscreen scene RT so SceneDepthView / GrabColorView
        // exist before we build the bind group. Idempotent.
        await _gpu.PrepareSceneTargets();

        // Opaque pipeline + depth-test + depth-write OFF, cull-back. The
        // shader composites the refracted background into the fragment via
        // the fog mix, so blending isn't needed; depth-write off keeps the
        // depth attachment at the terrain values so other water frags still
        // sort correctly and so sampling sceneDepth in the same pass is safe.
        _pipeline = await _gpu.CreateRenderPipelineWater(module, new object[]
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

        // Bind group. Six entries:
        //   0 ubo
        //   1 dudvSamp
        //   2 dudv               (uses dudvSamp via OpenGL group-default)
        //   3 sceneSamp
        //   4 sceneColor         (per-texture samplerId for OpenGL — clamp)
        //   5 sceneDepth         (sampled via textureLoad/texelFetch — sampler ignored)
        // The samplerId field on entries 4 & 5 is ignored by WebGPU's
        // createBindGroup (it uses the explicit sampler entries at bindings
        // 1 & 3) but consumed by the OpenGL backend so it can pair the
        // clamp sampler with sceneColor at texture unit 4.
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
            new { binding = 1, samplerId = _dudvSampler },
            new { binding = 2, textureViewId = _dudvTexId },
            new { binding = 3, samplerId = _sceneSampler },
            new { binding = 4, textureViewId = _gpu.GrabColorView,  samplerId = _sceneSampler },
            new { binding = 5, textureViewId = _gpu.SceneDepthView, samplerId = _sceneSampler },
        });

        var (verts, idx) = BuildWaterSphereMesh();
        _vbo = await _gpu.CreateVertexBuffer(verts);
        _ibo = await _gpu.CreateIndexBuffer(idx);
        _indexCount = idx.Length;

        _ready = true;
    }

    /// <summary>Draw the water sphere. Caller must have:
    ///   1. issued the terrain (and any other "behind water") passes,
    ///   2. called <see cref="IGPU.GrabSceneColor"/> to snapshot the
    ///      pre-water framebuffer into the grab texture,
    /// so the water shader's sceneColor sample sees terrain underneath
    /// and the depth attachment still holds terrain depth.</summary>
    public void Draw(float[] mvpRawFloats)
    {
        if (!_ready || !Visible) return;
        Array.Copy(mvpRawFloats, 0, _uni, 0, 16);
        _gpu.WriteBuffer(_ubo, _uni);
        // RenderWaterPass attaches sceneDepth read-only so the fragment
        // shader can sample it from the bind group while the pass also
        // depth-tests against it.
        _gpu.RenderWaterPass(_pipeline, _vbo, _ibo, _bindGroup, _indexCount);
    }

    /// <summary>Build a full-planet water sphere mesh at LevelH(0). Pos +
    /// normal interleaved (24-byte stride, 6 floats per vertex).</summary>
    private (float[] verts, ushort[] idx) BuildWaterSphereMesh()
    {
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
