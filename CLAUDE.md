# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WebAssembly RTS game engine prototype in C# (.NET 8) using the **sokol pattern** — a platform-agnostic abstraction layer separating rendering (IGPU) from app lifecycle (IRenderBackend). Currently renders an interactive spinning cube via WebGPU (WASM) and OpenGL 3.3+ (Desktop).

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
```

No test framework or linter is configured yet.

## Architecture

```
Game Logic (RtsEngine.Game)     — platform-agnostic game loop, renderers
Engine Core (RtsEngine.Core)    — IGPU, IRenderBackend, IRenderer interfaces
    ├── WASM Backend            — WebGPU via JS interop (gpu-proxy.js, app-shell.js)
    └── Desktop Backend         — OpenGL via Silk.NET P/Invoke
```

**Key interfaces in RtsEngine.Core:**
- `IGPU` — GPU abstraction (create buffers, pipelines, bind groups, render)
- `IRenderBackend` — App shell (canvas size, frame loop, input events)
- `IRenderer` — Draw call contract (Init, Render, Resize)

**Dependency rule:** Game and Core have zero platform awareness. All platform-specific code lives in the host projects (Wasm/Desktop).

## Critical Conventions

- **Matrix math:** Silk.NET.Maths uses row-major storage with row-vector multiplication. Composition is left-to-right: `MVP = Model * View * Projection`. Transpose to column-major before GPU upload.
- **Projection z-range:** WebGPU uses [0,1] (same as D3D/Vulkan), OpenGL uses [-1,1]. The `MatrixHelper` in Core handles this.
- **GPU abstraction:** IGPU uses integer handles for all GPU resources. WASM backend translates handles to JS objects via `gpu-proxy.js` handle tables. Desktop backend maps to native GL calls.
- **Shaders:** WGSL for WebGPU (`wwwroot/shaders/`), GLSL for Desktop.

## CI/CD

GitHub Actions workflow (`.github/workflows/deploy.yml`) triggers on any push:
- Builds + deploys WASM to GitHub Pages
- Builds desktop binaries for Linux/Windows/macOS (uploaded as artifacts)

## Dependencies

- `Silk.NET.Maths v2.21.0` — all projects (math types)
- `Silk.NET v2.21.0` — desktop only (OpenGL bindings, requires `AllowUnsafeBlocks`)
- `Microsoft.AspNetCore.Components.WebAssembly v8.0.25` — WASM host
