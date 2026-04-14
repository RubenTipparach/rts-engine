# Building a Modern Game Engine with a Material Shader Pipeline

This document outlines how to architect a cross-platform game engine in C# using Silk.NET, covering the rendering abstraction, the material/shader pipeline, and how it maps to both desktop (native OpenGL/Vulkan) and WASM (WebGL).

---

## 1. High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Game / Application                       │
│   Scenes, entities, game logic, scripting, UI                   │
├─────────────────────────────────────────────────────────────────┤
│                        Engine Core                              │
│   ECS, transform hierarchy, camera system, input pipeline       │
├──────────────┬──────────────┬──────────────┬────────────────────┤
│  Renderer    │  Asset Mgr   │  Physics     │  Audio             │
│  (abstract)  │  (meshes,    │  (optional)  │  (optional)        │
│              │   textures,  │              │                    │
│              │   shaders)   │              │                    │
├──────────────┴──────────────┴──────────────┴────────────────────┤
│                     Platform Backends                           │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────┐    │
│  │   Desktop    │  │     WASM     │  │   Mobile / Other   │    │
│  │ Silk.NET.GL  │  │  WebGL via   │  │   Silk.NET.GL ES   │    │
│  │ Silk.NET.Win │  │  JS interop  │  │   or native        │    │
│  │ Silk.NET.Inp │  │  thin bridge │  │   platform APIs    │    │
│  └──────────────┘  └──────────────┘  └────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

### Why JS exists in the WASM build

Browsers expose GPU access exclusively through WebGL/WebGPU JavaScript APIs. The .NET WASM runtime cannot P/Invoke into native OpenGL — there is no native GL driver in a browser sandbox. So the WASM backend uses a thin JS adapter that:

1. Creates a WebGL context on a `<canvas>` element
2. Exposes `init()`, `render(mvp)`, `dispose()` to C# via JS interop
3. Forwards input events (touch, mouse, wheel) back to C#

On desktop, this JS layer does not exist. Silk.NET.OpenGL calls the system's native OpenGL driver directly via P/Invoke. The engine itself never touches JS — only the `WebGLRenderBackend` adapter does.

---

## 2. The Render Backend Abstraction

The critical boundary that enables cross-platform is `IRenderBackend`:

```csharp
public interface IRenderBackend : IDisposable
{
    bool Initialize();
    void Render(float[] mvpColumnMajor);
    void StartLoop(Func<Task> onTick);
    void StopLoop();

    float CanvasWidth { get; }
    float CanvasHeight { get; }
    float AspectRatio => CanvasHeight > 0 ? CanvasWidth / CanvasHeight : 16f / 9f;

    event Action<float, float>? PointerDrag;
    event Action<float>? ScrollWheel;
    event Action? TapStart;
    event Action? ResetRequested;
}
```

### Desktop implementation (Silk.NET.OpenGL)

```csharp
public class SilkNetRenderBackend : IRenderBackend
{
    private IWindow _window;
    private GL _gl;

    public bool Initialize()
    {
        var opts = WindowOptions.Default with { Size = new(1280, 720) };
        _window = Window.Create(opts);
        _window.Load += () => {
            _gl = _window.CreateOpenGL();
            // compile shaders, create buffers — same GL calls as WebGL
            // but via Silk.NET's managed API, no JS involved
        };
        return true;
    }

    public void Render(float[] mvp)
    {
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.UniformMatrix4(mvpLocation, 1, false, mvp);
        _gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedShort, null);
    }
    // ...
}
```

### WASM implementation (WebGL via JS interop)

```csharp
public class WebGLRenderBackend : IRenderBackend
{
    private readonly IJSRuntime _js;

    public void Render(float[] mvp)
    {
        // Single JS interop call per frame — thin bridge to WebGL
        _ = _js.InvokeVoidAsync("WebGLEngine.render", mvp);
    }
    // ...
}
```

Same `IRenderBackend` interface, same `GameEngine` code, different platform wiring.

---

## 3. Material & Shader Pipeline

