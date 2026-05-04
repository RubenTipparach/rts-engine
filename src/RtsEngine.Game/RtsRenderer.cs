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
    private readonly EngineConfig _engineConfig;

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
    // (Y up = surface normal). HP bars now render screen-space via _hpVbo, so
    // there's no per-instance world-space quad mesh.
    private Mesh _discMesh;

    // Screen-space HP bar pass — separate from world-space markers because
    // bars need to stay axis-aligned with the screen regardless of camera
    // tilt, and stay a constant pixel size at every zoom. Uses the colour-
    // only ui.wgsl pipeline. The vertex/index buffers are sized for
    // <see cref="MaxHpBars"/> bars and rewritten in place each frame via
    // queue.writeBuffer rather than recreated, so there's no per-frame
    // allocation churn.
    private int _hpPipeline;
    private int _hpVbo, _hpIbo;
    private const int MaxHpBars = 256;
    // Two quads per entity (background + fill), each quad = 4 verts and 6
    // indices. Vert layout matches ui.wgsl: vec2 pos + vec4 color = 6 floats.
    private const int VertsPerBar = 8;
    private const int IndicesPerBar = 12;
    private const int FloatsPerVert = 6;
    private readonly float[] _hpVertScratch = new float[MaxHpBars * VertsPerBar * FloatsPerVert];

    // Path-debug pass — draws each unit's queued A* path as a line strip on
    // the planet surface when EngineConfig.Debug.ShowUnitPaths is on. Reuses
    // outline.wgsl (uniform mvp + color, vertex = pos3 only). Streaming
    // vertex buffer pre-sized for the max we'd ever care to draw at once.
    private int _pathPipeline;
    private int _pathVbo, _pathIbo, _pathUbo, _pathBindGroup;
    private const int MaxPathSegments = 8192;
    // Each segment is a line-list pair: 2 verts × 3 floats.
    private readonly float[] _pathVertScratch = new float[MaxPathSegments * 2 * 3];

    public RtsRenderer(IGPU gpu, RtsConfig config, EngineConfig engineConfig)
    {
        _gpu = gpu;
        _config = config;
        _engineConfig = engineConfig;
    }

    public async Task Setup(string shaderCode, string uiShaderCode, string lineShaderCode,
        Dictionary<string, string>? objs = null)
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

        // Marker pipeline (alpha blend, no depth write, cull-none) for
        // translucent overlays — selection discs, HP bars, future build/move
        // ghosts. NOT the alpha-blend pipeline used by the atmosphere; that
        // one culls front faces for inside-out shells, which would hide
        // these flat quads from any normal camera angle.
        _markerPipeline = await _gpu.CreateRenderPipelineMarker(shader, new object[]
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

        // Markers (selection disc, future build ghost) sample the texture too
        // — the result is mixed away by sunDir.w=1 in the fragment shader, but
        // the binding still has to be valid. Reuse any entity's bind group;
        // the first building works fine.
        _markerBindGroup = _bindGroups.Values.FirstOrDefault();

        // Screen-space HP bar pass. ui.wgsl takes vec2 NDC + vec4 color, no
        // uniforms. Vertex buffer is allocated once at MaxHpBars capacity and
        // rewritten in place each frame; the index buffer is static.
        var uiShader = await _gpu.CreateShaderModule(uiShaderCode);
        _hpPipeline = await _gpu.CreateRenderPipelineUI(uiShader, new object[]
        {
            new {
                arrayStride = FloatsPerVert * 4,
                attributes = new object[]
                {
                    new { format = "float32x2", offset = 0, shaderLocation = 0 },
                    new { format = "float32x4", offset = 8, shaderLocation = 1 },
                }
            }
        });
        _hpVbo = await _gpu.CreateVertexBuffer(_hpVertScratch);
        var hpIdx = new ushort[MaxHpBars * IndicesPerBar];
        for (int i = 0; i < MaxHpBars; i++)
        {
            int v = i * VertsPerBar;
            int o = i * IndicesPerBar;
            // Background quad (verts v..v+3) and fill quad (verts v+4..v+7),
            // each two triangles in (0,1,2)+(0,2,3) order.
            hpIdx[o + 0] = (ushort)(v + 0);
            hpIdx[o + 1] = (ushort)(v + 1);
            hpIdx[o + 2] = (ushort)(v + 2);
            hpIdx[o + 3] = (ushort)(v + 0);
            hpIdx[o + 4] = (ushort)(v + 2);
            hpIdx[o + 5] = (ushort)(v + 3);
            hpIdx[o + 6] = (ushort)(v + 4);
            hpIdx[o + 7] = (ushort)(v + 5);
            hpIdx[o + 8] = (ushort)(v + 6);
            hpIdx[o + 9] = (ushort)(v + 4);
            hpIdx[o + 10] = (ushort)(v + 6);
            hpIdx[o + 11] = (ushort)(v + 7);
        }
        _hpIbo = await _gpu.CreateIndexBuffer(hpIdx);

        // Path-debug pass. outline.wgsl uses pos3 + uniform { mvp, color }.
        var lineShader = await _gpu.CreateShaderModule(lineShaderCode);
        _pathPipeline = await _gpu.CreateRenderPipelineLines(lineShader, new object[]
        {
            new {
                arrayStride = 12,
                attributes = new object[] { new { format = "float32x3", offset = 0, shaderLocation = 0 } }
            }
        });
        _pathUbo = await _gpu.CreateUniformBuffer(80);
        _pathBindGroup = await _gpu.CreateBindGroup(_pathPipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _pathUbo },
        });
        _pathVbo = await _gpu.CreateVertexBuffer(_pathVertScratch);
        var pathIdx = new ushort[MaxPathSegments * 2];
        for (int i = 0; i < pathIdx.Length; i++) pathIdx[i] = (ushort)i;
        _pathIbo = await _gpu.CreateIndexBuffer(pathIdx);

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

    /// <summary>
    /// Draw all placed buildings and spawned units. <paramref name="planetMvp"/>
    /// is the same MVP used to render the planet (planet center = origin in
    /// the source space). Each instance gets a model matrix that places its
    /// base on the surface and aligns its Y axis with the cell's outward radial.
    /// </summary>
    public void Draw(RtsState state, PlanetMesh mesh, float[] planetMvp,
        Vector3 cameraPosLocal, float canvasW, float canvasH, int hoveredCell)
    {
        if (!_ready) return;

        var planetMvpMat = RawToMat(planetMvp);

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

        // Pass 2.5: building placement ghost — when a build is queued, draw
        // a translucent copy of the chosen building on the cell under the
        // cursor. Tinted green for valid, red for occupied, so the player
        // knows whether the click will land.
        DrawPlacementGhost(state, mesh, planetMvpMat, hoveredCell);

        // Pass 2.7: move-order destination markers — small disc on each
        // selected unit's path goal, so the player can see where they'll end
        // up. Only shown for selected units to keep the screen clean when
        // many units are moving at once.
        foreach (var u in state.Units)
        {
            if (!state.SelectedUnitInstanceIds.Contains(u.InstanceId)) continue;
            if (u.Path == null || u.Path.Count == 0) continue;
            int destCell = u.Path[u.Path.Count - 1];
            var def = _config.GetUnit(u.TypeId); if (def == null) continue;
            var up = mesh.GetCellCenter(destCell);
            float surfaceR = mesh.Radius + mesh.GetLevel(destCell) * mesh.StepHeight;
            DrawMoveDestMarker(up * surfaceR, up, def.HalfWidth * 1.4f, planetMvpMat);
        }

        // Pass 3: HP bars — screen-space, only over selected/hovered entities.
        DrawHealthBars(state, mesh, planetMvpMat, cameraPosLocal, canvasW, canvasH);

        // Pass 4 (debug only): unit paths — straight line strips from each
        // moving unit's current position along its remaining waypoints.
        if (_engineConfig.Debug.ShowUnitPaths)
            DrawUnitPaths(state, mesh, planetMvp);
    }

    /// <summary>When debug.showUnitPaths is on, emit a line-list pair for
    /// every segment in each <i>selected</i> unit's queued path. Showing
    /// paths for unselected units clutters the screen during large group
    /// orders; gating by selection keeps the debug viz scoped to "what am
    /// I currently inspecting". MovementSystem leaves <c>unit.Path</c> on
    /// the unit even after arrival (the consumed-state signal is PathIndex
    /// >= Count) so the route stays drawn until the next move order
    /// replaces it. One uniform write + one indexed line draw per frame
    /// regardless of unit count.</summary>
    private void DrawUnitPaths(RtsState state, PlanetMesh mesh, float[] planetMvp)
    {
        // Lift each segment endpoint a fraction of a step-height above its
        // own cell's surface — small enough to read as "on the ground", big
        // enough to clear the chamfer + slope geometry without z-fighting.
        // Mustn't be a full-radius lift: at RTS distance the camera sits at
        // ~1.15× radius, so anything lifted beyond that ends up behind the
        // camera for cells near the subcamera point and gets clipped.
        float lift = mesh.StepHeight * 0.5f;

        int segs = 0;
        foreach (var u in state.Units)
        {
            if (!state.SelectedUnitInstanceIds.Contains(u.InstanceId)) continue;
            var path = u.Path;
            if (path == null || path.Count < 2) continue;

            // Draw the whole route — start cell, every waypoint, goal.
            var startUp = mesh.GetCellCenter(path[0]);
            float startR = mesh.Radius + mesh.GetLevel(path[0]) * mesh.StepHeight + lift;
            Vector3 prev = startUp * startR;
            for (int k = 1; k < path.Count; k++)
            {
                if (segs >= MaxPathSegments) break;
                var cellUp = mesh.GetCellCenter(path[k]);
                float surfaceR = mesh.Radius + mesh.GetLevel(path[k]) * mesh.StepHeight + lift;
                var here = cellUp * surfaceR;
                int o = segs * 6;
                _pathVertScratch[o + 0] = prev.X;
                _pathVertScratch[o + 1] = prev.Y;
                _pathVertScratch[o + 2] = prev.Z;
                _pathVertScratch[o + 3] = here.X;
                _pathVertScratch[o + 4] = here.Y;
                _pathVertScratch[o + 5] = here.Z;
                segs++;
                prev = here;
            }
            if (segs >= MaxPathSegments) break;
        }

        if (segs == 0) return;

        // Uniform: same layout as outline.wgsl — mvp(64) + color(16).
        // 80 bytes total; padded to a 4-float color vec4. Magenta @ 0.85 alpha.
        var uni = new float[20];
        for (int i = 0; i < 16; i++) uni[i] = planetMvp[i];
        uni[16] = 1.00f; uni[17] = 0.20f; uni[18] = 0.85f; uni[19] = 0.85f;

        _gpu.WriteBuffer(_pathUbo, uni);
        _gpu.WriteBuffer(_pathVbo, _pathVertScratch);
        _gpu.RenderAdditional(_pathPipeline, _pathVbo, _pathIbo, _pathBindGroup, segs * 2);
    }

    /// <summary>Build a per-frame batch of screen-space HP bars for the
    /// entities that should currently show one (selected ∪ hovered), project
    /// each anchor world position into NDC, emit two coloured quads per bar
    /// into the streaming vertex buffer, and dispatch a single indexed draw.
    /// Skips back-facing entities and ones whose anchor lands behind the
    /// near plane so we don't pop bars onto the screen at the horizon.</summary>
    private void DrawHealthBars(RtsState state, PlanetMesh mesh,
        Matrix4X4<float> planetMvp, Vector3 cameraPosLocal,
        float canvasW, float canvasH)
    {
        if (canvasW < 1f || canvasH < 1f || _hpVbo == 0) return;

        // Sized to look the same at any zoom level. Tuned by eye against a
        // 1080p canvas — feel free to lift to engine.yaml if it ever needs
        // to vary by platform.
        const float BarWidthPx = 48f;
        const float BarHeightPx = 6f;
        // Lift the bar above the entity's silhouette in screen space — the
        // anchor is the model's top in world; this offset just gives some
        // breathing room before the bar.
        const float BarOffsetPx = 12f;

        int barCount = 0;
        var camDir = cameraPosLocal.LengthSquared() > 1e-8f
            ? Vector3.Normalize(cameraPosLocal) : new Vector3(0, 1, 0);

        // Collect entities that should display a bar, then project each.
        // Buildings.
        foreach (var b in state.Buildings)
        {
            bool show = b.InstanceId == state.SelectedBuildingInstanceId
                     || b.InstanceId == state.HoveredBuildingInstanceId;
            if (!show) continue;
            if (barCount >= MaxHpBars) break;

            var def = _config.GetBuilding(b.TypeId); if (def == null) continue;
            var up = mesh.GetCellCenter(b.CellIndex);
            // Cull if facing away from camera — projection would still place
            // the bar on screen via the back of the planet, which looks bad.
            if (Vector3.Dot(up, camDir) < -0.05f) continue;
            float surfaceR = mesh.Radius + mesh.GetLevel(b.CellIndex) * mesh.StepHeight;
            var anchor = up * (surfaceR + def.Height);
            if (TryEmitBar(barCount, anchor, planetMvp, canvasW, canvasH,
                BarWidthPx, BarHeightPx, BarOffsetPx,
                MathF.Max(0f, b.Hp / b.MaxHp))) barCount++;
        }
        // Units.
        foreach (var u in state.Units)
        {
            bool show = state.SelectedUnitInstanceIds.Contains(u.InstanceId)
                     || u.InstanceId == state.HoveredUnitInstanceId;
            if (!show) continue;
            if (barCount >= MaxHpBars) break;

            var def = _config.GetUnit(u.TypeId); if (def == null) continue;
            if (Vector3.Dot(u.SurfaceUp, camDir) < -0.05f) continue;
            var anchor = u.SurfacePoint + u.SurfaceUp * def.Height;
            if (TryEmitBar(barCount, anchor, planetMvp, canvasW, canvasH,
                BarWidthPx, BarHeightPx, BarOffsetPx,
                MathF.Max(0f, u.Hp / u.MaxHp))) barCount++;
        }

        if (barCount == 0) return;
        _gpu.WriteBuffer(_hpVbo, _hpVertScratch);
        _gpu.RenderNoBind(_hpPipeline, _hpVbo, _hpIbo, barCount * IndicesPerBar);
    }

    /// <summary>Project an anchor into NDC, emit the bg + fill quads for a
    /// single HP bar at <paramref name="slot"/>. Returns false (and writes
    /// nothing) if the anchor is behind the camera or off-screen by enough
    /// that the bar would be invisible.</summary>
    private bool TryEmitBar(int slot, Vector3 anchorWorld, Matrix4X4<float> planetMvp,
        float canvasW, float canvasH,
        float widthPx, float heightPx, float offsetPx, float hpFrac)
    {
        var clip = Vector4.Transform(new Vector4(anchorWorld, 1f),
            ToSysMat(planetMvp));
        if (clip.W <= 1e-3f) return false;

        // NDC after perspective divide. WebGPU: x ∈ [-1,1] right, y ∈ [-1,1]
        // up, z ∈ [0,1]. Bar X is centered on anchor; bar Y sits 'offsetPx'
        // above anchor (so it stays clear of the model in screen space).
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        if (ndcX < -1.5f || ndcX > 1.5f || ndcY < -1.5f || ndcY > 1.5f) return false;

        // Pixel → NDC scale. NDC spans 2 across the canvas.
        float halfWNdc = widthPx / canvasW;       // (widthPx/2)/canvasW * 2 = widthPx/canvasW
        float heightNdc = heightPx * 2f / canvasH;
        float offsetNdc = offsetPx * 2f / canvasH;

        // Bottom of the bar sits 'offsetPx' above the anchor.
        float yBot = ndcY + offsetNdc;
        float yTop = yBot + heightNdc;
        float xLeft = ndcX - halfWNdc;
        float xRight = ndcX + halfWNdc;

        // Background — dim red, full width.
        var bg = new Vector4(0.45f, 0.05f, 0.05f, 0.85f);
        // Fill — green-to-red gradient on hp%, max 0.95 alpha so it sits
        // distinctly on top of the bg.
        var fillRgb = hpFrac > 0.5f
            ? Vector3.Lerp(new Vector3(0.95f, 0.85f, 0.10f), new Vector3(0.20f, 0.95f, 0.25f),
                (hpFrac - 0.5f) * 2f)
            : Vector3.Lerp(new Vector3(0.95f, 0.20f, 0.20f), new Vector3(0.95f, 0.85f, 0.10f),
                hpFrac * 2f);
        var fg = new Vector4(fillRgb, 0.95f);

        // Fill width follows hp% from the left edge.
        float xFillRight = xLeft + (xRight - xLeft) * hpFrac;
        // Inset the fill quad vertically so the dim red bg shows around it
        // (matches the original world-space bar's 0.85× height aesthetic).
        float yBotInset = yBot + heightNdc * 0.075f;
        float yTopInset = yTop - heightNdc * 0.075f;

        int o = slot * VertsPerBar * FloatsPerVert;
        // Background verts (CCW: bl, br, tr, tl).
        WriteVert(o + 0 * FloatsPerVert, xLeft,  yBot, bg);
        WriteVert(o + 1 * FloatsPerVert, xRight, yBot, bg);
        WriteVert(o + 2 * FloatsPerVert, xRight, yTop, bg);
        WriteVert(o + 3 * FloatsPerVert, xLeft,  yTop, bg);
        // Fill verts (CCW), inset on Y, scaled on X by hp%.
        WriteVert(o + 4 * FloatsPerVert, xLeft,     yBotInset, fg);
        WriteVert(o + 5 * FloatsPerVert, xFillRight, yBotInset, fg);
        WriteVert(o + 6 * FloatsPerVert, xFillRight, yTopInset, fg);
        WriteVert(o + 7 * FloatsPerVert, xLeft,     yTopInset, fg);
        return true;
    }

    private void WriteVert(int offset, float x, float y, Vector4 c)
    {
        _hpVertScratch[offset + 0] = x;
        _hpVertScratch[offset + 1] = y;
        _hpVertScratch[offset + 2] = c.X;
        _hpVertScratch[offset + 3] = c.Y;
        _hpVertScratch[offset + 4] = c.Z;
        _hpVertScratch[offset + 5] = c.W;
    }

    private static System.Numerics.Matrix4x4 ToSysMat(Matrix4X4<float> m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

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
        // Right-handed basis pair to local +Z. cross(u, fwd) gives the
        // direction we'd traditionally call "left" in a +X-forward, +Y-up
        // model frame; flipping it makes local +Z the model's right side.
        var localZ = -right;

        // Row-major basis: rows are local +X / +Y / +Z axes mapped into
        // world space, then the translation row. Every unit/building .obj
        // in this project is authored with +X = forward (length / barrel
        // axis), +Y = up, +Z = left side, so:
        //   local +X → world fwd  (movement direction)
        //   local +Y → world up   (surface radial)
        //   local +Z → world -right (= cross(fwd, up), the model's left)
        // Earlier this mapped local +X → right, which is why tanks rendered
        // with their barrel sticking out perpendicular to their heading —
        // they were broadsiding their destination instead of facing it.
        var basis = new Matrix4X4<float>(
            fwd.X,    fwd.Y,    fwd.Z,    0f,
            u.X,      u.Y,      u.Z,      0f,
            localZ.X, localZ.Y, localZ.Z, 0f,
            pos.X,    pos.Y,    pos.Z,    1f);

        // Inverse-transpose of an orthonormal basis is the basis itself —
        // dotting world vectors with each row yields the model-space axes.
        var sunModel = new Vector3(
            Vector3.Dot(sunWorld, fwd),
            Vector3.Dot(sunWorld, u),
            Vector3.Dot(sunWorld, localZ));

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

    /// <summary>Cyan disc on a unit's path destination cell. Slightly larger
    /// and lifted higher than the selection disc so it doesn't z-fight with
    /// it when the unit is standing on the goal.</summary>
    private void DrawMoveDestMarker(Vector3 surfacePos, Vector3 surfaceUp, float radius,
        Matrix4X4<float> planetMvpMat)
    {
        var u = Vector3.Normalize(surfaceUp);
        var fwd = ArbitraryTangent(u);
        var right = Vector3.Normalize(Vector3.Cross(u, fwd));
        var center = surfacePos + u * 0.0025f;
        var basis = new Matrix4X4<float>(
            right.X * radius, right.Y * radius, right.Z * radius, 0f,
            u.X,              u.Y,              u.Z,              0f,
            fwd.X * radius,   fwd.Y * radius,   fwd.Z * radius,   0f,
            center.X,         center.Y,         center.Z,         1f);
        var modelMvp = Matrix4X4.Multiply(basis, planetMvpMat);
        WriteMarkerUniform(modelMvp, new Vector4(0.30f, 0.85f, 1.0f, 0.65f));
        _gpu.RenderAdditional(_markerPipeline, _discMesh.Vbo, _discMesh.Ibo, _markerBindGroup, _discMesh.IndexCount);
    }

    /// <summary>Render a translucent build-preview when placement mode is
    /// active and the cursor is over a cell. The mesh comes from the
    /// queued building; the tint switches to red if the target cell is
    /// already occupied, so the player can read placement validity at a
    /// glance.</summary>
    private void DrawPlacementGhost(RtsState state, PlanetMesh mesh,
        Matrix4X4<float> planetMvpMat, int hoveredCell)
    {
        var typeId = state.PlacementBuildingId;
        if (typeId == null || hoveredCell < 0) return;
        if (!_buildingMeshes.TryGetValue(typeId, out var bm)) return;
        if (!_bindGroups.TryGetValue(typeId, out var bg)) return;

        var up = mesh.GetCellCenter(hoveredCell);
        float surfaceR = mesh.Radius + mesh.GetLevel(hoveredCell) * mesh.StepHeight;
        var pos = up * surfaceR;

        var (modelMvp, _) = BuildModelMvpAndSun(pos, up, planetMvpMat, _sunDir, heading: null);
        bool valid = state.BuildingAtCell(hoveredCell) == null;
        var ghostColor = valid
            ? new Vector4(0.40f, 1.00f, 0.55f, 0.45f)   // green = clear to build
            : new Vector4(1.00f, 0.40f, 0.40f, 0.45f);  // red   = blocked
        WriteMarkerUniform(modelMvp, ghostColor);
        _gpu.RenderAdditional(_markerPipeline, bm.Vbo, bm.Ibo, bg, bm.IndexCount);
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
        if (_hpVbo > 0) _gpu.DestroyBuffer(_hpVbo);
        if (_hpIbo > 0) _gpu.DestroyBuffer(_hpIbo);
        if (_pathVbo > 0) _gpu.DestroyBuffer(_pathVbo);
        if (_pathIbo > 0) _gpu.DestroyBuffer(_pathIbo);
    }
}
