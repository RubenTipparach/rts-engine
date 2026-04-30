using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Renders placed buildings and spawned units as simple colored boxes on
/// the planet surface. One mesh per building/unit type (defined in YAML),
/// drawn once per instance with a per-instance MVP and color uploaded into
/// a shared UBO. Each Render() call submits its own command buffer, so
/// the shared UBO is safe — the GPU sees the writes ordered with the draws.
/// </summary>
public sealed class RtsRenderer : IDisposable
{
    public const int VertexFloats = 6;     // pos3 + normal3
    public const int VertexStride = 24;
    public const int UniformSize = 96;     // mvp(64) + color(16) + sunDir(16)

    private readonly IGPU _gpu;
    private readonly RtsConfig _config;

    private int _pipeline;
    private int _ubo;
    private int _bindGroup;
    private bool _ready;

    private Vector3 _sunDir = new(0.5f, 0.7f, 0.5f);

    private struct Mesh
    {
        public int Vbo, Ibo, IndexCount;
    }
    private readonly Dictionary<string, Mesh> _buildingMeshes = new();
    private readonly Dictionary<string, Mesh> _unitMeshes = new();
    private readonly Dictionary<string, Vector3> _buildingColors = new();
    private readonly Dictionary<string, Vector3> _unitColors = new();

    public RtsRenderer(IGPU gpu, RtsConfig config)
    {
        _gpu = gpu;
        _config = config;
    }