A production engine needs more than a hardcoded shader. Here's how to build a proper material system.

### 3.1 Shader Program Abstraction

```
┌──────────────────────────────────────────────────┐
│                  ShaderProgram                    │
│                                                  │
│  - vertex source    (GLSL / GLSL ES / SPIR-V)   │
│  - fragment source                               │
│  - uniform metadata  (name → type, location)     │
│  - attribute layout  (position, normal, uv, etc) │
│                                                  │
│  Compile()  →  backend-specific program handle   │
│  Bind()     →  set as active program             │
│  SetUniform(name, value)                         │
└──────────────────────────────────────────────────┘
```

Shaders should be authored in GLSL 300 ES (compatible with both desktop GL 3.3+ and WebGL 2). For WebGL 1 fallback, maintain GLSL 100 variants or use a transpiler.

```glsl
// pbr_vertex.glsl (GLSL 300 es)
#version 300 es
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMatrix;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vTexCoord;

void main() {
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;
    vNormal = normalize(uNormalMatrix * aNormal);
    vTexCoord = aTexCoord;
    gl_Position = uProjection * uView * worldPos;
}
```

### 3.2 Material Definition

A **Material** binds a **ShaderProgram** to a set of parameter values (uniforms, textures, render state). Think of the shader as the *template*, the material as the *instance*.

```
┌───────────────────────────────────────────┐
│                 Material                   │
│                                           │
│  ShaderProgram reference                  │
│                                           │
│  Parameters:                              │
│    albedoColor:   vec4(1, 0.2, 0.2, 1)   │
│    metallic:      float 0.8              │
│    roughness:     float 0.3              │
│    albedoMap:     Texture2D ref           │
│    normalMap:     Texture2D ref           │
│                                           │
│  Render State:                            │
│    blendMode:     Opaque | Alpha | Add    │
│    cullFace:      Back | Front | None     │
│    depthWrite:    true                    │
│    depthTest:     Less                    │
│                                           │
│  Apply() → binds shader, uploads uniforms,│
│            binds textures, sets GL state   │
└───────────────────────────────────────────┘
```

In C#:

```csharp
public class Material
{
    public ShaderProgram Shader { get; set; }
    public Dictionary<string, object> Parameters { get; } = new();
    public BlendMode Blend { get; set; } = BlendMode.Opaque;
    public CullMode Cull { get; set; } = CullMode.Back;
    public bool DepthWrite { get; set; } = true;

    public void Apply(IRenderBackend backend)
    {
        Shader.Bind(backend);
        foreach (var (name, value) in Parameters)
        {
            switch (value)
            {
                case float f:   Shader.SetUniform(name, f); break;
                case Vector3 v: Shader.SetUniform(name, v); break;
                case Vector4 v: Shader.SetUniform(name, v); break;
                case Texture t: t.Bind(backend); Shader.SetUniform(name, t.Unit); break;
                case Matrix4X4<float> m: Shader.SetUniform(name, m); break;
            }
        }
        backend.SetBlendMode(Blend);
        backend.SetCullMode(Cull);
        backend.SetDepthWrite(DepthWrite);
    }
}
```

### 3.3 The Render Pipeline

Each frame, the renderer executes a pipeline of passes:

```
Frame Start
│
├── 1. Shadow Pass (optional)
│   └── Render scene from each light's POV into depth textures
│
├── 2. Geometry / Opaque Pass
│   ├── Sort draw calls by material (minimize state changes)
│   ├── For each material group:
│   │   ├── material.Apply(backend)
│   │   └── Draw all meshes using that material
│   └── Output: color buffer + depth buffer
│
├── 3. Transparent Pass
│   ├── Sort back-to-front by distance from camera
│   ├── Enable alpha blending
│   └── Draw transparent objects
│
├── 4. Post-Processing Pass (optional)
│   ├── Read from framebuffer texture
│   ├── Apply screen-space effects (bloom, tone mapping, FXAA)
│   └── Output to screen
│
└── Frame End → swap buffers / present
```

### 3.4 Material Sort Key

