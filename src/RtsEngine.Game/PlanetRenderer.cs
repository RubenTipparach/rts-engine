using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

public sealed class PlanetRenderer : IRenderer, IDisposable
{
    // Terrain uniform: mvp(64) + sunDir(16) + camPos(16) + time+pad(16) = 112
    public const int TerrainUniformSize = 112;
    private const int TerrainUniFloats = 28;

    // Atmosphere uniform: mat4 mvp (64) + vec4 sunDir (16) + vec4 camPos (16) + vec4 params (16) = 112
    public const int AtmoUniformSize = 112;
    private const int AtmoUniFloats = 28;

    private static readonly float[] DefaultSunDir = { 0.5f, 0.7f, 0.5f, 0f };
    private PlanetConfig _config = new();

    public void ApplyConfig(PlanetConfig config)
    {
        _config = config;
        if (config.Atmosphere.SunDirection.Count >= 3)
        {
            _tUni[16] = config.Atmosphere.SunDirection[0];
            _tUni[17] = config.Atmosphere.SunDirection[1];
            _tUni[18] = config.Atmosphere.SunDirection[2];
            _aUni[16] = config.Atmosphere.SunDirection[0];
            _aUni[17] = config.Atmosphere.SunDirection[1];
            _aUni[18] = config.Atmosphere.SunDirection[2];
        }
    }

    private readonly IGPU _gpu;

    // Terrain (20 patches)
    private int _tPipeline, _tUbo, _tBindGroup;
    private int _samplerId, _atlasTexId, _dudvTexId, _normalTexId;
    private readonly int[] _patchVbo = new int[PlanetMesh.PatchCount];
    private readonly int[] _patchIbo = new int[PlanetMesh.PatchCount];
    private readonly int[] _patchIdxCount = new int[PlanetMesh.PatchCount];
    private readonly HashSet<int> _dirtyPatches = new();
    private readonly float[] _tUni = new float[TerrainUniFloats];

    // Atmosphere
    private int _aPipeline, _aVbo, _aIbo, _aUbo, _aBindGroup;
    private int _aIndexCount;
    private readonly float[] _aUni = new float[AtmoUniFloats];
    private bool _atmoReady;

    // Outline (hovered cell highlight — line-list mesh)
    private int _oPipeline, _oUbo, _oBindGroup;
    private int _oVbo = 0;
    private int _oIbo = 0; // sequential [0..11] for up to 12 verts
    private int _oVertCount = 0;
    private int _highlightedCell = -1;
    private bool _oReady;
    private bool _outlineDirty;
    private readonly float[] _oUni = new float[16];

    public PlanetMesh Mesh { get; }

    public PlanetRenderer(IGPU gpu, PlanetMesh mesh)
    {
        _gpu = gpu;
        Mesh = mesh;
        Array.Copy(DefaultSunDir, 0, _tUni, 16, 4);
        Array.Copy(DefaultSunDir, 0, _aUni, 16, 4);
    }

    public void SetTime(float seconds) => _tUni[24] = seconds;

    public void SetCameraPosition(float x, float y, float z)
    {
        _tUni[20] = x; _tUni[21] = y; _tUni[22] = z;
        _aUni[20] = x; _aUni[21] = y; _aUni[22] = z;
    }

    public void SetHighlightCell(int cell)
    {
        if (cell == _highlightedCell) return;
        _highlightedCell = cell;
        _outlineDirty = true;
    }

    // ── Setup ───────────────────────────────────────────────────────

    public async Task Setup(string terrainShader, string? atlasUrl = null)
    {
        var tShader = await _gpu.CreateShaderModule(terrainShader);
        _tUbo = await _gpu.CreateUniformBuffer(TerrainUniformSize);
        _atlasTexId = await _gpu.CreateTextureFromUrl(atlasUrl ?? _config.Terrain.AtlasUrl);
        _dudvTexId = await _gpu.CreateTextureFromUrl(_config.Water.DuDvUrl);
        _normalTexId = await _gpu.CreateTextureFromUrl(_config.Water.NormalUrl);
        _samplerId = await _gpu.CreateSampler("linear", "repeat");

        // Build per-patch VBO/IBO (20 patches)
        for (int p = 0; p < PlanetMesh.PatchCount; p++)
        {
            var (pv, pi) = Mesh.BuildPatchMesh(p);
            _patchVbo[p] = await _gpu.CreateVertexBuffer(pv);
            _patchIbo[p] = await _gpu.CreateIndexBuffer32(pi);
            _patchIdxCount[p] = pi.Length;
        }

        _tPipeline = await _gpu.CreateRenderPipeline(tShader, new object[]
        {
            new {
                arrayStride = PlanetMesh.VertexStrideBytes,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x3", offset = 12, shaderLocation = 1 },
                    new { format = "float32",   offset = 24, shaderLocation = 2 },
                }
            }
        });

