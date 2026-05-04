using System.Numerics;
using FontStash.NET;
using RtsEngine.Game;
using Silk.NET.OpenGL;

namespace RtsEngine.Desktop;

/// <summary>
/// FontStash-backed text renderer for desktop. Implements ITextRenderer so
/// the platform-agnostic EngineUI can drive it from the Game project without
/// pulling Silk.NET.OpenGL into Game.
///
/// Owns its own GL state — atlas texture, shader, VAO, VBO. FontStash hands
/// us per-frame glyph quads via its 5 callbacks; we batch them into the VBO
/// and emit one draw per <see cref="DrawLine"/> call. State is fully restored
/// before returning so the next IGPU draw isn't tripped up by leftover
/// program/buffer/texture bindings.
/// </summary>
internal sealed class FontStashTextRenderer : ITextRenderer, IDisposable
{
    private readonly GL _gl;
    private Fontstash _fons = null!;
    private readonly int _fontId;
    private uint _tex;
    private int _texW, _texH;

    private uint _program;
    private uint _vao;
    private uint _vbo;
    private int _uProjectionLoc;
    private int _uTexLoc;

    private float _canvasW = 1280, _canvasH = 720;

    public FontStashTextRenderer(GL gl, string ttfPath)
    {
        _gl = gl;
        BuildShader();
        BuildVao();

        var p = new FonsParams
        {
            width = 512,
            height = 512,
            flags = (byte)FonsFlags.ZeroTopleft,
            renderCreate = (w, h) => CreateAtlasTexture(w, h) == 1,
            renderResize = (w, h) => CreateAtlasTexture(w, h) == 1,
            renderUpdate = (rect, data) => { UpdateAtlasRegion(rect, data); },
            renderDraw   = (verts, tcoords, colors, nverts) => { DrawCallback(verts, tcoords, colors, nverts); },
            renderDelete = () => DestroyAtlasTexture(),
        };
        _fons = new Fontstash(p);

        _fontId = _fons.AddFont("ui", ttfPath, 0);
        if (_fontId == Fontstash.INVALID)
            Console.Error.WriteLine($"[fontstash] failed to load font: {ttfPath}");
    }

    public void SetCanvasSize(float w, float h)
    {
        _canvasW = w;
        _canvasH = h;
    }

    public void DrawLine(string text, float px, float py, float size, Vector4 color)
    {
        if (string.IsNullOrEmpty(text) || _fontId == Fontstash.INVALID) return;
        // FontStash baseline-aligns by default — we want the baseline at py +
        // size so callers can pass the top of the line as py. Easier: use
        // top alignment on the font setter.
        _fons.SetFont(_fontId);
        _fons.SetSize(size);
        _fons.SetColour(PackColor(color));
        _fons.SetAlign((int)(FonsAlign.Left | FonsAlign.Top));
        _fons.DrawText(px, py, text, null);
    }

