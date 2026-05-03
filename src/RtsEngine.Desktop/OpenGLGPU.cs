using System.Reflection;
using RtsEngine.Core;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RtsEngine.Desktop;

/// <summary>
/// OpenGL implementation of IGPU — same interface as WebGPU, backed by Silk.NET.
/// Real UBOs, real samplers, real textures, real bind groups, all pipeline
/// variants. Vertex layouts come from the layout descriptor objects (the same
/// anonymous-typed shape JS reads in gpu-proxy.js), parsed via reflection.
///
/// Shaders are loaded as GLSL — desktop's FileAssetSource swaps `.wgsl` paths
/// to `.glsl` so each WGSL shader has a hand-ported sibling next to it.
/// </summary>
internal sealed class OpenGLGPU : IGPU, IDisposable
{
    private readonly GL _gl;

    // ── Resource tables. All indexed by the IGPU integer handles. ──
    // Slot 0 is reserved as a null sentinel for every table.
    private readonly List<uint> _buffers = new() { 0 };
    private readonly Dictionary<int, BufferKind> _bufferKind = new();
    private readonly Dictionary<int, int> _uniformSize = new(); // uniform buffer byte size, for re-binding
    private readonly List<uint> _programs = new() { 0 };
    private readonly List<TextureEntry> _textures = new() { default };
    private readonly List<uint> _samplers = new() { 0 };

    private enum BufferKind { Vertex, Index16, Index32, Uniform }

    private struct TextureEntry { public uint Tex; public bool HasMipmaps; }

    /// <summary>
    /// A pipeline = shader program + vertex attribute layout + render-state
    /// flags. We don't bake VAOs because the IGPU contract lets the same
    /// pipeline be drawn against different VBOs; we re-apply attributes per
    /// draw using a single shared VAO.
    /// </summary>
    private sealed class Pipeline
    {
        public int Program;
        public List<VertexAttr> Attrs = new();
        public int Stride;
        public PrimitiveType Topology = PrimitiveType.Triangles;
        public bool DepthTest = true;
        public bool DepthWrite = true;
        public bool BlendAlpha;
        public bool CullBack = true;
        public bool CullFront;
    }

    private struct VertexAttr
    {
        public int Location;
        public int Components;       // 1, 2, 3, 4
        public VertexAttribPointerType Type;
        public bool Normalized;
        public int Offset;
    }

    private sealed class BindGroup
    {
        public int PipelineId;
        public List<BindEntry> Entries = new();
    }

    private struct BindEntry
    {
        public int Binding;      // shader binding slot
        public int? BufferId;
        public int? TextureViewId;
        public int? SamplerId;
    }

    private readonly Dictionary<int, Pipeline> _pipelines = new();
    private readonly Dictionary<int, BindGroup> _bindGroups = new();
    private int _nextPipelineId = 1;
    private int _nextBindGroupId = 1;
    private uint _vao;

    // Diagnostics for the first few frames.
    private int _drawCount;
    private int _frameLogIdx;

    public OpenGLGPU(GL gl)
    {
        _gl = gl;
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.ClearColor(0.005f, 0.007f, 0.018f, 1.0f);
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
    }

    // ── Shader compilation ─────────────────────────────────────────────────

