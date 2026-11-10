#!/usr/bin/env python3
"""Generate the Pick-It idle ribbon icons (docs/DESIGN.md, "Ribbon icons").

Family style: a full-bleed tile with anti-aliased rounded (transparent) corners
and a flat single-colour glyph. No outlines, gradients or text. The glyph is an
eyedropper lying on the 45-degree diagonal, tip bottom-left: a pointed nib, a
glass tube, a wide collar and a rounded bulb.

Revit's dark UI theme gets a dark variant of each icon: the same glyph in a
lighter blue on a dark tile. The add-in swaps them when Revit's theme changes.
These are the icons shown while no colour is picked; the picked-colour icon is
drawn at run time (src/PickIt.Revit/UI/ColorIcon.cs).

Usage:  python3 tools/make_icons.py
Writes: src/PickIt.Revit/Resources/icons/pick_32.png, pick_16.png,
        pick_32_dark.png and pick_16_dark.png
Needs:  Pillow
"""

from math import sqrt
from pathlib import Path

from PIL import Image, ImageDraw

# variant suffix -> (tile, glyph)
VARIANTS = {
    "": ((0xD9, 0xD9, 0xD9, 0xFF), (0x4F, 0x8E, 0xF7, 0xFF)),
    "_dark": ((0x2A, 0x30, 0x3A, 0xFF), (0x6F, 0xA3, 0xFA, 0xFF)),
}
SUPERSAMPLE = 8

# The glyph in its own axes: u runs along the eyedropper from the tip, v across it.
# size -> (corner radius px, tip (x, y) px, parts); a part is (u0, u1, half-width at u0,
# half-width at u1); the last part (the bulb) gets a round end.
SPECS = {
    32: (3, (5.5, 26.5), [(0, 5, 0.4, 2.0), (5, 15, 2.0, 2.0), (15, 18.5, 6.0, 6.0), (18.5, 25, 3.7, 3.7)]),
    16: (1.5, (2.5, 13.5), [(0, 2.5, 0.3, 1.0), (2.5, 7.2, 1.0, 1.0), (7.2, 9.2, 3.6, 3.6), (9.2, 12.6, 2.0, 2.0)]),
}

OUT = Path(__file__).resolve().parent.parent / "src" / "PickIt.Revit" / "Resources" / "icons"


def render(size, radius, tip, parts, tile, glyph) -> Image.Image:
    s = SUPERSAMPLE
    big = Image.new("RGBA", (size * s, size * s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(big)
    draw.rounded_rectangle((0, 0, size * s - 1, size * s - 1), radius=radius * s, fill=tile)

    k = 1 / sqrt(2)

    def point(u: float, v: float) -> tuple[float, float]:
        # u points up-right, v up-left.
        return ((tip[0] + (u - v) * k) * s, (tip[1] - (u + v) * k) * s)

    for u0, u1, w0, w1 in parts:
        draw.polygon([point(u0, -w0), point(u0, w0), point(u1, w1), point(u1, -w1)], fill=glyph)
    _, u1, _, w1 = parts[-1]
    cx, cy = point(u1, 0)
    draw.ellipse((cx - w1 * s, cy - w1 * s, cx + w1 * s, cy + w1 * s), fill=glyph)
    # Box-filter downsampling anti-aliases the diagonal edges and the corners.
    return big.reduce(s)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for suffix, (tile, glyph) in VARIANTS.items():
        for size, (radius, tip, parts) in SPECS.items():
            path = OUT / f"pick_{size}{suffix}.png"
            render(size, radius, tip, parts, tile, glyph).save(path, optimize=True)
            print(f"wrote {path.relative_to(OUT.parents[3])}")


if __name__ == "__main__":
    main()
