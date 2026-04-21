using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

public sealed class PlanetRenderer : IRenderer, IDisposable
{
    // Terrain uniform: mvp(64) + sunDir(16) + camPos(16) + time+pad(16) + highlightDir(16) = 128
    public const int TerrainUniformSize = 128;
    private const int TerrainUniFloats = 32;

    // Atmosphere uniform: mat4 mvp (64) + vec4 sunDir (16) + vec4 camPos (16) + vec4 params (16) = 112
    public const int AtmoUniformSize = 112;
    private const int AtmoUniFloats = 28;

    private static readonly float[] DefaultSunDir = { 0.5f, 0.7f, 0.5f, 0f };

    private readonly IGPU _gpu;

    // Terrain
    private int _tPipeline, _tVbo, _tIbo, _tUbo, _tBindGroup;
    private int _samplerId, _atlasTexId, _dudvTexId, _normalTexId;
    private int _tIndexCount;
    private readonly float[] _tUni = new float[TerrainUniFloats];

    // Atmosphere
    private int _aPipeline, _aVbo, _aIbo, _aUbo, _aBindGroup;
    private int _aIndexCount;
    private readonly float[] _aUni = new float[AtmoUniFloats];
    private bool _atmoReady;

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

    public void SetHighlight(float dx, float dy, float dz)
    {
        _tUni[28] = dx; _tUni[29] = dy; _tUni[30] = dz;
        _tUni[31] = (dx * dx + dy * dy + dz * dz) > 0.001f ? 1.0f : 0.0f;
    }

    // ── Setup ───────────────────────────────────────────────────────

    public async Task Setup(string terrainShader, string atlasUrl = "textures/terrain_atlas.png")
    {
        var tShader = await _gpu.CreateShaderModule(terrainShader);
        _tUbo = await _gpu.CreateUniformBuffer(TerrainUniformSize);
        _atlasTexId = await _gpu.CreateTextureFromUrl(atlasUrl);
        _dudvTexId = await _gpu.CreateTextureFromUrl("textures/water_dudv.png");
        _normalTexId = await _gpu.CreateTextureFromUrl("textures/water_normal.png");
        _samplerId = await _gpu.CreateSampler("linear", "repeat");

        var (tv, ti) = Mesh.BuildMesh();
        _tVbo = await _gpu.CreateVertexBuffer(tv);
        _tIbo = await _gpu.CreateIndexBuffer32(ti);
        _tIndexCount = ti.Length;

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

        float pR = Mesh.Radius * 0.92f;
        float aR = Mesh.Radius * 1.5f; // 50% thickness atmosphere shell
        _aUni[24] = pR;
        _aUni[25] = aR;
        _aUni[26] = 30.0f;

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

    // ── Draw ────────────────────────────────────────────────────────

    public void Draw(float[] mvpRawFloats)
    {
        // Terrain pass (clears)
        Array.Copy(mvpRawFloats, 0, _tUni, 0, 16);
        _gpu.WriteBuffer(_tUbo, _tUni);
        _gpu.Render(_tPipeline, _tVbo, _tIbo, _tBindGroup, _tIndexCount);

        // Atmosphere pass (alpha-blended on top)
        if (_atmoReady)
        {
            Array.Copy(mvpRawFloats, 0, _aUni, 0, 16);
            _gpu.WriteBuffer(_aUbo, _aUni);
            _gpu.RenderAdditional(_aPipeline, _aVbo, _aIbo, _aBindGroup, _aIndexCount);
        }
    }

    public async Task RebuildMesh()
    {
        _gpu.DestroyBuffer(_tVbo);
        _gpu.DestroyBuffer(_tIbo);

        var (v, i) = Mesh.BuildMesh();
        _tVbo = await _gpu.CreateVertexBuffer(v);
        _tIbo = await _gpu.CreateIndexBuffer32(i);
        _tIndexCount = i.Length;
    }

    public void Dispose()
    {
        _gpu.DestroyBuffer(_tVbo);
        _gpu.DestroyBuffer(_tIbo);
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
