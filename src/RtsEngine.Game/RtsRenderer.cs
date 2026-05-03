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
    public const int UniformSize = 112;    // mvp(64) + color(16) + sunDir(16) + teamColor(16)

    private readonly IGPU _gpu;
    private readonly RtsConfig _config;

    private int _pipeline;
    private int _markerPipeline;   // alpha-blend variant for selection discs / HP bars
    private int _ubo;
    private int _sampler;
    private int _markerBindGroup;  // any-texture bind group for flat-shaded markers
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
    /// <summary>One bind group per entity type (UBO + sampler + texture).
    /// Looked up at draw time so each instance samples its own surface texture.</summary>
    private readonly Dictionary<string, int> _bindGroups = new();

    /// <summary>
    /// Map team id → livery color. Index 0 is the local player; everything
    /// else is an enemy/AI. Hardcoded for now — expand into config when more
    /// teams matter.
    /// </summary>
    private static readonly Vector3[] TeamColors =
    {
        new(0.20f, 0.55f, 1.00f),  // 0 — player blue
        new(0.95f, 0.25f, 0.25f),  // 1 — enemy red
        new(0.30f, 0.85f, 0.40f),  // 2 — neutral green
        new(0.95f, 0.85f, 0.25f),  // 3 — extra yellow
    };

    private static Vector3 ColorForTeam(int team) =>
        team >= 0 && team < TeamColors.Length ? TeamColors[team] : TeamColors[0];

    // Shared marker geometry. Disc is a 24-segment fan in the local XZ plane
    // (Y up = surface normal). Quad is a unit rectangle in the local XY plane,
    // centered on (0,0) — for HP bars we billboard via a model matrix built
    // from camera right/up.
    private Mesh _discMesh;
    private Mesh _quadMesh;

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
        _sampler = await _gpu.CreateSampler("nearest", "repeat");

        // Alpha-blend variant of the same shader for translucent markers.
        _markerPipeline = await _gpu.CreateRenderPipelineAlphaBlend(shader, new object[]
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

        _discMesh = await BuildDiscMesh(24);
        _quadMesh = await BuildQuadMesh();

        foreach (var b in _config.Buildings)
        {
            _buildingMeshes[b.Id] = await BuildMesh(b.Id, objs, b.HalfWidth, b.Height);
            _buildingColors[b.Id] = ColorOf(b.Color);
            _bindGroups[b.Id] = await BuildBindGroup(b.Id);
        }
        foreach (var u in _config.Units)
        {
            _unitMeshes[u.Id] = await BuildMesh(u.Id, objs, u.HalfWidth, u.Height);
            _unitColors[u.Id] = ColorOf(u.Color);
            _bindGroups[u.Id] = await BuildBindGroup(u.Id);
        }

        // Markers (selection disc, HP bar) sample the texture too — the result
        // is mixed away by sunDir.w=1 in the fragment shader, but the binding
        // still has to be valid. Reuse any entity's bind group; the first
        // building works fine.
        _markerBindGroup = _bindGroups.Values.FirstOrDefault();

        _ready = true;
    }

    /// <summary>
    /// Per-entity bind group: shared UBO at slot 0, shared sampler at slot 1,
    /// the entity's surface texture at slot 2. Texture lookup goes through
    /// IGPU.CreateTextureFromUrl which on desktop reads from
    /// assets/textures/surfaces/, on WASM via HttpClient against the same
    /// path.
    /// </summary>
    private async Task<int> BuildBindGroup(string entityId)
    {
        var texPath = $"assets/textures/surfaces/{entityId}.png";
        int tex;
        try { tex = await _gpu.CreateTextureFromUrl(texPath); }
        catch { tex = 0; }
        return await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
            new { binding = 1, samplerId = _sampler },
            new { binding = 2, textureViewId = tex },
        });
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

    /// <summary>Disc in local XZ plane, normal = +Y. Triangle fan radiating
    /// from origin, radius 1. The model matrix scales it to the entity's
    /// footprint and orients +Y along the surface normal.</summary>
    private async Task<Mesh> BuildDiscMesh(int segments)
    {
        var verts = new List<float>();
        var idx = new List<ushort>();
        // Center vertex.
        verts.Add(0f); verts.Add(0f); verts.Add(0f);
        verts.Add(0f); verts.Add(1f); verts.Add(0f);
        for (int i = 0; i < segments; i++)
        {
            float a = 2f * MathF.PI * i / segments;
            verts.Add(MathF.Cos(a)); verts.Add(0f); verts.Add(MathF.Sin(a));
            verts.Add(0f);          verts.Add(1f); verts.Add(0f);
        }
        for (int i = 0; i < segments; i++)
        {
            idx.Add(0);
            idx.Add((ushort)(1 + i));
            idx.Add((ushort)(1 + (i + 1) % segments));
        }
        int vbo = await _gpu.CreateVertexBuffer(verts.ToArray());
        int ibo = await _gpu.CreateIndexBuffer(idx.ToArray());
        return new Mesh { Vbo = vbo, Ibo = ibo, IndexCount = idx.Count };
    }

    /// <summary>Unit quad in local XY plane (X = -0.5..0.5, Y = 0..1, Z = 0).
    /// Used for HP bars; the model matrix billboards it via camera basis.</summary>
    private async Task<Mesh> BuildQuadMesh()
    {
        float[] verts = {
            // pos, normal — normal points at +Z so face culling treats us
            // like a normal forward-facing quad.
            -0.5f, 0f, 0f,   0f, 0f, 1f,
             0.5f, 0f, 0f,   0f, 0f, 1f,
             0.5f, 1f, 0f,   0f, 0f, 1f,
            -0.5f, 1f, 0f,   0f, 0f, 1f,
        };
        ushort[] idx = { 0, 1, 2, 0, 2, 3 };
        int vbo = await _gpu.CreateVertexBuffer(verts);
        int ibo = await _gpu.CreateIndexBuffer(idx);
        return new Mesh { Vbo = vbo, Ibo = ibo, IndexCount = idx.Length };
    }

    /// <summary>
    /// Draw all placed buildings and spawned units. <paramref name="planetMvp"/>
    /// is the same MVP used to render the planet (planet center = origin in
    /// the source space). Each instance gets a model matrix that places its
    /// base on the surface and aligns its Y axis with the cell's outward radial.
    /// </summary>
    public void Draw(RtsState state, PlanetMesh mesh, float[] planetMvp, Vector3 cameraPosLocal)
    {
        if (!_ready) return;

        var planetMvpMat = RawToMat(planetMvp);

        // Camera basis in planet-local frame, used to billboard HP bars so
        // they always face the screen no matter which side of the planet the
        // entity sits on. Forward = camera looking at planet origin.
        var camFwd = cameraPosLocal.LengthSquared() > 1e-8f
            ? -Vector3.Normalize(cameraPosLocal)
            : new Vector3(0, 0, -1);
        var camRight = Vector3.Cross(camFwd, new Vector3(0, 1, 0));
        if (camRight.LengthSquared() < 1e-6f) camRight = new Vector3(1, 0, 0);
        camRight = Vector3.Normalize(camRight);
        var camUp = Vector3.Normalize(Vector3.Cross(camRight, camFwd));

        // Pass 1: building + unit meshes (lit, opaque).
        foreach (var b in state.Buildings)
        {
            if (!_buildingMeshes.TryGetValue(b.TypeId, out var bm)) continue;
            if (!_bindGroups.TryGetValue(b.TypeId, out var bg)) continue;
            var color = _buildingColors[b.TypeId];

            var up = mesh.GetCellCenter(b.CellIndex);
            float surfaceR = mesh.Radius + mesh.GetLevel(b.CellIndex) * mesh.StepHeight;
            var pos = up * surfaceR;

            var (modelMvp, sunModel) = BuildModelMvpAndSun(pos, up, planetMvpMat, _sunDir, heading: null);
            DrawInstance(bm, bg, modelMvp, color, sunModel, ColorForTeam(b.Team),
                selected: b.InstanceId == state.SelectedBuildingInstanceId);
        }

        foreach (var u in state.Units)
        {
            if (!_unitMeshes.TryGetValue(u.TypeId, out var um)) continue;
            if (!_bindGroups.TryGetValue(u.TypeId, out var bg)) continue;
            var color = _unitColors[u.TypeId];
            var (modelMvp, sunModel) = BuildModelMvpAndSun(u.SurfacePoint, u.SurfaceUp, planetMvpMat, _sunDir, heading: u.Heading);
            DrawInstance(um, bg, modelMvp, color, sunModel, ColorForTeam(u.Team),
                selected: state.SelectedUnitInstanceIds.Contains(u.InstanceId));
        }

        // Pass 2: selection discs (translucent, flat-shaded). Drawn after
        // entities so the disc sits on top of the surface rendering at the
        // entity's footprint.
        foreach (var b in state.Buildings)
        {
            if (b.InstanceId != state.SelectedBuildingInstanceId) continue;
            var def = _config.GetBuilding(b.TypeId); if (def == null) continue;
            var up = mesh.GetCellCenter(b.CellIndex);
            float surfaceR = mesh.Radius + mesh.GetLevel(b.CellIndex) * mesh.StepHeight;
            DrawSelectionDisc(up * surfaceR, up, def.HalfWidth * 1.6f, planetMvpMat);
        }
        foreach (var u in state.Units)
        {
            if (!state.SelectedUnitInstanceIds.Contains(u.InstanceId)) continue;
            var def = _config.GetUnit(u.TypeId); if (def == null) continue;
            DrawSelectionDisc(u.SurfacePoint, u.SurfaceUp, def.HalfWidth * 1.6f, planetMvpMat);
        }

        // Pass 3: HP bars — billboarded above each entity. Rendered for every
        // entity so the player can see relative HP at a glance.
        foreach (var b in state.Buildings)
        {
            var def = _config.GetBuilding(b.TypeId); if (def == null) continue;
            var up = mesh.GetCellCenter(b.CellIndex);
            float surfaceR = mesh.Radius + mesh.GetLevel(b.CellIndex) * mesh.StepHeight;
            var center = up * (surfaceR + def.Height + 0.015f);
            DrawHealthBar(center, camRight, camUp, def.HalfWidth * 2.2f,
                MathF.Max(0f, b.Hp / b.MaxHp), planetMvpMat);
        }
        foreach (var u in state.Units)
        {
            var def = _config.GetUnit(u.TypeId); if (def == null) continue;
            var center = u.SurfacePoint + u.SurfaceUp * (def.Height + 0.012f);
            DrawHealthBar(center, camRight, camUp, def.HalfWidth * 2.2f,
                MathF.Max(0f, u.Hp / u.MaxHp), planetMvpMat);
        }
    }

    private void DrawInstance(Mesh m, int bindGroup, Matrix4X4<float> modelMvp, Vector3 color,
        Vector3 sunDirModelSpace, Vector3 teamColor, bool selected)
    {
        // Selected buildings flash bright via a color tint — cheap visual
        // feedback layered on top of the texture.
        var c = selected ? Vector3.Lerp(color, new Vector3(1f, 0.95f, 0.5f), 0.55f) : color;

        var uni = new float[28];
        var raw = MatrixHelper.ToRawFloats(modelMvp);
        for (int i = 0; i < 16; i++) uni[i] = raw[i];
        uni[16] = c.X; uni[17] = c.Y; uni[18] = c.Z; uni[19] = 1f;
        // sunDir in MODEL space — rts.glsl multiplies it against vNormal which
        // is also model-space (passed straight through from the .obj). World-
        // space sun dir would mismatch and produce nonsense lighting that
        // doesn't track the planet's day/night terminator.
        uni[20] = sunDirModelSpace.X; uni[21] = sunDirModelSpace.Y; uni[22] = sunDirModelSpace.Z; uni[23] = 0f;
        uni[24] = teamColor.X; uni[25] = teamColor.Y; uni[26] = teamColor.Z; uni[27] = 1f;

        _gpu.WriteBuffer(_ubo, uni);
        // RenderAdditional loads the existing color + depth buffers — Render
        // would clear them, wiping out the planet/starfield/etc. Each instance
        // ticks onto the same accumulated frame.
        _gpu.RenderAdditional(_pipeline, m.Vbo, m.Ibo, bindGroup, m.IndexCount);
    }

    /// <summary>
    /// Build the model→clip MVP and a model-space sun direction in one pass.
    /// The basis (right, up, fwd) is the rotation half of the model matrix;
    /// to convert a world vector W into model space we just dot it against
    /// each row (right, up, fwd) → that's the local axis components.
    /// </summary>
    private static (Matrix4X4<float> mvp, Vector3 sunModel) BuildModelMvpAndSun(
        Vector3 pos, Vector3 up, Matrix4X4<float> mvp, Vector3 sunWorld,
        Vector3? heading = null)
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

        // Inverse-transpose of an orthonormal basis is the basis itself —
        // dotting world vectors with each row yields the model-space axes.
        var sunModel = new Vector3(
            Vector3.Dot(sunWorld, right),
            Vector3.Dot(sunWorld, u),
            Vector3.Dot(sunWorld, fwd));

        return (Matrix4X4.Multiply(basis, mvp), sunModel);
    }

    /// <summary>
    /// Translucent yellow ring on the surface under a selected entity. Disc
    /// lives in local XZ; the model matrix re-bases it onto the surface frame
    /// so it lies flat on the ground regardless of where on the planet it is.
    /// </summary>
    private void DrawSelectionDisc(Vector3 surfacePos, Vector3 surfaceUp, float radius,
        Matrix4X4<float> planetMvpMat)
    {
        var u = Vector3.Normalize(surfaceUp);
        var fwd = ArbitraryTangent(u);
        var right = Vector3.Normalize(Vector3.Cross(u, fwd));
        // Lift the disc a hair off the ground to avoid z-fighting with the
        // planet mesh patches (log-depth precision is generous but coplanar
        // surfaces can still flicker).
        var center = surfacePos + u * 0.0015f;
        var basis = new Matrix4X4<float>(
            right.X * radius, right.Y * radius, right.Z * radius, 0f,
            u.X,              u.Y,              u.Z,              0f,
            fwd.X * radius,   fwd.Y * radius,   fwd.Z * radius,   0f,
            center.X,         center.Y,         center.Z,         1f);
        var modelMvp = Matrix4X4.Multiply(basis, planetMvpMat);
        WriteMarkerUniform(modelMvp, new Vector4(1f, 0.92f, 0.25f, 0.55f));
        _gpu.RenderAdditional(_markerPipeline, _discMesh.Vbo, _discMesh.Ibo, _markerBindGroup, _discMesh.IndexCount);
    }

    /// <summary>
    /// HP bar billboarded against the camera basis. Two quads stacked: a
    /// dim red background full-width, then a green fill scaled by hp%
    /// rendered in front.
    /// </summary>
    private void DrawHealthBar(Vector3 center, Vector3 camRight, Vector3 camUp,
        float widthWorld, float hpFrac, Matrix4X4<float> planetMvpMat)
    {
        const float HeightWorld = 0.012f;

        // Background bar — dim red, full width. Quad mesh spans -0.5..0.5 in
        // local X and 0..1 in local Y; basis maps that into a camera-aligned
        // rect at `center`.
        var bgBasis = new Matrix4X4<float>(
            camRight.X * widthWorld, camRight.Y * widthWorld, camRight.Z * widthWorld, 0f,
            camUp.X    * HeightWorld, camUp.Y    * HeightWorld, camUp.Z    * HeightWorld, 0f,
            0f, 0f, 1f, 0f,
            center.X, center.Y, center.Z, 1f);
        var bgMvp = Matrix4X4.Multiply(bgBasis, planetMvpMat);
        WriteMarkerUniform(bgMvp, new Vector4(0.45f, 0.05f, 0.05f, 0.85f));
        _gpu.RenderAdditional(_markerPipeline, _quadMesh.Vbo, _quadMesh.Ibo, _markerBindGroup, _quadMesh.IndexCount);

        // Foreground fill. Width scaled by hp%, anchored to the bar's left
        // edge — that means the basis X column shrinks AND the translation
        // shifts left by the missing width.
        if (hpFrac <= 0f) return;
        float fillWidth = widthWorld * hpFrac;
        // bg goes -widthWorld/2 to +widthWorld/2 along camRight; we want
        // fill to start at the same left edge. Center of fill quad:
        //   leftEdge + fillWidth/2 = -widthWorld/2 + fillWidth/2
        float offsetX = -widthWorld * 0.5f + fillWidth * 0.5f;
        var fillCenter = center + camRight * offsetX;
        var fgBasis = new Matrix4X4<float>(
            camRight.X * fillWidth, camRight.Y * fillWidth, camRight.Z * fillWidth, 0f,
            camUp.X    * HeightWorld * 0.85f, camUp.Y * HeightWorld * 0.85f, camUp.Z * HeightWorld * 0.85f, 0f,
            0f, 0f, 1f, 0f,
            fillCenter.X, fillCenter.Y, fillCenter.Z, 1f);
        var fgMvp = Matrix4X4.Multiply(fgBasis, planetMvpMat);
        // Green when healthy, sliding to red when low.
        var fillColor = hpFrac > 0.5f
            ? Vector3.Lerp(new Vector3(0.95f, 0.85f, 0.1f), new Vector3(0.2f, 0.95f, 0.25f), (hpFrac - 0.5f) * 2f)
            : Vector3.Lerp(new Vector3(0.95f, 0.15f, 0.1f), new Vector3(0.95f, 0.85f, 0.1f), hpFrac * 2f);
        WriteMarkerUniform(fgMvp, new Vector4(fillColor, 0.95f));
        _gpu.RenderAdditional(_markerPipeline, _quadMesh.Vbo, _quadMesh.Ibo, _markerBindGroup, _quadMesh.IndexCount);
    }

    /// <summary>Marker uniform: same 28-float layout as a normal entity but
    /// with sunDir.w = 1 to flag flat shading and a no-op team color.</summary>
    private void WriteMarkerUniform(Matrix4X4<float> modelMvp, Vector4 color)
    {
        var uni = new float[28];
        var raw = MatrixHelper.ToRawFloats(modelMvp);
        for (int i = 0; i < 16; i++) uni[i] = raw[i];
        uni[16] = color.X; uni[17] = color.Y; uni[18] = color.Z; uni[19] = color.W;
        uni[20] = 0f; uni[21] = 1f; uni[22] = 0f; uni[23] = 1f;  // sunDir.w = 1 → flat
        uni[24] = 1f; uni[25] = 1f; uni[26] = 1f; uni[27] = 1f;  // teamColor unused
        _gpu.WriteBuffer(_ubo, uni);
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
        if (_discMesh.Vbo > 0) { _gpu.DestroyBuffer(_discMesh.Vbo); _gpu.DestroyBuffer(_discMesh.Ibo); }
        if (_quadMesh.Vbo > 0) { _gpu.DestroyBuffer(_quadMesh.Vbo); _gpu.DestroyBuffer(_quadMesh.Ibo); }
    }
}
