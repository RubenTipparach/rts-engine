using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// Goldberg sphere planet mesh. Subdivided icosahedron dual grid:
/// 12 pentagons + hex cells. No pole distortion, uniform cell sizes.
/// Each cell has a discrete cliff level (0=water, 1=sand, 2=grass, 3=rock).
/// </summary>
public sealed class PlanetMesh
{
    public const int LevelCount = 5;
    public const byte MaxLevel = 4;

    public static readonly Vector3[] LevelColors =
    {
        new(0.15f, 0.35f, 0.75f), // 0 water
        new(0.90f, 0.80f, 0.55f), // 1 sand
        new(0.30f, 0.65f, 0.25f), // 2 grass
        new(0.55f, 0.55f, 0.55f), // 3 rock
        new(0.95f, 0.97f, 1.00f), // 4 snow
    };

    public float Radius { get; }
    public float StepHeight { get; }
    public int CellCount => _centers.Length;

    private readonly Vector3[] _centers;     // unit-sphere position per cell
    private readonly int[][] _neighbors;     // ordered neighbor indices per cell (5 or 6)
    private readonly Vector3[][] _polyVerts; // ordered dual polygon boundary on unit sphere
    private readonly byte[] _levels;

    public PlanetMesh(int subdivisions = 4, float radius = 1.0f, float stepHeight = 0.04f)
    {
        Radius = radius;
        StepHeight = stepHeight;

        var (verts, tris) = BuildIcosphere(subdivisions);
        int V = verts.Count;

        // Build vertex adjacency
        var adj = new HashSet<int>[V];
        for (int i = 0; i < V; i++) adj[i] = new HashSet<int>();
        foreach (var (a, b, c) in tris)
        {
            adj[a].Add(b); adj[a].Add(c);
            adj[b].Add(a); adj[b].Add(c);
            adj[c].Add(a); adj[c].Add(b);
        }

        _centers = verts.ToArray();
        _neighbors = new int[V][];
        _polyVerts = new Vector3[V][];

        for (int v = 0; v < V; v++)
        {
            var nbrs = new List<int>(adj[v]);
            Vector3 N = _centers[v];
            Vector3 T = GetTangent(N);
            Vector3 B = Vector3.Cross(N, T);

            nbrs.Sort((a, b) =>
            {
                Vector3 da = _centers[a] - N;
                Vector3 db = _centers[b] - N;
                float aa = MathF.Atan2(Vector3.Dot(da, B), Vector3.Dot(da, T));
                float ab = MathF.Atan2(Vector3.Dot(db, B), Vector3.Dot(db, T));
                return aa.CompareTo(ab);
            });

            _neighbors[v] = nbrs.ToArray();

            int n = nbrs.Count;
            _polyVerts[v] = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector3 centroid = (_centers[v] + _centers[nbrs[i]] + _centers[nbrs[j]]) / 3f;
                _polyVerts[v][i] = Vector3.Normalize(centroid);
            }
        }