To minimize expensive GPU state changes, encode material properties into a sortable 64-bit key:

```
Bits 63-60: Render layer (background, opaque, transparent, overlay)
Bits 59-48: Shader program ID
Bits 47-32: Material ID
Bits 31-0:  Depth (front-to-back for opaque, back-to-front for transparent)
```

Sort all draw commands by this key before submitting. This batches identical materials together, reducing shader switches and texture binds.

---

## 4. Vertex Layout & Mesh Pipeline

### 4.1 Standard Vertex Format

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3D<float> Position;   // 12 bytes
    public Vector3D<float> Normal;     // 12 bytes
    public Vector2D<float> TexCoord;   // 8 bytes
    public Vector4D<float> Tangent;    // 16 bytes (w = handedness)
    public Vector4D<byte>  Color;      // 4 bytes (vertex color, RGBA)
}
// Total: 52 bytes per vertex
```

### 4.2 Mesh

```csharp
public class Mesh
{
    public Vertex[] Vertices { get; set; }
    public uint[] Indices { get; set; }

    // GPU handles (created by backend on upload)
    public uint VertexBufferHandle { get; set; }
    public uint IndexBufferHandle { get; set; }
    public uint VertexArrayHandle { get; set; }

    public void Upload(IRenderBackend backend) { /* ... */ }
    public void Draw(IRenderBackend backend) { /* ... */ }
}
```

---

## 5. Transform & Camera

### 5.1 MVP Matrix Construction

The Model-View-Projection matrix transforms vertices from object space to clip space. Getting this right is critical and platform-dependent.

```
Object Space ──[Model]──► World Space ──[View]──► View Space ──[Projection]──► Clip Space
```

**Silk.NET.Maths uses System.Numerics conventions:**
- Row-major storage (`M_rc` = row r, column c)
- Row-vector multiplication: `v_transformed = v * M`
- Composition left-to-right: `MVP = Model * View * Projection`

**WebGL expects column-major data in `uniformMatrix4fv`:**
- Transpose the row-major matrix to column-major before upload
- The GLSL shader then uses `gl_Position = uMVP * vec4(pos, 1.0)` (column-vector convention)

**Projection z-range mismatch:**

| API | Near/Far clip z-range | Library |
|---|---|---|
| Direct3D / Vulkan | [0, 1] | System.Numerics, Silk.NET.Maths default |
| OpenGL / WebGL | [-1, 1] | Must construct manually |
| WebGPU | [0, 1] | Same as D3D |

For WebGL, you **cannot** use `Matrix4X4.CreatePerspectiveFieldOfView` directly. Build the projection manually:

```csharp
static Matrix4X4<float> PerspectiveOpenGL(float fov, float aspect, float near, float far)
{
    float f = 1.0f / MathF.Tan(fov * 0.5f);
    float nf = 1.0f / (near - far);

    return new Matrix4X4<float>(
        f / aspect, 0,  0,                     0,
        0,          f,  0,                     0,
        0,          0,  (far + near) * nf,     2f * far * near * nf,
        0,          0, -1,                     0
    );
}
```

### 5.2 Camera System

```csharp
public class Camera
{
    public Vector3D<float> Position { get; set; }
    public Vector3D<float> Target { get; set; }
    public Vector3D<float> Up { get; set; } = new(0, 1, 0);

    public float FieldOfView { get; set; } = 60f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 1000f;

    public Matrix4X4<float> GetViewMatrix()
        => Matrix4X4.CreateLookAt(Position, Target, Up);

    public Matrix4X4<float> GetProjectionMatrix(float aspect)
        => PerspectiveOpenGL(
            Scalar.DegreesToRadians(FieldOfView), aspect, NearPlane, FarPlane);
}
```

---

## 6. Putting It Together: Frame Walk-Through

Here's what happens in a single frame of the spinning cube demo:

```
1. requestAnimationFrame fires (browser) / vsync fires (desktop)
       │
2. Backend calls engine.Tick()
       │
