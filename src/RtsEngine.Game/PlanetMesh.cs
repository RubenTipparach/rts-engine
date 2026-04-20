using System.Numerics;

namespace RtsEngine.Game;

public enum CubeFace { PosX = 0, NegX = 1, PosY = 2, NegY = 3, PosZ = 4, NegZ = 5 }

/// <summary>
/// Cube-sphere heightmap for a planet. 6 faces × N² cells. Each cell has a
/// discrete cliff level (0=water, 1=sand, 2=grass, 3=rock). Mesh is rebuilt
/// from the heightmap; cells at different levels produce visible cliff sides.
/// </summary>
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

    private readonly byte[] _levels; // flat: face*N*N + row*N + col

    public PlanetMesh(int gridResolution = 12, float radius = 1.0f, float stepHeight = 0.04f, byte initialLevel = 1)
    {
        GridResolution = gridResolution;
        Radius = radius;
        StepHeight = stepHeight;

        _levels = new byte[6 * gridResolution * gridResolution];
        if (initialLevel > 0)
            Array.Fill(_levels, initialLevel);
    }

    public byte GetLevel(CubeFace face, int row, int col)
        => _levels[Index(face, row, col)];

    public void SetLevel(CubeFace face, int row, int col, byte level)
    {
        if (level > MaxLevel) level = MaxLevel;
        _levels[Index(face, row, col)] = level;
    }

    public void CycleLevel(CubeFace face, int row, int col, int delta)
    {
        int cur = GetLevel(face, row, col);
        int next = ((cur + delta) % LevelCount + LevelCount) % LevelCount;
        SetLevel(face, row, col, (byte)next);
    }

    private int Index(CubeFace face, int row, int col)
        => ((int)face * GridResolution + row) * GridResolution + col;

    /// <summary>
    /// Build renderable mesh from the heightmap.
    /// Layout: pos3f + color3f per vertex, stride 24 bytes.
    /// Cliff sides are emitted where a cell is higher than an in-face neighbor.
    /// </summary>
    public (float[] vertices, ushort[] indices) BuildMesh()
    {
        int N = GridResolution;
        var verts = new List<float>(6 * N * N * 24);
        var indices = new List<ushort>(6 * N * N * 18);

        for (int f = 0; f < 6; f++)
        {
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

                Vector3 p00 = ProjectToSphere(f, u0, v0, h); // (col, row)
                Vector3 p10 = ProjectToSphere(f, u1, v0, h); // (col+1, row)
                Vector3 p11 = ProjectToSphere(f, u1, v1, h); // (col+1, row+1)
                Vector3 p01 = ProjectToSphere(f, u0, v1, h); // (col, row+1)

                // Top quad — winding chosen so outward normal faces away from planet center.
                // Face basis keeps u→right, v→up in the face's local frame, so CCW on the
                // outside is (p00, p10, p11, p01) for all 6 faces.
                ushort b = (ushort)(verts.Count / 6);
                EmitVertex(verts, p00, color);
                EmitVertex(verts, p10, color);
                EmitVertex(verts, p11, color);
                EmitVertex(verts, p01, color);
                indices.Add(b); indices.Add((ushort)(b + 1)); indices.Add((ushort)(b + 2));
                indices.Add(b); indices.Add((ushort)(b + 2)); indices.Add((ushort)(b + 3));

                // Cliff sides: emit a quad dropping to the neighbor's height when neighbor is lower.
                // Edge order: 0=south (row-1), 1=east (col+1), 2=north (row+1), 3=west (col-1)
                // At face edges, treat missing neighbor as level 0 so cliffs drop to water at borders.
                EmitCliffIfHigher(verts, indices, level, color, f, row - 1, col, p00, p10, h);
                EmitCliffIfHigher(verts, indices, level, color, f, row, col + 1, p10, p11, h);
                EmitCliffIfHigher(verts, indices, level, color, f, row + 1, col, p11, p01, h);
                EmitCliffIfHigher(verts, indices, level, color, f, row, col - 1, p01, p00, h);
            }
        }

        return (verts.ToArray(), indices.ToArray());
    }

    private void EmitCliffIfHigher(
        List<float> verts, List<ushort> indices,
        byte selfLevel, Vector3 selfColor,
        int face, int nRow, int nCol,
        Vector3 topA, Vector3 topB, float topH)
    {
        int N = GridResolution;
        byte nLevel;
        if (nRow < 0 || nRow >= N || nCol < 0 || nCol >= N)
            nLevel = 0; // face edge — drop cliff to water
        else
            nLevel = _levels[(face * N + nRow) * N + nCol];

        if (nLevel >= selfLevel) return;

        float bottomH = Radius + nLevel * StepHeight;
        Vector3 bottomA = Vector3.Normalize(topA) * bottomH;
        Vector3 bottomB = Vector3.Normalize(topB) * bottomH;

        Vector3 cliffColor = selfColor * 0.6f;

        // Quad (topA → topB → bottomB → bottomA). Winding outward.
        ushort b = (ushort)(verts.Count / 6);
        EmitVertex(verts, topA, cliffColor);
        EmitVertex(verts, topB, cliffColor);
        EmitVertex(verts, bottomB, cliffColor);
        EmitVertex(verts, bottomA, cliffColor);
        indices.Add(b); indices.Add((ushort)(b + 1)); indices.Add((ushort)(b + 2));
        indices.Add(b); indices.Add((ushort)(b + 2)); indices.Add((ushort)(b + 3));
    }

    private static void EmitVertex(List<float> verts, Vector3 pos, Vector3 color)
    {
        verts.Add(pos.X); verts.Add(pos.Y); verts.Add(pos.Z);
        verts.Add(color.X); verts.Add(color.Y); verts.Add(color.Z);
    }

    /// <summary>
    /// Project cube-face UV coord (u, v ∈ [-1, 1]) onto the sphere at radius h.
    /// Face basis convention:
    ///   +X:  (1,  v, -u)   -X:  (-1,  v,  u)
    ///   +Y:  (u,  1, -v)   -Y:  ( u, -1,  v)
    ///   +Z:  (u,  v,  1)   -Z:  (-u,  v, -1)
    /// Chosen so (u,v) maps to (local-right, local-up) on each face with outward-CCW winding.
    /// </summary>
    public static Vector3 ProjectToSphere(int face, float u, float v, float h)
    {
        Vector3 cube = face switch
        {
            0 => new Vector3( 1,  v, -u),
            1 => new Vector3(-1,  v,  u),
            2 => new Vector3( u,  1, -v),
            3 => new Vector3( u, -1,  v),
            4 => new Vector3( u,  v,  1),
            5 => new Vector3(-u,  v, -1),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
        return Vector3.Normalize(cube) * h;
    }

    /// <summary>
    /// Inverse of ProjectToSphere: given a unit-length point on the sphere in planet-local
    /// space, return the (face, u, v) it maps to.
    /// </summary>
    public static (int face, float u, float v) DirectionToFaceUV(Vector3 dir)
    {
        float ax = MathF.Abs(dir.X), ay = MathF.Abs(dir.Y), az = MathF.Abs(dir.Z);
        if (ax >= ay && ax >= az)
        {
            if (dir.X > 0) return (0,  -dir.Z / ax,  dir.Y / ax); // +X: u=-z, v=y
            else           return (1,   dir.Z / ax,  dir.Y / ax); // -X: u=+z, v=y
        }
        if (ay >= ax && ay >= az)
        {
            if (dir.Y > 0) return (2,   dir.X / ay, -dir.Z / ay); // +Y: u=x,  v=-z
            else           return (3,   dir.X / ay,  dir.Z / ay); // -Y: u=x,  v=+z
        }
        if (dir.Z > 0)     return (4,   dir.X / az,  dir.Y / az); // +Z: u=x,  v=y
        else               return (5,  -dir.X / az,  dir.Y / az); // -Z: u=-x, v=y
    }

    /// <summary>
    /// Convert a planet-local direction (unit vector) to a grid cell.
    /// Returns null if the direction is degenerate.
    /// </summary>
    public (CubeFace face, int row, int col)? DirectionToCell(Vector3 dir)
    {
        if (dir.LengthSquared() < 1e-12f) return null;
        dir = Vector3.Normalize(dir);
        var (face, u, v) = DirectionToFaceUV(dir);
        int col = Math.Clamp((int)MathF.Floor((u + 1f) * 0.5f * GridResolution), 0, GridResolution - 1);
        int row = Math.Clamp((int)MathF.Floor((v + 1f) * 0.5f * GridResolution), 0, GridResolution - 1);
        return ((CubeFace)face, row, col);
    }
}