    private static uint PackColor(Vector4 c)
    {
        // FontStash expects little-endian RGBA in a uint (R in low byte).
        uint r = (uint)Math.Clamp((int)(c.X * 255f), 0, 255);
        uint g = (uint)Math.Clamp((int)(c.Y * 255f), 0, 255);
        uint b = (uint)Math.Clamp((int)(c.Z * 255f), 0, 255);
        uint a = (uint)Math.Clamp((int)(c.W * 255f), 0, 255);
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    // ── FontStash GL callbacks ──────────────────────────────────────────────

    private unsafe int CreateAtlasTexture(int w, int h)
    {
        if (_tex != 0) _gl.DeleteTexture(_tex);
        _tex = _gl.GenTexture();
        _texW = w; _texH = h;
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)w, (uint)h, 0,
            PixelFormat.Red, PixelType.UnsignedByte, null);
        // Swizzle the single-channel R into RGBA so the atlas reads as white
        // glyphs with alpha = R. Saves the fragment shader an extra fold.
        var swizzle = stackalloc int[]
        {
            (int)GLEnum.One,
            (int)GLEnum.One,
            (int)GLEnum.One,
            (int)GLEnum.Red,
        };
        _gl.TexParameter(TextureTarget.Texture2D, GLEnum.TextureSwizzleRgba, swizzle);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return 1;
    }

    private unsafe int UpdateAtlasRegion(int[] rect, byte[] data)
    {
        if (_tex == 0) return 0;
        // rect is [x0, y0, x1, y1] in atlas pixels. data is the FULL atlas
        // buffer (texW × texH bytes); the changed region is at row y0..y1
        // within it, but the source pointer needs to be data + y0*texW + x0.
        int x0 = rect[0], y0 = rect[1], x1 = rect[2], y1 = rect[3];
        int rw = x1 - x0;
        int rh = y1 - y0;
        if (rw <= 0 || rh <= 0) return 0;

        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        // Specify the source row stride so we can upload the sub-region in
        // place from the full-atlas buffer.
        _gl.PixelStore(GLEnum.UnpackRowLength, _texW);
        fixed (byte* p = data)
        {
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0,
                x0, y0, (uint)rw, (uint)rh,
                PixelFormat.Red, PixelType.UnsignedByte,
                p + y0 * _texW + x0);
        }
        _gl.PixelStore(GLEnum.UnpackRowLength, 0);
        return 1;
    }

    private void DestroyAtlasTexture()
    {
        if (_tex != 0) { _gl.DeleteTexture(_tex); _tex = 0; }
    }

    /// <summary>
    /// FontStash hands us 3 parallel arrays (pos, uv, color) for `nverts`
    /// vertices, already in triangle-list order. Pack them into our VBO and
    /// dispatch a single glDrawArrays. Snapshots / restores enough GL state
    /// that the next IGPU draw doesn't see leftover bindings.
    /// </summary>
    private unsafe int DrawCallback(float[] verts, float[] tcoords, uint[] colors, int nverts)
    {
        if (nverts <= 0 || _tex == 0) return 0;

        // 5 floats per vertex: x, y, u, v, color-as-float-uint.
        var packed = new float[nverts * 5];
        for (int i = 0; i < nverts; i++)
        {
            packed[i * 5 + 0] = verts[i * 2 + 0];
            packed[i * 5 + 1] = verts[i * 2 + 1];
            packed[i * 5 + 2] = tcoords[i * 2 + 0];
            packed[i * 5 + 3] = tcoords[i * 2 + 1];
            packed[i * 5 + 4] = BitConverter.UInt32BitsToSingle(colors[i]);
        }

        // Snapshot a few state bits that the IGPU pipeline cares about so we
        // can put them back. (Program / VAO / Texture / Buffer bindings get
        // re-set per draw by IGPU, so we don't restore those.)
        _gl.GetInteger(GetPName.Blend, out int prevBlend);
        _gl.GetInteger(GetPName.DepthTest, out int prevDepth);
        _gl.GetInteger(GetPName.CullFace, out int prevCull);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        _gl.UseProgram(_program);

        // Ortho projection: pixel coords (0,0)=top-left, (canvasW, canvasH)=bottom-right.
        // Built column-major because GL stores mat4 column-major; this matches
        // the layout that glUniformMatrix4fv(..., transpose=false, ...) expects.
        var proj = new float[]
        {
             2f / _canvasW, 0,                  0,  0,
             0,            -2f / _canvasH,      0,  0,
             0,             0,                 -1,  0,
            -1,             1,                  0,  1,
        };
        fixed (float* pp = proj) _gl.UniformMatrix4(_uProjectionLoc, 1, false, pp);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.Uniform1(_uTexLoc, 0);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* pp = packed)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(packed.Length * sizeof(float)), pp, BufferUsageARB.StreamDraw);
        // Re-bind attribute pointers in case the VAO was unbound elsewhere.
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, 5 * sizeof(float), (void*)(4 * sizeof(float)));

        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)nverts);

        // Restore.
        if (prevBlend == 0) _gl.Disable(EnableCap.Blend);
        if (prevDepth != 0) _gl.Enable(EnableCap.DepthTest);
        if (prevCull != 0)  _gl.Enable(EnableCap.CullFace);
        return 1;
    }

    // ── Shader + VAO ────────────────────────────────────────────────────────

    private void BuildShader()
    {
        const string vs = @"
#version 420 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aColor;
uniform mat4 uProjection;
out vec2 vUV;
out vec4 vColor;
void main() {
    gl_Position = uProjection * vec4(aPos, 0.0, 1.0);
    vUV = aUV;
    vColor = aColor;
}
";
        const string fs = @"
#version 420 core
in vec2 vUV;
in vec4 vColor;
uniform sampler2D uTex;
out vec4 FragColor;
void main() {
    // Atlas swizzle gives RGB=1, A=R; multiply by vColor for tinted glyphs.
    vec4 t = texture(uTex, vUV);
    FragColor = vColor * t;
}
";
        var v = Compile(ShaderType.VertexShader, vs);
        var f = Compile(ShaderType.FragmentShader, fs);
        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, v);
        _gl.AttachShader(_program, f);
        _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out int link);
        if (link == 0)
            Console.Error.WriteLine($"[fontstash] program link FAILED: {_gl.GetProgramInfoLog(_program)}");
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        _uProjectionLoc = _gl.GetUniformLocation(_program, "uProjection");
        _uTexLoc = _gl.GetUniformLocation(_program, "uTex");
    }

    private uint Compile(ShaderType type, string source)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, GLEnum.CompileStatus, out int status);
        if (status == 0)
            Console.Error.WriteLine($"[fontstash] {type} compile FAILED: {_gl.GetShaderInfoLog(s)}");
        return s;
    }

    private void BuildVao()
    {
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        // Allocate a small initial buffer; DrawCallback re-uploads via
        // glBufferData every call so the size grows as needed.
        unsafe { _gl.BufferData(BufferTargetARB.ArrayBuffer, 1024, null, BufferUsageARB.StreamDraw); }
    }

    public void Dispose()
    {
        DestroyAtlasTexture();
        if (_program != 0) _gl.DeleteProgram(_program);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
    }
}
