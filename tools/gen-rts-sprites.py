#!/usr/bin/env python3
# Bake 32×32 RGBA UI sprite PNGs for every RTS building and unit. Output
# under assets/textures/sprites/. Deterministic — same input → same bytes.
# stdlib + zlib only (no Pillow), per static-asset guidelines.

from __future__ import annotations
import os, struct, zlib

W = H = 32

# Buildings + units. Color is the in-game tint from rts.yaml; sprites render
# a recognizable silhouette in that color so they read at small sizes.
ENTITIES = [
    # id,             kind,        color,                   shape
    ("command_center","building",  (0.65, 0.70, 0.85),     "tower"),
    ("barracks",      "building",  (0.55, 0.35, 0.25),     "barracks"),
    ("factory",       "building",  (0.40, 0.42, 0.48),     "factory"),
    ("worker",        "character", (0.95, 0.85, 0.30),     "worker"),
    ("marine",        "character", (0.30, 0.65, 0.30),     "marine"),
    ("scout",         "character", (0.70, 0.65, 0.35),     "scout"),
    ("tank",          "vehicle",   (0.45, 0.45, 0.50),     "tank"),
    ("apc",           "vehicle",   (0.50, 0.45, 0.30),     "apc"),
]


# ── tiny PNG writer ───────────────────────────────────────────────────────
def _chunk(typ: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + typ + data
            + struct.pack(">I", zlib.crc32(typ + data) & 0xffffffff))


def write_png(path: str, w: int, h: int, rgba: bytearray) -> None:
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)  # 8-bit RGBA
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)  # filter: None
        raw.extend(rgba[y * stride : (y + 1) * stride])
    idat = zlib.compress(bytes(raw), 9)
    with open(path, "wb") as f:
        f.write(sig)
        f.write(_chunk(b"IHDR", ihdr))
        f.write(_chunk(b"IDAT", idat))
        f.write(_chunk(b"IEND", b""))


# ── pixel ops ─────────────────────────────────────────────────────────────
def make_buf() -> bytearray:
    return bytearray(W * H * 4)


def put(buf: bytearray, x: int, y: int, rgb, a: int = 255) -> None:
    if not (0 <= x < W and 0 <= y < H): return
    i = (y * W + x) * 4
    buf[i + 0] = max(0, min(255, int(rgb[0] * 255)))
    buf[i + 1] = max(0, min(255, int(rgb[1] * 255)))
    buf[i + 2] = max(0, min(255, int(rgb[2] * 255)))
    buf[i + 3] = a


def rect(buf: bytearray, x0: int, y0: int, x1: int, y1: int, rgb) -> None:
    for y in range(y0, y1):
        for x in range(x0, x1):
            put(buf, x, y, rgb)


def shade(rgb, k: float):
    return (max(0.0, min(1.0, rgb[0] * k)),
            max(0.0, min(1.0, rgb[1] * k)),
            max(0.0, min(1.0, rgb[2] * k)))


def outline(buf: bytearray, x0: int, y0: int, x1: int, y1: int, rgb) -> None:
    for x in range(x0, x1):
        put(buf, x, y0, rgb); put(buf, x, y1 - 1, rgb)
    for y in range(y0, y1):
        put(buf, x0, y, rgb); put(buf, x1 - 1, y, rgb)


# ── per-shape silhouettes ─────────────────────────────────────────────────
def draw_tower(buf, c):
    # Wide base, narrower mid, capped — Command Center vibe.
    rect(buf,  6, 22, 26, 30, shade(c, 0.7))   # base
    rect(buf,  9, 12, 23, 22, c)               # body
    rect(buf, 12,  6, 20, 12, shade(c, 1.15))  # tower
    rect(buf, 14,  3, 18,  6, shade(c, 1.3))   # antenna
    outline(buf, 6, 22, 26, 30, shade(c, 0.4))
    outline(buf, 9, 12, 23, 22, shade(c, 0.4))
    # Door
    rect(buf, 14, 25, 18, 30, shade(c, 0.3))


def draw_barracks(buf, c):
    # Long low building with peaked roof — barracks vibe.
    rect(buf,  4, 18, 28, 28, c)
    # Roof: stepped triangle
    for i in range(12):
        rect(buf, 4 + i, 18 - (i if i < 6 else 11 - i), 28 - i, 18 - (i if i < 6 else 11 - i) + 1,
             shade(c, 0.7))
    rect(buf, 14, 22, 18, 28, shade(c, 0.3))   # door
    rect(buf,  7, 21,  9, 23, shade(c, 1.3))   # windows
    rect(buf, 23, 21, 25, 23, shade(c, 1.3))
    outline(buf, 4, 18, 28, 28, shade(c, 0.4))


def draw_factory(buf, c):
    # Boxy with two smokestacks.
    rect(buf,  3, 16, 29, 28, c)
    # Sawtooth roof
    for i in range(0, 24, 4):
        for j in range(3):
            put(buf, 3 + i + j, 14 - j, shade(c, 0.7))
            put(buf, 3 + i + j + 1, 14 - j, shade(c, 0.7))
    # Smokestacks
    rect(buf,  7,  6,  9, 16, shade(c, 0.5))
    rect(buf, 22,  4, 24, 16, shade(c, 0.5))
    # Smoke puffs
    for (x, y) in [(8, 4), (9, 3), (7, 2), (23, 2), (24, 1), (22, 0)]:
        put(buf, x, y, (0.85, 0.85, 0.88), 200)
    outline(buf, 3, 16, 29, 28, shade(c, 0.4))


