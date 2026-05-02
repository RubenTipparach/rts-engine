using System.Numerics;
using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// In-engine GPU-rendered UI buttons. Used as the desktop equivalent of the
/// browser's HTML overlay buttons — DesktopAppBackend forwards CreateUIButton /
/// ShowUIButton / hit-testing into here.
///
/// Buttons are colored quads positioned by the same JSON-CSS strings the HTML
/// overlay uses (top/left/bottom/right + width/height + background + color).
/// Labels are drawn by an injected <see cref="ITextRenderer"/> — desktop binds
/// FontStash here so we get crisp TTF text. The platform-agnostic side stays
/// in this file; the GL backend lives in the Desktop project.
/// </summary>
public sealed class EngineUI
{
    private readonly List<UIButton> _buttons = new();
    private readonly IGPU _gpu;

    private int _pipeline;
    private int _vbo, _ibo;
    private int _idxCount;
    private bool _ready, _dirty = true;
    private float _canvasW, _canvasH;

    /// <summary>Font size in pixels for button labels. Tuned to fit a 36px
    /// button height with breathing room.</summary>
    public float LabelSize { get; set; } = 14f;

    /// <summary>Pluggable text renderer (FontStash on desktop). When null,
    /// labels don't render — backgrounds still do.</summary>
    public ITextRenderer? Text { get; set; }

    public EngineUI(IGPU gpu) => _gpu = gpu;

    /// <summary>
    /// Set up the GPU resources for background quads. Labels are rendered by
    /// the injected <see cref="Text"/> renderer.
    /// </summary>
    public async Task Setup(string uiShaderCode)
    {
        var shader = await _gpu.CreateShaderModule(uiShaderCode);

        // Vertex layout: pos(vec2) + color(vec4), stride 24. Same shape as
        // ui.wgsl (the colour-only pipeline).
        _pipeline = await _gpu.CreateRenderPipelineUI(shader, new object[]
        {
            new {
                arrayStride = 24,
                attributes = new object[]
                {
                    new { format = "float32x2", offset = 0, shaderLocation = 0 },
                    new { format = "float32x4", offset = 8, shaderLocation = 1 },
                }
            }
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

    /// <summary>
    /// Add or update a button. cssJson is the same JSON-CSS string the WASM
    /// HTML overlay parses; we extract layout + colors from it.
    /// </summary>
    public void AddOrUpdate(string id, string label, string cssJson)
    {
        var rect = ParseCss(cssJson);
        var existing = _buttons.Find(b => b.Id == id);
        if (existing == null)
        {
            existing = new UIButton { Id = id };
            _buttons.Add(existing);
        }
        existing.Label = label ?? "";
        existing.Css = rect;
        if (rect.HasExplicitDisplay) existing.Visible = rect.DisplayBlock;
        _dirty = true;
    }

    public void SetVisible(string id, bool visible)
    {
        var b = _buttons.Find(x => x.Id == id);
        if (b != null && b.Visible != visible) { b.Visible = visible; _dirty = true; }
    }

    public void Remove(string id)
    {
        var idx = _buttons.FindIndex(b => b.Id == id);
        if (idx >= 0) { _buttons.RemoveAt(idx); _dirty = true; }
    }

    /// <summary>
    /// Hit test in canvas pixel coordinates. Returns the topmost visible
    /// button id or null. Buttons with pointerEvents:none (e.g. zoom_indicator)
    /// are skipped so they don't eat clicks meant for the world below.
    /// </summary>
    public string? HitTest(float cx, float cy)
    {
        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            var b = _buttons[i];
            if (!b.Visible || !b.Css.PointerEvents) continue;
            var (x, y, w, h) = ResolvePixelRect(b.Css);
            if (cx >= x && cx <= x + w && cy >= y && cy <= y + h) return b.Id;
        }
        return null;
    }

    /// <summary>Rebuild the background-quad mesh if any button changed
    /// since last frame.</summary>
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
            var (x, y, w, h) = ResolvePixelRect(b.Css);
            EmitBgQuadPx(verts, idx, x, y, w, h, b.Css.BgColor);
        }

        if (verts.Count == 0 || idx.Count == 0) { _idxCount = 0; return; }

        _vbo = await _gpu.CreateVertexBuffer(verts.ToArray());
        _ibo = await _gpu.CreateIndexBuffer(idx.ToArray());
        _idxCount = idx.Count;
    }

    /// <summary>Draw backgrounds, then ask the text renderer for each label.</summary>
    public void Draw()
    {
        if (!_ready) return;

        if (_idxCount > 0 && _vbo > 0 && _ibo > 0)
            _gpu.RenderNoBind(_pipeline, _vbo, _ibo, _idxCount);

        if (Text == null) return;
        Text.SetCanvasSize(_canvasW, _canvasH);

        foreach (var b in _buttons)
        {
            if (!b.Visible) continue;
            var lines = SplitLines(b.Label);
            if (lines.Count == 0) continue;

            var (x, y, w, h) = ResolvePixelRect(b.Css);
            float lineH = LabelSize + 2f;
            float blockH = lines.Count * lineH;
            float startY = y + MathF.Max(LabelSize, (h - blockH) * 0.5f + LabelSize);

            for (int li = 0; li < lines.Count; li++)
            {
                if (lines[li].Length == 0) continue;
                Text.DrawLine(lines[li], x + 8f, startY + li * lineH, LabelSize, b.Css.FgColor);
            }
        }
    }

