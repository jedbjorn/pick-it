#!/usr/bin/env python3
"""Generate the Pick-It MSI setup branding (installer/branding/).

Adapted from splash-it tools/make_installer_branding.py (splash-it commit 3f26fba),
from gantt-it tools/make_installer_branding.py (gantt-it commit aa83f35), which
follows rst-c's installer artwork: a Designs/OS navy sidebar with a white
wordmark and underline on the welcome/exit dialog, and a white banner with the
product mark in the right corner over a navy rule. The mark is the ribbon-icon
tile: a #D9D9D9 rounded tile with a #4F8EF7 eyedropper glyph.

The mark is tools/make_icons.py's 32 px glyph spec scaled to the size needed,
drawn by the same renderer as the ribbon icons, so the installer and the
ribbon share one mark.

The license page is generated from the repository's LICENSE (MIT), so the two
never drift apart.

Usage:  python3 tools/make_installer_branding.py
Writes: installer/branding/dialog.bmp    493x312, 24-bit BMP (WixUIDialogBmp)
        installer/branding/banner.bmp    493x58,  24-bit BMP (WixUIBannerBmp)
        installer/branding/pickit.ico  16/32/48/256 px      (ARPPRODUCTICON)
        installer/branding/License.rtf   from LICENSE         (WixUILicenseRtf)
Needs:  Pillow, and a bold sans font (Open Sans / DejaVu Sans / Liberation Sans)
        for the wordmark. The outputs are committed; rerun only to change them.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from make_icons import SPECS, VARIANTS, render

TILE, GLYPH = VARIANTS[""]
NAVY = (0x3B, 0x59, 0x98)  # rst-c installer sidebar / rule colour, as gantt-it
WHITE = (0xFF, 0xFF, 0xFF)

FONTS = [
    "/usr/share/fonts/TTF/OpenSans-Bold.ttf",
    "/usr/share/fonts/truetype/open-sans/OpenSans-Bold.ttf",
    "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/liberation/LiberationSans-Bold.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
]

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "installer" / "branding"


def mark(size: int) -> Image.Image:
    """The Pick-It mark at `size` px: the #4F8EF7 eyedropper on the #D9D9D9 tile."""
    radius, tip, parts = SPECS[32]
    k = size / 32
    return render(
        size,
        radius * k,
        (tip[0] * k, tip[1] * k),
        [(u0 * k, u1 * k, w0 * k, w1 * k) for u0, u1, w0, w1 in parts],
        TILE,
        GLYPH,
    )


def font(px: int) -> ImageFont.FreeTypeFont:
    for path in FONTS:
        if Path(path).exists():
            return ImageFont.truetype(path, px)
    raise SystemExit("no bold sans font found; edit FONTS in make_installer_branding.py")


def dialog() -> Image.Image:
    img = Image.new("RGB", (493, 312), WHITE)
    draw = ImageDraw.Draw(img)
    side = 165  # same sidebar width as rst-c; WixUI draws its text right of it
    draw.rectangle((0, 0, side - 1, 311), fill=NAVY)

    tile = mark(64)
    img.paste(tile, ((side - 64) // 2, 72), tile)

    f = font(26)
    text = "Pick-It"
    x0, y0, x1, y1 = draw.textbbox((0, 0), text, font=f)
    tx = (side - (x1 - x0)) // 2 - x0
    draw.text((tx, 160 - y0), text, font=f, fill=WHITE)

    draw.rectangle((20, 205, side - 21, 206), fill=WHITE)
    return img


def banner() -> Image.Image:
    img = Image.new("RGB", (493, 58), WHITE)
    draw = ImageDraw.Draw(img)
    tile = mark(32)
    img.paste(tile, (493 - 32 - 16, (56 - 32) // 2), tile)
    draw.rectangle((0, 56, 492, 57), fill=NAVY)
    return img


def rtf_escape(text: str) -> str:
    out = []
    for ch in text:
        if ch in "\\{}":
            out.append("\\" + ch)
        elif ord(ch) > 127:
            out.append(f"\\u{ord(ch)}?")
        else:
            out.append(ch)
    return "".join(out)


def license_rtf() -> str:
    """LICENSE as RTF, in gantt-it's License.rtf layout: a bold title line,
    then one RTF paragraph per LICENSE paragraph (hard line wraps joined)."""
    text = (ROOT / "LICENSE").read_text(encoding="utf-8")
    paras = [" ".join(p.split()) for p in text.strip().split("\n\n")]
    title, body = paras[0], paras[1:]
    lines = [
        r"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}",
        r"\viewkind4\uc1\pard\sa120\f0\fs20",
        r"{\b\fs28 Pick-It \endash  " + rtf_escape(title) + r"}\par",
    ]
    lines.append(r"\pard\sa120\fs20 " + rtf_escape(body[0]) + r"\par")
    for p in body[1:]:
        lines.append(rtf_escape(p) + r"\par")
    lines.append("}")
    return "\n".join(lines) + "\n"


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    dialog().save(OUT / "dialog.bmp")
    banner().save(OUT / "banner.bmp")
    sizes = [16, 32, 48, 256]
    icons = [mark(n) for n in sizes]
    icons[-1].save(OUT / "pickit.ico", sizes=[(n, n) for n in sizes], append_images=icons[:-1])
    (OUT / "License.rtf").write_text(license_rtf(), encoding="ascii", newline="\r\n")
    for name in ("dialog.bmp", "banner.bmp", "pickit.ico", "License.rtf"):
        print(f"wrote installer/branding/{name}")


if __name__ == "__main__":
    main()
