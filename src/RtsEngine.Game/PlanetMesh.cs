using System.Numerics;

namespace RtsEngine.Game;

/// <summary>Per-cell slope assignment: the two adjacent cells whose levels
/// the slope ramps between. The cell's own level is preserved (it usually
/// matches one end), but its top mesh tilts from low to high.</summary>
public sealed record SlopeInfo(int LowNeighbor, int HighNeighbor);

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

    private readonly Vector3[] _centers;
    private readonly int[][] _neighbors;
    private readonly Vector3[][] _polyVerts;
    private readonly byte[] _levels;

    /// <summary>
    /// Optional per-cell slope: when set, the cell's top surface tilts from
    /// the level of LowNeighbor to the level of HighNeighbor along the axis
    /// (highCenter - lowCenter), and the cell is traversable between those
    /// two adjacent levels by ground units. <c>null</c> = ordinary flat cell.
    /// </summary>
    private readonly SlopeInfo?[] _slopes;

    // Patch system: 20 base icosahedron triangles → 20 patches
    public const int PatchCount = 20;
    private readonly int[] _patchId;         // cell → patch (0..19)
    private readonly List<int>[] _patchCells; // patch → list of cell indices

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
        _slopes = new SlopeInfo?[V];

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

        // Assign cells to patches: compute 20 base triangle centers from the
        // un-subdivided icosahedron, then assign each cell to the nearest center.
        var (baseV, baseTris) = BuildIcosphere(0);
        var baseCenters = new Vector3[PatchCount];
        for (int t = 0; t < baseTris.Count && t < PatchCount; t++)
        {
            var (a, b, c) = baseTris[t];
            baseCenters[t] = Vector3.Normalize((baseV[a] + baseV[b] + baseV[c]) / 3f);
        }

        _patchId = new int[V];
        _patchCells = new List<int>[PatchCount];
        for (int p = 0; p < PatchCount; p++) _patchCells[p] = new List<int>();

        for (int v = 0; v < V; v++)
        {
            float bestDot = -2f;
            int bestPatch = 0;
            for (int p = 0; p < PatchCount; p++)
            {
                float d = Vector3.Dot(_centers[v], baseCenters[p]);
                if (d > bestDot) { bestDot = d; bestPatch = p; }
            }
            _patchId[v] = bestPatch;
            _patchCells[bestPatch].Add(v);
        }
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

    public int GetPatchId(int cell) => _patchId[cell];

    /// <summary>
    /// Returns the set of patch IDs that need rebuilding after editing the given cell.
    /// Includes the cell's own patch + any neighbor patches (for cliff side geometry).
    /// </summary>
    public HashSet<int> GetAffectedPatches(int cell)
    {
        var patches = new HashSet<int> { _patchId[cell] };
        foreach (int nbr in _neighbors[cell])
            patches.Add(_patchId[nbr]);
        return patches;
    }

    // ── Picking ─────────────────────────────────────────────────────

    public Vector3 GetCellCenter(int cell) => _centers[cell];

    /// <summary>Adjacency list for a cell — neighbors in radial order around
    /// the cell perimeter. Used by pathfinding and slope generation.</summary>
    public IReadOnlyList<int> GetNeighbors(int cell) => _neighbors[cell];

    /// <summary>Slope info if the cell ramps between two adjacent levels,
    /// else <c>null</c>. Slope cells are traversable by ground units; flat
    /// cells with mismatched levels are not (without hop capability).</summary>
    public SlopeInfo? GetSlope(int cell) => _slopes[cell];

    public bool HasSlope(int cell) => _slopes[cell] != null;

    /// <summary>Mark a cell as a slope ramp between two of its neighbors.
    /// Caller is responsible for ensuring lowNeighbor and highNeighbor are
    /// actually adjacent and on opposite sides of <paramref name="cell"/>.</summary>
    public void SetSlope(int cell, int lowNeighbor, int highNeighbor)
    {
        _slopes[cell] = new SlopeInfo(lowNeighbor, highNeighbor);
    }

    public void ClearSlopes()
    {
        for (int i = 0; i < _slopes.Length; i++) _slopes[i] = null;
    }

    /// <summary>
    /// Surface height at one of this cell's polygon vertices, accounting
    /// for slope tilt. For non-slope cells this is just the cell's level
    /// height; for slope cells it interpolates linearly between the low
    /// and high neighbor levels along the slope axis.
    /// </summary>
    public float GetVertexHeight(int cell, int vertIndex)
    {
        var slope = _slopes[cell];
        float baseH = Radius + _levels[cell] * StepHeight;
        if (slope == null) return baseH;

        float lowH = Radius + _levels[slope.LowNeighbor] * StepHeight;
        float highH = Radius + _levels[slope.HighNeighbor] * StepHeight;

        // Slope axis: tangent to the cell, pointing low → high. Project the
        // vertex's offset from the cell center onto the axis to get a
        // signed scalar t. Map t ∈ [-extent, +extent] to height ∈ [low, high].
        Vector3 cellNormal = _centers[cell];
        Vector3 axis = _centers[slope.HighNeighbor] - _centers[slope.LowNeighbor];
        axis -= cellNormal * Vector3.Dot(axis, cellNormal);
        if (axis.LengthSquared() < 1e-8f) return baseH;
        axis = Vector3.Normalize(axis);

        var verts = _polyVerts[cell];
        float vt = Vector3.Dot(verts[vertIndex] - cellNormal, axis);
        float minT = float.MaxValue, maxT = float.MinValue;
        for (int i = 0; i < verts.Length; i++)
        {
            float ti = Vector3.Dot(verts[i] - cellNormal, axis);
            if (ti < minT) minT = ti;
            if (ti > maxT) maxT = ti;
        }
        float range = maxT - minT;
        if (range < 1e-8f) return baseH;
        float u = (vt - minT) / range;
        return lowH + (highH - lowH) * u;
    }

    /// <summary>Surface height at the cell's center — average of low and
    /// high for slopes, level height for flat cells.</summary>
    public float GetCenterHeight(int cell)
    {
        var slope = _slopes[cell];
        if (slope == null) return Radius + _levels[cell] * StepHeight;
        float lowH = Radius + _levels[slope.LowNeighbor] * StepHeight;
        float highH = Radius + _levels[slope.HighNeighbor] * StepHeight;
        return (lowH + highH) * 0.5f;
    }

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
            EmitCellGeometry(verts, idx, cell);
        return (verts.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// Build mesh for a single patch (one of 20 base icosahedron regions).
    /// Same vertex layout as BuildMesh; includes cliff sides to neighbors
    /// even if those neighbors are in a different patch.
    /// </summary>
    public (float[] vertices, uint[] indices) BuildPatchMesh(int patchId)
    {
        var cells = _patchCells[patchId];
        var verts = new List<float>(cells.Count * 40 * VertexFloats);
        var idx = new List<uint>(cells.Count * 120);

        foreach (int cell in cells)
            EmitCellGeometry(verts, idx, cell);

        return (verts.ToArray(), idx.ToArray());
    }

    private void EmitCellGeometry(List<float> verts, List<uint> idx, int cell)
    {
        byte level = _levels[cell];
        float h = Radius + level * StepHeight;
        Vector3 cellNormal = _centers[cell];
        int n = _polyVerts[cell].Length;
        var slope = _slopes[cell];

        // Per-vertex heights — flat cells reuse `h`, slope cells get a
        // linear ramp between their low and high neighbor levels. Computed
        // once and shared between the top fan and any cliff walls.
        var vertH = new float[n];
        if (slope == null)
        {
            for (int i = 0; i < n; i++) vertH[i] = h;
        }
        else
        {
            for (int i = 0; i < n; i++) vertH[i] = GetVertexHeight(cell, i);
        }
        float centerH = slope == null ? h : GetCenterHeight(cell);

        // Slope cells skip the underwater rendering path even if their nominal
        // level is 0 — the tilt makes a flat seabed wrong, and slopes only
        // appear at cliff borders, never below water.
        if (level == 0 && slope == null)
        {
            float sandH = Radius - StepHeight;
            EmitCellFan(verts, idx, cell, sandH, cellNormal, 1);
            EmitCellFan(verts, idx, cell, h, cellNormal, 0);
        }
        else
        {
            // Use rock texture (level 3) for slope cells so they read as
            // walkable cliff faces rather than blending into the surface.
            byte topLevel = slope == null ? level : (byte)3;
            EmitCellFanCustom(verts, idx, cell, vertH, centerH, cellNormal, topLevel);
        }

        var nbrs = _neighbors[cell];
        for (int k = 0; k < nbrs.Length; k++)
        {
            byte nLevel = _levels[nbrs[k]];
            float nh = Radius + nLevel * StepHeight;

            int pA = ((k - 1) + n) % n;
            int pB = k;
            float topAH = vertH[pA];
            float topBH = vertH[pB];

            // Skip walls where the cell's surface at this edge already meets
            // the neighbor at or below its surface — happens at the slope's
            // low side and on flat cells with same/higher neighbor.
            if (topAH <= nh + 1e-5f && topBH <= nh + 1e-5f) continue;

            Vector3 topA = _polyVerts[cell][pA] * topAH;
            Vector3 topB = _polyVerts[cell][pB] * topBH;
            Vector3 botA = _polyVerts[cell][pA] * MathF.Min(nh, topAH);
            Vector3 botB = _polyVerts[cell][pB] * MathF.Min(nh, topBH);

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

    private void EmitCellFan(List<float> verts, List<uint> idx, int cell, float h, Vector3 normal, byte level)
    {
        int n = _polyVerts[cell].Length;
        var heights = new float[n];
        for (int i = 0; i < n; i++) heights[i] = h;
        EmitCellFanCustom(verts, idx, cell, heights, h, normal, level);
    }

    /// <summary>
    /// Triangle-fan emit with per-vertex heights. Used by both flat cells
    /// (uniform height) and slope cells (heights varying along the slope
    /// axis). Normal is computed flat for flat fans; slope fans use the
    /// face normal of the tilted plane so lighting matches the geometry.
    /// </summary>
    private void EmitCellFanCustom(List<float> verts, List<uint> idx, int cell,
        float[] vertHeights, float centerH, Vector3 fallbackNormal, byte level)
    {
        int n = _polyVerts[cell].Length;
        Vector3 cellNormal = _centers[cell];

        // Detect "is this slope tilted?" by checking height variance — if
        // all vertex heights match within a tiny epsilon, the fan is flat
        // and we can use the cell normal as before.
        float minH = vertHeights[0], maxH = vertHeights[0];
        for (int i = 1; i < n; i++)
        {
            if (vertHeights[i] < minH) minH = vertHeights[i];
            if (vertHeights[i] > maxH) maxH = vertHeights[i];
        }
        bool tilted = (maxH - minH) > 1e-5f;

        Vector3 faceNormal = fallbackNormal;
        if (tilted)
        {
            // Average vertex normal across the fan: each triangle (center,
            // vi, vj) has its own normal; the cell-wide normal is good enough
            // for cheap Lambert shading on slopes.
            Vector3 sum = Vector3.Zero;
            Vector3 c = cellNormal * centerH;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector3 a = _polyVerts[cell][i] * vertHeights[i];
                Vector3 b = _polyVerts[cell][j] * vertHeights[j];
                Vector3 nrm = Vector3.Cross(a - c, b - c);
                if (nrm.LengthSquared() > 1e-10f) sum += Vector3.Normalize(nrm);
            }
            if (sum.LengthSquared() > 1e-8f) faceNormal = Vector3.Normalize(sum);
        }

        uint ci = (uint)(verts.Count / VertexFloats);
        EmitVert(verts, cellNormal * centerH, faceNormal, level);
        for (int i = 0; i < n; i++)
            EmitVert(verts, _polyVerts[cell][i] * vertHeights[i], faceNormal, level);
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