    private void EmitBgQuadPx(List<float> verts, List<ushort> idx,
        float px, float py, float pw, float ph, Vector4 color)
    {
        float x0 = 2f * px / _canvasW - 1f;
        float x1 = 2f * (px + pw) / _canvasW - 1f;
        float y0 = 1f - 2f * py / _canvasH;
        float y1 = 1f - 2f * (py + ph) / _canvasH;

        ushort baseI = (ushort)(verts.Count / 6);
        EmitVert(verts, x0, y0, color);
        EmitVert(verts, x1, y0, color);
        EmitVert(verts, x1, y1, color);
        EmitVert(verts, x0, y1, color);
        idx.Add(baseI);
        idx.Add((ushort)(baseI + 1));
        idx.Add((ushort)(baseI + 2));
        idx.Add(baseI);
        idx.Add((ushort)(baseI + 2));
        idx.Add((ushort)(baseI + 3));
    }

    private static void EmitVert(List<float> v, float x, float y, Vector4 c)
    {
        v.Add(x); v.Add(y);
        v.Add(c.X); v.Add(c.Y); v.Add(c.Z); v.Add(c.W);
    }

    /// <summary>
    /// Split a label into renderable lines. Newlines split; non-printable
    /// chars (the emoji prefixes the HTML labels use) become spaces so the
    /// visual cadence of the original label is preserved. Trailing blank
    /// lines and the leading whitespace of each line are stripped so emoji-
    /// only prefixes don't render as empty void at the start of the label.
    /// </summary>
    private static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(s)) return lines;

        var sb = new System.Text.StringBuilder();
        void Flush()
        {
            lines.Add(sb.ToString().TrimStart());
            sb.Clear();
        }

        foreach (var c in s)
        {
            if (c == '\n') { Flush(); continue; }
            if (c == '\r') continue;
            if (c >= 32 && c <= 126) sb.Append(c);
            else sb.Append(' ');
        }
        Flush();

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private (float x, float y, float w, float h) ResolvePixelRect(CssRect c)
    {
        float w = c.W > 0 ? c.W : 160;
        float h = c.H > 0 ? c.H : 36;
        float x = c.Left.HasValue ? c.Left.Value
                : c.Right.HasValue ? _canvasW - c.Right.Value - w
                : 10;
        float y = c.Top.HasValue ? c.Top.Value
                : c.Bottom.HasValue ? _canvasH - c.Bottom.Value - h
                : 10;
        return (x, y, w, h);
    }

    // ── Tiny CSS-JSON parser ──────────────────────────────────────────────

    private struct CssRect
    {
        public float? Top, Bottom, Left, Right;
        public float W, H;
        public Vector4 BgColor;
        public Vector4 FgColor;
        public bool HasExplicitDisplay;
        public bool DisplayBlock;
        public bool PointerEvents;
    }

    private static CssRect ParseCss(string json)
    {
        var r = new CssRect
        {
            BgColor = new Vector4(0.1f, 0.2f, 0.3f, 0.85f),
            FgColor = new Vector4(0.5f, 0.9f, 1.0f, 1f),
            PointerEvents = true,
        };
        var body = json.Trim().Trim('{', '}');
        foreach (var raw in body.Split(','))
        {
            var pair = raw.Trim();
            if (pair.Length == 0) continue;
            var colon = pair.IndexOf(':');
            if (colon < 0) continue;
            var key = pair.Substring(0, colon).Trim().Trim('"');
            var val = pair.Substring(colon + 1).Trim().Trim('"');

            switch (key)
            {
                case "top":     r.Top    = ParsePx(val); break;
                case "bottom":  r.Bottom = ParsePx(val); break;
                case "left":    r.Left   = ParsePx(val); break;
                case "right":   r.Right  = ParsePx(val); break;
                case "width":   r.W      = ParsePx(val) ?? r.W; break;
                case "height":  r.H      = ParsePx(val) ?? r.H; break;
                case "background": r.BgColor = ParseColor(val, r.BgColor); break;
                case "color":   r.FgColor = ParseColor(val, r.FgColor); break;
                case "display":
                    r.HasExplicitDisplay = true;
                    r.DisplayBlock = val == "block";
                    break;
                case "pointerEvents":
                    r.PointerEvents = val != "none";
                    break;
            }
        }
        if (r.W <= 0) r.W = 160;
        if (r.H <= 0) r.H = 36;
        return r;
    }

    private static float? ParsePx(string s)
    {
        s = s.Trim();
        if (s.EndsWith("px")) s = s.Substring(0, s.Length - 2);
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static Vector4 ParseColor(string s, Vector4 fallback)
    {
        s = s.Trim();
        if (s.StartsWith("rgba("))
        {
            var inner = s.Substring(5, s.Length - 6);
            var parts = inner.Split(',');
            if (parts.Length >= 4
                && float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r)
                && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g)
                && float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)
                && float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a))
                return new Vector4(r / 255f, g / 255f, b / 255f, a);
        }
        else if (s.StartsWith("#") && (s.Length == 4 || s.Length == 7))
        {
            int r, g, b;
            if (s.Length == 4)
            {
                r = Convert.ToInt32(new string(s[1], 2), 16);
                g = Convert.ToInt32(new string(s[2], 2), 16);
                b = Convert.ToInt32(new string(s[3], 2), 16);
            }
            else
            {
                r = Convert.ToInt32(s.Substring(1, 2), 16);
                g = Convert.ToInt32(s.Substring(3, 2), 16);
                b = Convert.ToInt32(s.Substring(5, 2), 16);
            }
            return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        }
        return fallback;
    }

    private sealed class UIButton
    {
        public string Id = "";
        public string Label = "";
        public bool Visible;
        public CssRect Css;
    }
}
