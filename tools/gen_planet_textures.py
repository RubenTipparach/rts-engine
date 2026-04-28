"""
Generate terrain texture atlases for multiple planets.
Each planet gets 5 tileable 256×256 PNGs + a 1280×256 atlas.
Reuses the noise infrastructure from the Earth texture generator.
"""
import math
import numpy as np
from PIL import Image
from pathlib import Path

SIZE = 256
BASE_DIR = Path(__file__).resolve().parents[1] / "src" / "RtsEngine.Wasm" / "wwwroot" / "textures"


def value_noise_tileable(width, height, period, seed):
    rng = np.random.default_rng(seed)
    grid = rng.random((period + 1, period + 1)).astype(np.float32)
    grid[period, :] = grid[0, :]
    grid[:, period] = grid[:, 0]
    xs = np.linspace(0, period, width, endpoint=False)
    ys = np.linspace(0, period, height, endpoint=False)
    x, y = np.meshgrid(xs, ys)
    ix, iy = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - ix, y - iy
    ux = fx * fx * (3 - 2 * fx)
    uy = fy * fy * (3 - 2 * fy)
    a, b = grid[iy, ix], grid[iy, ix + 1]
    c, d = grid[iy + 1, ix], grid[iy + 1, ix + 1]
    return (1 - uy) * ((1 - ux) * a + ux * b) + uy * ((1 - ux) * c + ux * d)


def fbm(w, h, base_period, octaves, seed, persistence=0.5):
    total = np.zeros((h, w), np.float32)
    amp, mx = 1.0, 0.0
    for i in range(octaves):
        total += amp * value_noise_tileable(w, h, base_period * (2 ** i), seed + i * 31)
        mx += amp; amp *= persistence
    return total / mx


def clip01(a): return np.clip(a, 0.0, 1.0)
def lerp(a, b, t): return a + (b - a) * t


def save_rgb(out_dir, filename, rgb):
    img = (clip01(rgb) * 255).astype(np.uint8)
    Image.fromarray(img, "RGB").save(out_dir / filename)


def make_atlas(out_dir, names):
    tiles = [Image.open(out_dir / f"{n}.png") for n in names]
    atlas = Image.new("RGB", (SIZE * len(tiles), SIZE))
    for i, t in enumerate(tiles):
        atlas.paste(t, (i * SIZE, 0))
    atlas.save(out_dir / "terrain_atlas.png")


# ── Moon ─────────────────────────────────────────────────────────────

def gen_moon():
    out = BASE_DIR / "moon"
    out.mkdir(parents=True, exist_ok=True)
    seed_base = 7000

    # Level 0: deep crater floor (very dark gray)
    n = fbm(SIZE, SIZE, 4, 4, seed_base)
    rgb = np.stack([lerp(0.15, 0.22, n)] * 3, axis=-1)
    save_rgb(out, "crater_floor.png", rgb)

    # Level 1: lowland regolith (medium gray)
    n = fbm(SIZE, SIZE, 6, 4, seed_base + 100)
    f = fbm(SIZE, SIZE, 24, 3, seed_base + 101)
    pattern = n * 0.6 + f * 0.4
    rgb = np.stack([lerp(0.30, 0.42, pattern)] * 3, axis=-1)
    # Subtle brownish tint
    rgb[:, :, 0] *= 1.02; rgb[:, :, 2] *= 0.96
    save_rgb(out, "regolith.png", rgb)

    # Level 2: highland (lighter gray)
    n = fbm(SIZE, SIZE, 5, 4, seed_base + 200)
    rgb = np.stack([lerp(0.45, 0.58, n)] * 3, axis=-1)
    save_rgb(out, "highland.png", rgb)

    # Level 3: mountain/rim (bright gray with cracks)
    n = fbm(SIZE, SIZE, 3, 3, seed_base + 300)
    cracks = fbm(SIZE, SIZE, 8, 4, seed_base + 301)
    base = lerp(0.55, 0.70, n)
    crack_mask = np.abs(cracks - 0.5) < 0.03
    base[crack_mask] = 0.35
    rgb = np.stack([base] * 3, axis=-1)
    save_rgb(out, "mountain.png", rgb)

    # Level 4: peak (bright white-gray)
    n = fbm(SIZE, SIZE, 4, 3, seed_base + 400)
    rgb = np.stack([lerp(0.72, 0.82, n)] * 3, axis=-1)
    save_rgb(out, "peak.png", rgb)

    make_atlas(out, ["crater_floor", "regolith", "highland", "mountain", "peak"])
    print("  Moon textures done")


