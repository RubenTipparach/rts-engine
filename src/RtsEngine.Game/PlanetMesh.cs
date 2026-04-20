using System.Numerics;

namespace RtsEngine.Game;

public enum CubeFace { PosX = 0, NegX = 1, PosY = 2, NegY = 3, PosZ = 4, NegZ = 5 }

public sealed class PlanetMesh
{
    public const int LevelCount = 4;
    public const byte MaxLevel = 3;

    public static readonly Vector3[] LevelColors =
    {
        new(0.15f, 0.35f, 0.75f), // 0 water
        new(0.90f, 0.80f, 0.55f), // 1 sand
        new(0.30f, 0.65f, 0.25f), // 2 grass
        new(0.55f, 0.55f, 0.55f), // 3 rock
    };

    public int GridResolution { get; }
    public float Radius { get; }
    public float StepHeight { get; }

    private readonly byte[] _levels;

    public PlanetMesh(int gridResolution = 16, float radius = 1.0f, float stepHeight = 0.04f)
    {
        GridResolution = gridResolution;
        Radius = radius;
        StepHeight = stepHeight;
        _levels = new byte[6 * gridResolution * gridResolution];
    }

    public byte GetLevel(CubeFace face, int row, int col)
        => _levels[Idx(face, row, col)];

    public void SetLevel(CubeFace face, int row, int col, byte level)
    {
        if (level > MaxLevel) level = MaxLevel;
        _levels[Idx(face, row, col)] = level;
    }

    public void CycleLevel(CubeFace face, int row, int col, int delta)
    {
        int cur = GetLevel(face, row, col);
        int next = ((cur + delta) % LevelCount + LevelCount) % LevelCount;
        SetLevel(face, row, col, (byte)next);
    }

    private int Idx(CubeFace face, int row, int col)
        => ((int)face * GridResolution + row) * GridResolution + col;

    // ── Noise terrain generation ────────────────────────────────────

    public void GenerateFromNoise(int seed = 42, float frequency = 2.5f)
    {
        int N = GridResolution;
        for (int f = 0; f < 6; f++)
        for (int row = 0; row < N; row++)
        for (int col = 0; col < N; col++)
        {
            float u = (col + 0.5f) / N * 2f - 1f;
            float v = (row + 0.5f) / N * 2f - 1f;
            Vector3 pos = Vector3.Normalize(FaceToCube(f, u, v));

            float n = Noise3D.Octaves(
                pos.X * frequency, pos.Y * frequency, pos.Z * frequency,
                4, 0.5f, seed);

            float t = (n + 1f) * 0.5f; // [0,1]
            byte level;
            if (t < 0.35f) level = 0;      // water
            else if (t < 0.50f) level = 1;  // sand
            else if (t < 0.72f) level = 2;  // grass
            else level = 3;                  // rock

            _levels[(f * N + row) * N + col] = level;
        }
    }

    // ── Mesh generation ─────────────────────────────────────────────

    public (float[] vertices, ushort[] indices) BuildMesh()
    {
        int N = GridResolution;
        var verts = new List<float>(6 * N * N * 30);
        var idx = new List<ushort>(6 * N * N * 24);

        for (int f = 0; f < 6; f++)
        for (int row = 0; row < N; row++)
        for (int col = 0; col < N; col++)
        {
            byte level = _levels[(f * N + row) * N + col];
            float h = Radius + level * StepHeight;
            Vector3 color = LevelColors[level];

            float u0 = (float)col / N * 2f - 1f;
            float u1 = (float)(col + 1) / N * 2f - 1f;
            float v0 = (float)row / N * 2f - 1f;
            float v1 = (float)(row + 1) / N * 2f - 1f;

            Vector3 p00 = Sph(f, u0, v0, h);
            Vector3 p10 = Sph(f, u1, v0, h);
            Vector3 p11 = Sph(f, u1, v1, h);
            Vector3 p01 = Sph(f, u0, v1, h);

            EmitQuad(verts, idx, p00, p10, p11, p01, color);

            // Cliff sides — check all 4 edges. Use cross-face lookup at boundaries.
            EmitCliff(verts, idx, level, color, f, row, col, -1, 0, p00, p10, h); // south
            EmitCliff(verts, idx, level, color, f, row, col, 0, +1, p10, p11, h); // east
            EmitCliff(verts, idx, level, color, f, row, col, +1, 0, p11, p01, h); // north
            EmitCliff(verts, idx, level, color, f, row, col, 0, -1, p01, p00, h); // west
        }

        return (verts.ToArray(), idx.ToArray());
    }

    private void EmitCliff(
        List<float> verts, List<ushort> idx,
        byte selfLevel, Vector3 selfColor,
        int face, int row, int col, int dRow, int dCol,
        Vector3 topA, Vector3 topB, float topH)
    {
        byte nLevel = GetNeighborLevel(face, row + dRow, col + dCol);
        if (nLevel >= selfLevel) return;

        float bottomH = Radius + nLevel * StepHeight;
        Vector3 botA = Vector3.Normalize(topA) * bottomH;
        Vector3 botB = Vector3.Normalize(topB) * bottomH;
        Vector3 cliffColor = selfColor * 0.6f;

        // Winding: outward from the cell toward the lower neighbor.
        // topA→topB is the edge shared with the top face. The cliff quad
        // should face outward (toward the neighbor). We emit both windings
        // to avoid invisible back-faces regardless of edge orientation.
        ushort b = (ushort)(verts.Count / 6);
        EmitVert(verts, topA, cliffColor);
        EmitVert(verts, topB, cliffColor);
        EmitVert(verts, botB, cliffColor);
        EmitVert(verts, botA, cliffColor);

        // Front face
        idx.Add(b); idx.Add((ushort)(b + 2)); idx.Add((ushort)(b + 1));
        idx.Add(b); idx.Add((ushort)(b + 3)); idx.Add((ushort)(b + 2));
        // Back face
        idx.Add(b); idx.Add((ushort)(b + 1)); idx.Add((ushort)(b + 2));
        idx.Add(b); idx.Add((ushort)(b + 2)); idx.Add((ushort)(b + 3));
    }

