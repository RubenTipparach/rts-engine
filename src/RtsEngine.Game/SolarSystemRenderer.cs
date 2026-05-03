using System.Numerics;
using RtsEngine.Core;
using Silk.NET.Maths;

namespace RtsEngine.Game;

/// <summary>
/// Renders the solar system view: sun at center, orbit rings, planet dots.
/// Click a planet to enter planet edit mode for that world.
/// </summary>
public sealed class SolarSystemRenderer : IRenderer, IDisposable
{
    public const int VertexFloats = 10; // pos3 + normal3 + color3 + brightness1
    public const int VertexStride = 40;
    public const int BodyUniformSize = 96;  // mvp(64) + sunDir(16) + viewDir(16)
    public const int RingUniformSize = 80;  // mvp(64) + color(16, w=alpha)

    private const float FovYDegrees = 50f;
    private static readonly float FocalY = 1f / MathF.Tan(FovYDegrees * MathF.PI / 360f);

    private readonly IGPU _gpu;
    private readonly SolarSystemData _system;

    private int _pipeline, _linePipeline, _sunPipeline;
    private int _sunVbo, _sunIbo;
    private int _sunUbo, _sunBindGroup;
    private int _sunIndexCount;

    // Starfield/nebula procedural background: a fullscreen triangle drawn at
    // the far plane before everything else, so it shows through anywhere the
    // scene is empty.
    private int _starPipeline, _starVbo, _starIbo, _starUbo, _starBindGroup;

    private bool _ready;

    /// <summary>
    /// One per planet/moon. Mesh is baked at origin; the per-body UBO holds
    /// translate(currentWorldPos) * mvp, updated each frame so picking and
    /// rendering both see live orbital positions.
    /// </summary>
    private struct BodyRender
    {
        public OrbitalBody Body;
        public OrbitalBody? Parent;       // null for top-level planets
        public int Vbo, Ibo, IndexCount;
        public int Ubo, BindGroup;
        public int RingVbo, RingIbo, RingVertCount;
        public int RingUbo, RingBindGroup;
    }
    private readonly List<BodyRender> _bodies = new();

    // Camera. _distance is initialized from EngineConfig at construction so
    // changing solarSystemView.defaultDistance in engine.yaml actually moves
    // where the orbit camera starts.
    private float _azimuth, _elevation = 0.6f, _distance;
    private float _time;
    private bool _dragging;

    public SolarSystemRenderer(IGPU gpu, SolarSystemData system, EngineConfig config)
    {
        _gpu = gpu;
        _system = system;
        _distance = config.SolarSystemView.DefaultDistance;
    }

