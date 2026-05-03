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
    public float ChamferInset { get; }
    public float ChamferDrop { get; }
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

    public PlanetMesh(int subdivisions = 4, float radius = 1.0f, float stepHeight = 0.04f,
        float chamferInset = 0f, float chamferDrop = 0f)
    {
        Radius = radius;
        StepHeight = stepHeight;
        ChamferInset = chamferInset;
        ChamferDrop = chamferDrop;

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
    /// height. For slope cells the two polygon vertices flanking the low
    /// edge are pinned to <c>lowH</c> and the two flanking the high edge
    /// to <c>highH</c>, so the slope's rims sit flush against the adjacent
    /// flat cells (otherwise irregular Goldberg hexes leave a small ε gap
    /// because the corner vertices project at unequal axial extents).
    /// Remaining "perpendicular" vertices interpolate linearly along the
    /// axis between the two edge midpoints.
    /// </summary>
    public float GetVertexHeight(int cell, int vertIndex)
    {
        var slope = _slopes[cell];
        float baseH = Radius + _levels[cell] * StepHeight;
        if (slope == null) return baseH;

        float lowH = Radius + _levels[slope.LowNeighbor] * StepHeight;
        float highH = Radius + _levels[slope.HighNeighbor] * StepHeight;

        var nbrs = _neighbors[cell];
        int n = nbrs.Length;
        int lowIdx = -1, highIdx = -1;
        for (int i = 0; i < n; i++)
        {
            if (nbrs[i] == slope.LowNeighbor) lowIdx = i;
            if (nbrs[i] == slope.HighNeighbor) highIdx = i;
        }
        if (lowIdx < 0 || highIdx < 0) return baseH;

        // Polygon edge facing nbrs[k] is bordered by polyVerts[k-1] and
        // polyVerts[k]; both are corners of that edge.
        int lowA = (lowIdx - 1 + n) % n;
        int lowB = lowIdx;
        int highA = (highIdx - 1 + n) % n;
        int highB = highIdx;

        if (vertIndex == lowA || vertIndex == lowB) return lowH;
        if (vertIndex == highA || vertIndex == highB) return highH;

        var verts = _polyVerts[cell];
        Vector3 cellNormal = _centers[cell];
        Vector3 lowMid = (verts[lowA] + verts[lowB]) * 0.5f;
        Vector3 highMid = (verts[highA] + verts[highB]) * 0.5f;
        Vector3 axis = highMid - lowMid;
        axis -= cellNormal * Vector3.Dot(axis, cellNormal);
        if (axis.LengthSquared() < 1e-8f) return (lowH + highH) * 0.5f;
        axis = Vector3.Normalize(axis);

        float lowProj = Vector3.Dot(lowMid - cellNormal, axis);
        float highProj = Vector3.Dot(highMid - cellNormal, axis);
        float range = highProj - lowProj;
        if (MathF.Abs(range) < 1e-8f) return (lowH + highH) * 0.5f;
        float vt = Vector3.Dot(verts[vertIndex] - cellNormal, axis);
        float u = Math.Clamp((vt - lowProj) / range, 0f, 1f);
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

        // Per-edge convexity drives per-edge chamfer. An edge is convex
        // when both of its corner heights sit strictly above the neighbor's
        // top — only then does dropping it produce a visible cliff-top
        // bevel. Edges with same/higher neighbors keep their corners at
        // full vertH so adjacent cells meet flush. Asymmetric on purpose:
        // the higher cell drops, the lower cell does not (the wall is
        // shortened by `ChamferDrop` and the chamfer ring covers the
        // visual gap on top), so concave edges never get a bevel.
        var nbrs = _neighbors[cell];
        bool[] edgeConvex = new bool[n];
        bool anyConvex = false;
        for (int k = 0; k < n; k++)
        {
            byte nLevelE = _levels[nbrs[k]];
            float nhE = Radius + nLevelE * StepHeight;
            int pAe = ((k - 1) + n) % n;
            int pBe = k;
            float ourMinH = MathF.Min(vertH[pAe], vertH[pBe]);
            edgeConvex[k] = ourMinH > nhE + 1e-5f;
            if (edgeConvex[k]) anyConvex = true;
        }
        bool chamferActive = level > 0 && ChamferDrop > 0f && ChamferInset > 0f && anyConvex;
        var edgeDrop = new float[n];
        if (chamferActive)
        {
            for (int k = 0; k < n; k++)
                edgeDrop[k] = edgeConvex[k] ? ChamferDrop : 0f;
        }
        var insetDirs = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            insetDirs[i] = chamferActive
                ? Vector3.Normalize(Vector3.Lerp(_polyVerts[cell][i], cellNormal, ChamferInset))
                : _polyVerts[cell][i];
        }

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
            // Top fan emitted per-edge so each edge can decide for itself
            // whether to pull its corners in toward cellNormal (only at
            // convex edges, to make room for a chamfer bevel) or run the
            // top fan all the way out to polyVerts (at non-convex edges,
            // so adjacent same-level cells meet flush at the polygon
            // edge with no extra geometry sitting between them). The
            // center vertex is shared across all N triangles.
            Vector3 cellFaceNormalLocal = ComputeFaceNormal(cell);
            uint centerIdx = (uint)(verts.Count / VertexFloats);
            EmitVert(verts, cellNormal * centerH, cellFaceNormalLocal, level);
            for (int k = 0; k < n; k++)
            {
                int pA = ((k - 1) + n) % n;
                int pB = k;
                bool conv = chamferActive && edgeConvex[k];
                Vector3 cornerA = (conv ? insetDirs[pA] : _polyVerts[cell][pA]) * vertH[pA];
                Vector3 cornerB = (conv ? insetDirs[pB] : _polyVerts[cell][pB]) * vertH[pB];
                Vector3 nA = SmoothNormalAtCorner(cell, pA);
                Vector3 nB = SmoothNormalAtCorner(cell, pB);
                uint baseIdx = (uint)(verts.Count / VertexFloats);
                EmitVert(verts, cornerA, nA, level);
                EmitVert(verts, cornerB, nB, level);
                idx.Add(centerIdx);
                idx.Add(baseIdx);
                idx.Add(baseIdx + 1);
            }
            if (chamferActive) EmitChamferRing(verts, idx, cell, insetDirs, vertH, edgeConvex, level);
        }

        // Asymmetric drop: only the higher cell drops at a convex edge, so
        // the wall extends from our (vertH - drop) at the top down to the
        // neighbor's full vertH at the bottom. Wall is shorter by drop;
        // the chamfer ring on top covers the visual gap.
        float nominalH = h;
        for (int k = 0; k < nbrs.Length; k++)
        {
            byte nLevel = _levels[nbrs[k]];
            float nh = Radius + nLevel * StepHeight;

            int pA = ((k - 1) + n) % n;
            int pB = k;
            // Per-edge perim heights — drop only on convex edges (= where
            // edgeDrop[k] > 0). Non-convex edges keep topAH/topBH at vertH
            // so adjacent same-level cells meet flush.
            float topAH = vertH[pA] - edgeDrop[k];
            float topBH = vertH[pB] - edgeDrop[k];

            // Boundary height the wall meets at this edge:
            //   - flat cells: the neighbor's flat top (nh).
            //   - slope cells: capped at the cell's own nominal level so that
            //     higher neighbors (which already emit their own wall down to
            //     nominal) don't cause overlapping geometry. For lower
            //     neighbors this still resolves to nh.
            float bdyH = slope == null ? nh : MathF.Min(nh, nominalH);

            // For flat cells, suppress the wall when our edge sits below the
            // neighbor — the higher neighbor handles that wall and we'd
            // otherwise z-fight. Slope cells must emit walls in both
            // directions because perpendicular flat neighbors don't know
            // about the tilt and won't emit a gap-fill wall on their side.
            if (slope == null && topAH < bdyH - 1e-5f && topBH < bdyH - 1e-5f) continue;

            // Wall spans [vertH, bdyH] per corner — top is whichever is
            // higher, bottom whichever is lower. The two-sided index winding
            // below renders correctly regardless of which side is up.
            float aHi = MathF.Max(topAH, bdyH);
            float aLo = MathF.Min(topAH, bdyH);
            float bHi = MathF.Max(topBH, bdyH);
            float bLo = MathF.Min(topBH, bdyH);
            if (aHi - aLo < 1e-5f && bHi - bLo < 1e-5f) continue;

            Vector3 topA = _polyVerts[cell][pA] * aHi;
            Vector3 topB = _polyVerts[cell][pB] * bHi;
            Vector3 botA = _polyVerts[cell][pA] * aLo;
            Vector3 botB = _polyVerts[cell][pB] * bLo;

            // Per-corner smooth normals — the wall's two end columns sit on
            // polygon vertices that are shared by three cells in the
            // Goldberg dual (this cell + nbrs[idx] + nbrs[idx+1]). Using
            // the same 3-cell average as the top fan keeps lighting
            // continuous across the polygon edge where wall meets fan,
            // and removes the flat-shaded "one normal per wall" look that
            // made wall brightness flicker face by face.
            Vector3 nA = SmoothNormalAtCorner(cell, pA);
            Vector3 nB = SmoothNormalAtCorner(cell, pB);

            byte cliffLevel = 3;

            uint b = (uint)(verts.Count / VertexFloats);
            EmitVert(verts, topA, nA, cliffLevel);
            EmitVert(verts, topB, nB, cliffLevel);
            EmitVert(verts, botB, nB, cliffLevel);
            EmitVert(verts, botA, nA, cliffLevel);

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
        EmitCellFanCustom(verts, idx, cell, _polyVerts[cell], heights, h, normal, level);
    }

    /// <summary>
    /// Per-edge chamfer ring. For each polygon edge k, emits one quad:
    /// inner corners at <c>insetDirs[i] * vertHeights[i]</c> (full height,
    /// inset toward cell center) and outer corners at
    /// <c>polyVerts[i] * (vertHeights[i] - edgeDrop[k])</c>. Convex edges
    /// have <c>edgeDrop[k] = ChamferDrop</c> so the quad tilts down to the
    /// lowered perimeter — the visible 45° bevel. Non-convex edges have
    /// <c>edgeDrop[k] = 0</c> so the quad stays at full vertH — a flat
    /// ring filling the gap between inset top fan and perimeter without
    /// producing a bevel, so adjacent same-level cells meet flush.
    ///
    /// Where adjacent edges differ in their drop ("split vertex"), the two
    /// quads end at different heights at the shared polygon vertex; a
    /// small seal triangle bridges that height step to keep the geometry
    /// water-tight.
    ///
    /// Both windings are emitted (front- and back-facing) because the
    /// bevel can be viewed from either side as the camera moves around;
    /// matches the wall loop's two-sided convention.
    /// </summary>
    private void EmitChamferRing(List<float> verts, List<uint> idx, int cell,
        Vector3[] insetDirs, float[] vertHeights, bool[] edgeConvex, byte level)
    {
        int n = _polyVerts[cell].Length;
        const byte rockLevel = 3;

        // Per-CONVEX-edge ring quads. Non-convex edges get no geometry —
        // the top fan already runs straight to polyVerts on those sides,
        // so no fill is needed and adjacent same-level cells meet flush.
        for (int k = 0; k < n; k++)
        {
            if (!edgeConvex[k]) continue;
            int pA = ((k - 1) + n) % n;
            int pB = k;
            Vector3 innerA = insetDirs[pA] * vertHeights[pA];
            Vector3 innerB = insetDirs[pB] * vertHeights[pB];
            Vector3 outerB = _polyVerts[cell][pB] * (vertHeights[pB] - ChamferDrop);
            Vector3 outerA = _polyVerts[cell][pA] * (vertHeights[pA] - ChamferDrop);

            Vector3 nA = SmoothNormalAtCorner(cell, pA);
            Vector3 nB = SmoothNormalAtCorner(cell, pB);

            uint b = (uint)(verts.Count / VertexFloats);
            EmitVert(verts, innerA, nA, rockLevel);
            EmitVert(verts, innerB, nB, rockLevel);
            EmitVert(verts, outerB, nB, rockLevel);
            EmitVert(verts, outerA, nA, rockLevel);

            // Two-sided (front + back) so the bevel is visible from any angle.
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 1);
            idx.Add(b); idx.Add(b + 3); idx.Add(b + 2);
        }

        // Seal triangles at split vertices — where one adjacent edge is
        // convex and the other isn't, the convex side's top fan corner
        // sits at insetDirs[i] (pulled in) while the non-convex side's
        // sits at polyVerts[i] (full perimeter). The seal connects:
        //   - inner = insetDirs[i] * vertH (convex top fan corner)
        //   - outerNonConvex = polyVerts[i] * vertH (non-convex top fan corner)
        //   - outerConvex = polyVerts[i] * (vertH - drop) (convex chamfer ring outer)
        // closing the small triangular gap at the corner where the two
        // edge styles meet. Rock-textured since the seal continues
        // directly from the chamfer bevel.
        for (int i = 0; i < n; i++)
        {
            int kLeft = i;             // edge k where pB == i
            int kRight = (i + 1) % n;  // edge k where pA == i
            if (edgeConvex[kLeft] == edgeConvex[kRight]) continue;

            Vector3 inner = insetDirs[i] * vertHeights[i];
            Vector3 outerNonConvex = _polyVerts[cell][i] * vertHeights[i];
            Vector3 outerConvex = _polyVerts[cell][i] * (vertHeights[i] - ChamferDrop);
            Vector3 nrm = SmoothNormalAtCorner(cell, i);

            uint b = (uint)(verts.Count / VertexFloats);
            EmitVert(verts, inner, nrm, rockLevel);
            EmitVert(verts, outerNonConvex, nrm, rockLevel);
            EmitVert(verts, outerConvex, nrm, rockLevel);

            // Two-sided like the ring quads.
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 1);
        }
    }

    /// <summary>
    /// Smoothed normal at one of this cell's polygon corners. The corner
    /// is shared by three cells in the Goldberg dual (this cell, nbrs[i]
    /// and nbrs[i+1] — the same three centroids that defined polyVerts[i]
    /// in the constructor), so the smooth normal is the mean of those
    /// three cells' face normals. All three cells compute the same value
    /// at the shared corner, which is what makes lighting continuous
    /// across the polygon edge between any pair of adjacent surfaces —
    /// fan-to-fan at flat boundaries, and now fan-to-wall at cliff tops.
    /// </summary>
    private Vector3 SmoothNormalAtCorner(int cell, int cornerIdx)
    {
        var nbrs = _neighbors[cell];
        int n = nbrs.Length;
        Vector3 cellN = ComputeFaceNormal(cell);
        Vector3 nA = ComputeFaceNormal(nbrs[cornerIdx]);
        Vector3 nB = ComputeFaceNormal(nbrs[(cornerIdx + 1) % n]);
        Vector3 sum = cellN + nA + nB;
        return sum.LengthSquared() > 1e-8f ? Vector3.Normalize(sum) : cellN;
    }

    /// <summary>
    /// Cell-wide face normal: cellNormal for flat cells, the averaged
    /// triangle normal of the tilted top for slope cells. Used both for
    /// the cell's own center vertex and as one of three samples in the
    /// per-corner smoothing average at shared polygon vertices.
    /// </summary>
    private Vector3 ComputeFaceNormal(int cell)
    {
        var slope = _slopes[cell];
        Vector3 cellNormal = _centers[cell];
        if (slope == null) return cellNormal;

        int n = _polyVerts[cell].Length;
        float centerH = GetCenterHeight(cell);
        Vector3 c = cellNormal * centerH;
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Vector3 a = _polyVerts[cell][i] * GetVertexHeight(cell, i);
            Vector3 b = _polyVerts[cell][j] * GetVertexHeight(cell, j);
            Vector3 nrm = Vector3.Cross(a - c, b - c);
            if (nrm.LengthSquared() > 1e-10f) sum += Vector3.Normalize(nrm);
        }
        return sum.LengthSquared() > 1e-8f ? Vector3.Normalize(sum) : cellNormal;
    }

    /// <summary>
    /// Triangle-fan emit with per-vertex heights. Each polygon vertex is
    /// shared by three cells (this cell + the two neighbors flanking the
    /// vertex on the Goldberg dual graph), so its smoothed normal is the
    /// mean of those three cells' face normals — guaranteeing all three
    /// cells write an identical normal at the shared point and the
    /// lighting transitions smoothly across cell boundaries.
    /// </summary>
    private void EmitCellFanCustom(List<float> verts, List<uint> idx, int cell,
        Vector3[] outerDirs, float[] vertHeights, float centerH, Vector3 fallbackNormal, byte level)
    {
        int n = _polyVerts[cell].Length;
        Vector3 cellNormal = _centers[cell];
        var nbrs = _neighbors[cell];

        // The cell's own face normal — used for the center vertex (where
        // there's no neighbor to average with) and as one of the three
        // samples per polygon corner.
        Vector3 cellFaceNormal = ComputeFaceNormal(cell);

        uint ci = (uint)(verts.Count / VertexFloats);
        EmitVert(verts, cellNormal * centerH, cellFaceNormal, level);
        for (int i = 0; i < n; i++)
        {
            // polyVerts[i] is the centroid of (cell, nbrs[i], nbrs[i+1]),
            // so those two neighbors are the cells sharing this vertex.
            Vector3 nA = ComputeFaceNormal(nbrs[i]);
            Vector3 nB = ComputeFaceNormal(nbrs[(i + 1) % n]);
            Vector3 smooth = cellFaceNormal + nA + nB;
            if (smooth.LengthSquared() > 1e-8f) smooth = Vector3.Normalize(smooth);
            else smooth = cellFaceNormal;
            EmitVert(verts, outerDirs[i] * vertHeights[i], smooth, level);
        }
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
