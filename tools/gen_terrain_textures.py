"""
Generate seamless tileable terrain textures for the planet editor.
Outputs 256x256 PNGs into src/RtsEngine.Wasm/wwwroot/textures/.

Each texture uses hash-based value noise (summed octaves) so the result
is fully deterministic per seed.

Textures:
  water.png  — deep/light blue with wave ripples
  sand.png   — tan with fine granular noise + occasional pebbles
  grass.png  — green with patchy shades + darker blades
  rock.png   — gray with cracks + darker streaks
  snow.png   — white with subtle blue shadows in low areas
"""

import os
import math
import random
from pathlib import Path

import numpy as np
from PIL import Image

SIZE = 256
OUT_DIR = Path(__file__).resolve().parents[1] / "src" / "RtsEngine.Wasm" / "wwwroot" / "textures"
OUT_DIR.mkdir(parents=True, exist_ok=True)


def hash2(x, y, seed):
    """Stable per-pixel hash → [0, 1]."""
    a = (x * 374761393) ^ (y * 668265263) ^ seed
    a = (a ^ (a >> 13)) * 1103515245
    a = a & 0xFFFFFFFF
    return (a >> 8) / 0xFFFFFF


def value_noise_tileable(width, height, period, seed):
    """
    Seamlessly tileable value noise. `period` = noise cells per full texture.
    Hash coords are modulo `period` so the noise wraps at the texture edge.
    """
    rng = np.random.default_rng(seed)
    grid = rng.random((period + 1, period + 1)).astype(np.float32)
    # Make tileable: wrap grid
    grid[period, :] = grid[0, :]
    grid[:, period] = grid[:, 0]

    xs = np.linspace(0, period, width, endpoint=False)
    ys = np.linspace(0, period, height, endpoint=False)
    x, y = np.meshgrid(xs, ys)

    ix = np.floor(x).astype(int)
    iy = np.floor(y).astype(int)
    fx = x - ix
    fy = y - iy

    # Smoothstep
    ux = fx * fx * (3 - 2 * fx)
    uy = fy * fy * (3 - 2 * fy)

    a = grid[iy,     ix    ]
    b = grid[iy,     ix + 1]
    c = grid[iy + 1, ix    ]
    d = grid[iy + 1, ix + 1]

    return (1 - uy) * ((1 - ux) * a + ux * b) + uy * ((1 - ux) * c + ux * d)


def fbm_tileable(width, height, base_period, octaves, seed, persistence=0.5):
    """Fractal brownian motion (summed octaves) that tiles seamlessly."""
    total = np.zeros((height, width), dtype=np.float32)
    amp = 1.0
    max_amp = 0.0
    for i in range(octaves):
        period = base_period * (2 ** i)
        total += amp * value_noise_tileable(width, height, period, seed + i * 31)
        max_amp += amp
        amp *= persistence
    return total / max_amp


def clip01(a):
    return np.clip(a, 0.0, 1.0)


def save_rgb(filename, rgb):
    """rgb: float array (h, w, 3) in [0, 1]."""
    img_u8 = (clip01(rgb) * 255).astype(np.uint8)
    Image.fromarray(img_u8, mode="RGB").save(OUT_DIR / filename)
    print(f"  wrote {filename}")


def lerp(a, b, t):
    return a + (b - a) * t


# ── Water ───────────────────────────────────────────────────────────
def gen_water():
    n1 = fbm_tileable(SIZE, SIZE, 4, 4, seed=1001)
    n2 = fbm_tileable(SIZE, SIZE, 8, 3, seed=1002)
    # Wave ripples: combine two sine patterns for interference
    xs = np.linspace(0, 2 * math.pi, SIZE, endpoint=False)
    ys = np.linspace(0, 2 * math.pi, SIZE, endpoint=False)
    x, y = np.meshgrid(xs, ys)
    ripple1 = np.sin(x * 6 + n1 * 3) * 0.5 + 0.5
    ripple2 = np.sin(y * 5 + n2 * 2.5) * 0.5 + 0.5
    pattern = (ripple1 * 0.4 + ripple2 * 0.4 + n1 * 0.2)
    pattern = clip01(pattern)

    deep = np.array([0.05, 0.18, 0.42])
    light = np.array([0.30, 0.55, 0.85])
    rgb = np.stack([
        lerp(deep[0], light[0], pattern),
        lerp(deep[1], light[1], pattern),
        lerp(deep[2], light[2], pattern),
    ], axis=-1)
    save_rgb("water.png", rgb)