    /// <summary>
    /// Get neighbor cell level, handling cross-face adjacency.
    /// Projects the neighbor's cube-space position through the sphere
    /// to find which face/cell it actually belongs to.
    /// </summary>
    private byte GetNeighborLevel(int face, int row, int col)
    {
        int N = GridResolution;
        if (row >= 0 && row < N && col >= 0 && col < N)
            return _levels[(face * N + row) * N + col];

        // Cross-face: compute the position the neighbor would occupy on the cube,
        // project to sphere, then look up which face/cell that maps to.
        float u = (Math.Clamp(col, 0, N - 1) + 0.5f) / N * 2f - 1f;
        float v = (Math.Clamp(row, 0, N - 1) + 0.5f) / N * 2f - 1f;

        // Push the UV beyond the face edge in the appropriate direction
        float step = 2f / N;
        if (col < 0) u = -1f - step * 0.5f;
        else if (col >= N) u = 1f + step * 0.5f;
        if (row < 0) v = -1f - step * 0.5f;
        else if (row >= N) v = 1f + step * 0.5f;

        Vector3 cubePos = FaceToCube(face, u, v);
        var cell = DirectionToCell(cubePos);
        if (cell == null) return 0;
        var (nf, nr, nc) = cell.Value;
        return _levels[((int)nf * N + nr) * N + nc];
    }

    private static void EmitQuad(
        List<float> verts, List<ushort> idx,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color)
    {
        ushort bi = (ushort)(verts.Count / 6);
        EmitVert(verts, a, color);
        EmitVert(verts, b, color);
        EmitVert(verts, c, color);
        EmitVert(verts, d, color);
        idx.Add(bi); idx.Add((ushort)(bi + 1)); idx.Add((ushort)(bi + 2));
        idx.Add(bi); idx.Add((ushort)(bi + 2)); idx.Add((ushort)(bi + 3));
    }

    private static void EmitVert(List<float> verts, Vector3 pos, Vector3 color)
    {
        verts.Add(pos.X); verts.Add(pos.Y); verts.Add(pos.Z);
        verts.Add(color.X); verts.Add(color.Y); verts.Add(color.Z);
    }

    // ── Cube-sphere projection ──────────────────────────────────────

    private static Vector3 Sph(int face, float u, float v, float h)
        => Vector3.Normalize(FaceToCube(face, u, v)) * h;

    public static Vector3 ProjectToSphere(int face, float u, float v, float h)
        => Sph(face, u, v, h);

    private static Vector3 FaceToCube(int face, float u, float v) => face switch
    {
        0 => new Vector3( 1,  v, -u),
        1 => new Vector3(-1,  v,  u),
        2 => new Vector3( u,  1, -v),
        3 => new Vector3( u, -1,  v),
        4 => new Vector3( u,  v,  1),
        5 => new Vector3(-u,  v, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    // ── Inverse projection ──────────────────────────────────────────

    public static (int face, float u, float v) DirectionToFaceUV(Vector3 dir)
    {
        float ax = MathF.Abs(dir.X), ay = MathF.Abs(dir.Y), az = MathF.Abs(dir.Z);
        if (ax >= ay && ax >= az)
        {
            if (dir.X > 0) return (0, -dir.Z / ax,  dir.Y / ax);
            else           return (1,  dir.Z / ax,  dir.Y / ax);
        }
        if (ay >= ax && ay >= az)
        {
            if (dir.Y > 0) return (2,  dir.X / ay, -dir.Z / ay);
            else           return (3,  dir.X / ay,  dir.Z / ay);
        }
        if (dir.Z > 0)     return (4,  dir.X / az,  dir.Y / az);
        else               return (5, -dir.X / az,  dir.Y / az);
    }

    public (CubeFace face, int row, int col)? DirectionToCell(Vector3 dir)
    {
        if (dir.LengthSquared() < 1e-12f) return null;
        dir = Vector3.Normalize(dir);
        var (face, u, v) = DirectionToFaceUV(dir);
        int c = Math.Clamp((int)MathF.Floor((u + 1f) * 0.5f * GridResolution), 0, GridResolution - 1);
        int r = Math.Clamp((int)MathF.Floor((v + 1f) * 0.5f * GridResolution), 0, GridResolution - 1);
        return ((CubeFace)face, r, c);
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
                     Grad(Hash(ix + 1, iy, iz, seed), fx - 1, fy, fz), u),
                Lerp(Grad(Hash(ix, iy + 1, iz, seed), fx, fy - 1, fz),
                     Grad(Hash(ix + 1, iy + 1, iz, seed), fx - 1, fy - 1, fz), u), v),
            Lerp(
                Lerp(Grad(Hash(ix, iy, iz + 1, seed), fx, fy, fz - 1),
                     Grad(Hash(ix + 1, iy, iz + 1, seed), fx - 1, fy, fz - 1), u),
                Lerp(Grad(Hash(ix, iy + 1, iz + 1, seed), fx, fy - 1, fz - 1),
                     Grad(Hash(ix + 1, iy + 1, iz + 1, seed), fx - 1, fy - 1, fz - 1), u), v),
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