# ── Mars ─────────────────────────────────────────────────────────────

def gen_mars():
    out = BASE_DIR / "mars"
    out.mkdir(parents=True, exist_ok=True)
    seed_base = 8000

    # 0: deep canyon (dark red-brown)
    n = fbm(SIZE, SIZE, 4, 4, seed_base)
    r = lerp(0.30, 0.42, n); g = lerp(0.12, 0.18, n); b = lerp(0.08, 0.12, n)
    save_rgb(out, "canyon.png", np.stack([r, g, b], -1))

    # 1: lowland dust (orange-red)
    n = fbm(SIZE, SIZE, 6, 4, seed_base + 100)
    f = fbm(SIZE, SIZE, 20, 3, seed_base + 101)
    p = n * 0.6 + f * 0.4
    r = lerp(0.65, 0.80, p); g = lerp(0.30, 0.42, p); b = lerp(0.15, 0.22, p)
    save_rgb(out, "dust.png", np.stack([r, g, b], -1))

    # 2: plains (tan-orange)
    n = fbm(SIZE, SIZE, 5, 4, seed_base + 200)
    r = lerp(0.72, 0.85, n); g = lerp(0.42, 0.55, n); b = lerp(0.25, 0.32, n)
    save_rgb(out, "plains.png", np.stack([r, g, b], -1))

    # 3: volcanic rock (dark basalt)
    n = fbm(SIZE, SIZE, 3, 3, seed_base + 300)
    r = lerp(0.25, 0.38, n); g = lerp(0.20, 0.30, n); b = lerp(0.18, 0.25, n)
    save_rgb(out, "basalt.png", np.stack([r, g, b], -1))

    # 4: ice cap (white with blue tinge)
    n = fbm(SIZE, SIZE, 4, 3, seed_base + 400)
    r = lerp(0.85, 0.95, n); g = lerp(0.88, 0.96, n); b = lerp(0.92, 1.00, n)
    save_rgb(out, "ice_cap.png", np.stack([r, g, b], -1))

    make_atlas(out, ["canyon", "dust", "plains", "basalt", "ice_cap"])
    print("  Mars textures done")


# ── Venus ────────────────────────────────────────────────────────────

