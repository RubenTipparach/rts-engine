# Editor-First RTS Engine Design Document

> Inspired by Warcraft III's World Editor — the editor is the engine's primary authoring surface.
> Every gameplay element (terrain, units, triggers, cutscenes) is defined as data in the editor
> and interpreted by the runtime. The engine exists to play what the editor creates.

---

## Table of Contents

1. [Editor-First Philosophy](#1-editor-first-philosophy)
2. [Terrain System](#2-terrain-system)
3. [Units, Buildings & Entities](#3-units-buildings--entities)
4. [Animation System](#4-animation-system)
5. [Campaign, Missions & Triggers](#5-campaign-missions--triggers)
6. [Cutscenes & Briefing Screens](#6-cutscenes--briefing-screens)
7. [Multiplayer Architecture](#7-multiplayer-architecture)
8. [Editor UI & Workflow](#8-editor-ui--workflow)
9. [Data Formats & Asset Pipeline](#9-data-formats--asset-pipeline)
10. [LLM Integration Points](#10-llm-integration-points)

---

## 1. Editor-First Philosophy

### 1.1 What "Editor-First" Means

The engine is **not** a code library that you program games with. It is a **data-driven runtime** that loads maps, entity definitions, trigger scripts, and cutscene sequences — all authored through a visual editor. This mirrors how Warcraft III works: Blizzard shipped the World Editor alongside the game, and campaigns/custom games are `.w3x` map files containing terrain, objects, triggers, and scripts.

**Core principle:** If a human needs to decide it (unit stats, spawn points, camera angles, dialog text, victory conditions), it lives in the editor. If the engine can compute it (pathfinding, physics, rendering), it lives in code.

### 1.2 Why Editor-First for LLM-Assisted Development

An editor-first architecture creates a clean contract between human authoring and LLM implementation:

| Concern | Owner | Format |
|---|---|---|
| Map layout, terrain painting | Human (editor) | Serialized map data |
| Unit/building stats & abilities | Human (editor) | Entity definition files |
| Mission triggers & objectives | Human (editor) | Trigger graphs / scripts |
| Cutscene choreography | Human (editor) | Timeline data |
| Dialog & briefing text | Human (editor) | Structured text files |
| Rendering, pathfinding, AI | LLM + engineer (code) | C# engine code |
| Networking, state sync | LLM + engineer (code) | C# engine code |
| Physics, collision | LLM + engineer (code) | C# engine code |

The editor produces **well-structured data files** that serve as unambiguous specs for the engine runtime. An LLM can read a trigger definition and implement the engine systems that interpret it, without needing to understand artistic intent.

### 1.3 Architecture Layers

```
┌─────────────────────────────────────────────────────────────────┐
│                        EDITOR (Authoring)                       │
│  Terrain Painter │ Entity Placer │ Trigger Editor │ Cutscene    │
│  Briefing Editor │ Object Editor │ Sound Manager  │ Timeline    │
├─────────────────────────────────────────────────────────────────┤
│                     DATA LAYER (Serialized)                     │
│  .map files │ .entity defs │ .trigger graphs │ .cutscene seqs  │
│  All data is JSON/binary — no compiled code in map files        │
├─────────────────────────────────────────────────────────────────┤
│                     ENGINE RUNTIME (Execution)                  │
│  Map Loader │ Entity System │ Trigger Interpreter │ Cutscene    │
│  Renderer   │ Pathfinding   │ Combat System       │ Player      │
│  Audio      │ Networking    │ AI / Behaviors      │ Camera Sys  │
├─────────────────────────────────────────────────────────────────┤
│                     PLATFORM LAYER (Existing)                   │
│  IGPU (WebGPU/OpenGL) │ IRenderBackend │ Input │ Audio Backend  │
└─────────────────────────────────────────────────────────────────┘
```

### 1.4 Map File as the Unit of Content

Everything lives inside a **map file** — a single archive containing:

```
my_map.rtsmap/
├── terrain.json          # Heightmap, textures, water, cliffs
├── entities.json         # Placed units, buildings, doodads with transforms
├── regions.json          # Named rectangular/polygonal areas
├── cameras.json          # Named camera positions and paths
├── triggers.json         # Event → condition → action graphs
├── cutscenes.json        # Timeline sequences
├── briefing.json         # Pre-mission briefing screens
├── strings.json          # All localizable text
├── player_setup.json     # Player slots, teams, colors, races
├── sounds.json           # Sound event references
└── metadata.json         # Map name, author, description, bounds
```

This is analogous to WC3's `.w3x` containing `.w3e` (terrain), `.w3u` (units), `.w3t` (triggers), etc.

