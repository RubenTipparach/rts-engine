using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Minimal screen-space UI rendered via WebGPU. Buttons are colored quads
/// with text labels baked into vertex color. Hit-testing is done in screen
/// space before scene picking, consuming the click if it hits a button.
/// </summary>
public sealed class EngineUI
{
    private readonly List<UIButton> _buttons = new();
    private readonly IGPU _gpu;

    private int _pipeline, _vbo, _ibo, _ubo, _bindGroup;
    private int _vertCount, _idxCount;
    private bool _ready, _dirty = true;
    private float _canvasW, _canvasH;
    private readonly float[] _uni = new float[16]; // ortho projection mat4

    public EngineUI(IGPU gpu) => _gpu = gpu;

    public UIButton AddButton(string id, string label, float x, float y, float w, float h)
    {
        var btn = new UIButton { Id = id, Label = label, X = x, Y = y, W = w, H = h };
        _buttons.Add(btn);
        _dirty = true;
        return btn;
    }

    public void RemoveButton(string id)
    {
        _buttons.RemoveAll(b => b.Id == id);
        _dirty = true;
    }

    public void SetButtonVisible(string id, bool visible)
    {
        var btn = _buttons.Find(b => b.Id == id);
        if (btn != null && btn.Visible != visible) { btn.Visible = visible; _dirty = true; }
    }

    /// <summary>
    /// Test if a click hits any visible button. Returns the button ID, or null.
    /// Coordinates are in device pixels (same as PointerClick).
    /// </summary>
    public string? HitTest(float cx, float cy)
    {
        foreach (var b in _buttons)
        {
            if (!b.Visible) continue;
            if (cx >= b.X && cx <= b.X + b.W && cy >= b.Y && cy <= b.Y + b.H)
                return b.Id;
        }
        return null;
    }

    public async Task Setup(string shaderCode)
    {
        var shader = await _gpu.CreateShaderModule(shaderCode);
        _ubo = await _gpu.CreateUniformBuffer(64);

        _pipeline = await _gpu.CreateRenderPipelineAlphaBlend(shader, new object[]
        {
            new {
                arrayStride = 24, // pos2 + color4 = 6 floats
                attributes = new object[]
                {
                    new { format = "float32x2", offset = 0,  shaderLocation = 0 },
                    new { format = "float32x4", offset = 8,  shaderLocation = 1 },
                }
            }
        });
        _bindGroup = await _gpu.CreateBindGroup(_pipeline, 0, new object[]
        {
            new { binding = 0, bufferId = _ubo },
        });
        _ready = true;
    }

    public void SetCanvasSize(float w, float h)
    {
        if (MathF.Abs(w - _canvasW) > 1 || MathF.Abs(h - _canvasH) > 1)
        {
            _canvasW = w; _canvasH = h; _dirty = true;
        }
    }

    public async Task SyncBuffers()
    {
        if (!_dirty || !_ready) return;
        _dirty = false;

        if (_vbo > 0) { _gpu.DestroyBuffer(_vbo); _vbo = 0; }
        if (_ibo > 0) { _gpu.DestroyBuffer(_ibo); _ibo = 0; }
        _idxCount = 0;

        var verts = new List<float>();
        var idx = new List<ushort>();

        foreach (var b in _buttons)
        {
            if (!b.Visible) continue;
            ushort bi = (ushort)(verts.Count / 6);

            // Normalized device coords: x=[0,canvasW] → [-1,1], y=[0,canvasH] → [1,-1]
            float x0 = 2f * b.X / _canvasW - 1f;
            float x1 = 2f * (b.X + b.W) / _canvasW - 1f;
            float y0 = 1f - 2f * b.Y / _canvasH;
            float y1 = 1f - 2f * (b.Y + b.H) / _canvasH;

            // Background quad
            EmitVert(verts, x0, y0, b.BgColor);
            EmitVert(verts, x1, y0, b.BgColor);
            EmitVert(verts, x1, y1, b.BgColor);
            EmitVert(verts, x0, y1, b.BgColor);
            idx.Add(bi); idx.Add((ushort)(bi + 1)); idx.Add((ushort)(bi + 2));
            idx.Add(bi); idx.Add((ushort)(bi + 2)); idx.Add((ushort)(bi + 3));

            // Simple arrow/icon: small triangle pointing left for "back" buttons
            if (b.HasArrow)
            {
                float ax = x0 + (x1 - x0) * 0.15f;
                float amx = x0 + (x1 - x0) * 0.35f;
                float ay = (y0 + y1) * 0.5f;
                float ady = (y0 - y1) * 0.25f;

                ushort ai = (ushort)(verts.Count / 6);
                EmitVert(verts, ax, ay, b.FgColor);
                EmitVert(verts, amx, ay + ady, b.FgColor);
                EmitVert(verts, amx, ay - ady, b.FgColor);
                idx.Add(ai); idx.Add((ushort)(ai + 1)); idx.Add((ushort)(ai + 2));
            }
        }

        if (verts.Count == 0 || idx.Count == 0)
        {
            _vertCount = 0; _idxCount = 0;
            return;
        }

        _vbo = await _gpu.CreateVertexBuffer(verts.ToArray());
        // Ensure at least 4 bytes for the index buffer (WebGPU requires size > 0)
        var idxArr = idx.ToArray();
        if (idxArr.Length == 0) { _idxCount = 0; return; }
        _ibo = await _gpu.CreateIndexBuffer(idxArr);
        _vertCount = verts.Count / 6;
        _idxCount = idx.Count;
    }

    public void Draw()
    {
        if (!_ready || _idxCount == 0) return;
        // Identity ortho (NDC passthrough)
        for (int i = 0; i < 16; i++) _uni[i] = 0;
        _uni[0] = 1; _uni[5] = 1; _uni[10] = 1; _uni[15] = 1;
        _gpu.WriteBuffer(_ubo, _uni);
        _gpu.RenderAdditional(_pipeline, _vbo, _ibo, _bindGroup, _idxCount);
    }

    private static void EmitVert(List<float> v, float x, float y, Vector4 color)
    {
        v.Add(x); v.Add(y);
        v.Add(color.X); v.Add(color.Y); v.Add(color.Z); v.Add(color.W);
    }
}

public sealed class UIButton
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; } = 120;
    public float H { get; set; } = 36;
    public bool Visible { get; set; } = true;
    public bool HasArrow { get; set; }
    public Vector4 BgColor { get; set; } = new(0.1f, 0.2f, 0.3f, 0.85f);
    public Vector4 FgColor { get; set; } = new(0f, 1f, 1f, 1f);
}
