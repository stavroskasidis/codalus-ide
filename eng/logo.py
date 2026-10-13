#!/usr/bin/env python3
"""Writes the Codalus IDE logo files from one geometry (M39, ADR 0056): the mark chosen by the maintainer on 2026-10-13
("F4": two wings of two feathers each, violet and ember).

    python eng/logo.py            # assets/logo/*.svg, assets/logo/png/*.png, assets/logo/codalus.ico, the site's copies

Needs Pillow. The PNGs are drawn here, 4x supersampled, so they don't depend on an SVG renderer.
"""
import io
import os
import struct

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOGO = os.path.join(ROOT, "assets", "logo")

# The mark on a 256 x 256 canvas: each wing an outer and an inner feather, mirrored.
FEATHERS = [
    # (points, gradient, opacity)
    ([(20, 218), (124, 38), (124, 92), (58, 208)], "violet", 1.0),
    ([(74, 204), (124, 117.4), (124, 161.4), (104, 196)], "violet", 0.82),
    ([(236, 218), (132, 38), (132, 92), (198, 208)], "ember", 1.0),
    ([(182, 204), (132, 117.4), (132, 161.4), (152, 196)], "ember", 0.82),
]
# Vertical gradients from the wings' tips (y = 38) to their bases (y = 218).
GRADIENTS = {
    "violet": ("#A47BFF", "#5B2EC7"),
    "ember": ("#FFC24A", "#F0662A"),
}
MONO = "#6B3DDB"
WORD_NAME = {"light": "#1E1636", "dark": "#F2EEFF"}
WORD_SUB = {"light": "#6A3DD8", "dark": "#B9A0FF"}
PNG_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024]
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]


def svg_points(points):
    return " ".join(f"{x:g},{y:g}" for x, y in points)


def svg_defs():
    stops = []
    for name, (top, bottom) in GRADIENTS.items():
        stops.append(
            f'    <linearGradient id="{name}" gradientUnits="userSpaceOnUse" x1="0" y1="38" x2="0" y2="218">\n'
            f'      <stop offset="0" stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/>\n'
            f"    </linearGradient>")
    return "  <defs>\n" + "\n".join(stops) + "\n  </defs>\n"


def svg_feathers(indent="  "):
    lines = []
    for points, gradient, opacity in FEATHERS:
        extra = "" if opacity == 1 else f' fill-opacity="{opacity:g}"'
        lines.append(f'{indent}<polygon fill="url(#{gradient})"{extra} points="{svg_points(points)}"/>')
    return "\n".join(lines) + "\n"


COMMENT = "  <!-- Codalus IDE: two wings (code and Daedalus), each an outer and an inner feather, violet and ember. -->\n"


def mark_svg():
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Codalus IDE">\n'
            + COMMENT + svg_defs() + svg_feathers() + "</svg>\n")


def mono_svg():
    lines = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Codalus IDE">',
             "  <!-- Single-colour variant (follows currentColor); the right wing and the inner feathers are lighter. -->",
             '  <g fill="currentColor">']
    for i, (points, _, opacity) in enumerate(FEATHERS):
        alpha = opacity * (1.0 if i < 2 else 0.6)
        extra = "" if alpha == 1 else f' opacity="{alpha:.2f}"'
        lines.append(f'    <polygon{extra} points="{svg_points(points)}"/>')
    lines += ["  </g>", "</svg>", ""]
    return "\n".join(lines)


def wordmark_svg(theme):
    style = ""
    if theme == "both":
        style = ("  <style>\n    .name { fill: %s; }\n    .sub { fill: %s; }\n"
                 "    @media (prefers-color-scheme: dark) { .name { fill: %s; } .sub { fill: %s; } }\n  </style>\n"
                 % (WORD_NAME["light"], WORD_SUB["light"], WORD_NAME["dark"], WORD_SUB["dark"]))
    else:
        style = "  <style>\n    .name { fill: %s; }\n    .sub { fill: %s; }\n  </style>\n" % (WORD_NAME[theme], WORD_SUB[theme])
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 960 256" role="img" aria-label="Codalus IDE">\n'
            + style + svg_defs() + svg_feathers()
            + '  <text x="272" y="166" font-family="\'Segoe UI Variable Display\', \'Segoe UI\', Inter, \'Helvetica Neue\', Arial, sans-serif" font-size="104" letter-spacing="-2">\n'
            + '    <tspan class="name" font-weight="700">Codalus</tspan><tspan class="sub" font-weight="400" dx="24">IDE</tspan>\n'
            + "  </text>\n</svg>\n")


def hex_rgb(value):
    return tuple(int(value[i:i + 2], 16) for i in (1, 3, 5))


def render(size):
    """The mark as an RGBA image of size x size, drawn at 4x and reduced."""
    scale = 4
    big = size * scale
    factor = big / 256
    out = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    for points, gradient, opacity in FEATHERS:
        top, bottom = (hex_rgb(c) for c in GRADIENTS[gradient])
        fill = Image.new("RGBA", (big, big))
        draw = ImageDraw.Draw(fill)
        for y in range(big):
            t = min(1.0, max(0.0, (y / factor - 38) / (218 - 38)))
            color = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
            draw.line([(0, y), (big, y)], fill=color + (255,))
        mask = Image.new("L", (big, big), 0)
        ImageDraw.Draw(mask).polygon([(x * factor, y * factor) for x, y in points], fill=round(255 * opacity))
        fill.putalpha(mask)
        out = Image.alpha_composite(out, fill)
    return out.resize((size, size), Image.LANCZOS)


def ico(images):
    """An .ico of PNG images (Vista and later read PNG entries at every size)."""
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for image in images:
        buffer = io.BytesIO()
        image.save(buffer, "PNG", optimize=True)
        blob = buffer.getvalue()
        w = image.width if image.width < 256 else 0
        entries.append(struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)
    return struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs)


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    mode = "wb" if isinstance(data, bytes) else "w"
    with open(path, mode, **({} if mode == "wb" else {"encoding": "utf-8", "newline": "\n"})) as f:
        f.write(data)


def main():
    write(os.path.join(LOGO, "codalus.svg"), mark_svg())
    write(os.path.join(LOGO, "codalus-mono.svg"), mono_svg())
    write(os.path.join(LOGO, "codalus-wordmark.svg"), wordmark_svg("both"))
    write(os.path.join(LOGO, "codalus-wordmark-light.svg"), wordmark_svg("light"))
    write(os.path.join(LOGO, "codalus-wordmark-dark.svg"), wordmark_svg("dark"))
    rendered = {size: render(size) for size in PNG_SIZES}
    for size, image in rendered.items():
        buffer = io.BytesIO()
        image.save(buffer, "PNG", optimize=True)
        write(os.path.join(LOGO, "png", f"codalus-{size}.png"), buffer.getvalue())
    write(os.path.join(LOGO, "codalus.ico"), ico([rendered[s] for s in ICO_SIZES]))

    site = os.path.join(ROOT, "website", "wwwroot", "assets")
    write(os.path.join(site, "logo.svg"), mark_svg())
    for name, size in (("favicon-32.png", 32), ("icon-256.png", 256)):
        buffer = io.BytesIO()
        rendered[size].save(buffer, "PNG", optimize=True)
        write(os.path.join(site, name), buffer.getvalue())
    print("logo files written")


if __name__ == "__main__":
    main()
