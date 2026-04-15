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

---

## 6. Cutscenes & Briefing Screens

### 6.1 Cutscene System Overview

Cutscenes are **in-engine cinematics** — they use the same map, units, and renderer as gameplay but take control of the camera and script unit actions. Like WC3, cutscenes are not pre-rendered video; they are real-time sequences authored in the editor.

**During a cutscene:**
- Player input is disabled (no unit selection, no commands)
- Camera is script-controlled (pans, zooms, follows units)
- UI is hidden (or replaced with cinematic bars / letterbox)
- Units execute scripted movements, animations, and dialog
- The game world may be modified (spawn units, change terrain, trigger effects)
- Player can **skip** the cutscene (jumps to end state)

### 6.2 Cutscene Timeline

A cutscene is a **timeline** of tracks, similar to a video editor NLE:

```
Cutscene {
    id: string
    name: string
    duration: float                     // Total length in seconds
    skippable: bool                     // Can the player press ESC to skip
    letterbox: bool                     // Show cinematic black bars

    tracks: CutsceneTrack[]
}
```

Tracks run in parallel along the timeline:

```
Time: 0s     2s     4s     6s     8s     10s
      ├──────┼──────┼──────┼──────┼──────┤

Camera:  [Pan to castle───────][Zoom in──][Follow hero──]

Unit A:  [Walk to gate──────────][Stand───][Attack──────]

Unit B:  ·····[Spawn][Walk to bridge────────────────────]

Dialog:  ·····[Hero: "The gate is breached!"]···········
              ···········[Villain: "You're too late!"]···

Sound:   [Ambient wind──────────────────────────────────]
         ·····[Explosion SFX]·····[Battle music────────]

Effects: ·····[Fire particle at gate]····················
```

### 6.3 Track Types

```
CutsceneTrack types:
├── CameraTrack
│   ├── CameraMove(target, duration, easing)        // Pan to position
│   ├── CameraFollow(unit, offset, duration)         // Track a unit
│   ├── CameraZoom(fov, duration, easing)
│   ├── CameraShake(intensity, duration)
│   └── CameraFade(color, alpha, duration)           // Fade to black/white
│
├── UnitTrack (one track per scripted unit)
│   ├── UnitMove(destination, speed)                 // Walk/run to point
│   ├── UnitFace(target|angle, duration)             // Turn to face
│   ├── UnitPlayAnimation(animName, duration)        // Force specific animation
│   ├── UnitSetVisibility(visible)                   // Show/hide
│   ├── UnitCreate(type, position, player)           // Spawn mid-cutscene
│   ├── UnitDie()                                    // Kill with death animation
│   └── UnitPause(bool)                              // Freeze in place
│
├── DialogTrack
│   ├── DialogLine(speaker, text, duration, voiceover?)
│   ├── DialogChoice(options[], variable)            // Branching dialog (rare in RTS)
│   └── ClearDialog()
│
├── SoundTrack
│   ├── PlaySound(soundId, volume, position?)
│   ├── PlayMusic(trackId, fadeIn)
│   ├── StopMusic(fadeOut)
│   └── SetAmbience(ambienceId, volume)
│
├── EffectTrack
│   ├── SpawnEffect(particleId, position, duration)
│   ├── AttachEffect(particleId, unit, attachPoint)
│   ├── SetWeather(region, weatherType)
│   └── SetTimeOfDay(hour, transitionDuration)
│
└── GameStateTrack
    ├── SetVariable(name, value)
    ├── EnableTrigger(triggerName)
    ├── CreateDestructable(type, position)
    ├── ModifyTerrain(position, texture)
    └── SetPlayerState(player, property, value)
```

### 6.4 Cutscene Skip Handling

When a player skips a cutscene, the engine must fast-forward game state to the cutscene's end state without playing animations. This requires each cutscene to declare its **end state snapshot:**

```
CutsceneEndState {
    unitPositions: { unitId: position }[]   // Where units should be after cutscene
    unitStates: { unitId: alive|dead }[]    // Whether units survived
    variableValues: { name: value }[]       // Variable changes that happened
    cameraPosition: vec3                    // Where camera should end up
    triggersEnabled: string[]               // Triggers toggled during cutscene
}
```