def draw_humanoid(buf, c, helmet=None, accent=None):
    skin = (0.85, 0.70, 0.55)
    accent = accent or shade(c, 0.6)
    helmet = helmet or shade(c, 1.2)
    # Head
    rect(buf, 13, 6, 19, 12, skin)
    rect(buf, 12, 5, 20, 8, helmet)        # helmet top
    # Torso
    rect(buf, 11, 12, 21, 22, c)
    # Belt
    rect(buf, 11, 21, 21, 23, accent)
    # Arms
    rect(buf,  8, 13, 11, 22, c)
    rect(buf, 21, 13, 24, 22, c)
    # Hands
    rect(buf,  8, 22, 11, 24, skin)
    rect(buf, 21, 22, 24, 24, skin)
    # Legs
    rect(buf, 12, 23, 16, 30, accent)
    rect(buf, 16, 23, 20, 30, accent)
    # Boots
    rect(buf, 11, 29, 16, 31, shade(accent, 0.5))
    rect(buf, 16, 29, 21, 31, shade(accent, 0.5))


def draw_worker(buf, c):
    # Yellow worker with hard hat (helmet) + tool silhouette.
    draw_humanoid(buf, c, helmet=(1.0, 0.85, 0.2), accent=(0.40, 0.30, 0.20))
    # Wrench in right hand
    rect(buf, 22, 24, 24, 30, (0.55, 0.55, 0.6))
    rect(buf, 21, 28, 25, 30, (0.55, 0.55, 0.6))


def draw_marine(buf, c):
    # Green marine with full helmet + rifle.
    draw_humanoid(buf, c, helmet=shade(c, 0.7), accent=shade(c, 0.5))
    # Rifle
    rect(buf,  4, 16, 11, 18, (0.20, 0.20, 0.20))
    rect(buf, 10, 14, 12, 20, (0.30, 0.30, 0.30))


def draw_scout(buf, c):
    # Tan scout, no helmet, hat brim, binoculars.
    draw_humanoid(buf, c, helmet=shade(c, 1.1), accent=shade(c, 0.6))
    # Hat brim
    rect(buf, 10, 7, 22, 8, shade(c, 0.5))
    # Binoculars at face
    rect(buf, 13, 8, 16, 10, (0.15, 0.15, 0.15))
    rect(buf, 16, 8, 19, 10, (0.15, 0.15, 0.15))


def draw_tank(buf, c):
    # Hull
    rect(buf,  3, 18, 29, 26, c)
    # Treads
    rect(buf,  2, 25, 30, 29, (0.15, 0.15, 0.15))
    for x in range(2, 30, 3):
        rect(buf, x, 26, x + 2, 28, (0.05, 0.05, 0.05))
    # Turret
    rect(buf, 10, 12, 22, 19, shade(c, 1.1))
    # Barrel
    rect(buf, 22, 14, 30, 17, shade(c, 0.7))
    # Hatch
    rect(buf, 14, 13, 18, 14, shade(c, 0.5))
    outline(buf, 3, 18, 29, 26, shade(c, 0.4))


def draw_apc(buf, c):
    # Long wedge shape
    rect(buf,  3, 17, 29, 26, c)
    # Sloped front
    for i in range(5):
        rect(buf, 3 + i, 14 + i, 8 + i, 15 + i, c)
    # Wheels
    for cx in (7, 14, 21, 27):
        rect(buf, cx - 2, 25, cx + 2, 29, (0.10, 0.10, 0.10))
        put(buf, cx, 27, (0.45, 0.45, 0.45))
    # Hatch / window
    rect(buf, 12, 16, 18, 19, shade(c, 1.4))
    # Antenna
    rect(buf, 25, 11, 26, 17, (0.20, 0.20, 0.20))
    outline(buf, 3, 17, 29, 26, shade(c, 0.4))


SHAPE_FNS = {
    "tower": draw_tower,
    "barracks": draw_barracks,
    "factory": draw_factory,
    "worker": draw_worker,
    "marine": draw_marine,
    "scout": draw_scout,
    "tank": draw_tank,
    "apc": draw_apc,
}


def main() -> None:
    out_dir = os.path.join(os.path.dirname(__file__), "..", "assets", "textures", "sprites")
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    for ent_id, kind, color, shape in ENTITIES:
        buf = make_buf()
        # Faint backdrop tile so transparent areas read against any UI color.
        for y in range(H):
            for x in range(W):
                if (x // 4 + y // 4) & 1:
                    put(buf, x, y, (0.10, 0.12, 0.14), 40)
                else:
                    put(buf, x, y, (0.06, 0.08, 0.10), 40)
        SHAPE_FNS[shape](buf, color)
        path = os.path.join(out_dir, f"{ent_id}.png")
        write_png(path, W, H, buf)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
