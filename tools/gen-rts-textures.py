#!/usr/bin/env python3
# Bake 32×32 RGBA diffuse surface textures for every RTS building and unit.
# Output under assets/textures/surfaces/. One per entity, deterministic.
# stdlib + zlib only.

from __future__ import annotations
import os, struct, zlib

W = H = 32

# id, base color, pattern. Base color matches rts.yaml so the texture and
# untextured fallback agree.
ENTITIES = [
    ("command_center", (0.65, 0.70, 0.85), "panels"),
    ("barracks",       (0.55, 0.35, 0.25), "bricks"),
    ("factory",        (0.40, 0.42, 0.48), "corrugated"),
    ("worker",         (0.95, 0.85, 0.30), "uniform"),
    ("marine",         (0.30, 0.65, 0.30), "uniform"),
    ("scout",          (0.70, 0.65, 0.35), "uniform"),
    ("tank",           (0.45, 0.45, 0.50), "armor"),
    ("apc",            (0.50, 0.45, 0.30), "armor"),
]


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


def shade(rgb, k):
    return (max(0.0, min(1.0, rgb[0] * k)),
            max(0.0, min(1.0, rgb[1] * k)),
            max(0.0, min(1.0, rgb[2] * k)))


def put(buf, x, y, rgb, a=255):
    if not (0 <= x < W and 0 <= y < H): return
    i = (y * W + x) * 4
    buf[i + 0] = max(0, min(255, int(rgb[0] * 255)))
    buf[i + 1] = max(0, min(255, int(rgb[1] * 255)))
    buf[i + 2] = max(0, min(255, int(rgb[2] * 255)))
    buf[i + 3] = a


# ── pattern generators ────────────────────────────────────────────────────
def gen_panels(c, seed):
    # Clean panels separated by darker seams every 8 px.
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            # Per-pixel grain
            n = noise(x, y, seed) * 0.10 - 0.05
            k = 1.0 + n
            if x % 8 == 0 or y % 8 == 0:
                k *= 0.6
            elif x % 8 == 7 or y % 8 == 7:
                k *= 0.85
            put(buf, x, y, shade(c, k))
    return buf


def gen_bricks(c, seed):
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
    return buf


def gen_corrugated(c, seed):
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            # Vertical ribs every 4 px with smooth shading.
            phase = (x % 4) / 3.0  # 0..1
            # Triangle wave: dark at rib edges, bright at peaks.
            k = 0.7 + 0.55 * (1.0 - abs(phase - 0.5) * 2.0)
            n = noise(x, y, seed) * 0.10 - 0.05
            # Horizontal grime band
            if 14 <= y <= 18:
                k *= 0.85
            put(buf, x, y, shade(c, k + n))
    return buf


def gen_armor(c, seed):
    # Rolled steel: low-frequency mottle + occasional rivet dots.
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            n = (noise(x // 3, y // 3, seed) - 0.5) * 0.2
            n2 = (noise(x, y, seed + 7) - 0.5) * 0.06
            put(buf, x, y, shade(c, 1.0 + n + n2))
    # Rivets at 8-px lattice
    for ry in range(4, H, 8):
        for rx in range(4, W, 8):
            put(buf, rx, ry, shade(c, 0.4))
            put(buf, rx + 1, ry, shade(c, 0.55))
            put(buf, rx, ry + 1, shade(c, 0.55))
    return buf


def gen_uniform(c, seed):
    # Simple woven cloth: per-pixel tint variation, no large features.
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            n = (noise(x, y, seed) - 0.5) * 0.16
            # Slight stitch lines every 2 px
            if (x + y) & 1:
                n -= 0.05
            put(buf, x, y, shade(c, 1.0 + n))
    return buf


PATTERN_FNS = {
    "panels": gen_panels,
    "bricks": gen_bricks,
    "corrugated": gen_corrugated,
    "armor": gen_armor,
    "uniform": gen_uniform,
}


def stable_seed(text: str) -> int:
    # Python's built-in hash() randomizes strings across runs (PYTHONHASHSEED),
    # so derive a stable per-entity seed from a deterministic hash of the bytes.
    return zlib.adler32(text.encode("utf-8")) & 0xffffffff


def main():
    out_dir = os.path.normpath(os.path.join(os.path.dirname(__file__),
                                            "..", "assets", "textures", "surfaces"))
    os.makedirs(out_dir, exist_ok=True)
    for i, (ent_id, color, pattern) in enumerate(ENTITIES):
        seed = stable_seed(ent_id)
        buf = PATTERN_FNS[pattern](color, seed)
        path = os.path.join(out_dir, f"{ent_id}.png")
        write_png(path, W, H, buf)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