def gen_venus():
    out = BASE_DIR / "venus"
    out.mkdir(parents=True, exist_ok=True)
    seed_base = 9000

    # 0: deep lowland (dark amber)
    n = fbm(SIZE, SIZE, 3, 4, seed_base)
    r = lerp(0.35, 0.48, n); g = lerp(0.25, 0.35, n); b = lerp(0.10, 0.15, n)
    save_rgb(out, "lowland.png", np.stack([r, g, b], -1))

    # 1: volcanic plains (orange-yellow)
    n = fbm(SIZE, SIZE, 5, 4, seed_base + 100)
    r = lerp(0.70, 0.85, n); g = lerp(0.50, 0.62, n); b = lerp(0.18, 0.25, n)
    save_rgb(out, "volcanic.png", np.stack([r, g, b], -1))

    # 2: tessera (cracked highland, yellowish)
    n = fbm(SIZE, SIZE, 4, 4, seed_base + 200)
    cracks = fbm(SIZE, SIZE, 10, 3, seed_base + 201)
    r = lerp(0.75, 0.88, n); g = lerp(0.60, 0.72, n); b = lerp(0.28, 0.35, n)
    rgb = np.stack([r, g, b], -1)
    crack_mask = np.abs(cracks - 0.5) < 0.03
    rgb[crack_mask] = np.array([0.40, 0.30, 0.12])
    save_rgb(out, "tessera.png", rgb)

    # 3: shield volcano (dark with lava streaks)
    n = fbm(SIZE, SIZE, 3, 3, seed_base + 300)
    r = lerp(0.30, 0.45, n); g = lerp(0.18, 0.28, n); b = lerp(0.08, 0.12, n)
    # Lava veins
    lava = fbm(SIZE, SIZE, 12, 3, seed_base + 301)
    lava_mask = lava > 0.7
    rgb = np.stack([r, g, b], -1)
    rgb[lava_mask] = np.array([0.90, 0.35, 0.05])
    save_rgb(out, "shield.png", rgb)

    # 4: maxwell peak (bright sulfur yellow)
    n = fbm(SIZE, SIZE, 4, 3, seed_base + 400)
    r = lerp(0.88, 0.95, n); g = lerp(0.82, 0.90, n); b = lerp(0.45, 0.55, n)
    save_rgb(out, "maxwell.png", np.stack([r, g, b], -1))

    make_atlas(out, ["lowland", "volcanic", "tessera", "shield", "maxwell"])
    print("  Venus textures done")


# ── Ice planet ───────────────────────────────────────────────────────

def gen_ice():
    out = BASE_DIR / "ice"
    out.mkdir(parents=True, exist_ok=True)
    seed_base = 10000

    # 0: frozen ocean (dark blue-gray)
    n = fbm(SIZE, SIZE, 4, 4, seed_base)
    xs = np.linspace(0, 2*math.pi, SIZE, endpoint=False)
    ys = np.linspace(0, 2*math.pi, SIZE, endpoint=False)
    x, y = np.meshgrid(xs, ys)
    ripple = np.sin(x * 5 + n * 2) * 0.5 + 0.5
    r = lerp(0.12, 0.22, ripple); g = lerp(0.18, 0.30, ripple); b = lerp(0.30, 0.48, ripple)
    save_rgb(out, "frozen_ocean.png", np.stack([r, g, b], -1))

    # 1: ice shelf (pale blue)
    n = fbm(SIZE, SIZE, 6, 4, seed_base + 100)
    r = lerp(0.65, 0.78, n); g = lerp(0.72, 0.85, n); b = lerp(0.82, 0.95, n)
    save_rgb(out, "ice_shelf.png", np.stack([r, g, b], -1))

    # 2: tundra (gray-green)
    n = fbm(SIZE, SIZE, 5, 4, seed_base + 200)
    r = lerp(0.35, 0.48, n); g = lerp(0.40, 0.52, n); b = lerp(0.35, 0.45, n)
    save_rgb(out, "tundra.png", np.stack([r, g, b], -1))

    # 3: frozen rock (blue-gray)
    n = fbm(SIZE, SIZE, 3, 3, seed_base + 300)
    r = lerp(0.38, 0.50, n); g = lerp(0.42, 0.55, n); b = lerp(0.52, 0.65, n)
    save_rgb(out, "frozen_rock.png", np.stack([r, g, b], -1))

    # 4: glacier peak (bright white-blue)
    n = fbm(SIZE, SIZE, 4, 3, seed_base + 400)
    r = lerp(0.85, 0.95, n); g = lerp(0.90, 0.98, n); b = lerp(0.95, 1.00, n)
    save_rgb(out, "glacier.png", np.stack([r, g, b], -1))

    make_atlas(out, ["frozen_ocean", "ice_shelf", "tundra", "frozen_rock", "glacier"])
    print("  Ice planet textures done")


if __name__ == "__main__":
    print("Generating planet textures...")
    gen_moon()
    gen_mars()
    gen_venus()
    gen_ice()
    print("Done.")