        _levels = new byte[V];
    }

    // ── Cell access ─────────────────────────────────────────────────

    public byte GetLevel(int cell) => _levels[cell];

    public void SetLevel(int cell, byte level)
    {
        if (level > MaxLevel) level = MaxLevel;
        _levels[cell] = level;
    }

    /// <summary>
    /// Change a cell's level by delta, clamped to [0, MaxLevel].
    /// Raising a max-level cell or lowering a level-0 cell is a no-op.
    /// </summary>
    public void ChangeLevel(int cell, int delta)
    {
        int next = Math.Clamp(_levels[cell] + delta, 0, MaxLevel);
        _levels[cell] = (byte)next;
    }

    // ── Picking ─────────────────────────────────────────────────────

    public Vector3 GetCellCenter(int cell) => _centers[cell];

    /// <summary>
    /// Build a line-list mesh (pos3f per vertex) outlining the hex/pentagon
    /// boundary of the given cell, slightly above the cell's top surface.
    /// Returns N line segments = 2N vertices for an N-sided polygon.
    /// </summary>
    public float[] BuildCellOutline(int cell)
    {
        byte level = _levels[cell];
        float h = Radius + level * StepHeight + 0.003f; // slightly above to avoid z-fighting
        var poly = _polyVerts[cell];
        int n = poly.Length;
        var data = new float[n * 2 * 3];
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var a = poly[i] * h;
            var b = poly[j] * h;
            data[i * 6 + 0] = a.X; data[i * 6 + 1] = a.Y; data[i * 6 + 2] = a.Z;
            data[i * 6 + 3] = b.X; data[i * 6 + 4] = b.Y; data[i * 6 + 5] = b.Z;
        }
        return data;
    }

    public int? DirectionToCell(Vector3 dir)
    {
        if (dir.LengthSquared() < 1e-12f) return null;
        dir = Vector3.Normalize(dir);
        int best = -1;
        float bestDot = -2f;
        for (int i = 0; i < CellCount; i++)
        {
            float d = Vector3.Dot(dir, _centers[i]);
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best >= 0 ? best : null;
    }

    // ── Noise terrain generation ────────────────────────────────────

    public void GenerateFromNoise(int seed = 42, float frequency = 2.5f, float[]? thresholds = null)
    {
        var th = thresholds ?? new[] { 0.30f, 0.45f, 0.65f, 0.82f };
        for (int i = 0; i < CellCount; i++)
        {
            Vector3 p = _centers[i];
            float n = Noise3D.Octaves(
                p.X * frequency, p.Y * frequency, p.Z * frequency,
                4, 0.5f, seed);
            float t = (n + 1f) * 0.5f;
            byte lvl = 4;
            for (int k = 0; k < th.Length && k < 4; k++)
                if (t < th[k]) { lvl = (byte)k; break; }
            _levels[i] = lvl;
        }
    }

    // ── Mesh generation ─────────────────────────────────────────────
    //
    // Vertex layout: pos(3f) + normal(3f) + level(1f) = 7 floats, stride 28 bytes.
    // - pos: world-space position (planet at origin, no model transform)
    // - normal: outward surface normal (for Lambert lighting, triplanar weighting)
    // - level: terrain tier 0..4 (selects atlas tile in fragment shader)

    public const int VertexFloats = 7;
    public const int VertexStrideBytes = 28;

    public (float[] vertices, uint[] indices) BuildMesh()
    {
        var verts = new List<float>(CellCount * 40 * VertexFloats);
        var idx = new List<uint>(CellCount * 120);

        for (int cell = 0; cell < CellCount; cell++)
        {
            byte level = _levels[cell];
            float h = Radius + level * StepHeight;
            Vector3 cellNormal = _centers[cell];
            int n = _polyVerts[cell].Length;

            // Water cells: emit sand seabed below, then water surface on top
            if (level == 0)
            {
                float sandH = Radius - StepHeight;
                EmitCellFan(verts, idx, cell, sandH, cellNormal, 1); // sand floor
                EmitCellFan(verts, idx, cell, h, cellNormal, 0);     // water surface
            }
            else
            {
                EmitCellFan(verts, idx, cell, h, cellNormal, level);
            }

            var nbrs = _neighbors[cell];
            for (int k = 0; k < nbrs.Length; k++)
            {
                byte nLevel = _levels[nbrs[k]];
                if (nLevel >= level) continue;

                float nh = Radius + nLevel * StepHeight;
                int pA = ((k - 1) + n) % n;
                int pB = k;

                Vector3 topA = _polyVerts[cell][pA] * h;
                Vector3 topB = _polyVerts[cell][pB] * h;
                Vector3 botA = _polyVerts[cell][pA] * nh;
                Vector3 botB = _polyVerts[cell][pB] * nh;

                Vector3 cliffMid = Vector3.Normalize((topA + topB) * 0.5f);
                Vector3 cliffNormal = Vector3.Normalize(cliffMid - cellNormal * Vector3.Dot(cliffMid, cellNormal));
                if (cliffNormal.LengthSquared() < 1e-6f) cliffNormal = cellNormal;

                byte cliffLevel = 3;

                uint b = (uint)(verts.Count / VertexFloats);
                EmitVert(verts, topA, cliffNormal, cliffLevel);
                EmitVert(verts, topB, cliffNormal, cliffLevel);
                EmitVert(verts, botB, cliffNormal, cliffLevel);
                EmitVert(verts, botA, cliffNormal, cliffLevel);

                idx.Add(b); idx.Add(b + 2); idx.Add(b + 1);
                idx.Add(b); idx.Add(b + 3); idx.Add(b + 2);
                idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
                idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
            }
        }

        return (verts.ToArray(), idx.ToArray());
    }

    private void EmitCellFan(List<float> verts, List<uint> idx, int cell, float h, Vector3 normal, byte level)
    {
        int n = _polyVerts[cell].Length;
        uint ci = (uint)(verts.Count / VertexFloats);
        EmitVert(verts, _centers[cell] * h, normal, level);
        for (int i = 0; i < n; i++)
            EmitVert(verts, _polyVerts[cell][i] * h, normal, level);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            idx.Add(ci);
            idx.Add(ci + 1 + (uint)i);
            idx.Add(ci + 1 + (uint)j);
        }
    }

    private static void EmitVert(List<float> verts, Vector3 pos, Vector3 normal, byte level)
    {
        verts.Add(pos.X); verts.Add(pos.Y); verts.Add(pos.Z);
        verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z);
        verts.Add(level);
    }

    // ── Icosphere generation ────────────────────────────────────────

    private static (List<Vector3> verts, List<(int a, int b, int c)> tris) BuildIcosphere(int subdivisions)
    {
        float phi = (1f + MathF.Sqrt(5f)) / 2f;

        var verts = new List<Vector3>
        {
            Vector3.Normalize(new(-1,  phi, 0)), Vector3.Normalize(new( 1,  phi, 0)),
            Vector3.Normalize(new(-1, -phi, 0)), Vector3.Normalize(new( 1, -phi, 0)),
            Vector3.Normalize(new(0, -1,  phi)), Vector3.Normalize(new(0,  1,  phi)),
            Vector3.Normalize(new(0, -1, -phi)), Vector3.Normalize(new(0,  1, -phi)),
            Vector3.Normalize(new( phi, 0, -1)), Vector3.Normalize(new( phi, 0,  1)),
            Vector3.Normalize(new(-phi, 0, -1)), Vector3.Normalize(new(-phi, 0,  1)),
        };

        var tris = new List<(int, int, int)>
        {
            (0,11,5),  (0,5,1),   (0,1,7),   (0,7,10),  (0,10,11),
            (1,5,9),   (5,11,4),  (11,10,2),  (10,7,6),  (7,1,8),
            (3,9,4),   (3,4,2),   (3,2,6),   (3,6,8),   (3,8,9),
            (4,9,5),   (2,4,11),  (6,2,10),  (8,6,7),   (9,8,1),
        };

        var midCache = new Dictionary<long, int>();
        for (int s = 0; s < subdivisions; s++)
        {
            var next = new List<(int, int, int)>(tris.Count * 4);
            midCache.Clear();
            foreach (var (a, b, c) in tris)
            {
                int ab = Mid(verts, midCache, a, b);
                int bc = Mid(verts, midCache, b, c);
                int ca = Mid(verts, midCache, c, a);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }
            tris = next;
        }

        return (verts, tris);
    }

    private static int Mid(List<Vector3> verts, Dictionary<long, int> cache, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (cache.TryGetValue(key, out int mid)) return mid;
        mid = verts.Count;
        verts.Add(Vector3.Normalize((verts[a] + verts[b]) * 0.5f));
        cache[key] = mid;
        return mid;
    }

    private static Vector3 GetTangent(Vector3 n)
    {
        Vector3 up = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(n, up));
    }
}

