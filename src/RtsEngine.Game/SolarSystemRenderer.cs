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
    public const int VertexFloats = 7; // pos3 + color3 + brightness1
    public const int VertexStride = 28;
    public const int UniformSize = 64; // just MVP

    private readonly IGPU _gpu;
    private readonly SolarSystemData _system;

    private int _pipeline, _linePipeline;
    private int _bodyVbo, _bodyIbo, _orbitVbo, _orbitIbo;
    private int _ubo, _bindGroup, _lineBindGroup;
    private int _bodyIndexCount, _orbitVertCount;
    private bool _ready;

    // Camera
    private float _azimuth, _elevation = 0.6f, _distance = 80f;
    private float _time;
    private bool _dragging;

    public SolarSystemRenderer(IGPU gpu, SolarSystemData system)
    {
        _gpu = gpu;
        _system = system;
    }

    public async Task Setup(string shaderCode, string outlineShaderCode)
    {
        var shader = await _gpu.CreateShaderModule(shaderCode);
        var lineShader = await _gpu.CreateShaderModule(outlineShaderCode);
        _ubo = await _gpu.CreateUniformBuffer(UniformSize);

        // Body pipeline (triangles)
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

        // Orbit line pipeline
        _linePipeline = await _gpu.CreateRenderPipelineLines(lineShader, new object[]
        {
            new {
                arrayStride = 12,
                attributes = new object[] { new { format = "float32x3", offset = 0, shaderLocation = 0 } }
            }
        });
        _lineBindGroup = await _gpu.CreateBindGroup(_linePipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
        });

        await RebuildMeshAsync(0f);
        _ready = true;
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
    public void SetDragging(bool d) => _dragging = d;
    public bool IsDragging => _dragging;

    public string? PickPlanet(float cx, float cy, float w, float h)
    {
        var mvp = FloatsToMatrix(BuildMvpFloats(w / h));
        float bestDist = float.MaxValue;
        string? best = null;

        void CheckBody(OrbitalBody body, Vector3 parentPos)
        {
            var pos = parentPos + body.GetPosition(_time);
            var clip = Vector4.Transform(new Vector4(pos, 1f), mvp);
            if (clip.W <= 0.01f) return;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * w;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * h;
            float d = (sx - cx) * (sx - cx) + (sy - cy) * (sy - cy);
            if (d < bestDist) { bestDist = d; best = body.ConfigFile; }
            foreach (var moon in body.Moons) CheckBody(moon, pos);
        }

        foreach (var p in _system.Planets) CheckBody(p, Vector3.Zero);
        return bestDist < 50 * 50 ? best : null;
    }

    // ── Mesh ────────────────────────────────────────────────────────

    private async Task RebuildMeshAsync(float time)
    {
        if (_bodyVbo > 0) _gpu.DestroyBuffer(_bodyVbo);
        if (_bodyIbo > 0) _gpu.DestroyBuffer(_bodyIbo);
        if (_orbitVbo > 0) _gpu.DestroyBuffer(_orbitVbo);
        if (_orbitIbo > 0) _gpu.DestroyBuffer(_orbitIbo);

        var bv = new List<float>();
        var bi = new List<uint>();
        var ov = new List<float>(); // orbit line verts (pos3 only)

        // Sun
        EmitSphere(bv, bi, Vector3.Zero, _system.SunRadius, _system.SunColor, 1.5f, 12);

        // Planets
        foreach (var planet in _system.Planets)
        {
            var pos = planet.GetPosition(time);
            EmitSphere(bv, bi, pos, planet.DisplayRadius, planet.Color, 1.0f, 8);
            EmitOrbitRing(ov, Vector3.Zero, planet.OrbitRadius, 64);

            foreach (var moon in planet.Moons)
            {
                var moonPos = pos + moon.GetPosition(time);
                EmitSphere(bv, bi, moonPos, moon.DisplayRadius, moon.Color, 0.8f, 6);
                EmitOrbitRing(ov, pos, moon.OrbitRadius, 32);
            }
        }

        _bodyVbo = await _gpu.CreateVertexBuffer(bv.ToArray());
        _bodyIbo = await _gpu.CreateIndexBuffer32(bi.ToArray());
        _bodyIndexCount = bi.Count;

        // Orbit lines — need a sequential IBO for the line pipeline
        var oiArr = new ushort[ov.Count / 3];
        for (int i = 0; i < oiArr.Length; i++) oiArr[i] = (ushort)i;
        _orbitVbo = await _gpu.CreateVertexBuffer(ov.ToArray());
        _orbitIbo = await _gpu.CreateIndexBuffer(oiArr);
        _orbitVertCount = oiArr.Length;
    }

    private static void EmitSphere(List<float> v, List<uint> idx,
        Vector3 center, float radius, Vector3 color, float brightness, int segments)
    {
        // Simple lat-long sphere
        uint baseIdx = (uint)(v.Count / VertexFloats);

        // Top vertex
        Emit(v, center + new Vector3(0, radius, 0), color, brightness);
        int rings = segments / 2;
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            float y = MathF.Cos(phi) * radius;
            float ringR = MathF.Sin(phi) * radius;
            for (int s = 0; s < segments; s++)
            {
                float theta = 2f * MathF.PI * s / segments;
                var pos = center + new Vector3(MathF.Cos(theta) * ringR, y, MathF.Sin(theta) * ringR);
                Emit(v, pos, color, brightness);
            }
        }
        // Bottom vertex
        Emit(v, center + new Vector3(0, -radius, 0), color, brightness);

        // Triangles: top cap
        for (int s = 0; s < segments; s++)
        {
            idx.Add(baseIdx);
            idx.Add(baseIdx + 1 + (uint)s);
            idx.Add(baseIdx + 1 + (uint)((s + 1) % segments));
        }
        // Middle strips
        for (int r = 0; r < rings - 2; r++)
        {
            uint row0 = baseIdx + 1 + (uint)(r * segments);
            uint row1 = baseIdx + 1 + (uint)((r + 1) * segments);
            for (int s = 0; s < segments; s++)
            {
                uint s0 = (uint)s, s1 = (uint)((s + 1) % segments);
                idx.Add(row0 + s0); idx.Add(row1 + s0); idx.Add(row1 + s1);
                idx.Add(row0 + s0); idx.Add(row1 + s1); idx.Add(row0 + s1);
            }
        }
        // Bottom cap
        uint bottom = baseIdx + 1 + (uint)((rings - 1) * segments);
        uint lastRow = baseIdx + 1 + (uint)((rings - 2) * segments);
        for (int s = 0; s < segments; s++)
        {
            idx.Add(bottom);
            idx.Add(lastRow + (uint)((s + 1) % segments));
            idx.Add(lastRow + (uint)s);
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

    private static void Emit(List<float> v, Vector3 pos, Vector3 color, float brightness)
    {
        v.Add(pos.X); v.Add(pos.Y); v.Add(pos.Z);
        v.Add(color.X); v.Add(color.Y); v.Add(color.Z);
        v.Add(brightness);
    }

    // ── Draw ────────────────────────────────────────────────────────

    public void Draw(float[] mvpRawFloats)
    {
        if (!_ready) return;
        _gpu.WriteBuffer(_ubo, mvpRawFloats);
        _gpu.Render(_pipeline, _bodyVbo, _bodyIbo, _bindGroup, _bodyIndexCount);
        if (_orbitVertCount > 0)
            _gpu.RenderAdditional(_linePipeline, _orbitVbo, _orbitIbo, _lineBindGroup, _orbitVertCount);
    }

    private float _lastRebuildTime = -999f;
    private bool _rebuilding;

    public async Task UpdatePositionsIfNeeded()
    {
        if (_rebuilding) return;
        if (MathF.Abs(_time - _lastRebuildTime) < 0.5f) return; // rebuild at most 2x/sec
        _rebuilding = true;
        _lastRebuildTime = _time;
        await RebuildMeshAsync(_time);
        _rebuilding = false;
    }

    public float[] BuildMvpFloats(float aspect)
    {
        float cx = _distance * MathF.Cos(_elevation) * MathF.Cos(_azimuth);
        float cy = _distance * MathF.Sin(_elevation);
        float cz = _distance * MathF.Cos(_elevation) * MathF.Sin(_azimuth);
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(cx, cy, cz),
            new Vector3D<float>(0, 0, 0),
            new Vector3D<float>(0, 1, 0));
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(
            Scalar.DegreesToRadians(50.0f), aspect, 1f, 500.0f);
        return MatrixHelper.ToRawFloats(Matrix4X4.Multiply(view, proj));
    }

    private static Matrix4x4 FloatsToMatrix(float[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    public void Dispose()
    {
        if (_bodyVbo > 0) _gpu.DestroyBuffer(_bodyVbo);
        if (_bodyIbo > 0) _gpu.DestroyBuffer(_bodyIbo);
        if (_orbitVbo > 0) _gpu.DestroyBuffer(_orbitVbo);
        if (_orbitIbo > 0) _gpu.DestroyBuffer(_orbitIbo);
        _gpu.DestroyBuffer(_ubo);
    }
}