        _tBindGroup = await _gpu.CreateBindGroup(_tPipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _tUbo },
            new { binding = 1, samplerId = _samplerId },
            new { binding = 2, textureViewId = _atlasTexId },
            new { binding = 3, textureViewId = _dudvTexId },
            new { binding = 4, textureViewId = _normalTexId },
        });
    }

    public async Task SetupAtmosphere(string atmosphereShader)
    {
        var aShader = await _gpu.CreateShaderModule(atmosphereShader);
        _aUbo = await _gpu.CreateUniformBuffer(AtmoUniformSize);

        float pR = Mesh.Radius * _config.Atmosphere.InnerRadiusMul;
        float aR = Mesh.Radius * _config.Atmosphere.OuterRadiusMul;
        _aUni[24] = pR;
        _aUni[25] = aR;
        _aUni[26] = _config.Atmosphere.SunIntensity;

        var (av, ai) = BuildAtmoSphere(aR, 3);
        _aVbo = await _gpu.CreateVertexBuffer(av);
        _aIbo = await _gpu.CreateIndexBuffer(ai);
        _aIndexCount = ai.Length;

        _aPipeline = await _gpu.CreateRenderPipelineAlphaBlend(aShader, new object[]
        {
            new {
                arrayStride = 12,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0, shaderLocation = 0 },
                }
            }
        });

        _aBindGroup = await _gpu.CreateBindGroup(_aPipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _aUbo },
        });

        _atmoReady = true;
    }

    public async Task SetupOutline(string outlineShader)
    {
        var oShader = await _gpu.CreateShaderModule(outlineShader);
        _oUbo = await _gpu.CreateUniformBuffer(64);

        _oPipeline = await _gpu.CreateRenderPipelineLines(oShader, new object[]
        {
            new {
                arrayStride = 12,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0, shaderLocation = 0 },
                }
            }
        });
        _oBindGroup = await _gpu.CreateBindGroup(_oPipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _oUbo },
        });

        // Sequential indices [0..11] — covers any cell (max 6 edges × 2 verts)
        var seq = new ushort[12];
        for (ushort i = 0; i < 12; i++) seq[i] = i;
        _oIbo = await _gpu.CreateIndexBuffer(seq);

        _oReady = true;
    }

    // ── Draw ────────────────────────────────────────────────────────

    public void Draw(float[] mvpRawFloats)
    {
        Array.Copy(mvpRawFloats, 0, _tUni, 0, 16);
        _gpu.WriteBuffer(_tUbo, _tUni);

        // Draw first patch with Render (clears), rest with RenderAdditional (loads)
        bool first = true;
        for (int p = 0; p < PlanetMesh.PatchCount; p++)
        {
            if (_patchIdxCount[p] == 0) continue;
            if (first)
            {
                _gpu.Render(_tPipeline, _patchVbo[p], _patchIbo[p], _tBindGroup, _patchIdxCount[p]);
                first = false;
            }
            else
            {
                _gpu.RenderAdditional(_tPipeline, _patchVbo[p], _patchIbo[p], _tBindGroup, _patchIdxCount[p]);
            }
        }

        // Atmosphere pass (alpha-blended on top)
        if (_atmoReady)
        {
            Array.Copy(mvpRawFloats, 0, _aUni, 0, 16);
            _gpu.WriteBuffer(_aUbo, _aUni);
            _gpu.RenderAdditional(_aPipeline, _aVbo, _aIbo, _aBindGroup, _aIndexCount);
        }

        // Outline pass — yellow line-list around hovered cell
        if (_oReady && _oVbo > 0 && _oVertCount > 0)
        {
            Array.Copy(mvpRawFloats, 0, _oUni, 0, 16);
            _gpu.WriteBuffer(_oUbo, _oUni);
            _gpu.RenderAdditional(_oPipeline, _oVbo, _oIbo, _oBindGroup, _oVertCount);
        }
    }

    /// <summary>Rebuild the outline VBO if the highlighted cell changed. Call from the tick before Draw.</summary>
    public async Task SyncOutline()
    {
        if (!_outlineDirty) return;
        _outlineDirty = false;

        if (_oVbo > 0) { _gpu.DestroyBuffer(_oVbo); _oVbo = 0; _oVertCount = 0; }
        if (_highlightedCell < 0) return;

        var data = Mesh.BuildCellOutline(_highlightedCell);
        _oVbo = await _gpu.CreateVertexBuffer(data);
        _oVertCount = data.Length / 3;
    }

    /// <summary>Mark patches dirty for the edited cell (+ its neighbor patches for cliff updates).</summary>
    public void MarkDirty(int cell)
    {
        foreach (int p in Mesh.GetAffectedPatches(cell))
            _dirtyPatches.Add(p);
    }

    /// <summary>Rebuild only dirty patches. Call once per frame before Draw.</summary>
    public async Task RebuildDirtyPatches()
    {
        if (_dirtyPatches.Count == 0) return;

        foreach (int p in _dirtyPatches)
        {
            _gpu.DestroyBuffer(_patchVbo[p]);
            _gpu.DestroyBuffer(_patchIbo[p]);

            var (v, i) = Mesh.BuildPatchMesh(p);
            _patchVbo[p] = await _gpu.CreateVertexBuffer(v);
            _patchIbo[p] = await _gpu.CreateIndexBuffer32(i);
            _patchIdxCount[p] = i.Length;
        }
        _dirtyPatches.Clear();
    }

    public void Dispose()
    {
        for (int p = 0; p < PlanetMesh.PatchCount; p++)
        {
            _gpu.DestroyBuffer(_patchVbo[p]);
            _gpu.DestroyBuffer(_patchIbo[p]);
        }
        _gpu.DestroyBuffer(_tUbo);
    }

    // ── Atmosphere icosphere mesh ───────────────────────────────────

    private static (float[] verts, ushort[] indices) BuildAtmoSphere(float radius, int subdivisions)
    {
        float phi = (1f + MathF.Sqrt(5f)) / 2f;
        var v = new List<Vector3>
        {
            Nrm(-1,  phi, 0), Nrm( 1,  phi, 0), Nrm(-1, -phi, 0), Nrm( 1, -phi, 0),
            Nrm(0, -1,  phi), Nrm(0,  1,  phi), Nrm(0, -1, -phi), Nrm(0,  1, -phi),
            Nrm( phi, 0, -1), Nrm( phi, 0,  1), Nrm(-phi, 0, -1), Nrm(-phi, 0,  1),
        };
        var tris = new List<(int, int, int)>
        {
            (0,11,5),(0,5,1),(0,1,7),(0,7,10),(0,10,11),(1,5,9),(5,11,4),(11,10,2),(10,7,6),(7,1,8),
            (3,9,4),(3,4,2),(3,2,6),(3,6,8),(3,8,9),(4,9,5),(2,4,11),(6,2,10),(8,6,7),(9,8,1),
        };

        var midCache = new Dictionary<long, int>();
        for (int s = 0; s < subdivisions; s++)
        {
            var next = new List<(int, int, int)>();
            midCache.Clear();
            foreach (var (a, b, c) in tris)
            {
                int ab = GetMidpoint(v, midCache, a, b);
                int bc = GetMidpoint(v, midCache, b, c);
                int ca = GetMidpoint(v, midCache, c, a);
                next.Add((a, ab, ca)); next.Add((b, bc, ab));
                next.Add((c, ca, bc)); next.Add((ab, bc, ca));
            }
            tris = next;
        }

        var verts = new float[v.Count * 3];
        for (int i = 0; i < v.Count; i++)
        {
            var p = v[i] * radius;
            verts[i * 3]     = p.X;
            verts[i * 3 + 1] = p.Y;
            verts[i * 3 + 2] = p.Z;
        }

        var idx = new ushort[tris.Count * 3];
        for (int i = 0; i < tris.Count; i++)
        {
            idx[i * 3]     = (ushort)tris[i].Item1;
            idx[i * 3 + 1] = (ushort)tris[i].Item2;
            idx[i * 3 + 2] = (ushort)tris[i].Item3;
        }

        return (verts, idx);
    }

    private static Vector3 Nrm(float x, float y, float z) => Vector3.Normalize(new(x, y, z));

    private static int GetMidpoint(List<Vector3> v, Dictionary<long, int> cache, int a, int b)
    {
        long key = ((long)Math.Min(a, b) << 32) | (long)Math.Max(a, b);
        if (cache.TryGetValue(key, out int mid)) return mid;
        mid = v.Count;
        v.Add(Vector3.Normalize((v[a] + v[b]) * 0.5f));
        cache[key] = mid;
        return mid;
    }
}
