#!/usr/bin/env python3
# Bake 32x32 RGBA diffuse surface textures for every RTS building and unit.
# Output under assets/textures/surfaces/. Deterministic. stdlib + zlib only.
#
# Color palette: a small tile-friendly set of 64 RGB triplets — same shape as
# popular lospec packs (AAP-64, Resurrect-64). One slot, LIVERY (pure magenta
# 1,0,1), is reserved as the team-color marker. The shader detects this exact
# color in the sampled texel and replaces it with the per-instance team color.
# Magenta was picked because it's nowhere else in the palette and stands out
# in source files when artists are editing them by hand.

from __future__ import annotations
import os, struct, zlib

W = H = 32

# ── 64-color palette ─────────────────────────────────────────────────────
# Hand-picked pixel-art-friendly set. The last entry is the livery marker
# (pure magenta) — anything painted in this exact color becomes team color
# at render time. Don't reuse it elsewhere or unrelated regions will recolor
# with the team.
PALETTE = [
    # Earthen browns / dirt
    (0.10, 0.07, 0.05), (0.20, 0.13, 0.09), (0.32, 0.20, 0.13), (0.45, 0.30, 0.20),
    (0.55, 0.38, 0.25), (0.65, 0.48, 0.32), (0.75, 0.58, 0.40), (0.84, 0.70, 0.55),
    # Greens (vegetation, uniforms)
    (0.05, 0.12, 0.05), (0.10, 0.22, 0.08), (0.18, 0.35, 0.13), (0.25, 0.50, 0.20),
    (0.35, 0.60, 0.28), (0.45, 0.70, 0.35), (0.60, 0.80, 0.45), (0.78, 0.92, 0.60),
    # Blues
    (0.04, 0.06, 0.18), (0.08, 0.13, 0.30), (0.13, 0.22, 0.45), (0.20, 0.35, 0.60),
    (0.30, 0.50, 0.75), (0.45, 0.65, 0.85), (0.60, 0.78, 0.92), (0.78, 0.90, 0.98),
    # Reds / oranges
    (0.18, 0.04, 0.04), (0.35, 0.10, 0.08), (0.55, 0.18, 0.10), (0.72, 0.30, 0.15),
    (0.85, 0.45, 0.20), (0.95, 0.65, 0.30), (0.98, 0.80, 0.45), (1.00, 0.92, 0.65),
    # Yellows / sand
    (0.30, 0.25, 0.05), (0.50, 0.42, 0.10), (0.70, 0.60, 0.20), (0.85, 0.78, 0.35),
    (0.95, 0.90, 0.55), (0.98, 0.95, 0.75), (1.00, 0.98, 0.88), (0.92, 0.86, 0.62),
    # Greys / steel
    (0.05, 0.05, 0.05), (0.12, 0.12, 0.13), (0.22, 0.22, 0.25), (0.32, 0.32, 0.36),
    (0.45, 0.45, 0.50), (0.58, 0.58, 0.62), (0.72, 0.72, 0.75), (0.88, 0.88, 0.90),
    # Purples / accent
    (0.08, 0.04, 0.18), (0.18, 0.08, 0.32), (0.30, 0.15, 0.45), (0.45, 0.25, 0.58),
    # Cyans
    (0.04, 0.18, 0.20), (0.10, 0.35, 0.40), (0.20, 0.55, 0.62), (0.40, 0.78, 0.85),
    # Pure white / black for outlines
    (0.00, 0.00, 0.00), (1.00, 1.00, 1.00),
    # Padding to round out the natural-color area before the livery marker
    (0.55, 0.50, 0.45), (0.65, 0.60, 0.55), (0.40, 0.42, 0.38), (0.20, 0.22, 0.18),
    # ── Livery marker — last slot, pure magenta. The shader replaces this
    # exact color with team color. Don't rebrand it for "actual purple".
    (1.00, 0.00, 1.00),
]

LIVERY = PALETTE[-1]  # (1.0, 0.0, 1.0) pure magenta


# ── PNG writer ────────────────────────────────────────────────────────────
def _chunk(typ, data):
    return (struct.pack(">I", len(data)) + typ + data
            + struct.pack(">I", zlib.crc32(typ + data) & 0xffffffff))


def write_png(path, w, h, rgba):
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)
        raw.extend(rgba[y * stride : (y + 1) * stride])
    idat = zlib.compress(bytes(raw), 9)
    with open(path, "wb") as f:
        f.write(sig)
        f.write(_chunk(b"IHDR", ihdr))
        f.write(_chunk(b"IDAT", idat))
        f.write(_chunk(b"IEND", b""))


# ── deterministic value noise ─────────────────────────────────────────────
def hash_xy(x: int, y: int, seed: int) -> int:
    h = (x * 374761393 + y * 668265263 + seed * 982451653) & 0xffffffff
    h ^= h >> 13
    h = (h * 1274126177) & 0xffffffff
    h ^= h >> 16
    return h & 0xffffffff


def noise(x: int, y: int, seed: int) -> float:
    return (hash_xy(x, y, seed) & 0xffff) / 65535.0


def palette_quantize(rgb):
    """Snap an RGB triplet to the closest palette color, preserving the
    livery slot only when EXACTLY (1, 0, 1) is requested (no quantization
    bleed onto the marker)."""
    if rgb == LIVERY:
        return LIVERY
    best = PALETTE[0]
    best_d = 1e9
    for p in PALETTE[:-1]:  # exclude livery from auto-quantization
        d = (rgb[0] - p[0]) ** 2 + (rgb[1] - p[1]) ** 2 + (rgb[2] - p[2]) ** 2
        if d < best_d: best_d = d; best = p
    return best


