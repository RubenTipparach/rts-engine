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

**Hard rule: unless a value is an exact constant that will never be changed or modified, do not hardcode magic numbers in code. Always add the value to a config file somewhere.** This applies to every camera distance, threshold, blend percentage, LOD knob, transition duration, scroll factor, lerp rate, fade band, sphere segment count, lighting intensity, color, FOV, near/far plane, drag sensitivity, density, density factor, decay rate — anything a designer or future Claude run might want to tune.

Config homes:

1. **Planet config** (`wwwroot/planets/<name>.yaml`): radius, subdivisions, stepHeight, terrain levels, atmosphere, noise, textures, zoom min/max
2. **Solar system config** (`wwwroot/config/solarsystem.yaml`): sun properties, orbital bodies, display radii, orbit distances/speeds
3. **Engine config** (`wwwroot/config/engine.yaml`): camera defaults, transition speed, LOD distance thresholds, lighting params, sphere segment counts, RTS camera tuning, slope density
4. **RTS gameplay config** (`wwwroot/config/rts.yaml`): buildings + units, speeds, sizes, colors, hop capability

Config files are loaded at startup via `HttpClient.GetStringAsync` (WASM) or `File.ReadAllText` (Desktop), parsed by `YamlDotNet`. All config classes live in `RtsEngine.Game` with `FromYaml()` static factory methods.

**Allowed exceptions** (everything else goes in config):
- Mathematical constants — π, e, conversion factors (deg→rad), small epsilons used purely for numerical safety (1e-6 etc).
- Structural invariants that aren't going to change without a code rewrite — icosahedron patch count = 20, vertex stride floats per-shader, the fact that hex cells have 6 sides.
- Format-level constants — PNG signature bytes, .obj line prefixes, shader binding indices.

**When in doubt, put it in config.** A duplicate config knob that turns out to never need tuning is cheap to delete; a hardcoded number that turns out to need tuning means hunting through C# at the wrong moment.

## Editor Modes

- **SolarSystem:** Sun + planets + orbit rings. Click planet → load + zoom to planet edit.
- **PlanetEdit:** Goldberg sphere heightmap editor. Left-click raise, right-click lower. Hex outline highlight. In-engine "Back" button returns to solar system. Zoom in close to ground level transitions the orbit camera into a tilted RTS view (see `engine.yaml` `rtsCamera:` block).
- **StarMap:** Hierarchical galaxy navigation (galaxy → sector → cluster → group → star).

## Static game asset guidelines

These rules apply to every asset Claude generates, hand-edits, or asks the user to drop into the repo. Keep them in mind whenever a task involves "generate a model / texture / animation".

- **Textures** — if the task involves generating textures, the result must be baked to PNG (8-bit RGBA, ≤32×32 unless the existing pipeline allows otherwise) and committed under `assets/textures/`. The Makefile runs `mksprite` to convert each PNG to a `.sprite` at build time. Do not commit the `.sprite` outputs — only the source PNG.
- **3D models** — if the task involves generating 3D models, the canonical on-disk format is a baked `.obj` file under `assets/models/`. A matching JSON sidecar may also be written for the in-browser model editor, but the `.obj` is the source of truth that downstream tools (and Claude on later runs) read first. Generators live under `tools/` and write both.
- **Animations** — if the task involves animation (walk cycles, machinery motion, scripted camera moves, anything that sweeps a transform over time), the keyframes must be baked to an animation file under `assets/animations/`. The current pipeline uses simple JSON `*.anim.json` (frames + per-bone transforms) plus an optional baked C header generated by a `tools/gen-*-c.py` script. Procedural per-frame tweaks layered on top of the baked clip are fine; the base motion must come from the file.
- **Generation scripts** go under `tools/`. Each generator follows the pattern of `tools/gen-textures.py` / `tools/gen-character.py` / `tools/gen-character-c.py`: a Python entry point that writes out the baked artifact deterministically, with no third-party deps beyond what the existing scripts already use (stdlib + zlib).

## Post-commit links

After each commit, always show these links to the user:

- Play the games: https://rubentipparach.github.io/rts-engine/
- Track CI builds: https://github.com/RubenTipparach/rts-engine/actions

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