3. engine.Update()
   ├── Compute delta time
   ├── Apply angular velocity to rotation
   └── Apply damping
       │
4. engine.BuildMvp(aspectRatio)
   ├── Model  = RotationX(θx) * RotationY(θy)     ← Silk.NET.Maths
   ├── View   = LookAt(eye, target, up)            ← Silk.NET.Maths
   ├── Proj   = PerspectiveOpenGL(fov, ar, n, f)   ← custom (z: [-1,1])
   └── MVP    = Model * View * Proj                ← row-major composition
       │
5. ToColumnMajor(mvp) → float[16]
       │
6. backend.Render(mvp)
   ├── Desktop: _gl.UniformMatrix4fv(loc, 1, false, mvp)
   │            _gl.DrawElements(...)
   └── WASM:    JS interop → WebGLEngine.render(mvp)
                             gl.uniformMatrix4fv(loc, false, mvp)
                             gl.drawElements(...)
       │
7. Swap buffers / requestAnimationFrame returns
```

---

## 7. Scaling Up: What Comes Next

### Immediate next steps for this engine:
1. **Texture loading** — load PNG/KTX via Silk.NET.Core, bind to sampler units
2. **Phong/PBR shader** — replace vertex colors with lit materials
3. **Mesh loading** — OBJ/glTF parser, upload to GPU via backend
4. **Scene graph** — parent-child transforms, frustum culling
5. **Multiple draw calls** — render queue sorted by material key

### Medium-term:
6. **Shadow mapping** — depth-only pass from light POV
7. **Instanced rendering** — draw thousands of objects in one call
8. **Skinned animation** — bone matrices uploaded as uniform arrays
9. **Render-to-texture** — framebuffer objects for post-processing
10. **WebGPU backend** — next-gen browser GPU API (z: [0,1], compute shaders)

### Long-term / production:
11. **ECS (Entity Component System)** — data-oriented entity management
12. **Asset pipeline** — offline baking, compression, streaming
13. **Compute shaders** — particle systems, GPU culling (WebGPU / GL 4.3+)
14. **Multi-threaded rendering** — command buffer recording on worker threads
15. **Editor** — scene inspector, live shader editing, profiler overlay

---

## 8. Project Structure (Target)

```
rts-engine/
├── src/
│   ├── RtsEngine.Core/            ← Shared engine (zero platform deps)
│   │   ├── Engine/
│   │   │   ├── GameEngine.cs
│   │   │   ├── Camera.cs
│   │   │   ├── Transform.cs
│   │   │   └── Scene.cs
│   │   ├── Rendering/
│   │   │   ├── IRenderBackend.cs
│   │   │   ├── Material.cs
│   │   │   ├── ShaderProgram.cs
│   │   │   ├── Mesh.cs
│   │   │   ├── Texture.cs
│   │   │   └── RenderQueue.cs
│   │   ├── Math/                  ← Silk.NET.Maths extensions
│   │   │   └── MvpHelper.cs
│   │   └── Input/
│   │       └── InputState.cs
│   │
│   ├── RtsEngine.Desktop/        ← Desktop host
│   │   ├── SilkNetRenderBackend.cs
│   │   ├── SilkNetInputBackend.cs
│   │   └── Program.cs
│   │
│   └── RtsEngine.Wasm/           ← WASM host (current)
│       ├── WebGLRenderBackend.cs
│       ├── Pages/Home.razor
│       └── wwwroot/
│           └── js/webgl-engine.js  ← only JS in the whole engine
│
├── shaders/
│   ├── basic_vertex.glsl
│   ├── basic_frag.glsl
│   ├── pbr_vertex.glsl
│   └── pbr_frag.glsl
│
├── .github/workflows/deploy.yml
├── engine.md                      ← this file
└── README.md
```

The key principle: **everything above the `Platform Backends` line is pure C#**. Silk.NET.Maths, Silk.NET.OpenGL types (for desktop), and the `IRenderBackend` interface are the only external dependencies in the core. The JS file exists solely because browsers don't expose a native GL driver — it's an adapter, not engine code.