    public async Task Setup(string shaderCode, Dictionary<string, string>? objs = null)
    {
        var shader = await _gpu.CreateShaderModule(shaderCode);
        _pipeline = await _gpu.CreateRenderPipeline(shader, new object[]
        {
            new {
                arrayStride = VertexStride,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x3", offset = 12, shaderLocation = 1 },
                }
            }
        });
        _ubo = await _gpu.CreateUniformBuffer(UniformSize);
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
        });

        foreach (var b in _config.Buildings)
        {
            _buildingMeshes[b.Id] = await BuildMesh(b.Id, objs, b.HalfWidth, b.Height);
            _buildingColors[b.Id] = ColorOf(b.Color);
        }
        foreach (var u in _config.Units)
        {
            _unitMeshes[u.Id] = await BuildMesh(u.Id, objs, u.HalfWidth, u.Height);
            _unitColors[u.Id] = ColorOf(u.Color);
        }
        _ready = true;
    }

    /// <summary>
    /// Try the baked .obj first (canonical artifact under assets/models/),
    /// fall back to a procedural box if no .obj was provided. Box dimensions
    /// come from the YAML so the silhouette still matches roughly even when
    /// running in environments where the asset pipeline isn't wired up.
    /// </summary>
    private async Task<Mesh> BuildMesh(string entityId, Dictionary<string, string>? objs,
        float halfWidth, float height)
    {
        float[] verts; ushort[] idx;
        if (objs != null && objs.TryGetValue(entityId, out var objText))
        {
            (verts, idx) = ObjLoader.Parse(objText);
        }
        else
        {
            (verts, idx) = MakeBox(halfWidth, height);
        }
        int vbo = await _gpu.CreateVertexBuffer(verts);
        int ibo = await _gpu.CreateIndexBuffer(idx);
        return new Mesh { Vbo = vbo, Ibo = ibo, IndexCount = idx.Length };
    }

    public void SetSunDirection(Vector3 dir) => _sunDir = dir;

    /// <summary>
    /// Draw all placed buildings and spawned units. <paramref name="planetMvp"/>
    /// is the same MVP used to render the planet (planet center = origin in
    /// the source space). Each instance gets a model matrix that places its
    /// base on the surface and aligns its Y axis with the cell's outward radial.
    /// </summary>
    public void Draw(RtsState state, PlanetMesh mesh, float[] planetMvp)
    {
        if (!_ready) return;

        var planetMvpMat = RawToMat(planetMvp);

        foreach (var b in state.Buildings)
        {
            if (!_buildingMeshes.TryGetValue(b.TypeId, out var bm)) continue;
            var color = _buildingColors[b.TypeId];

            var up = mesh.GetCellCenter(b.CellIndex);
            float surfaceR = mesh.Radius + mesh.GetLevel(b.CellIndex) * mesh.StepHeight;
            var pos = up * surfaceR;

            var modelMvp = BuildModelMvp(pos, up, planetMvpMat);
            DrawInstance(bm, modelMvp, color, selected: b.InstanceId == state.SelectedBuildingInstanceId);
        }

        foreach (var u in state.Units)
        {
            if (!_unitMeshes.TryGetValue(u.TypeId, out var um)) continue;
            var color = _unitColors[u.TypeId];
            // Face the unit's local +Z along its current heading so models
            // look like they're walking the path, not facing a fixed compass.
            var modelMvp = BuildModelMvp(u.SurfacePoint, u.SurfaceUp, planetMvpMat,
                                         heading: u.Heading);
            DrawInstance(um, modelMvp, color, selected: u.InstanceId == state.SelectedUnitInstanceId);
        }
    }

    private void DrawInstance(Mesh m, Matrix4X4<float> modelMvp, Vector3 color, bool selected)
    {
        // Selected buildings flash bright via a color tint — cheap visual
        // feedback without needing a separate outline pass.
        var c = selected ? Vector3.Lerp(color, new Vector3(1f, 0.95f, 0.5f), 0.55f) : color;

        var uni = new float[24];
        var raw = MatrixHelper.ToRawFloats(modelMvp);
        for (int i = 0; i < 16; i++) uni[i] = raw[i];
        uni[16] = c.X; uni[17] = c.Y; uni[18] = c.Z; uni[19] = 1f;
        uni[20] = _sunDir.X; uni[21] = _sunDir.Y; uni[22] = _sunDir.Z; uni[23] = 0f;

        _gpu.WriteBuffer(_ubo, uni);
        _gpu.Render(_pipeline, m.Vbo, m.Ibo, _bindGroup, m.IndexCount);
    }

    /// <summary>
    /// Build a model matrix that maps Y-up local space onto the surface
    /// tangent frame at <paramref name="pos"/>. Optional <paramref name="heading"/>
    /// rotates the model around the surface up so it faces the direction
    /// of travel; without one we pick an arbitrary tangent "right".
    /// </summary>
    private static Matrix4X4<float> BuildModelMvp(Vector3 pos, Vector3 up,
        Matrix4X4<float> mvp, Vector3? heading = null)
    {
        var u = Vector3.Normalize(up);
        Vector3 fwd;
        if (heading is { } hd && hd.LengthSquared() > 1e-8f)
        {
            // Project heading onto the tangent plane so it stays perpendicular
            // to the surface up — keeps the model upright even on a tilted
            // slope cell where the heading vector picks up a radial component.
            var h = hd - u * Vector3.Dot(hd, u);
            fwd = h.LengthSquared() > 1e-8f ? Vector3.Normalize(h) : ArbitraryTangent(u);
        }
        else
        {
            fwd = ArbitraryTangent(u);
        }
        var right = Vector3.Normalize(Vector3.Cross(u, fwd));

        // Row-major basis: rows are right/up/fwd, then translation row.
        var basis = new Matrix4X4<float>(
            right.X, right.Y, right.Z, 0f,
            u.X,     u.Y,     u.Z,     0f,
            fwd.X,   fwd.Y,   fwd.Z,   0f,
            pos.X,   pos.Y,   pos.Z,   1f);
        return Matrix4X4.Multiply(basis, mvp);
    }

    private static Vector3 ArbitraryTangent(Vector3 up)
    {
        var worldUp = new Vector3(0, 1, 0);
        var right = Vector3.Cross(worldUp, up);
        if (right.LengthSquared() < 1e-5f) right = new Vector3(1, 0, 0);
        right = Vector3.Normalize(right);
        return Vector3.Normalize(Vector3.Cross(up, right));
    }

    private static Matrix4X4<float> RawToMat(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    private static Vector3 ColorOf(List<float> c) =>
        c.Count >= 3 ? new Vector3(c[0], c[1], c[2]) : new Vector3(0.7f);

    /// <summary>Axis-aligned box mesh, base centered at origin Y=0, top at
    /// Y=height. 24 verts (4 per face) so each face has a flat normal.</summary>
    private static (float[] verts, ushort[] indices) MakeBox(float halfSize, float height)
    {
        var verts = new List<float>();
        var idx = new List<ushort>();
        float s = halfSize;

        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
        {
            ushort start = (ushort)(verts.Count / VertexFloats);
            void Push(Vector3 p)
            {
                verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
                verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
            }
            Push(p0); Push(p1); Push(p2); Push(p3);
            idx.Add(start); idx.Add((ushort)(start + 1)); idx.Add((ushort)(start + 2));
            idx.Add(start); idx.Add((ushort)(start + 2)); idx.Add((ushort)(start + 3));
        }

        var b0 = new Vector3(-s, 0, -s);
        var b1 = new Vector3( s, 0, -s);
        var b2 = new Vector3( s, 0,  s);
        var b3 = new Vector3(-s, 0,  s);
        var t0 = new Vector3(-s, height, -s);
        var t1 = new Vector3( s, height, -s);
        var t2 = new Vector3( s, height,  s);
        var t3 = new Vector3(-s, height,  s);

        Quad(b3, b2, b1, b0, new Vector3(0, -1, 0));
        Quad(t0, t1, t2, t3, new Vector3(0,  1, 0));
        Quad(b0, b1, t1, t0, new Vector3(0,  0, -1));
        Quad(b1, b2, t2, t1, new Vector3(1,  0,  0));
        Quad(b2, b3, t3, t2, new Vector3(0,  0,  1));
        Quad(b3, b0, t0, t3, new Vector3(-1, 0,  0));

        return (verts.ToArray(), idx.ToArray());
    }

    public void Dispose()
    {
        foreach (var m in _buildingMeshes.Values) { _gpu.DestroyBuffer(m.Vbo); _gpu.DestroyBuffer(m.Ibo); }
        foreach (var m in _unitMeshes.Values)     { _gpu.DestroyBuffer(m.Vbo); _gpu.DestroyBuffer(m.Ibo); }
    }
}