When skipped, the engine applies the end state instantly and resumes gameplay.

### 6.5 Briefing Screens

Briefing screens appear **before** a mission starts. They set the story context, display objectives, and let the player prepare.

```
BriefingDefinition {
    id: string
    missionName: string
    screens: BriefingScreen[]
}

BriefingScreen {
    type: enum                      // Narrative, Objectives, Map
    background: string              // Background image/art asset
    
    // For Narrative screens
    title: string
    bodyText: string                // Story text (supports rich formatting)
    speakerPortrait: string         // Character portrait image
    speakerName: string
    voiceoverSound: string?         // Audio narration

    // For Objectives screens
    objectives: [
        {
            text: string            // "Destroy the Undead base"
            type: enum              // Primary, Secondary, Optional
            hintText: string?       // Expanded description
        }
    ]

    // For Map screens (shows a tactical map)
    mapRegion: rect                 // Area of the map to show
    mapMarkers: [
        {
            position: vec2
            icon: string            // "ally", "enemy", "objective", "start"
            label: string
        }
    ]
}
```

**Briefing flow:**
1. Campaign triggers mission load
2. Briefing screens display sequentially (player clicks "Next" to advance)
3. Final screen shows objectives summary
4. Player clicks "Start Mission" → map loads, gameplay begins
5. Objectives display in-game in the quest log UI

### 6.6 In-Game Dialog System

