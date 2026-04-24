using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Renders the star map with hierarchical navigation:
///   Galaxy → Sector → Cluster → Group
/// Stars in the current selection context are brighter; others are dimmed.
/// Click to zoom in on a selection, scroll out to go back up.
/// </summary>
public sealed class StarMapRenderer : IRenderer, IDisposable
{
    public const int VertexFloats = 7; // pos3 + color3 + brightness1
    public const int VertexStride = 28;
    public const int UniformSize = 128; // mvp(64) + camRight(16) + camUp(16) + pad(16)

    private readonly IGPU _gpu;
    private readonly GalaxyData _galaxy;

    private int _pipeline, _vbo, _ibo, _ubo, _bindGroup;
    private int _indexCount;
    private readonly float[] _uni = new float[32];
    private bool _ready;

    // View state
    public StarMapLevel Level { get; private set; } = StarMapLevel.Galaxy;
    public int SelectedSector { get; private set; } = -1;
    public int SelectedCluster { get; private set; } = -1;
    public int SelectedGroup { get; private set; } = -1;

    // Camera
    private Vector3 _focusCenter = Vector3.Zero;
    private float _azimuth, _elevation = 0.3f, _distance = 200f;
    private bool _dragging;

    public StarMapRenderer(IGPU gpu, GalaxyData galaxy)
    {
        _gpu = gpu;
        _galaxy = galaxy;
    }

