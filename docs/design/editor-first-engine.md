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

---

## 3. Units, Buildings & Entities

All gameplay objects — units, buildings, heroes, items, projectiles — share a common **entity** model. Definitions are authored in the editor's **Object Editor** (like WC3's Object Editor), and instances are placed on maps.

### 3.1 Entity Definition vs. Entity Instance

**Definition** (template, like WC3's "Footman" base type):
- Lives in a global object database, shared across all maps
- Defines the archetype: stats, abilities, model, sounds, icon
- Can inherit from another definition and override fields

**Instance** (placed on a map, like a specific Footman at position X,Y):
- References a definition by ID
- Stores per-instance overrides: position, rotation, player owner, custom name
- Can override any definition field (e.g., a "Veteran Footman" with +10 HP)

This two-tier system lets the editor define archetypes once and stamp instances across maps, while still allowing per-instance customization.

### 3.2 Entity Categories

```
Entity
├── Unit                    # Mobile combat/worker entities
│   ├── Worker              # Can gather resources, build structures
│   ├── MeleeUnit           # Ground melee combatant
│   ├── RangedUnit          # Ground ranged combatant
│   ├── SiegeUnit           # Anti-structure specialist
│   ├── AirUnit             # Flying unit
│   ├── NavalUnit           # Water-bound unit
│   └── Hero                # Unique leveling unit with inventory
├── Building                # Stationary structures
│   ├── ProductionBuilding  # Trains units (barracks, stable)
│   ├── ResourceBuilding    # Resource drop-off or generation (town hall, mine)
│   ├── DefenseBuilding     # Towers, walls
│   ├── ResearchBuilding    # Unlocks upgrades
│   └── SpecialBuilding     # Altars, shops, faction-specific
├── Item                    # Pickupable objects (potions, equipment, powerups)
├── Destructible            # Breakable terrain objects (trees, gates, barrels)
└── Projectile              # Missiles, arrows, spell effects (spawned by combat)
```

### 3.3 Unit Definition

```
UnitDefinition {
    // Identity
    id: string                      // Unique ID (e.g., "human_footman")
    name: string                    // Display name ("Footman")
    parentId: string?               // Inherits from this definition
    category: UnitCategory          // Melee, Ranged, Worker, Hero, Air, etc.
    race: string                    // "human", "orc", "undead", "nightelf", "neutral"

    // Combat Stats
    hitPoints: int                  // Maximum HP
    mana: int                       // Maximum mana (0 = no mana)
    armor: int                      // Damage reduction
    armorType: enum                 // Light, Medium, Heavy, Fortified, Hero, Unarmored
    damage: int[]                   // [min, max] per attack
    attackType: enum                // Normal, Pierce, Siege, Magic, Chaos, Hero
    attackSpeed: float              // Seconds between attacks
    attackRange: float              // 0 = melee, >0 = ranged
    numberOfAttacks: int            // Usually 1, some units have 2 attacks

    // Movement
    moveSpeed: float                // Units per second
    moveType: enum                  // Foot, Horse, Fly, Float, Amphibious, Hover
    turnRate: float                 // Radians per second
    collisionRadius: float          // Pathing circle size

    // Production
    goldCost: int
    lumberCost: int
    supplyCost: int                 // Population consumed
    buildTime: float                // Seconds to train/build
    hotkey: string                  // Keyboard shortcut in production menu

    // Abilities
    abilities: string[]             // List of ability definition IDs
    defaultOrders: string[]         // Auto-cast abilities

    // Visuals (references to assets, not the assets themselves)
    model: string                   // Path to 3D model asset
    icon: string                    // Path to portrait icon
    portrait: string                // Path to animated portrait model
    selectionScale: float           // Size of selection circle
    shadowTexture: string           // Blob shadow asset
    tintColor: vec4?                // Optional team-color tint

    // Audio
    soundSet: string                // References a sound set (attack, death, ready, pissed, etc.)

    // Flags
    isHero: bool
    canAttack: bool
    canMove: bool
    canBuild: bool
    isInvulnerable: bool
    isDetector: bool                // Can see invisible units
    isBurrowed: bool                // Starts burrowed
    reviveTime: float               // Hero revive timer (0 = cannot revive)
}
```

### 3.4 Building Definition

Buildings share most fields with units but add:

```
BuildingDefinition extends UnitDefinition {
    moveType: None                  // Buildings don't move

    // Building-specific
    trainList: string[]             // Unit IDs this building can produce
    researchList: string[]          // Upgrade IDs this building can research
    buildingFootprint: int[2]       // Grid cells occupied [width, height] (e.g., [3,3])
    pathingMap: string              // Asset that defines which cells are blocked
    constructionModel: string       // Model shown during build phase
    constructionTime: float         // Seconds to construct
    sellValue: float                // Gold refund ratio when destroyed/sold (0.5 = 50%)

    // Garrison
    garrisonCapacity: int           // Number of units that can enter (0 = cannot garrison)
    garrisonHealRate: float         // HP/sec healed while garrisoned

    // Adjacency bonuses (like lumber mill bonus)
    adjacencyBonus: {
        affectsType: string         // What entity type benefits
        bonusStat: string           // Which stat is boosted
        bonusValue: float           // Amount of boost
        range: int                  // How many cells away
    }?
}
```

### 3.5 Hero System

Heroes are special units that gain experience, level up, and carry items:

```
HeroDefinition extends UnitDefinition {
    isHero: true

    // Leveling
    startingLevel: int              // Usually 1
    maxLevel: int                   // Usually 10
    experienceTable: int[]          // XP thresholds per level
    statGainPerLevel: {
        hitPoints: int              // HP gained per level
        mana: int                   // Mana gained per level
        damage: int                 // Damage gained per level
        armor: float                // Armor gained per level
        strength: int               // Primary attribute gain
        agility: int
        intelligence: int
    }

    // Abilities (heroes learn abilities at specific levels)
    heroAbilities: [
        { abilityId: string, learnLevel: int, maxRank: int }
    ]

    // Inventory
    inventorySlots: int             // Usually 6
    canPickupItems: bool            // true
    canUseItems: bool               // true

    // Attribute system (WC3-style)
    primaryAttribute: enum          // Strength, Agility, Intelligence
    baseStrength: int
    baseAgility: int
    baseIntelligence: int
}
```

### 3.6 Abilities

Abilities are defined separately and referenced by entities:

```
AbilityDefinition {
    id: string                      // "holy_light", "thunder_clap"
    name: string
    icon: string
    hotkey: string
    levels: int                     // Max ability rank

    // Per-level stats
    levelData: [
        {
            manaCost: int
            cooldown: float
            castRange: float
            areaOfEffect: float
            duration: float
            damage: int
            healAmount: int
            // ... ability-specific fields
        }
    ]

    // Targeting
    targetType: enum                // None, Unit, Point, UnitOrPoint, Passive, AutoCast
    allowedTargets: flags           // Self, Ally, Enemy, Ground, Air, Building, Organic, Mechanical
    castAnimation: string           // Animation to play on caster

    // Effects
    effectType: string              // "damage", "heal", "buff", "summon", "teleport", etc.
    projectile: string?             // Projectile entity ID for ranged abilities
    buffId: string?                 // Buff/debuff applied to target
    summonId: string?               // Entity summoned by this ability
}
```

### 3.7 Upgrades & Research

```
UpgradeDefinition {
    id: string                      // "human_armor_upgrade"
    name: string
    icon: string
    levels: int                     // Number of ranks (e.g., 3 for Iron/Steel/Mithril)

    // Per-level costs
    levelData: [
        {
            goldCost: int
            lumberCost: int
            researchTime: float
        }
    ]

    // Effects applied globally to all owned units matching the filter
    effects: [
        {
            affectsCategory: string     // "MeleeUnit", "RangedUnit", "all"
            affectsRace: string         // "human", "all"
            stat: string                // "armor", "damage", "moveSpeed", etc.
            bonus: float                // Amount added per level
        }
    ]

    // Requirements
    requiresBuilding: string[]      // Buildings that must exist to research
    requiresUpgrade: string[]       // Other upgrades that must be completed first
}
```

### 3.8 Items

```
ItemDefinition {
    id: string
    name: string
    icon: string
    model: string                   // World model when dropped on ground
    goldCost: int
    isConsumable: bool              // Destroyed on use (potions) vs permanent (equipment)
    isPowerup: bool                 // Auto-used on pickup (permanent stat boost)
    isDroppedOnDeath: bool

    // Effects
    passiveBonus: {                 // Stats granted while held
        stat: string
        value: float
    }[]
    activeAbility: string?          // Ability ID usable from inventory
    charges: int                    // Number of uses (-1 = unlimited)
    cooldown: float                 // Seconds between uses

    // Shop
    stockMaximum: int               // Max in shop inventory
    stockReplenishInterval: float   // Seconds to restock
}
```

### 3.9 Entity Placement Data (Per-Map)

When entities are placed on a map, the instance data is minimal — just enough to override the definition:

```json
{
  "entities": [
    {
      "instanceId": "unit_001",
      "definitionId": "human_footman",
      "owner": 0,
      "position": [2048.0, 0.0, 1024.0],
      "rotation": 90.0,
      "customFields": {
        "name": "Captain Marcus",
        "hitPoints": 500
      }
    },
    {
      "instanceId": "building_001",
      "definitionId": "human_barracks",
      "owner": 0,
      "position": [1920.0, 0.0, 1920.0],
      "rotation": 0.0
    },
    {
      "instanceId": "item_001",
      "definitionId": "potion_of_healing",
      "owner": -1,
      "position": [3000.0, 0.0, 3000.0]
    }
  ]
}
```

---

## 4. Animation System

Animations are a critical part of the RTS feel — every unit needs to walk, attack, die, stand idle, and respond to abilities. Like WC3, animations are **named sequences** baked into model files, and the engine selects them based on gameplay state.

### 4.1 Animation Concepts

**Skeletal animation:** Models use a bone hierarchy. Each animation is a set of keyframes defining bone transforms over time. The runtime interpolates between keyframes to produce smooth motion.

**Animation sequences** are named clips stored in the model file:

```
Standard Animation Set (per entity model):
├── stand              # Idle stance
├── stand_alternate    # Secondary idle (fidget, look around)
├── walk               # Moving
├── run                # Moving fast (optional, fallback to walk)
├── attack             # Primary attack swing/shoot
├── attack_alternate   # Secondary attack (if entity has two attacks)
├── spell              # Generic spellcasting
├── spell_channel      # Sustained channeled ability
├── death              # Death (plays once, holds last frame for corpse)
├── decay              # Body decomposition (after death)
├── build              # Construction animation (buildings)
├── upgrade            # Building upgrade in progress
├── birth              # Summoned/spawned (plays once)
├── portrait           # 3D portrait idle (for selection UI)
├── morph              # Transformation (e.g., catapult unpack)
├── sleep              # Sleeping / passive state
├── cinematic          # Special animation for cutscenes
└── victory            # Win screen celebration (optional)
```

### 4.2 Animation State Machine

The engine uses a **state machine** to select the current animation based on gameplay:

```
┌──────────┐   move order   ┌──────────┐
│  STAND   │ ──────────────►│   WALK   │
│          │◄────────────── │          │
└────┬─────┘   stop/arrive  └────┬─────┘
     │                           │
     │ attack order              │ attack order
     ▼                           ▼
┌──────────┐               ┌──────────┐
│  ATTACK  │               │  ATTACK  │
│          │──► backswing  │  (WALK)  │──► move between attacks
└────┬─────┘    to STAND   └──────────┘
     │
     │ death
     ▼
┌──────────┐   timer    ┌──────────┐
│  DEATH   │──────────►│  DECAY   │──► remove entity
└──────────┘           └──────────┘
```

**Transitions:**
- Blending between animations uses crossfade (configurable duration, typically 0.1–0.2 seconds)
- Some transitions are instant (death interrupts everything)
- Attack animations have **damage point** and **backswing** timing markers

### 4.3 Animation Events

Animations can fire **events** at specific keyframes to synchronize gameplay with visuals:

```
AnimationEvent {
    frame: int              // Keyframe index (or normalized time 0.0–1.0)
    type: enum              // DamagePoint, Sound, Particle, FootStep, ProjectileLaunch
    data: string            // Event-specific payload
}
```

Key events:
- **DamagePoint:** The frame where a melee attack actually deals damage (not the start of the swing)
- **ProjectileLaunch:** The frame where a ranged unit releases the projectile
- **Sound:** Triggers a sound effect (sword clang, bow twang, spell whoosh)
- **Particle:** Spawns a particle effect at a bone attachment point
- **FootStep:** Triggers footstep sound (varies by terrain texture under foot)

### 4.4 Attachment Points

Models define named **attachment points** (bones with special names) where effects and props are mounted:

```
Standard Attachment Points:
├── origin             # Model root (center of feet)
├── chest              # Center of torso
├── head               # Head/overhead (for overhead effects, name text)
├── hand_right         # Right hand (weapon, held items)
├── hand_left          # Left hand (shield, offhand)
├── overhead           # Above head (buff/debuff icons, rally flag)
├── sprite_first       # Primary particle mount (spell effects)
├── sprite_second      # Secondary particle mount
├── weapon             # Weapon tip (for attack trails)
└── mount              # Mount point (for mounted units/riders)
```

### 4.5 Team Color & Texture Swaps

Like WC3, certain parts of unit models use **team color** — a material region that is tinted to the owning player's color at runtime:

```
TeamColorConfig {
    playerColors: vec4[]    // Palette of team colors (red, blue, teal, purple, ...)
    teamGlowTexture: string // Glow mask texture (white = team colored, black = not)
}
```

The shader multiplies the team color into pixels where the glow mask is white. This avoids needing separate model files per player.

### 4.6 Editor Animation Preview

The editor provides:
- **Animation browser:** List all sequences in a model, play/pause/scrub
- **Event marker editor:** Place/remove animation events on the timeline
- **Attachment point visualizer:** Show bone attachment points as gizmos
- **Blend preview:** Preview crossfade between two animation states
- **Team color preview:** Cycle through player colors to verify team color regions

### 4.7 Particle Effects

Particle systems for spell effects, explosions, ambient atmosphere:

```
ParticleEmitterDefinition {
    id: string
    texture: string             // Sprite sheet or single texture
    emissionRate: float         // Particles per second
    lifetime: float[2]          // [min, max] seconds
    speed: float[2]             // [min, max] initial velocity
    scale: float[2]             // [start, end] size over lifetime
    color: vec4[2]              // [start, end] color over lifetime (includes alpha fade)
    gravity: float              // Downward acceleration
    spread: float               // Emission cone angle in degrees
    blendMode: enum             // Additive, Alpha, Modulate
    emitterShape: enum          // Point, Sphere, Disc, Line
    maxParticles: int           // Pool size cap
    worldSpace: bool            // true = particles stay in world, false = follow emitter
}
```

---

## 5. Campaign, Missions & Triggers

### 5.1 Campaign Structure

A campaign is an ordered sequence of missions, each backed by a map file:

```
CampaignDefinition {
    id: string                      // "human_campaign"
    name: string                    // "The Scourge of Lordaeron"
    race: string                    // Associated faction
    missions: [
        {
            id: string              // "human_01"
            name: string            // "The Defense of Strahnbrad"
            mapFile: string         // "maps/campaign/human_01.rtsmap"
            briefingId: string      // Reference to briefing screen data
            nextMission: string     // ID of next mission (null = campaign end)
            unlockCondition: string // "previous_complete" or custom trigger
        }
    ]
    // Persistent state across missions
    persistentHeroes: string[]      // Hero IDs that carry over between missions
    globalVariables: {              // State shared across all missions
        name: string
        type: string                // "int", "bool", "string"
        defaultValue: any
    }[]
}
```

### 5.2 Mission Setup (Per-Map)

Each mission map contains player configuration and victory/defeat conditions:

```json
{
  "playerSetup": {
    "players": [
      {
        "slot": 0,
        "name": "Player 1",
        "race": "human",
        "color": "red",
        "controller": "human",
        "startLocation": "player1_start",
        "allies": [2],
        "startingGold": 500,
        "startingLumber": 150,
        "maxSupply": 100
      },
      {
        "slot": 1,
        "name": "Undead Forces",
        "race": "undead",
        "color": "purple",
        "controller": "computer",
        "aiDifficulty": "hard",
        "startLocation": "enemy_base",
        "startingGold": 99999,
        "startingLumber": 99999
      },
      {
        "slot": 2,
        "name": "Villagers",
        "race": "human",
        "color": "blue",
        "controller": "passive",
        "startLocation": "village_center"
      }
    ],
    "teams": [
      { "name": "Alliance", "players": [0, 2] },
      { "name": "Scourge", "players": [1] }
    ]
  }
}
```

### 5.3 Trigger System

The trigger system is the heart of the editor's scripting capability. Like WC3's trigger editor, it uses an **Event → Condition → Action** model that non-programmers can use.

```
Trigger {
    name: string                    // Human-readable name
    enabled: bool                   // Can be disabled/re-enabled by other triggers
    initiallyOn: bool               // Active when map starts

    events: TriggerEvent[]          // WHEN (any of these fires, evaluate conditions)
    conditions: TriggerCondition[]  // IF (all must be true)
    actions: TriggerAction[]        // THEN (execute in order)
}
```

### 5.4 Trigger Events

Events are things that happen in the game world:

```
TriggerEvent categories:
├── Time
│   ├── MapInitialization           // Map first loads
│   ├── ElapsedGameTime(seconds)    // X seconds into the game
│   ├── PeriodicTimer(interval)     // Every X seconds
│
├── Unit Events
│   ├── UnitEntersRegion(region)    // A unit walks into a named region
│   ├── UnitLeavesRegion(region)
│   ├── UnitDies(filter?)           // A unit is killed (optional: specific unit/type)
│   ├── UnitIsAttacked(filter?)
│   ├── UnitAcquiresItem(item?)
│   ├── UnitFinishedTraining(unit?)
│   ├── UnitBeginsResearch(upgrade?)
│   ├── UnitSpellEffect(ability?)   // A unit casts a specific ability
│   ├── HeroLevelUp(hero?)
│   └── UnitSelected(unit)          // Player selects a specific unit
│
├── Player Events
│   ├── PlayerDefeated(player)
│   ├── PlayerLeavesGame(player)
│   ├── PlayerTypesChat(message)    // Chat message matches a pattern
│   ├── AllianceChanged(p1, p2)
│   └── ResourceChanged(player, resource, threshold)
│
├── Building Events
│   ├── ConstructionStarted(building?)
│   ├── ConstructionFinished(building?)
│   ├── BuildingUpgraded(building?)
│   └── BuildingDies(building?)
│
└── Custom
    ├── VariableChanged(variable, value)
    └── CustomEventFired(eventName)     // Fired explicitly by another trigger
```

### 5.5 Trigger Conditions

Conditions filter whether actions execute:

```
TriggerCondition categories:
├── Comparisons
│   ├── IntegerComparison(a, op, b)     // ==, !=, <, >, <=, >=
│   ├── BooleanComparison(a, op, b)
│   └── StringComparison(a, op, b)
│
├── Unit Conditions
│   ├── UnitIsAlive(unit)
│   ├── UnitTypeIs(unit, type)
│   ├── UnitOwnerIs(unit, player)
│   ├── UnitHasAbility(unit, ability)
│   ├── UnitHasItem(unit, item)
│   ├── UnitHPPercent(unit, op, percent)
│   └── UnitIsInRegion(unit, region)
│
├── Player Conditions
│   ├── PlayerHasUnits(player, count)
│   ├── PlayerGold(player, op, amount)
│   ├── PlayerSupply(player, op, amount)
│   └── IsPlayerAlly(player1, player2)
│
└── Logical
    ├── And(conditions[])
    ├── Or(conditions[])
    └── Not(condition)
```

### 5.6 Trigger Actions

Actions change game state:

```
TriggerAction categories:
├── Unit Actions
│   ├── CreateUnit(type, player, position, facing)
│   ├── KillUnit(unit)
│   ├── RemoveUnit(unit)                    // Remove without death event
│   ├── MoveUnit(unit, position)            // Instant teleport
│   ├── OrderUnitToMove(unit, position)     // Issue movement order
│   ├── OrderUnitToAttack(unit, target)
│   ├── SetUnitHP(unit, value)
│   ├── SetUnitOwner(unit, newPlayer)
│   ├── SetUnitInvulnerable(unit, bool)
│   ├── PauseUnit(unit, bool)              // Freeze in place
│   ├── AddAbility(unit, ability)
│   └── RemoveAbility(unit, ability)
│
├── Hero Actions
│   ├── SetHeroLevel(hero, level)
│   ├── AddHeroXP(hero, amount)
│   ├── CreateItemForHero(hero, itemType)
│   └── ReviveHero(hero, position)
│
├── Player Actions
│   ├── SetGold(player, amount)
│   ├── AddGold(player, amount)
│   ├── SetLumber(player, amount)
│   ├── DefeatPlayer(player)
│   ├── VictoryPlayer(player)               // Trigger win condition
│   ├── SetAllianceState(p1, p2, allied)
│   └── SetTechResearched(player, upgrade, level)
│
├── Camera Actions
│   ├── PanCamera(player, position, duration)
│   ├── SetCameraPosition(player, namedCamera)
│   ├── LockCameraToUnit(player, unit)
│   ├── ResetCamera(player)
│   └── SetCameraBounds(player, region)
│
├── UI / Dialog Actions
│   ├── DisplayTextMessage(player, text, duration)
│   ├── DisplayFloatingText(position, text, color)
│   ├── PingMinimap(player, position, color)
│   ├── ShowDialog(dialogId)                // Show a modal dialog
│   ├── ClearScreen(player)
│   └── SetMissionObjective(text, state)    // "Discovered", "Completed", "Failed"
│
├── Sound Actions
│   ├── PlaySound(soundId)
│   ├── PlayMusic(trackId)
│   ├── StopMusic(fadeTime)
│   └── SetSoundVolume(channel, volume)
│
├── Environment Actions
│   ├── SetTimeOfDay(hours)                 // 0.0–24.0
│   ├── SetWeather(region, weatherType)
│   ├── CreateDestructable(type, position)
│   ├── KillDestructable(destructable)
│   └── SetTerrainType(position, textureId)
│
├── Flow Control
│   ├── Wait(seconds)                       // Pause trigger execution
│   ├── IfThenElse(condition, thenActions, elseActions)
│   ├── ForLoop(variable, start, end, actions)
│   ├── ForEachUnit(unitGroup, actions)
│   ├── SetVariable(name, value)
│   ├── EnableTrigger(triggerName)
│   ├── DisableTrigger(triggerName)
│   ├── RunTrigger(triggerName)
│   └── FireCustomEvent(eventName)
│
└── Cutscene Actions
    ├── StartCutscene(cutsceneId)
    ├── SkipToSequence(cutsceneId, sequenceIndex)
    └── EndCutscene()
```

### 5.7 Variables

Triggers can read and write **map variables** — typed values scoped to the running map:

```
Variable {
    name: string
    type: enum              // Integer, Float, Boolean, String, Unit, UnitGroup, Point, Region
    defaultValue: any
    isArray: bool           // Allows indexed access (variable[0], variable[1], ...)
    arraySize: int          // If array, pre-allocated size
}
```

Variables bridge triggers together: one trigger sets a variable, another reads it. Campaign-global variables persist across mission transitions.