Beyond cutscenes, the game needs a dialog system for real-time conversations during gameplay (like WC3's transmission messages):

```
TransmissionMessage {
    speaker: string              // Unit instance ID or hero name
    speakerName: string          // Display name
    portrait: string             // Portrait to show in dialog box
    text: string                 // Dialog text
    voiceover: string?           // Audio file
    duration: float              // How long to display (auto-calculated from text length if omitted)
    pingMinimap: bool            // Flash the speaker's position on minimap
    pauseGame: bool              // Pause gameplay during message (rare)
}
```

Transmissions appear as a portrait + text box at the bottom of the screen and auto-advance after their duration. Multiple transmissions can be queued.

---

## 7. Multiplayer Architecture

### 7.1 Networking Model: Lockstep Simulation

RTS games with hundreds of units cannot afford to sync every unit's position every frame. Instead, we use **deterministic lockstep** — the same model used by WC3, StarCraft, and Age of Empires:

```
┌──────────┐     commands     ┌──────────┐
│ Player A │ ───────────────► │  Server  │
│  (sim)   │ ◄─────────────── │ (relay)  │
│          │   all commands    │          │
│ Player B │ ───────────────► │          │
│  (sim)   │ ◄─────────────── │          │
└──────────┘                  └──────────┘

Each client runs the FULL simulation.
Only player COMMANDS are sent over the network.
The server collects commands and broadcasts them to all players.
```

**How it works:**
1. Game time is divided into **turns** (e.g., every 100ms = 10 turns/second)
2. Each turn, every player sends their commands to the server (move unit X to Y, build Z, attack Q)
3. The server collects all commands for turn N and broadcasts them to all players
4. Each client executes the same commands in the same order on the same game state
5. Because the simulation is **deterministic**, all clients arrive at the same result

**Bandwidth:** Only commands are sent — maybe 50-200 bytes per turn per player, regardless of unit count. A 4-player game with 400 units total uses less bandwidth than a single FPS player.

### 7.2 Determinism Requirements

For lockstep to work, the simulation must be **bit-for-bit identical** on all clients. This means:

**Must be deterministic:**
- Pathfinding (same algorithm, same tie-breaking)
- Combat calculations (same damage, same order of operations)
- AI behavior (same decisions given same inputs)
- Random number generation (same seed, same sequence)
- Entity update order (deterministic iteration, e.g., by entity ID)
- Fixed-point or integer math for gameplay (no floating-point drift)

**Does NOT need to be deterministic (client-local):**
- Rendering (different resolutions, quality settings)
- Audio (local mixing)
- Camera position (each player has their own view)
- UI state (selection, hover, minimap)
- Particle effects (visual only)
- Animation blending (visual only)

### 7.3 Turn Structure

```
Turn Timeline:

    Turn N-1        Turn N          Turn N+1
    ┌───────┐      ┌───────┐      ┌───────┐
    │execute│      │execute│      │execute│
    │cmds   │      │cmds   │      │cmds   │
    └───┬───┘      └───┬───┘      └───┬───┘
        │              │              │
   ┌────▼────┐    ┌────▼────┐    ┌────▼────┐
   │ collect │    │ collect │    │ collect │
   │ input   │    │ input   │    │ input   │
   └────┬────┘    └────┬────┘    └────┬────┘
        │              │              │
   ┌────▼────┐    ┌────▼────┐    ┌────▼────┐
   │  send   │    │  send   │    │  send   │
   │ commands│    │ commands│    │ commands│
   └─────────┘    └─────────┘    └─────────┘

Input latency = 1 turn (~100-200ms) — commands issued in turn N
are executed in turn N+1 (or N+2 for high-latency connections).
```

### 7.4 Command Protocol

Commands are the only data sent over the network:

```
GameCommand {
    playerId: int               // Who issued this command
    turnNumber: int             // Which simulation turn to execute on
    type: CommandType
    payload: bytes              // Command-specific data
}

CommandType enum:
├── MoveUnits(unitIds[], targetPosition, isAttackMove)
├── AttackTarget(unitIds[], targetUnitId)
├── BuildStructure(workerId, buildingType, position)
├── TrainUnit(buildingId, unitType)
├── ResearchUpgrade(buildingId, upgradeId)
├── UseAbility(unitId, abilityId, target?)
├── SetRallyPoint(buildingId, position)
├── Patrol(unitIds[], waypoints[])
├── HoldPosition(unitIds[])
├── Stop(unitIds[])
├── LoadUnit(transportId, unitId)
├── UnloadUnit(transportId, position?)
├── DropItem(heroId, itemSlot)
├── GiveItem(heroId, targetHeroId, itemSlot)
├── SelectGroup(groupNumber, unitIds[])       // Ctrl+# group assignment
├── ChatMessage(text, channel)                // All, allies, observers
├── AllianceChange(targetPlayer, state)
├── Surrender()
└── Checksum(hash)                            // Desync detection
```

### 7.5 Desync Detection

Since all clients must stay in sync, we need to detect when they diverge:

```
Checksum Verification:
1. Every N turns (e.g., every 10 turns), each client computes a hash of critical game state:
   - All unit positions, HP, mana
   - All player resources
   - Random number generator state
   - Turn counter

2. Clients send their checksum to the server
3. Server compares all checksums
4. If mismatch → DESYNC detected

Desync handling options:
├── Log & alert (development: dump state for debugging)
├── Resync from host (one client's state becomes authoritative)
└── Disconnect desync'd player (competitive: anti-cheat)
```

### 7.6 Lobby & Matchmaking

```
Game Lobby:
├── Host creates lobby with map selection
├── Players join lobby (via invite link, server browser, or matchmaking)
├── Host assigns player slots to map slots
├── Each player selects:
│   ├── Race / faction
│   ├── Team
│   ├── Color
│   └── Ready status
├── Map settings configured:
│   ├── Game speed
│   ├── Starting resources
│   ├── Victory conditions (melee, custom)
│   └── Observers allowed
└── Host starts game when all players ready

Matchmaking (ranked play):
├── Player queues for game mode (1v1, 2v2, 3v3, FFA)
├── Server matches players by skill rating (ELO/MMR)
├── Map selected from ranked map pool (random or veto)
├── Automatic slot assignment
└── Game starts after countdown
```

### 7.7 Game Modes

```
Melee:
- Standard competitive RTS. Each player starts with a town hall and workers.
- Victory: destroy all enemy buildings, or all enemies surrender.
- Map provides start locations, resources, neutral creeps/shops.

Custom Game:
- Map triggers define everything: objectives, win/loss, special rules.
- Supports PvE, co-op, tower defense, hero arenas, RPGs, etc.
- The trigger system is the scripting layer (Section 5).

Observer / Replay:
- Observers see all players' views (full map revealed).
- Replays are stored as command logs — replay = re-simulate from turn 0.
- Replay files are tiny (just commands + initial RNG seed + map reference).
```

### 7.8 Replay System

Because the simulation is deterministic, replays are just command logs:

```
ReplayFile {
    formatVersion: int
    mapFile: string                 // Which map was played
    mapChecksum: hash               // Verify map hasn't changed
    randomSeed: int                 // Initial RNG seed
    gameSpeed: float                // Simulation speed multiplier
    players: [
        { id: int, name: string, race: string, color: string }
    ]
    commands: GameCommand[]         // Every command, in order, with turn numbers
    result: {
        winner: int                 // Player ID or team
        duration: float             // Game length in seconds
        endReason: string           // "victory", "surrender", "disconnect"
    }
}
```

To replay: load the map, set the RNG seed, and feed commands turn by turn. The simulation reproduces the exact game. Playback supports fast-forward, rewind (by re-simulating from start), and player-perspective switching.

### 7.9 Network Architecture Options

```
Option A: Peer-to-Peer with Host
├── One player acts as relay (host)
├── Simplest to implement
├── Host has latency advantage
├── If host disconnects, game ends (or host migration)
└── Good for: casual games, LAN play

Option B: Dedicated Relay Server
├── Server collects and broadcasts commands
├── No latency advantage for any player
├── Server does NOT run simulation (just relays)
├── Server can enforce turn timing
└── Good for: ranked play, competitive

Option C: Authoritative Server (alternative to lockstep)
├── Server runs the simulation
├── Clients send commands, receive state updates
├── No determinism requirement
├── Much higher bandwidth (sending unit positions)
├── Better for games with few units, worse for RTS scale
└── NOT recommended for this engine
```

**Recommendation:** Start with Option A (P2P with host) for development, migrate to Option B (relay server) for competitive play. Both use the same lockstep protocol.

---

## 8. Editor UI & Workflow

The editor is a **web application** (matching our WASM engine target) that provides visual authoring tools for all map content. It renders the map in real-time using the same engine renderer, with overlay gizmos for selection, placement, and region visualization.

### 8.1 Editor Layout

```
┌─────────────────────────────────────────────────────────────────────┐
│  Menu Bar: File│Edit│View│Map│Layer│Test│Help                       │
├──────────┬──────────────────────────────────────────────┬───────────┤
│          │                                              │           │
│  Tool    │            3D Viewport                       │  Property │
│  Palette │         (engine renderer +                   │  Inspector│
│          │          editor overlays)                    │           │
│ ──────── │                                              │  ──────── │
│ Terrain  │     Camera: orbit, pan, zoom                │  Selected │
│  ▪ Raise │     Grid overlay toggle                      │  object's │
│  ▪ Lower │     Region wireframes                        │  fields   │
│  ▪ Paint │     Unit selection boxes                     │           │
│  ▪ Cliff │     Pathing overlay                          │  Context- │
│  ▪ Water │     Fog-of-war preview                       │  sensitive │
│          │                                              │  based on │
│ Entities │                                              │  what's   │
│  ▪ Units │                                              │  selected │
│  ▪ Build │                                              │           │
│  ▪ Items │                                              │           │
│  ▪ Doodad│                                              │           │
│          │                                              │           │
│ Triggers │                                              │           │
│ Cameras  │                                              │           │
│ Regions  │                                              │           │
├──────────┴──────────────────────────────────────────────┴───────────┤
│  Bottom Panel (tabbed):                                             │
│  [Trigger Editor] [Cutscene Timeline] [Object Editor] [Console]    │
└─────────────────────────────────────────────────────────────────────┘
```

### 8.2 Editor Modes

The editor operates in **modes** selected from the tool palette:

```
Terrain Mode:
├── Raise/Lower brush (adjustable size + strength)
├── Flatten brush (sets height to a target level)
├── Smooth brush (averages heights with neighbors)
├── Cliff raise/lower (discrete level changes)
├── Ramp placement (connect two cliff levels)
├── Texture paint brush (select from palette, paint base/overlay)
├── Water level brush (raise/lower water plane)
└── Undo/redo per stroke

Entity Mode:
├── Place entity (select from object browser, click to stamp)
├── Select entity (click, box-select, shift-click to multi-select)
├── Move entity (drag or type coordinates)
├── Rotate entity (drag handle or type angle)
├── Scale entity (drag handle or type value)
├── Delete selected (DEL key)
├── Duplicate selected (Ctrl+D)
├── Set owner/player (dropdown)
└── Edit properties (opens in Property Inspector)

Region Mode:
├── Draw rectangular region (click + drag)
├── Draw polygonal region (click vertices, close loop)
├── Select/move/resize regions
├── Name region
└── Set region properties (weather, ambient sound, color)

Camera Mode:
├── Save named camera position (current viewport → saved camera)
├── Preview camera (snap viewport to saved camera)
├── Edit camera properties (position, target, FOV, roll)
└── Create camera paths (sequence of positions with interpolation)
```

### 8.3 Object Editor

The Object Editor is a **data browser + editor** for entity definitions (like WC3's Object Editor). It operates on the global object database, not per-map instances.

```
┌─────────────────────────────────────────────────────────────┐
│  Object Editor                                              │
├──────────────┬──────────────────────────────────────────────┤
│              │                                              │
│  Category    │  Selected: Footman (human_footman)           │
│  ▸ Units     │  ─────────────────────────────────           │
│    ▸ Human   │  Parent: Base Human Unit                     │
│      Footman │                                              │
│      Rifleman│  Combat                                      │
│      Knight  │    Hit Points: [420]                          │
│    ▸ Orc     │    Armor:      [2]                           │
│    ▸ Undead  │    Armor Type: [Heavy ▼]                     │
│  ▸ Buildings │    Damage:     [12] – [13]                   │
│  ▸ Heroes   │    Attack Type: [Normal ▼]                   │
│  ▸ Items    │    Attack Speed: [1.35]                      │
│  ▸ Abilities│    Attack Range: [0] (melee)                 │
│  ▸ Upgrades │                                              │
│  ▸ Buffs    │  Movement                                    │
│              │    Speed: [270]                               │
│  [+ New]     │    Type:  [Foot ▼]                           │
│  [× Delete]  │    Collision: [32]                           │
│              │                                              │
│  Filter: [  ]│  Production                                  │
│              │    Gold: [135]  Lumber: [0]  Supply: [2]     │
│              │    Build Time: [20]                           │
│              │    Hotkey: [F]                                │
│              │                                              │
│              │  Abilities: [Defend ×] [+ Add]               │
│              │  Model: models/human/footman.glb [Browse]    │
│              │  Icon: icons/human/footman.png [Browse]      │
└──────────────┴──────────────────────────────────────────────┘
```

**Features:**
- **Inheritance:** Objects can inherit from a parent and override specific fields. Modified fields shown in bold. "Reset to parent" per field.
- **Custom objects:** Create new types by cloning a base type (like WC3's custom units)
- **Search & filter:** Find objects by name, ID, category, or field value
- **Batch edit:** Select multiple objects, edit a shared field, apply to all
- **Diff view:** See what's modified from the base definition

### 8.4 Trigger Editor

The trigger editor provides a visual **GUI for scripting** without writing code:

```
┌─────────────────────────────────────────────────────────────────┐
│  Trigger Editor                                                 │
├──────────────┬──────────────────────────────────────────────────┤
│              │                                                  │
│  Trigger List│  Trigger: "Ambush at Bridge"                     │
│              │  ☑ Initially On                                  │
│  ▸ Init      │                                                  │
│  ▸ Victory   │  ── Events ──────────────────────────────────    │
│  ● Ambush ◄──│  ▪ Unit enters region "bridge_crossing"          │
│  ▸ Boss Fight│                                                  │
│  ▸ Cinematic │  ── Conditions ──────────────────────────────    │
│              │  ▪ Triggering unit owner == Player 1 (Red)       │
│  [+ New]     │  ▪ Variable "ambush_triggered" == false          │
│  [Folder]    │                                                  │
│              │  ── Actions ─────────────────────────────────    │
│              │  ▪ Set variable "ambush_triggered" = true        │
│              │  ▪ Create 4 "orc_grunt" at region "ambush_spot"  │
│              │     for Player 2 (Blue)                          │
│              │  ▪ Pan camera for Player 1 to "ambush_spot"      │
│              │     over 1.0 seconds                             │
│              │  ▪ Display text "You've been ambushed!" to       │
│              │     Player 1 for 3.0 seconds                     │
│              │  ▪ Order created units to attack-move to         │
│              │     "bridge_crossing"                             │
│              │                                                  │
│              │  [+ Add Event] [+ Add Condition] [+ Add Action] │
└──────────────┴──────────────────────────────────────────────────┘
```

**GUI-driven:** Each event, condition, and action is selected from a categorized dropdown menu. Parameters are filled in via typed input fields, entity pickers, region pickers, and variable selectors. No code writing required — the trigger is pure structured data.

### 8.5 Cutscene Timeline Editor

Visual timeline editor for authoring in-engine cinematics:

```
┌──────────────────────────────────────────────────────────────────┐
│  Cutscene: "Arthas Arrives"              Duration: 12.0s        │
│  [▶ Play] [⏸ Pause] [⏹ Stop]  Speed: [1x ▼]  [🔊 Preview]     │
├──────────────────────────────────────────────────────────────────┤
│  Tracks           │ 0s    2s    4s    6s    8s    10s   12s     │
│  ─────────────────┼─────────────────────────────────────────     │
│  📷 Camera        │ [Pan to gate──][Zoom──][Follow Arthas──]    │
│  🗡️ Arthas        │ [Ride in──────────][Dismount][Walk───]      │
│  🏰 Gate Guard    │ [Stand──][Salute──][Walk to Arthas──────]   │
│  💬 Dialog        │ ·····["My lord!"]·····["The dead rise..."]  │
│  🔊 Sound         │ [Horse hooves─────][Gate creak]·[Fanfare]  │
│  ✨ Effects       │ ···········[Dust particles──]···············│
│  ─────────────────┼─────────────────────────────────────────     │
│  [+ Add Track]    │         ▲ Playhead (draggable)              │
├──────────────────────────────────────────────────────────────────┤
│  Selected: Camera keyframe at 4.0s                              │
│  Position: [100, 50, 200]  Target: [120, 0, 180]  FOV: [45]   │
│  Easing: [Ease In-Out ▼]                                        │
└──────────────────────────────────────────────────────────────────┘
```

### 8.6 Test Play

The editor includes a **Test Map** button that:
1. Serializes the current map state to a temporary file
2. Launches the game runtime in a new window/tab
3. Loads the map and starts gameplay
4. The editor remains open for quick iteration
5. On exit, returns to the editor with the map unchanged

**Test options:**
- Start at a specific trigger point (skip early game)
- Enable cheats (instant build, full map vision, infinite resources)
- Start from a specific cutscene
- Set initial game speed

### 8.7 Undo/Redo System

Every editor action is an **undoable command**:

```
Command {
    execute()       // Apply the change
    undo()          // Reverse the change
    description: string  // "Move Footman to (100, 200)"
}

CommandHistory {
    undoStack: Command[]
    redoStack: Command[]
    
    Execute(cmd) → push to undoStack, clear redoStack
    Undo()       → pop undoStack, execute undo(), push to redoStack
    Redo()       → pop redoStack, execute(), push to undoStack
}
```

All terrain edits, entity placements, property changes, and trigger modifications are commands. This provides unlimited undo/redo.

---

## 9. Data Formats & Asset Pipeline

### 9.1 File Format Strategy

All editor-authored data uses **JSON** for human readability and LLM accessibility. Binary formats are reserved for assets that need compact storage (textures, models, audio).

```
Format Matrix:

Data Type          │ Editor Format    │ Runtime Format     │ Notes
───────────────────┼──────────────────┼────────────────────┼─────────────────
Map files          │ JSON             │ JSON (or msgpack)  │ Human-readable
Entity definitions │ JSON             │ JSON               │ Editable by hand
Trigger graphs     │ JSON             │ JSON               │ Structured data
Cutscene timelines │ JSON             │ JSON               │ Structured data
Briefing screens   │ JSON             │ JSON               │ Structured data
String tables      │ JSON             │ JSON               │ Localization
─────────────────────────────────────────────────────────────────────────────
3D Models          │ glTF/GLB         │ GLB (binary glTF)  │ Industry standard
Textures           │ PNG/TGA          │ KTX2 (compressed)  │ GPU-ready
Audio              │ WAV/OGG          │ OGG/WebM           │ Web-compatible
Icons/UI           │ PNG/SVG          │ PNG atlas           │ Sprite sheets
Fonts              │ TTF/OTF          │ MSDF atlas          │ Signed-distance field
```

### 9.2 Asset References

All data files reference assets by **path relative to the project root**, never by absolute path or embedded data:

```json
{
  "model": "assets/models/human/footman.glb",
  "icon": "assets/icons/human/footman.png",
  "portrait": "assets/models/human/footman_portrait.glb",
  "soundSet": "assets/sounds/human/footman.json"
}
```

This ensures maps are portable — they reference assets, and the runtime resolves paths at load time.

### 9.3 Project Structure

```
my_rts_project/
├── assets/
│   ├── models/
│   │   ├── human/
│   │   │   ├── footman.glb
│   │   │   ├── knight.glb
│   │   │   └── barracks.glb
│   │   ├── orc/
│   │   └── neutral/
│   ├── textures/
│   │   ├── terrain/
│   │   │   ├── grass_01.png
│   │   │   ├── dirt_01.png
│   │   │   └── cliff_01.png
│   │   ├── units/
│   │   └── buildings/
│   ├── icons/
│   │   ├── abilities/
│   │   ├── items/
│   │   └── units/
│   ├── sounds/
│   │   ├── music/
│   │   ├── sfx/
│   │   ├── voice/
│   │   └── ambient/
│   └── shaders/
│       ├── terrain.wgsl
│       ├── model.wgsl
│       ├── water.wgsl
│       ├── particle.wgsl
│       └── ui.wgsl
├── data/
│   ├── objects/                    # Global entity definitions
│   │   ├── units.json
│   │   ├── buildings.json
│   │   ├── heroes.json
│   │   ├── abilities.json
│   │   ├── upgrades.json
│   │   ├── items.json
│   │   └── buffs.json
│   ├── tilesets/                   # Terrain texture palettes
│   │   ├── lordaeron_summer.json
│   │   ├── northrend.json
│   │   └── barrens.json
│   └── ui/                        # UI layout definitions
│       ├── game_hud.json
│       ├── minimap.json
│       └── menus.json
├── maps/
│   ├── campaign/
│   │   ├── human_01.rtsmap/
│   │   ├── human_02.rtsmap/
│   │   └── ...
│   └── multiplayer/
│       ├── twisted_meadows.rtsmap/
│       ├── lost_temple.rtsmap/
│       └── ...
├── campaigns/
│   ├── human_campaign.json
│   └── orc_campaign.json
└── project.json                    # Project metadata, version, settings
```

### 9.4 Asset Pipeline

```
Source Asset                Build Step               Runtime Asset
─────────────         ─────────────────────        ─────────────────
footman.blend    →    Export to glTF/GLB      →    footman.glb
grass_01.psd     →    Export PNG → compress   →    grass_01.ktx2
sword_hit.wav    →    Encode OGG             →    sword_hit.ogg
footman_icon.psd →    Export PNG → atlas pack →    unit_icons.png + atlas.json

Build steps can be automated (CI/CD) or manual (artist exports from DCC tool).
The editor works with source-format assets directly for preview.
The publish step converts to optimized runtime formats.
```

### 9.5 Map Packaging

For distribution, a `.rtsmap` directory is packaged into a single archive:

```
Packaging:
1. Collect all JSON files in the map directory
2. Resolve asset references → copy referenced assets into the package
3. Compress textures to GPU-ready format (KTX2)
4. Optionally strip editor-only data (region colors, viewport state)
5. Produce a single .rtsmap file (ZIP archive with known structure)

Runtime loading:
1. Open .rtsmap archive
2. Parse metadata.json for map info
3. Load terrain.json → build terrain mesh
4. Load entities.json → instantiate placed units/buildings
5. Load triggers.json → register event listeners
6. Load player_setup.json → configure player slots
7. Pre-load referenced assets (models, textures, sounds)
8. Signal ready → game begins
```

### 9.6 Versioning & Compatibility

```
Data file headers include:
{
  "formatVersion": 1,
  "engineVersion": "0.1.0",
  "editorVersion": "0.1.0",
  ...
}

Migration strategy:
├── Each format version bump includes a migrator function
├── Migrators are chained: v1 → v2 → v3 → current
├── Old maps auto-upgrade on load (with backup)
└── Editor saves always use the current format version
```

---

## 10. LLM Integration Points

This section identifies where the editor-data-driven architecture creates clean interfaces for LLM-assisted implementation.

### 10.1 What the LLM Implements (Engine Runtime)

The editor produces structured data. The LLM's job is to implement the C# engine systems that **interpret** this data:

```
Editor Output (data)         →    Engine System (code, LLM implements)
─────────────────────────         ─────────────────────────────────────
terrain.json                 →    TerrainLoader, TerrainRenderer, TerrainCollision
entities.json + object defs  →    EntityFactory, EntityManager (ECS)
triggers.json                →    TriggerInterpreter, EventBus, ConditionEvaluator
cutscenes.json               →    CutscenePlayer, TimelineExecutor
briefing.json                →    BriefingUI, ScreenFlow
player_setup.json            →    PlayerManager, TeamSystem, ResourceTracker
regions.json                 →    RegionSystem, SpatialQueries
cameras.json                 →    CameraController, CameraPathInterpolator
```

### 10.2 What the Human Does (Editor Authoring)

The human uses the editor to define **content** — things that require creative judgment:

- Drawing terrain: hills, valleys, cliffs, rivers, forests
- Placing units: army compositions, patrol routes, ambush positions
- Writing dialog: character voices, briefing text, objective descriptions
- Designing missions: pacing, difficulty curve, optional objectives
- Choreographing cutscenes: camera angles, character blocking, dramatic timing
- Balancing stats: unit costs, damage values, build times
- Creating multiplayer maps: balanced start positions, resource distribution

### 10.3 The Contract Between Editor & Engine

The data formats in this document serve as the **API contract** between human-authored content and LLM-implemented systems. For each system:

1. **Read the data format** in this document (e.g., `TriggerEvent`, `CutsceneTrack`)
2. **Implement the C# runtime** that loads and interprets that data
3. **Test against sample data** — the editor will produce JSON conforming to these schemas
4. **Iterate** — if the engine needs new data, add it to the schema and the editor

This means the LLM never needs to ask "what should the camera do here?" — that's authored in the cutscene timeline. The LLM only needs to ask "how do I smoothly interpolate between these two camera positions?" — which is a pure engineering question.

### 10.4 Implementation Priority

Suggested order for implementing engine systems:

```
Phase 1: Foundation
├── Terrain loading & rendering (Section 2)
├── Entity instantiation from definitions (Section 3)
├── Basic camera system (orbit, pan, zoom)
├── Selection & command input (click to select, right-click to move)
└── Basic pathfinding (A* on terrain grid)

Phase 2: Gameplay
├── Combat system (attack, damage types, armor)
├── Resource gathering (workers, gold mines, lumber)
├── Building construction (placement, build timer, production queue)
├── Upgrades & research
├── Fog of war & line of sight
└── Basic AI (computer player behaviors)

Phase 3: Content Systems
├── Trigger interpreter (event → condition → action)
├── Cutscene player (timeline execution)
├── Briefing screen flow
├── Campaign progression (mission sequence, persistent heroes)
└── Dialog / transmission system

Phase 4: Multiplayer
├── Deterministic simulation (fixed-point math, deterministic iteration)
├── Command serialization & networking
├── Lockstep turn system
├── Lobby & player management
├── Replay recording & playback
└── Desync detection

Phase 5: Polish
├── Audio system (positional sound, music, ambience)
├── Particle effects
├── Advanced rendering (shadows, water reflections, post-processing)
├── Minimap
├── Hero system (leveling, inventory, abilities)
└── Shop / neutral buildings
```

