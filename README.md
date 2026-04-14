# RTS Engine - Silk.NET WASM Prototype

A WebAssembly game engine prototype built with **Silk.NET** and **Blazor WebAssembly**, featuring a real-time spinning cube rendered via WebGL.

## Demo

Deployed automatically to GitHub Pages on push.

### Controls

| Input | Action |
|---|---|
| **Drag** (mouse / touch) | Change spin direction and velocity |
| **Scroll wheel** | Boost or reduce spin speed |
| **Tap** (touch) | Impulse — adds burst of spin energy |
| **Double-click / double-tap** | Reset cube to default state |

## Tech Stack

- **.NET 8** Blazor WebAssembly (standalone)
- **Silk.NET.Maths** — matrix/vector math (perspective projection, rotation, MVP)
- **WebGL** — GPU-accelerated rendering via JS interop
- **GitHub Actions** — CI/CD pipeline: build, publish, deploy to GitHub Pages

## Architecture

```
C# (Blazor WASM)                    JavaScript
┌──────────────────┐                ┌──────────────────┐
│  GameEngine.cs   │  ──MVP mat──►  │  webgl-engine.js │
│  - Silk.NET math │                │  - WebGL context  │
│  - rotation/vel  │  ◄──input───  │  - shader program │
│  - game loop     │                │  - input events   │
└──────────────────┘                └──────────────────┘
```

## Building Locally

```bash
# Requires .NET 8 SDK + wasm-tools workload
dotnet workload install wasm-tools
dotnet build src/RtsEngine.Wasm/RtsEngine.Wasm.csproj
dotnet run --project src/RtsEngine.Wasm
```

## Deploying

Push to `main` or any `claude/*` branch to trigger the GitHub Actions workflow, which builds and deploys to GitHub Pages automatically.