def shade(rgb, k):
    return (max(0.0, min(1.0, rgb[0] * k)),
            max(0.0, min(1.0, rgb[1] * k)),
            max(0.0, min(1.0, rgb[2] * k)))


def put(buf, x, y, rgb, a=255):
    if not (0 <= x < W and 0 <= y < H): return
    rgb = palette_quantize(rgb)
    i = (y * W + x) * 4
    buf[i + 0] = max(0, min(255, int(rgb[0] * 255)))
    buf[i + 1] = max(0, min(255, int(rgb[1] * 255)))
    buf[i + 2] = max(0, min(255, int(rgb[2] * 255)))
    buf[i + 3] = a


def put_livery(buf, x, y, a=255):
    """Paint a pixel with the livery marker. Bypasses palette quantization
    so the exact (1, 0, 1) round-trips through the texture pipeline; the
    shader does an exact-equality test."""
    if not (0 <= x < W and 0 <= y < H): return
    i = (y * W + x) * 4
    buf[i + 0] = 255
    buf[i + 1] = 0
    buf[i + 2] = 255
    buf[i + 3] = a


# ── pattern generators ────────────────────────────────────────────────────
def gen_panels(c, seed, livery_band):
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            n = noise(x, y, seed) * 0.10 - 0.05
            k = 1.0 + n
            if x % 8 == 0 or y % 8 == 0:
                k *= 0.6
            elif x % 8 == 7 or y % 8 == 7:
                k *= 0.85
            put(buf, x, y, shade(c, k))
    if livery_band:
        # Top stripe = team livery — most visible angle on a building.
        for y in range(0, 4):
            for x in range(W):
                put_livery(buf, x, y)
    return buf


def gen_bricks(c, seed, livery_band):
    buf = bytearray(W * H * 4)
    for y in range(H):
        row = y // 4
        offset = (row & 1) * 4
        for x in range(W):
            xx = (x + offset) % 8
            seam = (xx == 0 or y % 4 == 0)
            n = noise(x // 2, y // 2, seed) * 0.18 - 0.09
            k = 0.5 if seam else 1.0 + n
            put(buf, x, y, shade(c, k))
    if livery_band:
        # Banner on the upper third.
        for y in range(4, 8):
            for x in range(W):
                put_livery(buf, x, y)
    return buf


def gen_corrugated(c, seed, livery_band):
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            phase = (x % 4) / 3.0
            k = 0.7 + 0.55 * (1.0 - abs(phase - 0.5) * 2.0)
            n = noise(x, y, seed) * 0.10 - 0.05
            if 14 <= y <= 18:
                k *= 0.85
            put(buf, x, y, shade(c, k + n))
    if livery_band:
        for y in range(2, 5):
            for x in range(W):
                put_livery(buf, x, y)
    return buf


def gen_armor(c, seed, livery_band):
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            n = (noise(x // 3, y // 3, seed) - 0.5) * 0.2
            n2 = (noise(x, y, seed + 7) - 0.5) * 0.06
            put(buf, x, y, shade(c, 1.0 + n + n2))
    for ry in range(4, H, 8):
        for rx in range(4, W, 8):
            put(buf, rx, ry, shade(c, 0.4))
            put(buf, rx + 1, ry, shade(c, 0.55))
            put(buf, rx, ry + 1, shade(c, 0.55))
    if livery_band:
        # Side panel — vertical stripe down the middle for a tank/APC livery.
        for y in range(8, 24):
            for x in range(14, 18):
                put_livery(buf, x, y)
    return buf


def gen_uniform(c, seed, livery_band):
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            n = (noise(x, y, seed) - 0.5) * 0.16
            if (x + y) & 1: n -= 0.05
            put(buf, x, y, shade(c, 1.0 + n))
    if livery_band:
        # Shoulder patch — a small block in the upper-left corner so the
        # tiny unit silhouette still gets a recognisable team marker.
        for y in range(2, 8):
            for x in range(2, 8):
                put_livery(buf, x, y)
    return buf


PATTERN_FNS = {
    "panels": gen_panels,
    "bricks": gen_bricks,
    "corrugated": gen_corrugated,
    "armor": gen_armor,
    "uniform": gen_uniform,
}


def stable_seed(text: str) -> int:
    return zlib.adler32(text.encode("utf-8")) & 0xffffffff


# id, base color, pattern, has-livery-band
ENTITIES = [
    ("command_center", (0.65, 0.70, 0.85), "panels",     True),
    ("barracks",       (0.55, 0.35, 0.25), "bricks",     True),
    ("factory",        (0.40, 0.42, 0.48), "corrugated", True),
    ("worker",         (0.95, 0.85, 0.30), "uniform",    True),
    ("marine",         (0.30, 0.65, 0.30), "uniform",    True),
    ("scout",          (0.70, 0.65, 0.35), "uniform",    True),
    ("tank",           (0.45, 0.45, 0.50), "armor",      True),
    ("apc",            (0.50, 0.45, 0.30), "armor",      True),
]


def main():
    out_dir = os.path.normpath(os.path.join(os.path.dirname(__file__),
                                            "..", "assets", "textures", "surfaces"))
    os.makedirs(out_dir, exist_ok=True)
    for ent_id, color, pattern, has_livery in ENTITIES:
        seed = stable_seed(ent_id)
        buf = PATTERN_FNS[pattern](color, seed, has_livery)
        path = os.path.join(out_dir, f"{ent_id}.png")
        write_png(path, W, H, buf)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