# ── Sand ────────────────────────────────────────────────────────────
def gen_sand():
    coarse = fbm_tileable(SIZE, SIZE, 3, 3, seed=2001)
    fine = fbm_tileable(SIZE, SIZE, 16, 4, seed=2002)
    granular = fbm_tileable(SIZE, SIZE, 64, 2, seed=2003)
    pattern = coarse * 0.3 + fine * 0.5 + granular * 0.3
    pattern = clip01(pattern)

    base = np.array([0.88, 0.76, 0.50])
    highlight = np.array([0.98, 0.92, 0.72])
    shadow = np.array([0.65, 0.54, 0.32])
    # 3-way blend
    rgb = np.empty((SIZE, SIZE, 3), dtype=np.float32)
    for i in range(3):
        shadow_mix = np.where(pattern < 0.3,
                              lerp(shadow[i], base[i], pattern / 0.3),
                              base[i])
        rgb[:, :, i] = np.where(pattern > 0.7,
                                lerp(base[i], highlight[i], (pattern - 0.7) / 0.3),
                                shadow_mix)

    # Occasional pebble specks
    rng = np.random.default_rng(2099)
    specks = rng.random((SIZE, SIZE)) > 0.997
    rgb[specks] = np.array([0.45, 0.38, 0.25])

    save_rgb("sand.png", rgb)


# ── Grass ───────────────────────────────────────────────────────────
def gen_grass():
    patchy = fbm_tileable(SIZE, SIZE, 4, 4, seed=3001)
    blades = fbm_tileable(SIZE, SIZE, 24, 3, seed=3002)
    pattern = patchy * 0.6 + blades * 0.4

    dark = np.array([0.18, 0.38, 0.12])
    mid = np.array([0.32, 0.58, 0.22])
    light = np.array([0.48, 0.72, 0.35])
    rgb = np.empty((SIZE, SIZE, 3), dtype=np.float32)
    for i in range(3):
        lo = np.where(pattern < 0.5,
                      lerp(dark[i], mid[i], pattern / 0.5),
                      mid[i])
        rgb[:, :, i] = np.where(pattern > 0.5,
                                lerp(mid[i], light[i], (pattern - 0.5) / 0.5),
                                lo)

    # Scatter darker dots for individual "blade" clumps
    rng = np.random.default_rng(3099)
    clumps = rng.random((SIZE, SIZE)) > 0.99
    rgb[clumps] = np.array([0.15, 0.30, 0.08])

    save_rgb("grass.png", rgb)


# ── Rock ────────────────────────────────────────────────────────────
def gen_rock():
    coarse = fbm_tileable(SIZE, SIZE, 2, 3, seed=4001)
    cracks_noise = fbm_tileable(SIZE, SIZE, 8, 4, seed=4002)
    streaks = fbm_tileable(SIZE, SIZE, 6, 2, seed=4003)
    pattern = coarse * 0.4 + cracks_noise * 0.4 + streaks * 0.2

    dark = np.array([0.28, 0.28, 0.30])
    mid = np.array([0.55, 0.55, 0.55])
    light = np.array([0.72, 0.70, 0.68])
    rgb = np.empty((SIZE, SIZE, 3), dtype=np.float32)
    for i in range(3):
        lo = np.where(pattern < 0.5,
                      lerp(dark[i], mid[i], pattern / 0.5),
                      mid[i])
        rgb[:, :, i] = np.where(pattern > 0.5,
                                lerp(mid[i], light[i], (pattern - 0.5) / 0.5),
                                lo)

    # Dark cracks where cracks_noise is in a thin band
    crack_mask = np.abs(cracks_noise - 0.5) < 0.04
    rgb[crack_mask] = np.array([0.12, 0.12, 0.14])

    save_rgb("rock.png", rgb)


# ── Snow ────────────────────────────────────────────────────────────
def gen_snow():
    coarse = fbm_tileable(SIZE, SIZE, 3, 3, seed=5001)
    fine = fbm_tileable(SIZE, SIZE, 32, 4, seed=5002)
    pattern = coarse * 0.5 + fine * 0.5

    shadow = np.array([0.78, 0.82, 0.92])
    base = np.array([0.92, 0.94, 0.98])
    highlight = np.array([1.00, 1.00, 1.00])
    rgb = np.empty((SIZE, SIZE, 3), dtype=np.float32)
    for i in range(3):
        lo = np.where(pattern < 0.5,
                      lerp(shadow[i], base[i], pattern / 0.5),
                      base[i])
        rgb[:, :, i] = np.where(pattern > 0.5,
                                lerp(base[i], highlight[i], (pattern - 0.5) / 0.5),
                                lo)

    save_rgb("snow.png", rgb)


def gen_atlas():
    """Composite the 5 terrain tiles into a 1280x256 horizontal atlas."""
    atlas = Image.new("RGB", (SIZE * 5, SIZE))
    for i, name in enumerate(["water", "sand", "grass", "rock", "snow"]):
        tile = Image.open(OUT_DIR / f"{name}.png")
        atlas.paste(tile, (i * SIZE, 0))
    atlas.save(OUT_DIR / "terrain_atlas.png")
    print(f"  wrote terrain_atlas.png")


if __name__ == "__main__":
    print(f"Generating terrain textures → {OUT_DIR}")
    gen_water()
    gen_sand()
    gen_grass()
    gen_rock()
    gen_snow()
    gen_atlas()
    print("done.")