    public Task<int> CreateShaderModule(string shaderCode)
    {
        // GLSL is loaded as a single source containing both stages, gated by
        // `#ifdef VERTEX` / `#ifdef FRAGMENT`. We expand each stage and prepend
        // the `#define` so the preprocessor pulls only its block.
        var (vsSource, fsSource) = SplitStages(shaderCode);

        var vs = Compile(ShaderType.VertexShader, vsSource);
        var fs = Compile(ShaderType.FragmentShader, fsSource);

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, GLEnum.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            var log = _gl.GetProgramInfoLog(program);
            Console.Error.WriteLine($"[GL] program link FAILED: {log}");
        }
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        var id = _programs.Count;
        _programs.Add(program);
        return Task.FromResult(id);
    }

    private uint Compile(ShaderType type, string source)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, GLEnum.CompileStatus, out int status);
        if (status == 0)
        {
            var log = _gl.GetShaderInfoLog(s);
            Console.Error.WriteLine($"[GL] {type} compile FAILED:");
            Console.Error.WriteLine(log);
            Console.Error.WriteLine("--- source ---");
            Console.Error.WriteLine(source);
            Console.Error.WriteLine("--- end ---");
        }
        return s;
    }

    /// <summary>
    /// Pull out the body of `#ifdef VERTEX` and `#ifdef FRAGMENT` from a
    /// combined GLSL source. Anything outside any #ifdef block is shared
    /// between stages (uniform blocks, helper functions). The `#version`
    /// declaration is stripped so we can prepend our own consistent header.
    /// </summary>
    private static (string vs, string fs) SplitStages(string src)
    {
        // 4.2 enables `layout(binding = N)` on UBOs and samplers — matches the
        // WebGPU @binding(N) model directly. Shader files don't need to declare
        // a #version themselves; if they do, ExtractStage strips it.
        const string Header = "#version 420 core\n";
        return (Header + ExtractStage(src, "VERTEX"), Header + ExtractStage(src, "FRAGMENT"));
    }

    private static string ExtractStage(string src, string stage)
    {
        var lines = src.Split('\n');
        var sb = new System.Text.StringBuilder();
        // null = outside any #ifdef. true/false = inside an #ifdef whose name matches/doesn't.
        bool? include = null;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("#version")) continue;
            if (t.StartsWith("// --")) continue;

            if (t.StartsWith("#ifdef"))
            {
                var rest = t.Substring("#ifdef".Length).Trim();
                include = rest == stage;
                continue;
            }
            if (t.StartsWith("#endif"))
            {
                include = null;
                continue;
            }

            if (include == null || include == true)
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    // ── Buffer creation ────────────────────────────────────────────────────

    public unsafe Task<int> CreateVertexBuffer(float[] data)
    {
        var buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        var id = _buffers.Count;
        _buffers.Add(buf);
        _bufferKind[id] = BufferKind.Vertex;
        return Task.FromResult(id);
    }

    public unsafe Task<int> CreateIndexBuffer(ushort[] data)
    {
        var buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, buf);
        fixed (ushort* p = data)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(data.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        var id = _buffers.Count;
        _buffers.Add(buf);
        _bufferKind[id] = BufferKind.Index16;
        return Task.FromResult(id);
    }

    public unsafe Task<int> CreateIndexBuffer32(uint[] data)
    {
        var buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, buf);
        fixed (uint* p = data)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(data.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        var id = _buffers.Count;
        _buffers.Add(buf);
        _bufferKind[id] = BufferKind.Index32;
        return Task.FromResult(id);
    }

    public Task<int> CreateUniformBuffer(int sizeBytes)
    {
        var buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, buf);
        // Round to a multiple of 16 because std140 layouts are 16-aligned and
        // some drivers grumble if the buffer is smaller than the block.
        var size = (sizeBytes + 15) & ~15;
        unsafe { _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)size, null, BufferUsageARB.DynamicDraw); }
        var id = _buffers.Count;
        _buffers.Add(buf);
        _bufferKind[id] = BufferKind.Uniform;
        _uniformSize[id] = size;
        return Task.FromResult(id);
    }

    public unsafe void WriteBuffer(int bufferId, float[] data)
    {
        if (!_bufferKind.TryGetValue(bufferId, out var kind)) return;
        // Match WebGPU writeBuffer semantics: it uploads exactly the floats
        // given, starting at offset 0, into whatever buffer kind was created.
        // We bind to the matching target so streaming a vertex buffer (e.g.
        // the per-frame path-debug or HP-bar VBOs) actually lands in the GL
        // backend; previously this was a silent no-op for non-uniform buffers
        // and the driver kept showing the buffer's initial zero contents.
        var target = kind switch
        {
            BufferKind.Uniform => BufferTargetARB.UniformBuffer,
            BufferKind.Index16 => BufferTargetARB.ElementArrayBuffer,
            BufferKind.Index32 => BufferTargetARB.ElementArrayBuffer,
            _                  => BufferTargetARB.ArrayBuffer,
        };
        _gl.BindBuffer(target, _buffers[bufferId]);
        fixed (float* p = data)
            _gl.BufferSubData(target, 0, (nuint)(data.Length * sizeof(float)), p);
    }

    public void DestroyBuffer(int bufferId)
    {
        if (bufferId > 0 && bufferId < _buffers.Count && _buffers[bufferId] != 0)
        {
            _gl.DeleteBuffer(_buffers[bufferId]);
            _buffers[bufferId] = 0;
            _bufferKind.Remove(bufferId);
            _uniformSize.Remove(bufferId);
        }
    }

    // ── Textures + samplers ────────────────────────────────────────────────

    public Task<int> CreateTextureFromUrl(string url)
    {
        try
        {
            // url here is a wwwroot-relative path because the renderers were
            // built for HttpClient.GetStringAsync. On desktop, FileAssetSource
            // would resolve config/yaml/shaders, but textures bypass it — the
            // path is just relative. Resolve against the same root.
            var path = ResolveTexturePath(url);
            using var img = Image.Load<Rgba32>(path);
            return Task.FromResult(UploadTexture(img));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[GL] CreateTextureFromUrl({url}) FAILED: {e.Message}. Falling back to white.");
            return Task.FromResult(MakeFallbackTexture());
        }
    }

    private string ResolveTexturePath(string relative)
    {
        // Mirror Program.ResolveAssetRoots — but we don't have a back-reference,
        // so probe the same set of locations. The repo-root entry is what makes
        // requests for "assets/textures/foo.png" work in dev mode (the WASM
        // csproj surfaces those at runtime; on desktop they live at the
        // top-level /assets folder).
        var rel = relative.Replace('/', Path.DirectorySeparatorChar);
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "wwwroot", rel);
        if (File.Exists(sideBySide)) return sideBySide;
        var devWwwroot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "RtsEngine.Wasm", "wwwroot", rel));
        if (File.Exists(devWwwroot)) return devWwwroot;
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", rel));
        if (File.Exists(repoRoot)) return repoRoot;
        return Path.Combine(AppContext.BaseDirectory, rel);
    }

    private unsafe int UploadTexture(Image<Rgba32> img)
    {
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);

        // Pull pixels into a contiguous byte buffer. ImageSharp doesn't
        // guarantee a single backing block, so DangerousTryGetSinglePixelMemory
        // can fail on very large or sliced images — copy if it does.
        var pixels = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(pixels);

        fixed (byte* p = pixels)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)img.Width, (uint)img.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        _gl.GenerateMipmap(TextureTarget.Texture2D);

        var id = _textures.Count;
        _textures.Add(new TextureEntry { Tex = tex, HasMipmaps = true });
        return id;
    }

    private unsafe int MakeFallbackTexture()
    {
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        var white = new byte[] { 255, 255, 255, 255 };
        fixed (byte* p = white)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var id = _textures.Count;
        _textures.Add(new TextureEntry { Tex = tex, HasMipmaps = false });
        return id;
    }

    public Task<int> CreateSampler(string filter = "linear", string wrap = "repeat")
    {
        var samp = _gl.GenSampler();
        var min = filter == "nearest" ? GLEnum.NearestMipmapNearest : GLEnum.LinearMipmapLinear;
        var mag = filter == "nearest" ? GLEnum.Nearest : GLEnum.Linear;
        _gl.SamplerParameter(samp, GLEnum.TextureMinFilter, (int)min);
        _gl.SamplerParameter(samp, GLEnum.TextureMagFilter, (int)mag);
        var w = wrap == "clamp" ? (int)GLEnum.ClampToEdge : (int)GLEnum.Repeat;
        _gl.SamplerParameter(samp, GLEnum.TextureWrapS, w);
        _gl.SamplerParameter(samp, GLEnum.TextureWrapT, w);
        _gl.SamplerParameter(samp, GLEnum.TextureWrapR, w);

        var id = _samplers.Count;
        _samplers.Add(samp);
        return Task.FromResult(id);
    }

    // ── Render pipelines ───────────────────────────────────────────────────
    //
    // A WebGPU pipeline bundles shader + vertex layout + render state. On GL
    // we keep all that on a Pipeline struct and re-apply per draw. The pipeline
    // variants differ only in fixed-function state (blending, depth, cull,
    // topology); they share creation code.

    public Task<int> CreateRenderPipeline(int shaderModuleId, object[] vertexBufferLayouts)
        => MakePipeline(shaderModuleId, vertexBufferLayouts, p => { });

    public Task<int> CreateRenderPipelineLines(int shaderModuleId, object[] vertexBufferLayouts)
        => MakePipeline(shaderModuleId, vertexBufferLayouts, p =>
        {
            p.Topology = PrimitiveType.Lines;
            // Match outline.wgsl pipeline: alpha blending, no depth-write, depth
            // test less-equal so coplanar lines on terrain still draw.
            p.BlendAlpha = true;
            p.DepthWrite = false;
            p.CullBack = false;
        });

    public Task<int> CreateRenderPipelineAlphaBlend(int shaderModuleId, object[] vertexBufferLayouts)
        => MakePipeline(shaderModuleId, vertexBufferLayouts, p =>
        {
            p.BlendAlpha = true;
            p.DepthWrite = false;
            // WebGPU atmosphere pipeline cull-mode = 'front' (we're inside the
            // shell sphere so the back-face is what we want to discard).
            p.CullBack = false;
            p.CullFront = true;
        });

    public Task<int> CreateRenderPipelineMarker(int shaderModuleId, object[] vertexBufferLayouts)
        => MakePipeline(shaderModuleId, vertexBufferLayouts, p =>
        {
            // World-space markers (HP bars, selection discs): alpha blend,
            // depth test on (so they get occluded by terrain in front), no
            // depth write, and cull-none so flat quads render regardless of
            // facing the camera.
            p.BlendAlpha = true;
            p.DepthWrite = false;
            p.CullBack = false;
            p.CullFront = false;
        });

    public Task<int> CreateRenderPipelineUI(int shaderModuleId, object[] vertexBufferLayouts)
        => MakePipeline(shaderModuleId, vertexBufferLayouts, p =>
        {
            p.BlendAlpha = true;
            p.DepthTest = false;
            p.DepthWrite = false;
            p.CullBack = false;
        });

    private Task<int> MakePipeline(int shaderModuleId, object[] vertexBufferLayouts, Action<Pipeline> tweak)
    {
        var p = new Pipeline
        {
            Program = (int)_programs[shaderModuleId],
        };

        if (vertexBufferLayouts.Length > 0)
        {
            // Only one layout supported (matches every renderer's usage). Read
            // the descriptor via reflection so we accept the same anonymous-typed
            // shape that WASM's gpu-proxy.js consumes verbatim.
            var layout = vertexBufferLayouts[0];
            p.Stride = (int)Convert.ToInt32(layout.GetType().GetProperty("arrayStride")!.GetValue(layout));
            var attrs = (object[])layout.GetType().GetProperty("attributes")!.GetValue(layout)!;
            foreach (var a in attrs)
            {
                var format = (string)a.GetType().GetProperty("format")!.GetValue(a)!;
                var offset = Convert.ToInt32(a.GetType().GetProperty("offset")!.GetValue(a));
                var loc = Convert.ToInt32(a.GetType().GetProperty("shaderLocation")!.GetValue(a));
                p.Attrs.Add(MakeAttr(format, offset, loc));
            }
        }

        tweak(p);

        var id = _nextPipelineId++;
        _pipelines[id] = p;
        return Task.FromResult(id);
    }

    private static VertexAttr MakeAttr(string format, int offset, int location)
    {
        // WebGPU vertex format strings → GL component count + type.
        return format switch
        {
            "float32"   => new() { Location = location, Components = 1, Type = VertexAttribPointerType.Float, Offset = offset },
            "float32x2" => new() { Location = location, Components = 2, Type = VertexAttribPointerType.Float, Offset = offset },
            "float32x3" => new() { Location = location, Components = 3, Type = VertexAttribPointerType.Float, Offset = offset },
            "float32x4" => new() { Location = location, Components = 4, Type = VertexAttribPointerType.Float, Offset = offset },
            _ => throw new NotSupportedException($"vertex format '{format}' not supported"),
        };
    }

    // ── Bind groups ────────────────────────────────────────────────────────

    public Task<int> CreateBindGroup(int pipelineId, int groupIndex, object[] entries)
    {
        var bg = new BindGroup { PipelineId = pipelineId };
        foreach (var e in entries)
        {
            var t = e.GetType();
            var binding = Convert.ToInt32(t.GetProperty("binding")!.GetValue(e));
            int? buf = TryReadInt(t.GetProperty("bufferId"), e);
            int? texView = TryReadInt(t.GetProperty("textureViewId"), e);
            int? samp = TryReadInt(t.GetProperty("samplerId"), e);
            bg.Entries.Add(new BindEntry
            {
                Binding = binding, BufferId = buf, TextureViewId = texView, SamplerId = samp,
            });
        }
        var id = _nextBindGroupId++;
        _bindGroups[id] = bg;
        return Task.FromResult(id);
    }

    private static int? TryReadInt(PropertyInfo? prop, object obj)
    {
        if (prop == null) return null;
        var v = prop.GetValue(obj);
        if (v == null) return null;
        return Convert.ToInt32(v);
    }

    // ── Drawing ────────────────────────────────────────────────────────────

    public void Render(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => DoDraw(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount,
            ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    public void RenderAdditional(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => DoDraw(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount, 0);

    public void RenderOverlay(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId, int indexCount)
        => DoDraw(pipelineId, vertexBufferId, indexBufferId, bindGroupId, indexCount, ClearBufferMask.DepthBufferBit);

    public void RenderNoBind(int pipelineId, int vertexBufferId, int indexBufferId, int indexCount)
        => DoDraw(pipelineId, vertexBufferId, indexBufferId, 0, indexCount, 0);

    private unsafe void DoDraw(int pipelineId, int vertexBufferId, int indexBufferId, int bindGroupId,
        int indexCount, ClearBufferMask clearMask)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var p))
        {
            if (_frameLogIdx < 3) Console.Error.WriteLine($"[GL] DoDraw: unknown pipelineId={pipelineId}");
            return;
        }

        if (clearMask != 0)
        {
            // glClear writes only to channels currently masked in. The previous
            // draw (e.g. the EngineUI pass at end-of-frame) may have left
            // DepthMask=false; without re-enabling it here, the depth bit of
            // glClear is silently dropped, the depth buffer keeps stale values
            // from the previous frame, and new fragments fail the depth test
            // sporadically — produces black-speckled pixels everywhere the
            // stale depth wins.
            if ((clearMask & ClearBufferMask.DepthBufferBit) != 0) _gl.DepthMask(true);
            _gl.Clear(clearMask);
        }

        // Render-state per pipeline. Set every draw call because a previous
        // draw may have left state in the wrong configuration.
        if (p.DepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(p.DepthWrite);
        if (p.BlendAlpha)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
                BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        }
        else
        {
            _gl.Disable(EnableCap.Blend);
        }
        if (p.CullBack || p.CullFront)
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(p.CullFront ? GLEnum.Front : GLEnum.Back);
        }
        else
        {
            _gl.Disable(EnableCap.CullFace);
        }

        _gl.UseProgram((uint)p.Program);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _buffers[vertexBufferId]);

        // Disable any previously-enabled attribute we don't use this draw —
        // otherwise stale pointers from the last pipeline can trip the GPU.
        for (int loc = 0; loc < 8; loc++) _gl.DisableVertexAttribArray((uint)loc);
        foreach (var a in p.Attrs)
        {
            _gl.EnableVertexAttribArray((uint)a.Location);
            _gl.VertexAttribPointer((uint)a.Location, a.Components, a.Type, a.Normalized,
                (uint)p.Stride, (void*)a.Offset);
        }

        if (indexBufferId > 0)
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _buffers[indexBufferId]);

        // Bind group → glBindBufferBase / glBindTextureUnit / glBindSampler.
        // Texture bindings map to texture units = binding slot, and each shader
        // declares `layout(binding = N) uniform sampler2D foo;` so unit N
        // matches binding N.
        //
        // Impedance mismatch: WGSL has separate sampler + texture entries,
        // GLSL's sampler2D combines them. The renderers' bind groups always
        // include exactly one sampler entry (the "default sampler for this
        // group"). Apply that sampler to every texture unit in the group so
        // sampler2D reads pick up the right filtering/wrapping.
        if (bindGroupId > 0 && _bindGroups.TryGetValue(bindGroupId, out var bg))
        {
            uint? groupSampler = null;
            foreach (var e in bg.Entries)
            {
                if (e.SamplerId is int sa && sa > 0 && sa < _samplers.Count && e.TextureViewId == null)
                    groupSampler = _samplers[sa];
            }

            foreach (var e in bg.Entries)
            {
                if (e.BufferId is int b && b > 0)
                {
                    _gl.BindBufferBase(BufferTargetARB.UniformBuffer, (uint)e.Binding, _buffers[b]);
                }
                if (e.TextureViewId is int tv && tv > 0 && tv < _textures.Count)
                {
                    _gl.ActiveTexture(TextureUnit.Texture0 + e.Binding);
                    _gl.BindTexture(TextureTarget.Texture2D, _textures[tv].Tex);
                    if (groupSampler.HasValue)
                        _gl.BindSampler((uint)e.Binding, groupSampler.Value);
                }
                else if (e.SamplerId is int sa2 && sa2 > 0 && sa2 < _samplers.Count && e.TextureViewId == null)
                {
                    // Standalone sampler entry — also bind it to its own slot
                    // in case a shader has bound a sampler-only at that slot
                    // (no current shader does, but this matches WGSL semantics).
                    _gl.BindSampler((uint)e.Binding, _samplers[sa2]);
                }
            }
        }

        if (indexBufferId > 0)
        {
            var elemType = _bufferKind.TryGetValue(indexBufferId, out var k) && k == BufferKind.Index32
                ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort;
            _gl.DrawElements(p.Topology, (uint)indexCount, elemType, null);
        }
        else
        {
            _gl.DrawArrays(p.Topology, 0, (uint)indexCount);
        }

        var err = _gl.GetError();
        if (err != GLEnum.NoError && _frameLogIdx < 3)
            Console.Error.WriteLine($"[GL] glError after draw (pipeline={pipelineId}, vbo={vertexBufferId}, ibo={indexBufferId}, count={indexCount}): {err}");

        _drawCount++;
    }

    public void EndFrame()
    {
        if (_frameLogIdx < 3)
        {
            Console.Error.WriteLine($"[GL] frame {_frameLogIdx}: {_drawCount} draw calls");
            _frameLogIdx++;
        }
        _drawCount = 0;
    }

    public void Dispose()
    {
        for (int i = 1; i < _buffers.Count; i++)
            if (_buffers[i] != 0) _gl.DeleteBuffer(_buffers[i]);
        for (int i = 1; i < _programs.Count; i++)
            if (_programs[i] != 0) _gl.DeleteProgram(_programs[i]);
        for (int i = 1; i < _textures.Count; i++)
            if (_textures[i].Tex != 0) _gl.DeleteTexture(_textures[i].Tex);
        for (int i = 1; i < _samplers.Count; i++)
            if (_samplers[i] != 0) _gl.DeleteSampler(_samplers[i]);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
    }
}