    public async Task Setup(string shaderCode, string outlineShaderCode, string sunShaderCode,
        string? starfieldShaderCode = null)
    {
        var shader = await _gpu.CreateShaderModule(shaderCode);
        var lineShader = await _gpu.CreateShaderModule(outlineShaderCode);
        var sunShader = await _gpu.CreateShaderModule(sunShaderCode);
        _sunUbo = await _gpu.CreateUniformBuffer(80); // mvp(64) + params(16)

        // Body pipeline (triangles) — bind groups created per-body in BuildBody.
        _pipeline = await _gpu.CreateRenderPipeline(shader, new object[]
        {
            new {
                arrayStride = VertexStride,
                attributes = new object[]
                {
                    new { format = "float32x3", offset = 0,  shaderLocation = 0 }, // pos
                    new { format = "float32x3", offset = 12, shaderLocation = 1 }, // normal
                    new { format = "float32x3", offset = 24, shaderLocation = 2 }, // color
                    new { format = "float32",   offset = 36, shaderLocation = 3 }, // brightness
                }
            }
        });

        // Orbit line pipeline — bind groups created per-body.
        _linePipeline = await _gpu.CreateRenderPipelineLines(lineShader, new object[]
        {
            new {
                arrayStride = 12,
                attributes = new object[] { new { format = "float32x3", offset = 0, shaderLocation = 0 } }
            }
        });

        // Sun pipeline (pos3 only, stride 12 — sun shader does its own coloring)
        _sunPipeline = await _gpu.CreateRenderPipeline(sunShader, new object[]
        {
            new {
                arrayStride = 12,
                attributes = new object[] { new { format = "float32x3", offset = 0, shaderLocation = 0 } }
            }
        });
        _sunBindGroup = await _gpu.CreateBindGroup(_sunPipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _sunUbo },
        });

        // Build sun mesh (pos3 only)
        var (sv, si) = BuildSunMesh(_system.SunRadius, 48);
        _sunVbo = await _gpu.CreateVertexBuffer(sv);
        _sunIbo = await _gpu.CreateIndexBuffer(si);
        _sunIndexCount = si.Length;

        // One mesh per body, baked at origin. Per-frame translation lives in the UBO.
        foreach (var planet in _system.Planets)
        {
            _bodies.Add(await BuildBody(planet, null));
            foreach (var moon in planet.Moons)
                _bodies.Add(await BuildBody(moon, planet));
        }

        // Procedural star + nebula background.
        if (starfieldShaderCode != null)
        {
            var starShader = await _gpu.CreateShaderModule(starfieldShaderCode);
            _starPipeline = await _gpu.CreateRenderPipeline(starShader, new object[]
            {
                new {
                    arrayStride = 12,
                    attributes = new object[] { new { format = "float32x3", offset = 0, shaderLocation = 0 } }
                }
            });
            _starUbo = await _gpu.CreateUniformBuffer(64); // camRight + camUp + camForward + params
            _starBindGroup = await _gpu.CreateBindGroup(_starPipeline, 0, new object[]
            {
                new { binding = 0, bufferId = _starUbo },
            });
            // Fullscreen triangle: 3 verts cover NDC [-1,1] x [-1,1] after clip.
            float[] starVerts = { -1f, -1f, 0f,   3f, -1f, 0f,   -1f, 3f, 0f };
            _starVbo = await _gpu.CreateVertexBuffer(starVerts);
            _starIbo = await _gpu.CreateIndexBuffer(new ushort[] { 0, 1, 2 });
        }

        _ready = true;
    }

    /// <summary>
    /// Render the star + nebula background. Caller passes the camera basis
    /// (already in world space) plus FOV/aspect — same numbers used to build
    /// the MVP, so the starfield's reconstructed view ray matches the scene.
    /// Clears the framebuffer; everything else this frame must use additive
    /// render calls.
    /// </summary>
    public void DrawStarfield(Vector3 forward, Vector3 right, Vector3 up,
        float fovYDegrees, float aspect)
    {
        if (!_ready || _starPipeline == 0) return;

        var uni = new float[16];
        uni[0] = right.X; uni[1] = right.Y; uni[2] = right.Z;
        uni[4] = up.X; uni[5] = up.Y; uni[6] = up.Z;
        uni[8] = forward.X; uni[9] = forward.Y; uni[10] = forward.Z;
        uni[12] = MathF.Tan(fovYDegrees * MathF.PI / 360f); // tan(fov/2)
        uni[13] = aspect;
        uni[14] = _time;
        _gpu.WriteBuffer(_starUbo, uni);
        _gpu.Render(_starPipeline, _starVbo, _starIbo, _starBindGroup, 3);
    }

    /// <summary>
    /// Camera basis (forward, right, up) for the current solar-system view.
    /// Useful for the starfield shader and other rays-from-camera tricks.
    /// </summary>
    public (Vector3 forward, Vector3 right, Vector3 up) GetCameraBasis()
    {
        var eye = ComputeCameraWorldPos();
        var fwd = Vector3.Normalize(_focusTarget - eye);
        var right = Vector3.Normalize(Vector3.Cross(fwd, new Vector3(0, 1, 0)));
        var up = Vector3.Cross(right, fwd);
        return (fwd, right, up);
    }

    public float FovYDegreesPublic => FovYDegrees;

    private async Task<BodyRender> BuildBody(OrbitalBody body, OrbitalBody? parent)
    {
        int segs = parent == null ? 40 : 24;
        float brightness = parent == null ? 1.0f : 0.8f;

        var bv = new List<float>();
        var bi = new List<uint>();
        EmitNoiseSphere(bv, bi, Vector3.Zero, body.DisplayRadius, brightness, segs, body);

        var rv = new List<float>();
        EmitOrbitRing(rv, Vector3.Zero, body.OrbitRadius, parent == null ? 64 : 32);
        var ringIdx = new ushort[rv.Count / 3];
        for (int i = 0; i < ringIdx.Length; i++) ringIdx[i] = (ushort)i;

        int vbo = await _gpu.CreateVertexBuffer(bv.ToArray());
        int ibo = await _gpu.CreateIndexBuffer32(bi.ToArray());
        int ubo = await _gpu.CreateUniformBuffer(BodyUniformSize);
        int bg = await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = ubo },
        });

        int rvbo = await _gpu.CreateVertexBuffer(rv.ToArray());
        int ribo = await _gpu.CreateIndexBuffer(ringIdx);
        int rubo = await _gpu.CreateUniformBuffer(RingUniformSize);
        int rbg = await _gpu.CreateBindGroup(_linePipeline, 0, new object[]
        {
            new { binding = 0, bufferId = rubo },
        });

        return new BodyRender
        {
            Body = body, Parent = parent,
            Vbo = vbo, Ibo = ibo, IndexCount = bi.Count,
            Ubo = ubo, BindGroup = bg,
            RingVbo = rvbo, RingIbo = ribo, RingVertCount = ringIdx.Length,
            RingUbo = rubo, RingBindGroup = rbg,
        };
    }

    public void SetTime(float t) => _time = t;
    public void Orbit(float dx, float dy)
    {
        _azimuth += dx * 0.005f;
        _elevation += dy * 0.005f;
        _elevation = Math.Clamp(_elevation, 0.1f, 1.5f);
    }
    public void Zoom(float delta)
    {
        _distance -= delta * _distance * 0.001f;
        _distance = Math.Clamp(_distance, 20f, 200f);
    }
    public float Distance { get => _distance; set => _distance = value; }
    public float Azimuth { get => _azimuth; set => _azimuth = value; }
    public float Elevation { get => _elevation; set => _elevation = Math.Clamp(value, 0.1f, 1.5f); }
    public void SetDragging(bool d) => _dragging = d;
    public bool IsDragging => _dragging;

    public (string? config, Vector3 position, float displayRadius) PickPlanet(float cx, float cy, float w, float h)
    {
        var mvp = FloatsToMatrix(BuildMvpFloats(w / h));
        float bestScore = float.MaxValue;
        string? best = null;
        Vector3 bestPos = Vector3.Zero;
        float bestDisplayR = 1f;

        void CheckBody(OrbitalBody body, Vector3 parentPos)
        {
            var pos = parentPos + body.GetPosition(_time);
            var clip = Vector4.Transform(new Vector4(pos, 1f), mvp);
            if (clip.W <= 0.01f) return;

            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;

            // Perspective-correct screen radius: at view-space depth clip.W, a sphere
            // of radius r projects to (r * focalY / clip.W) in NDC, or that times h/2
            // in pixels. Using world-axis offsets (as before) gave 0 when the offset
            // was along the view direction — making most clicks miss.
            float screenRadius = MathF.Max(body.DisplayRadius * FocalY * h * 0.5f / clip.W, 25f);

            float dx = sx - cx, dy = sy - cy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            // Score: distance relative to screen radius. <1 means inside the sphere.
            float score = dist / screenRadius;
            if (score < bestScore) { bestScore = score; best = body.ConfigFile; bestPos = pos; bestDisplayR = body.DisplayRadius; }

            foreach (var moon in body.Moons) CheckBody(moon, pos);
        }

        foreach (var p in _system.Planets) CheckBody(p, Vector3.Zero);
        return bestScore < 3f ? (best, bestPos, bestDisplayR) : (null, Vector3.Zero, 1f);
    }

    // ── Mesh ────────────────────────────────────────────────────────

    private static (float[] verts, ushort[] indices) BuildSunMesh(float radius, int segments)
    {
        var v = new List<float>();
        var idx = new List<ushort>();
        int rings = segments / 2;

        v.Add(0); v.Add(radius); v.Add(0); // top
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            float y = MathF.Cos(phi) * radius;
            float ringR = MathF.Sin(phi) * radius;
            for (int s = 0; s < segments; s++)
            {
                float th = 2f * MathF.PI * s / segments;
                v.Add(MathF.Cos(th) * ringR); v.Add(y); v.Add(MathF.Sin(th) * ringR);
            }
        }
        v.Add(0); v.Add(-radius); v.Add(0); // bottom

        for (int s = 0; s < segments; s++)
        {
            idx.Add(0);
            idx.Add((ushort)(1 + (s + 1) % segments));
            idx.Add((ushort)(1 + s));
        }
        for (int r = 0; r < rings - 2; r++)
        {
            ushort row0 = (ushort)(1 + r * segments);
            ushort row1 = (ushort)(1 + (r + 1) * segments);
            for (int s = 0; s < segments; s++)
            {
                ushort s0 = (ushort)s, s1 = (ushort)((s + 1) % segments);
                idx.Add((ushort)(row0 + s0)); idx.Add((ushort)(row1 + s1)); idx.Add((ushort)(row1 + s0));
                idx.Add((ushort)(row0 + s0)); idx.Add((ushort)(row0 + s1)); idx.Add((ushort)(row1 + s1));
            }
        }
        ushort bot = (ushort)(1 + (rings - 1) * segments);
        ushort lastR = (ushort)(1 + (rings - 2) * segments);
        for (int s = 0; s < segments; s++)
        {
            idx.Add(bot);
            idx.Add((ushort)(lastR + s));
            idx.Add((ushort)(lastR + (s + 1) % segments));
        }

        return (v.ToArray(), idx.ToArray());
    }

    private static void EmitSphere(List<float> v, List<uint> idx,
        Vector3 center, float radius, Vector3 color, float brightness, int segments)
    {
        uint baseIdx = (uint)(v.Count / VertexFloats);
        int rings = segments / 2;

        Emit(v, center + new Vector3(0, radius, 0), Vector3.UnitY, color, brightness);
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            float y = MathF.Cos(phi); float ringR = MathF.Sin(phi);
            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * MathF.PI * s / segments;
                var dir = new Vector3(MathF.Cos(theta) * ringR, y, MathF.Sin(theta) * ringR);
                Emit(v, center + dir * radius, dir, color, brightness);
            }
        }
        Emit(v, center + new Vector3(0, -radius, 0), -Vector3.UnitY, color, brightness);

        // Triangles: top cap
        for (int s = 0; s < segments; s++)
        {
            idx.Add(baseIdx);
            idx.Add(baseIdx + 1 + (uint)((s + 1) % segments));
            idx.Add(baseIdx + 1 + (uint)s);
        }
        // Middle strips
        for (int r = 0; r < rings - 2; r++)
        {
            uint row0 = baseIdx + 1 + (uint)(r * segments);
            uint row1 = baseIdx + 1 + (uint)((r + 1) * segments);
            for (int s = 0; s < segments; s++)
            {
                uint s0 = (uint)s, s1 = (uint)((s + 1) % segments);
                idx.Add(row0 + s0); idx.Add(row1 + s1); idx.Add(row1 + s0);
                idx.Add(row0 + s0); idx.Add(row0 + s1); idx.Add(row1 + s1);
            }
        }
        // Bottom cap
        uint bottom = baseIdx + 1 + (uint)((rings - 1) * segments);
        uint lastRow = baseIdx + 1 + (uint)((rings - 2) * segments);
        for (int s = 0; s < segments; s++)
        {
            idx.Add(bottom);
            idx.Add(lastRow + (uint)s);
            idx.Add(lastRow + (uint)((s + 1) % segments));
        }
    }

    private static void EmitNoiseSphere(List<float> v, List<uint> idx,
        Vector3 center, float radius, float brightness, int segments, OrbitalBody body)
    {
        uint baseIdx = (uint)(v.Count / VertexFloats);
        int rings = segments / 2;
        var th = body.NoiseThresholds;
        var cols = body.LevelColors;

        Vector3 ColorAt(Vector3 dir)
        {
            float n = Noise3D.Octaves(dir.X * body.NoiseFrequency, dir.Y * body.NoiseFrequency,
                dir.Z * body.NoiseFrequency, 3, 0.5f, body.NoiseSeed);
            float t = (n + 1f) * 0.5f;
            // Same quantization as PlanetMesh.GenerateFromNoise
            int lvl = cols.Length - 1;
            for (int k = 0; k < th.Length && k < cols.Length - 1; k++)
                if (t < th[k]) { lvl = k; break; }
            return lvl < cols.Length ? cols[lvl] : body.Color;
        }

        var topDir = Vector3.UnitY;
        Emit(v, center + topDir * radius, topDir, ColorAt(topDir), brightness);
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            float y = MathF.Cos(phi); float ringR = MathF.Sin(phi);
            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * MathF.PI * s / segments;
                var dir = new Vector3(MathF.Cos(theta) * ringR, y, MathF.Sin(theta) * ringR);
                Emit(v, center + dir * radius, dir, ColorAt(dir), brightness);
            }
        }
        Emit(v, center - Vector3.UnitY * radius, -Vector3.UnitY, ColorAt(-Vector3.UnitY), brightness);

        for (int s = 0; s < segments; s++)
        {
            idx.Add(baseIdx);
            idx.Add(baseIdx + 1 + (uint)((s + 1) % segments));
            idx.Add(baseIdx + 1 + (uint)s);
        }
        for (int r = 0; r < rings - 2; r++)
        {
            uint row0 = baseIdx + 1 + (uint)(r * segments);
            uint row1 = baseIdx + 1 + (uint)((r + 1) * segments);
            for (int s = 0; s < segments; s++)
            {
                uint s0 = (uint)s, s1 = (uint)((s + 1) % segments);
                idx.Add(row0 + s0); idx.Add(row1 + s1); idx.Add(row1 + s0);
                idx.Add(row0 + s0); idx.Add(row0 + s1); idx.Add(row1 + s1);
            }
        }
        uint bottom = baseIdx + 1 + (uint)((rings - 1) * segments);
        uint lastRow = baseIdx + 1 + (uint)((rings - 2) * segments);
        for (int s = 0; s < segments; s++)
        {
            idx.Add(bottom);
            idx.Add(lastRow + (uint)s);
            idx.Add(lastRow + (uint)((s + 1) % segments));
        }
    }

    private static void EmitOrbitRing(List<float> v, Vector3 center, float radius, int segments)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = 2f * MathF.PI * i / segments;
            float a1 = 2f * MathF.PI * ((i + 1) % segments) / segments;
            var p0 = center + new Vector3(MathF.Cos(a0) * radius, 0, MathF.Sin(a0) * radius);
            var p1 = center + new Vector3(MathF.Cos(a1) * radius, 0, MathF.Sin(a1) * radius);
            v.Add(p0.X); v.Add(p0.Y); v.Add(p0.Z);
            v.Add(p1.X); v.Add(p1.Y); v.Add(p1.Z);
        }
    }

    private static void Emit(List<float> v, Vector3 pos, Vector3 normal, Vector3 color, float brightness)
    {
        v.Add(pos.X); v.Add(pos.Y); v.Add(pos.Z);
        v.Add(normal.X); v.Add(normal.Y); v.Add(normal.Z);
        v.Add(color.X); v.Add(color.Y); v.Add(color.Z);
        v.Add(brightness);
    }

    // ── Draw ────────────────────────────────────────────────────────

    public void Draw(float[] mvpRawFloats)
    {
        if (!_ready) return;

        // Camera in solar-system world coords — derived from view params so
        // shaders can compute view-direction and fresnel rim glow per body.
        var cameraWorldPos = ComputeCameraWorldPos();

        // Sun is additive: GameEngine renders the starfield first each frame
        // and clears the framebuffer there. Everything inside Draw stacks on
        // top of that.
        var sunUni = new float[20]; // mvp(16) + params(4)
        Array.Copy(mvpRawFloats, 0, sunUni, 0, 16);
        sunUni[16] = _time;
        _gpu.WriteBuffer(_sunUbo, sunUni);
        _gpu.RenderAdditional(_sunPipeline, _sunVbo, _sunIbo, _sunBindGroup, _sunIndexCount);

        // Bodies + orbit rings: each body's mesh is at the origin, so we
        // upload a translated MVP per body, per frame. This gives live orbit
        // animation without rebuilding any geometry.
        var mvpMat = RawToSilkMat(mvpRawFloats);

        foreach (var br in _bodies)
        {
            var parentPos = br.Parent != null ? br.Parent.GetPosition(_time) : Vector3.Zero;
            var bodyPos = parentPos + br.Body.GetPosition(_time);

            // Body mesh of the focused planet is hidden during transitions —
            // the detailed planet renderer is standing in for it at the same
            // world position, and overlaying both produces z-fight + visual
            // doubling. The orbit RING isn't replaced by anything though, so
            // it should always draw alongside the others; otherwise selecting
            // a planet makes its orbit path snap out of existence while every
            // other ring keeps fading naturally.
            if (br.Body.ConfigFile != _hiddenPlanetConfig)
            {
                WriteBodyUniform(br.Ubo, bodyPos, mvpMat, cameraWorldPos);
                _gpu.RenderAdditional(_pipeline, br.Vbo, br.Ibo, br.BindGroup, br.IndexCount);
            }

            // Orbit ring is centered on the body's parent (origin for top-level planets).
            WriteRingUniform(br.RingUbo, parentPos, mvpMat, alpha: 1.0f);
            _gpu.RenderAdditional(_linePipeline, br.RingVbo, br.RingIbo, br.RingBindGroup, br.RingVertCount);
        }
    }

    private Vector3 ComputeCameraWorldPos()
    {
        return _focusTarget + new Vector3(
            _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth),
            _distance * MathF.Sin(_elevation),
            _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth));
    }

    /// <summary>
    /// Pack the body's MVP (translate(translation) * vp) + sunDir + viewDir
    /// into its UBO. <paramref name="translation"/> places the body's mesh in
    /// clip space, while <paramref name="lightFromWorldPos"/> (defaults to
    /// translation) is the body's true world position used to compute
    /// sunDir = -P.normalized. In planet-edit mode the two differ because the
    /// world is shifted so the focused planet sits at the origin, but lighting
    /// must still use the unshifted positions.
    ///
    /// viewDir = (cameraWorldPos - bodyWorldPos).normalized is treated as
    /// constant across the body's surface — fine since bodies are tiny relative
    /// to camera distance — and used by the shader's fresnel rim glow.
    /// </summary>
    private void WriteBodyUniform(int ubo, Vector3 translation, Matrix4X4<float> vpMat,
        Vector3 cameraWorldPos, Vector3? lightFromWorldPos = null, float dither = 0f)
    {
        var modelMvp = Matrix4X4.Multiply(
            Matrix4X4.CreateTranslation(new Vector3D<float>(translation.X, translation.Y, translation.Z)),
            vpMat);
        var raw = MatrixHelper.ToRawFloats(modelMvp);
        var uni = new float[24];
        Array.Copy(raw, 0, uni, 0, 16);

        var lightPos = lightFromWorldPos ?? translation;
        if (lightPos.LengthSquared() > 1e-6f)
        {
            var sunDir = -Vector3.Normalize(lightPos);
            uni[16] = sunDir.X; uni[17] = sunDir.Y; uni[18] = sunDir.Z;
        }
        else
        {
            uni[16] = 0f; uni[17] = 1f; uni[18] = 0f; // fallback when at origin
        }

        var toCam = cameraWorldPos - lightPos;
        if (toCam.LengthSquared() > 1e-6f)
        {
            var viewDir = Vector3.Normalize(toCam);
            uni[20] = viewDir.X; uni[21] = viewDir.Y; uni[22] = viewDir.Z;
        }
        else
        {
            uni[20] = 0f; uni[21] = 0f; uni[22] = 1f;
        }
        uni[23] = dither;
        _gpu.WriteBuffer(ubo, uni);
    }

    /// <summary>Pack a ring's MVP + color (rgba) into its UBO.</summary>
    private void WriteRingUniform(int ubo, Vector3 ringCenter, Matrix4X4<float> vpMat, float alpha)
    {
        var modelMvp = ringCenter == Vector3.Zero
            ? vpMat
            : Matrix4X4.Multiply(
                Matrix4X4.CreateTranslation(new Vector3D<float>(ringCenter.X, ringCenter.Y, ringCenter.Z)),
                vpMat);
        var raw = MatrixHelper.ToRawFloats(modelMvp);
        var uni = new float[20];
        Array.Copy(raw, 0, uni, 0, 16);
        // Yellow orbit-ring color, alpha controls fade.
        uni[16] = 1.0f; uni[17] = 0.9f; uni[18] = 0.2f; uni[19] = alpha;
        _gpu.WriteBuffer(ubo, uni);
    }

    private static Matrix4X4<float> RawToSilkMat(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /// <summary>
    /// Draw the sun and all bodies *except* <paramref name="selfConfig"/>, with
    /// the world translated so that <paramref name="selfPos"/> sits at the origin.
    /// Used by the planet edit view to render the rest of the solar system as a
    /// backdrop around the focused planet (which is rendered at the origin).
    /// <paramref name="cameraWorldPos"/> is in solar-system coords; the renderer
    /// uses it (un-shifted) for fresnel rim glow on each body. <paramref name="ringAlpha"/>
    /// fades the orbit rings in as the camera zooms out — 0 hides them entirely.
    /// </summary>
    public void DrawBackdrop(float[] planetMvpRawFloats, string? selfConfig, Vector3 selfPos,
        Vector3 cameraWorldPos, float ringAlpha = 0f)
    {
        if (!_ready) return;

        var planetMvpMat = RawToSilkMat(planetMvpRawFloats);
        var worldShift = Matrix4X4.CreateTranslation(
            new Vector3D<float>(-selfPos.X, -selfPos.Y, -selfPos.Z));
        var shiftedMvp = Matrix4X4.Multiply(worldShift, planetMvpMat);

        // Sun at solar-system origin: shifted by -selfPos, then projected.
        var sunUni = new float[20];
        Array.Copy(MatrixHelper.ToRawFloats(shiftedMvp), 0, sunUni, 0, 16);
        sunUni[16] = _time;
        _gpu.WriteBuffer(_sunUbo, sunUni);
        _gpu.RenderAdditional(_sunPipeline, _sunVbo, _sunIbo, _sunBindGroup, _sunIndexCount);

        // Distance from the camera to the focused planet (which sits at the
        // origin in the shifted frame). Used to decide if a backdrop body has
        // crossed in front of the planet from the camera's POV.
        float camToPlanet = (cameraWorldPos - selfPos).Length();

        // Other bodies. Skip only the focused planet itself (the detailed mesh
        // is at the origin standing in for it) — the focused planet's moons
        // stay visible so the player can see them swing past.
        foreach (var br in _bodies)
        {
            if (br.Body.ConfigFile == selfConfig) continue;

            var parentPos = br.Parent != null ? br.Parent.GetPosition(_time) : Vector3.Zero;
            var bodyPos = parentPos + br.Body.GetPosition(_time);

            // Dither bodies that cross between camera and the focused planet
            // so the planet stays readable (e.g. the moon swinging past Earth
            // shouldn't fully occlude it). Fade in over a 1-unit margin.
            float camToBody = (cameraWorldPos - bodyPos).Length();
            float delta = camToPlanet - camToBody; // > 0 when body is closer
            float dither = Math.Clamp(delta * 0.7f, 0f, 0.85f);

            // Position passed to the shader is shifted (planet at origin) but
            // sunDir + viewDir must use the true solar-system world positions so
            // lighting matches what the body would look like in solar system view.
            WriteBodyUniform(br.Ubo, bodyPos - selfPos, planetMvpMat, cameraWorldPos,
                lightFromWorldPos: bodyPos, dither: dither);
            _gpu.RenderAdditional(_pipeline, br.Vbo, br.Ibo, br.BindGroup, br.IndexCount);
        }

        if (ringAlpha > 0.001f)
        {
            // Orbit rings (top-level planets only — keeps the planet view tidy
            // while still revealing orbital structure as the player zooms out).
            foreach (var br in _bodies)
            {
                if (br.Parent != null) continue;
                if (br.Body.ConfigFile == selfConfig) continue;
                WriteRingUniform(br.RingUbo, -selfPos, planetMvpMat, ringAlpha);
                _gpu.RenderAdditional(_linePipeline, br.RingVbo, br.RingIbo, br.RingBindGroup, br.RingVertCount);
            }
            // Show the focused planet's own ring too, so the player sees its
            // orbit traced through itself.
            foreach (var br in _bodies)
            {
                if (br.Parent != null) continue;
                if (br.Body.ConfigFile != selfConfig) continue;
                WriteRingUniform(br.RingUbo, -selfPos, planetMvpMat, ringAlpha);
                _gpu.RenderAdditional(_linePipeline, br.RingVbo, br.RingIbo, br.RingBindGroup, br.RingVertCount);
            }
        }
    }

    /// <summary>Current world position of the body identified by config file.</summary>
    public Vector3 GetBodyWorldPosition(string? configFile)
    {
        if (configFile == null) return Vector3.Zero;
        foreach (var p in _system.Planets)
        {
            if (p.ConfigFile == configFile) return p.GetPosition(_time);
            foreach (var m in p.Moons)
                if (m.ConfigFile == configFile)
                    return p.GetPosition(_time) + m.GetPosition(_time);
        }
        return Vector3.Zero;
    }

    /// <summary>
    /// Pick a body visible in the planet-edit backdrop. Same logic as
    /// <see cref="PickPlanet"/> but the body positions are projected through
    /// the planet view's MVP after subtracting <paramref name="selfPos"/> (the
    /// focused planet sits at origin in that frame). Bodies that DrawBackdrop
    /// would skip (the focused planet itself, and its moons) are skipped here
    /// too so the player can't pick something they can't see.
    /// </summary>
    public (string? config, Vector3 worldPos) PickBackdrop(
        float cx, float cy, float w, float h,
        float[] planetMvpRawFloats, Vector3 selfPos, string? selfConfig,
        float focalY)
    {
        var mvp = FloatsToMatrix(planetMvpRawFloats);
        float bestScore = float.MaxValue;
        string? best = null;
        Vector3 bestWorld = Vector3.Zero;

        void Check(OrbitalBody body, OrbitalBody? parent)
        {
            if (body.ConfigFile == selfConfig) return;

            var parentPos = parent != null ? parent.GetPosition(_time) : Vector3.Zero;
            var worldPos = parentPos + body.GetPosition(_time);
            var shifted = worldPos - selfPos; // backdrop frame: focused planet at origin

            var clip = Vector4.Transform(new Vector4(shifted, 1f), mvp);
            if (clip.W <= 0.01f) return;

            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;
            float screenRadius = MathF.Max(body.DisplayRadius * focalY * h * 0.5f / clip.W, 25f);

            float dx = sx - cx, dy = sy - cy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float score = dist / screenRadius;
            if (score < bestScore)
            {
                bestScore = score;
                best = body.ConfigFile;
                bestWorld = worldPos;
            }
        }

        foreach (var p in _system.Planets)
        {
            Check(p, null);
            foreach (var m in p.Moons) Check(m, p);
        }
        return bestScore < 3f ? (best, bestWorld) : (null, Vector3.Zero);
    }

    private string? _hiddenPlanetConfig;
    private Vector3 _focusTarget = Vector3.Zero;

    /// <summary>Hide the noise sphere for this planet (during transition, the real PlanetRenderer replaces it).</summary>
    public void HidePlanet(string? configFile) => _hiddenPlanetConfig = configFile;
    public void SetFocusTarget(Vector3 target) => _focusTarget = target;

    public float[] BuildMvpFloats(float aspect)
    {
        float cx = _focusTarget.X + _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _focusTarget.Y + _distance * MathF.Sin(_elevation);
        float cz = _focusTarget.Z + _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(cx, cy, cz),
            new Vector3D<float>(_focusTarget.X, _focusTarget.Y, _focusTarget.Z),
            new Vector3D<float>(0, 1, 0));
        // Far plane matches the planet view's projection so log-depth in WGSL
        // (FAR=10000) produces a consistent depth curve across views.
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(FovYDegrees), aspect, 0.5f, 10000.0f);
        return MatrixHelper.ToRawFloats(Matrix4X4.Multiply(view, proj));
    }

    private static Matrix4x4 FloatsToMatrix(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    public void Dispose()
    {
        foreach (var br in _bodies)
        {
            _gpu.DestroyBuffer(br.Vbo);
            _gpu.DestroyBuffer(br.Ibo);
            _gpu.DestroyBuffer(br.Ubo);
            _gpu.DestroyBuffer(br.RingVbo);
            _gpu.DestroyBuffer(br.RingIbo);
            _gpu.DestroyBuffer(br.RingUbo);
        }
        _bodies.Clear();
        if (_sunVbo > 0) _gpu.DestroyBuffer(_sunVbo);
        if (_sunIbo > 0) _gpu.DestroyBuffer(_sunIbo);
        if (_sunUbo > 0) _gpu.DestroyBuffer(_sunUbo);
    }
}