    public async Task Setup(string shaderCode)
    {
        var shader = await _gpu.CreateShaderModule(shaderCode);
        _ubo = await _gpu.CreateUniformBuffer(UniformSize);

        _pipeline = await _gpu.CreateRenderPipeline(shader, new object[]
        {
            new {
                arrayStride = VertexStride,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x3", offset = 12, shaderLocation = 1 },
                    new { format = "float32",   offset = 24, shaderLocation = 2 },
                }
            }
        });
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
        });

        await RebuildMesh();
        _ready = true;
    }

    // ── View navigation ─────────────────────────────────────────────

    public void DrillDown(int index)
    {
        switch (Level)
        {
            case StarMapLevel.Galaxy:
                if (index >= 0 && index < _galaxy.Sectors.Count)
                {
                    SelectedSector = index;
                    Level = StarMapLevel.Sector;
                    _focusCenter = _galaxy.Sectors[index].Center;
                    _distance = 60f;
                }
                break;
            case StarMapLevel.Sector:
                var sector = _galaxy.Sectors[SelectedSector];
                if (index >= 0 && index < sector.Clusters.Count)
                {
                    SelectedCluster = index;
                    Level = StarMapLevel.Cluster;
                    _focusCenter = sector.Clusters[index].Center;
                    _distance = 20f;
                }
                break;
            case StarMapLevel.Cluster:
                var cluster = _galaxy.Sectors[SelectedSector].Clusters[SelectedCluster];
                if (index >= 0 && index < cluster.Groups.Count)
                {
                    SelectedGroup = index;
                    Level = StarMapLevel.Group;
                    _focusCenter = cluster.Groups[index].Center;
                    _distance = 8f;
                }
                break;
        }
    }

    public void ZoomOut()
    {
        switch (Level)
        {
            case StarMapLevel.Group:
                Level = StarMapLevel.Cluster;
                SelectedGroup = -1;
                _focusCenter = _galaxy.Sectors[SelectedSector].Clusters[SelectedCluster].Center;
                _distance = 20f;
                break;
            case StarMapLevel.Cluster:
                Level = StarMapLevel.Sector;
                SelectedCluster = -1;
                _focusCenter = _galaxy.Sectors[SelectedSector].Center;
                _distance = 60f;
                break;
            case StarMapLevel.Sector:
                Level = StarMapLevel.Galaxy;
                SelectedSector = -1;
                _focusCenter = Vector3.Zero;
                _distance = 200f;
                break;
        }
    }

    // ── Camera ──────────────────────────────────────────────────────

    public void Orbit(float dx, float dy)
    {
        _azimuth += dx * 0.005f;
        _elevation += dy * 0.005f;
        _elevation = Math.Clamp(_elevation, -1.4f, 1.4f);
    }

    public void Zoom(float delta)
    {
        _distance -= delta * _distance * 0.001f;
        _distance = Math.Clamp(_distance, 2f, 300f);
    }

    public void SetDragging(bool d) => _dragging = d;
    public bool IsDragging => _dragging;

    private Vector3 CameraPosition()
    {
        float cx = _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _distance * MathF.Sin(_elevation);
        float cz = _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);
        return _focusCenter + new Vector3(cx, cy, cz);
    }

    // ── Picking ─────────────────────────────────────────────────────

    /// <summary>
    /// Pick the child at the current level nearest to the screen position.
    /// Returns the child index, or -1 if nothing close enough.
    /// </summary>
    public int PickChild(float canvasX, float canvasY, float canvasW, float canvasH)
    {
        var centers = GetChildCenters();
        if (centers.Count == 0) return -1;

        var mvpFloats = BuildMvpFloats(canvasW / canvasH);
        var mvp = FloatsToMatrix(mvpFloats);

        float bestDist = float.MaxValue;
        int best = -1;
        for (int i = 0; i < centers.Count; i++)
        {
            var clip = Vector4.Transform(new Vector4(centers[i], 1f), mvp);
            if (clip.W <= 0.01f) continue;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * canvasW;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * canvasH;
            float d = (sx - canvasX) * (sx - canvasX) + (sy - canvasY) * (sy - canvasY);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (bestDist > 60f * 60f) return -1;
        return best;
    }

    private List<Vector3> GetChildCenters()
    {
        var centers = new List<Vector3>();
        switch (Level)
        {
            case StarMapLevel.Galaxy:
                foreach (var s in _galaxy.Sectors) centers.Add(s.Center);
                break;
            case StarMapLevel.Sector:
                foreach (var c in _galaxy.Sectors[SelectedSector].Clusters) centers.Add(c.Center);
                break;
            case StarMapLevel.Cluster:
                foreach (var g in _galaxy.Sectors[SelectedSector].Clusters[SelectedCluster].Groups)
                    centers.Add(g.Center);
                break;
            case StarMapLevel.Group:
                foreach (var st in _galaxy.Sectors[SelectedSector].Clusters[SelectedCluster]
                    .Groups[SelectedGroup].Stars)
                    centers.Add(st.Position);
                break;
        }
        return centers;
    }

    // ── Mesh ────────────────────────────────────────────────────────

    public async Task RebuildMesh()
    {
        if (_vbo > 0) _gpu.DestroyBuffer(_vbo);
        if (_ibo > 0) _gpu.DestroyBuffer(_ibo);

        var verts = new List<float>();
        var idx = new List<uint>();

        foreach (var sector in _galaxy.Sectors)
        foreach (var cluster in sector.Clusters)
        foreach (var group in cluster.Groups)
        foreach (var star in group.Stars)
        {
            float brightness = ComputeBrightness(sector, cluster, group);
            var color = GalaxyData.TempToColor(star.Temperature) * star.Luminosity;
            float size = 0.3f + star.Luminosity * 0.3f;

            EmitStarQuad(verts, idx, star.Position, color, brightness, size);
        }

        _vbo = await _gpu.CreateVertexBuffer(verts.ToArray());
        _ibo = await _gpu.CreateIndexBuffer32(idx.ToArray());
        _indexCount = idx.Count;
    }

    private float ComputeBrightness(Sector sector, StarCluster cluster, StarGroup group)
    {
        int si = _galaxy.Sectors.IndexOf(sector);
        switch (Level)
        {
            case StarMapLevel.Galaxy: return 0.6f;
            case StarMapLevel.Sector:
                return si == SelectedSector ? 1.0f : 0.15f;
            case StarMapLevel.Cluster:
                if (si != SelectedSector) return 0.08f;
                int ci = _galaxy.Sectors[si].Clusters.IndexOf(cluster);
                return ci == SelectedCluster ? 1.0f : 0.2f;
            case StarMapLevel.Group:
                if (si != SelectedSector) return 0.05f;
                int ci2 = _galaxy.Sectors[si].Clusters.IndexOf(cluster);
                if (ci2 != SelectedCluster) return 0.1f;
                int gi = _galaxy.Sectors[si].Clusters[ci2].Groups.IndexOf(group);
                return gi == SelectedGroup ? 1.0f : 0.25f;
            default: return 0.5f;
        }
    }

    private static void EmitStarQuad(List<float> verts, List<uint> idx,
        Vector3 pos, Vector3 color, float brightness, float size)
    {
        // 6 vertices forming a small cross/diamond (2 triangles)
        uint b = (uint)(verts.Count / VertexFloats);

        // Tiny world-space offsets (will be visible due to perspective)
        var offsets = new Vector3[]
        {
            new(-size, 0, 0), new(0, size, 0), new(size, 0, 0), new(0, -size, 0),
        };

        // Center vertex + 4 corners → 4 triangles (fan)
        Emit(verts, pos, color, brightness);
        for (int i = 0; i < 4; i++)
            Emit(verts, pos + offsets[i], color, brightness);

        for (int i = 0; i < 4; i++)
        {
            idx.Add(b);
            idx.Add(b + 1 + (uint)i);
            idx.Add(b + 1 + (uint)((i + 1) % 4));
        }
    }

    private static void Emit(List<float> v, Vector3 pos, Vector3 color, float brightness)
    {
        v.Add(pos.X); v.Add(pos.Y); v.Add(pos.Z);
        v.Add(color.X); v.Add(color.Y); v.Add(color.Z);
        v.Add(brightness);
    }

    // ── Draw ────────────────────────────────────────────────────────

    public void Draw(float[] mvpRawFloats)
    {
        if (!_ready || _indexCount == 0) return;
        Array.Copy(mvpRawFloats, 0, _uni, 0, 16);
        _gpu.WriteBuffer(_ubo, _uni);
        _gpu.Render(_pipeline, _vbo, _ibo, _bindGroup, _indexCount);
    }

    public float[] BuildMvpFloats(float aspect)
    {
        var pos = CameraPosition();
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(pos.X, pos.Y, pos.Z),
            new Vector3D<float>(_focusCenter.X, _focusCenter.Y, _focusCenter.Z),
            new Vector3D<float>(0, 1, 0));
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(50.0f), aspect, 0.5f, 500.0f);
        var mvp = Matrix4X4.Multiply(view, proj);
        return MatrixHelper.ToRawFloats(mvp);
    }

    private static Matrix4x4 FloatsToMatrix(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    public void Dispose()
    {
        if (_vbo > 0) _gpu.DestroyBuffer(_vbo);
        if (_ibo > 0) _gpu.DestroyBuffer(_ibo);
        _gpu.DestroyBuffer(_ubo);
    }
}