// ── 3D Gradient Noise ───────────────────────────────────────────────

public static class Noise3D
{
    public static float Sample(float x, float y, float z, int seed = 0)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
        float fx = x - ix, fy = y - iy, fz = z - iz;
        float u = Fade(fx), v = Fade(fy), w = Fade(fz);

        return Lerp(
            Lerp(
                Lerp(Grad(Hash(ix, iy, iz, seed), fx, fy, fz),
                     Grad(Hash(ix+1, iy, iz, seed), fx-1, fy, fz), u),
                Lerp(Grad(Hash(ix, iy+1, iz, seed), fx, fy-1, fz),
                     Grad(Hash(ix+1, iy+1, iz, seed), fx-1, fy-1, fz), u), v),
            Lerp(
                Lerp(Grad(Hash(ix, iy, iz+1, seed), fx, fy, fz-1),
                     Grad(Hash(ix+1, iy, iz+1, seed), fx-1, fy, fz-1), u),
                Lerp(Grad(Hash(ix, iy+1, iz+1, seed), fx, fy-1, fz-1),
                     Grad(Hash(ix+1, iy+1, iz+1, seed), fx-1, fy-1, fz-1), u), v),
            w);
    }

    public static float Octaves(float x, float y, float z, int count, float persistence, int seed)
    {
        float total = 0f, amp = 1f, max = 0f;
        for (int i = 0; i < count; i++)
        {
            total += Sample(x, y, z, seed + i * 31) * amp;
            max += amp;
            amp *= persistence;
            x *= 2f; y *= 2f; z *= 2f;
        }
        return total / max;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    private static int Hash(int x, int y, int z, int seed)
    {
        int h = seed ^ (x * 374761393) ^ (y * 668265263) ^ (z * 1274126177);
        h = (h ^ (h >> 13)) * 1103515245;
        h ^= h >> 16;
        return h;
    }

    private static float Grad(int hash, float x, float y, float z)
    {
        int h = hash & 15;
        float u = h < 8 ? x : y;
        float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
    }
}
