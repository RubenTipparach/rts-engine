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

---

## 2. Terrain System

The terrain is the foundation of every map. Like WC3, terrain is a grid-based heightmap with painted textures, cliff levels, water planes, and decorative doodads.

### 2.1 Terrain Grid

The map is a 2D grid of **terrain cells**. Each cell stores:

```
TerrainCell {
    height: float           // Elevation at this grid point (vertex height)
    cliffLevel: int         // Discrete cliff tier (0 = ground, 1 = raised, 2 = high ground, ...)
    textureLayerBase: int   // Index into terrain texture palette (grass, dirt, stone, ...)
    textureLayerOverlay: int // Optional secondary texture (road, crops, snow-dusted, ...)
    overlayBlend: float     // 0.0–1.0 blend factor for overlay texture
    waterLevel: float       // Water surface height at this cell (-1 = no water)
    passability: flags      // Bitmask: walkable, flyable, buildable, amphibious
    rampDirection: enum     // None, North, South, East, West (connects cliff levels)
}
```

**Grid dimensions** are defined at map creation (e.g., 64x64, 128x128, 256x256). Like WC3, each cell typically represents a fixed world-space size (e.g., 128 game units per cell). Heights are interpolated between grid vertices for smooth terrain.

### 2.2 Cliff System

Cliffs create discrete elevation changes — critical for RTS gameplay (high ground advantage, line-of-sight blocking, chokepoints).

```
Cliff Levels:

    Level 2  ┌──────────┐
             │  HIGH    │
    Level 1  ├──────────┤──────────┐
             │  MID     │  RAMP ↗  │
    Level 0  ├──────────┼──────────┤
             │  GROUND  │  GROUND  │
             └──────────┴──────────┘
```

**Editor behavior:**
- Raising/lowering terrain adjusts `cliffLevel` in discrete steps
- Cliff geometry is auto-generated from level transitions between adjacent cells
- Ramps are placed explicitly by the editor user to connect two cliff levels
- Cliff tile art is selected based on surrounding cliff topology (convex corner, concave corner, straight edge)

**Runtime behavior:**
- Pathfinding treats cliff edges as impassable unless a ramp connects them
- Units on higher cliff levels gain vision/range bonuses (configurable per-game)
- Line-of-sight raycasts check cliff level transitions

### 2.3 Texture Painting

The editor provides a **texture palette** — a set of terrain textures (grass, dirt, cobblestone, snow, etc.). Painting works per-cell with blend support:

- **Base layer:** Every cell has exactly one base texture
- **Overlay layer:** Optional second texture blended on top (for paths, scorched earth, etc.)
- **Blend factor:** Controls how much overlay shows through (0 = pure base, 1 = pure overlay)
- **Auto-tiling:** Transitions between different base textures use blended edge tiles

The texture palette is defined per-tileset (e.g., "Lordaeron Summer", "Northrend", "Ashenvale"). A map uses exactly one tileset.

### 2.4 Water

Water is a **plane** at a configurable height per cell. Cells with `waterLevel >= 0` render water.

**Properties (per-map, not per-cell):**
- `waterColor: vec4` — tint color
- `waterOpacity: float` — transparency
- `waveAmplitude: float` — vertex displacement strength
- `waveSpeed: float` — animation speed
- `shoreLineTexture: string` — foam/shore effect

**Editor:** Water is painted like terrain — a water brush raises/lowers the water level in cells. Shallow water vs. deep water is determined by depth relative to terrain height (configurable threshold).

**Gameplay impact:** Cells where `waterLevel - height > deepThreshold` are impassable to ground units but passable to amphibious/naval units.

### 2.5 Doodads (Terrain Decorations)

Doodads are non-interactive decorative objects placed on the terrain: trees, rocks, bushes, ruins, campfires, fences, etc.

```
Doodad {
    typeId: string           // Reference to doodad definition (e.g., "tree_lordaeron_01")
    position: vec3           // World position (snapped to terrain height by editor)
    rotation: float          // Y-axis rotation in degrees
    scale: float             // Uniform scale multiplier
    variation: int           // Visual variant index (same type, different model)
    isDestructible: bool     // Can be destroyed (trees for lumber, rocks blocking paths)
    hitPoints: int           // HP if destructible (0 = invulnerable)
    pathingTexture: string   // Reference to a pathing map that blocks movement around this doodad
}
```

**Destructible doodads** (like trees) interact with gameplay — harvesting lumber, clearing paths. Their state is synced in multiplayer.

### 2.6 Terrain Data Format

```json
{
  "terrain": {
    "gridWidth": 128,
    "gridHeight": 128,
    "cellSize": 128.0,
    "tileset": "lordaeron_summer",
    "texturePalette": [
      { "id": 0, "name": "grass", "asset": "textures/terrain/grass_01.png" },
      { "id": 1, "name": "dirt", "asset": "textures/terrain/dirt_01.png" },
      { "id": 2, "name": "stone", "asset": "textures/terrain/stone_01.png" },
      { "id": 3, "name": "road", "asset": "textures/terrain/road_01.png" }
    ],
    "heightMap": [ /* 129x129 float array (vertices = cells+1 in each dimension) */ ],
    "cells": [
      {
        "x": 0, "y": 0,
        "cliffLevel": 0,
        "baseTexture": 0,
        "overlayTexture": -1,
        "overlayBlend": 0.0,
        "waterLevel": -1.0,
        "passability": "walkable|buildable"
      }
    ],
    "doodads": [
      {
        "typeId": "tree_lordaeron_01",
        "position": [1024.0, 0.0, 512.0],
        "rotation": 45.0,
        "scale": 1.0,
        "variation": 0,
        "destructible": true,
        "hitPoints": 50
      }
    ]
  }
}
```

### 2.7 Regions

Regions are named areas on the map used by triggers and gameplay logic (spawn zones, camera bounds, detection areas, item drop zones).

```
Region {
    name: string             // Human-readable name (e.g., "player1_start", "boss_arena")
    shape: enum              // Rectangle or Polygon
    bounds: rect | vec2[]    // Axis-aligned rect or polygon vertices
    color: vec4              // Editor-only display color
    weatherEffect: string    // Optional weather in this region (rain, snow, fog)
    ambientSound: string     // Optional ambient sound loop
}
```

Regions are **editor-only geometry** — they have no visual representation in-game but are essential for trigger scripting ("when unit enters region X, do Y").

