# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WebAssembly RTS game engine prototype in C# (.NET 8) using the **sokol pattern** — a platform-agnostic abstraction layer separating rendering (IGPU) from app lifecycle (IRenderBackend). Features a planet terrain editor with Goldberg sphere geometry, textured terrain + water shaders, atmospheric scattering, and a solar system navigation view.

## Build & Run Commands

```bash
# Prerequisites: .NET 8 SDK
dotnet workload install wasm-tools    # one-time setup for WASM target

# Build entire solution
dotnet build RtsEngine.sln

# Run WASM (dev server at http://localhost:5266)
dotnet run --project src/RtsEngine.Wasm

# Run Desktop (native window with OpenGL)
dotnet run --project src/RtsEngine.Desktop

# Publish WASM for release
dotnet publish src/RtsEngine.Wasm/RtsEngine.Wasm.csproj -c Release -o release --nologo

# Generate planet textures (requires Python 3 + Pillow + numpy)
python3 tools/gen_terrain_textures.py   # Earth textures
python3 tools/gen_planet_textures.py    # Moon, Mars, Venus, Ice textures
```

No test framework or linter is configured yet.

## Architecture

```
RtsEngine.Game (platform-agnostic)
├── GameEngine          — tick loop, mode switching, camera, picking, input
├── PlanetMesh          — Goldberg sphere heightmap (icosphere dual), 20-patch chunked
├── PlanetRenderer      — textured terrain + atmosphere + outline (multi-pass)
├── SolarSystemRenderer — sun + orbit rings + planet spheres
├── StarMapRenderer     — hierarchical galaxy/cluster/group star map
├── EngineUI            — screen-space GPU-rendered buttons (no HTML)
├── PlanetConfig        — YAML-driven planet definition (YamlDotNet)
├── GalaxyData          — procedural galaxy hierarchy
├── SolarSystemData     — orbital bodies with positions
└── Noise3D             — gradient noise for terrain generation

RtsEngine.Core (interfaces only)
├── IGPU                — GPU abstraction (buffers, pipelines, textures, samplers, render)
├── IRenderBackend      — app shell (canvas, frame loop, input: drag/click/scroll/move/key)
├── IRenderer           — Draw(float[] mvp)
└── MatrixHelper        — row-major → raw floats for GPU upload

RtsEngine.Wasm (Blazor WebAssembly host — COMPILE LAYER ONLY)
├── WebGPU : IGPU       — JS interop → gpu-proxy.js
├── WebGLRenderBackend  — JS interop → app-shell.js
├── Home.razor          — bootstrap only, NO game UI (UI lives in GameEngine/EngineUI)
└── wwwroot/
    ├── shaders/        — terrain.wgsl, atmosphere.wgsl, starmap.wgsl, outline.wgsl, ui.wgsl
    ├── textures/       — terrain_atlas.png, water_dudv.png, water_normal.png
    ├── textures/{moon,mars,venus,ice}/ — per-planet texture sets
    ├── planets/        — earth.yaml, moon.yaml, mars.yaml, venus.yaml, ice.yaml
    └── js/             — gpu-proxy.js, app-shell.js

RtsEngine.Desktop (Silk.NET OpenGL host)
└── OpenGLGPU : IGPU    — native GL calls (texture support stubbed)
```

## Critical Conventions

- **Blazor is a compile/host layer only.** All game logic, UI, mode switching, and rendering lives in RtsEngine.Game. Home.razor bootstraps the engine and handles planet hot-swap loading. No game UI in HTML/Blazor markup.
- **Matrix math:** Silk.NET.Maths row-major, row-vector multiplication. `MVP = View * Proj`. `MatrixHelper.ToRawFloats` extracts row-major; WGSL interprets as column-major (auto-transpose).
- **Projection z-range:** WebGPU [0,1], OpenGL [-1,1].
- **GPU abstraction:** IGPU uses integer handles. WASM → gpu-proxy.js handle tables. Desktop → GL calls.
- **Shaders:** WGSL for WebGPU (wwwroot/shaders/), GLSL embedded in Desktop Program.cs.
- **textureSampleLevel only:** Never use `textureSample` in shaders — it requires uniform control flow which breaks on mobile GPUs. Always use `textureSampleLevel(tex, samp, uv, 0.0)`.
- **Texture creation:** `copyExternalImageToTexture` requires `TEXTURE_BINDING | COPY_DST | RENDER_ATTACHMENT` usage flags (Dawn/Chrome requirement).
- **Index buffers:** Use `CreateIndexBuffer32(uint[])` for meshes that may exceed 65535 vertices (planet terrain). Use `CreateIndexBuffer(ushort[])` for small meshes (atmosphere, UI).
- **Planet config:** YAML files in wwwroot/planets/ drive all planet parameters (radius, subdivisions, textures, atmosphere, noise). New planet = new YAML + texture set, no code changes.
- **20-patch chunked rebuild:** Planet mesh is split into 20 icosahedron patches with independent VBO/IBO. Edits only rebuild affected patches (~5-15% of mesh).

## Config-Driven Design (NO HARDCODED MAGIC NUMBERS)

**Every game-design or visual parameter that a user would want to tweak MUST live in a YAML config file.** Never hardcode values like camera distances, LOD thresholds, transition speeds, sphere segment counts, lighting intensities, or color values directly in C# code. Instead:

1. **Planet config** (`wwwroot/planets/<name>.yaml`): radius, subdivisions, stepHeight, terrain levels, atmosphere, noise, textures, zoom min/max
2. **Solar system config** (`wwwroot/config/solarsystem.yaml`): sun properties, orbital bodies, display radii, orbit distances/speeds
3. **Engine config** (`wwwroot/config/engine.yaml`): camera defaults, transition speed, LOD distance thresholds, lighting params, sphere segment counts

Config files are loaded at startup via `HttpClient.GetStringAsync` (WASM) or `File.ReadAllText` (Desktop), parsed by `YamlDotNet`. All config classes live in `RtsEngine.Game` with `FromYaml()` static factory methods.

**When adding new features:** if a value affects visuals, gameplay feel, or anything a designer would tweak — put it in config, not code. The code should read config values and apply them. The only constants allowed in code are mathematical ones (π, conversion factors) and structural ones (patch count = 20, vertex stride).

## Editor Modes

- **SolarSystem:** Sun + planets + orbit rings. Click planet → load + zoom to planet edit.
- **PlanetEdit:** Goldberg sphere heightmap editor. Left-click raise, right-click lower. Hex outline highlight. In-engine "Back" button returns to solar system.
- **StarMap:** Hierarchical galaxy navigation (galaxy → sector → cluster → group → star).

## CI/CD

GitHub Actions (`.github/workflows/deploy.yml`) triggers on any push:
- Builds + deploys WASM to GitHub Pages
- Builds desktop binaries for Linux/Windows/macOS (artifacts)

## Dependencies

- `Silk.NET.Maths v2.21.0` — all projects
- `Silk.NET v2.21.0` — desktop only (OpenGL, requires `AllowUnsafeBlocks`)
- `YamlDotNet v15.1.6` — Game project (planet config parsing)
- `Microsoft.AspNetCore.Components.WebAssembly v8.0.25` — WASM host
- `Pillow + numpy` — texture generation scripts (Python, not a runtime dep)
