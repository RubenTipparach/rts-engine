#!/usr/bin/env python3
"""
Generate assets/textures/font_8x16.png — a 256-cell monospace bitmap font
atlas used by EngineUI for in-engine GPU-rendered button labels.

Layout
------
    16 columns x 16 rows = 256 cells, each 8 wide x 16 tall.
    Total atlas size: 128 x 256 px, RGBA8 with alpha = glyph mask.

    Cell (0, 0) = ASCII 0 (NUL) — reserved as a fully opaque white texel.
    EngineUI samples this slot when drawing button background quads, so the
    same shader / pipeline / mesh handles both backgrounds and text.
    Cells 32..126 contain printable ASCII glyphs in white-on-transparent.
    All other cells stay transparent (blank).

Re-run when changing the source font or cell size:
    python tools/gen-font-atlas.py
"""

import os
import sys
from PIL import Image, ImageDraw, ImageFont

CELL_W, CELL_H = 8, 16
GRID = 16
W, H = CELL_W * GRID, CELL_H * GRID  # 128 x 256

img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# Reserved fully-white cell at (0,0). EngineUI uses this UV for backgrounds
# so the text shader's color * tex.a fold gives the unmodulated bg color.
for y in range(CELL_H):
    for x in range(CELL_W):
        img.putpixel((x, y), (255, 255, 255, 255))

# Pick a monospace TrueType font. Anything 8-pixel-wide-friendly works;
# the rasterised pixels go straight into a fixed cell so we just bake the
# best-looking option that's actually present.
font = None
for path, size in [
    ("C:/Windows/Fonts/consola.ttf", 13),
    ("C:/Windows/Fonts/cour.ttf",    13),
    ("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 13),
    ("/System/Library/Fonts/Menlo.ttc", 13),
]:
    if os.path.exists(path):
        try:
            font = ImageFont.truetype(path, size)
            print(f"using font: {path} @ {size}pt", file=sys.stderr)
            break
        except Exception as e:
            print(f"couldn't load {path}: {e}", file=sys.stderr)
if font is None:
    print("no TrueType font found, falling back to PIL default", file=sys.stderr)
    font = ImageFont.load_default()

# Render printable ASCII into its cell. Vertical fudge centres typical
# ascenders/descenders within the 16-pixel cell.
for code in range(32, 127):
    col = code % GRID
    row = code // GRID
    cx = col * CELL_W
    cy = row * CELL_H
    draw.text((cx, cy - 2), chr(code), font=font, fill=(255, 255, 255, 255))

out = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "assets", "textures", "font_8x16.png"))
os.makedirs(os.path.dirname(out), exist_ok=True)
img.save(out)
print(f"wrote {out} ({W}x{H})")
