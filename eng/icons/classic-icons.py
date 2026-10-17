#!/usr/bin/env python3
"""Draws the built-in "classic" file, folder and project icons (src/Codalus.Shell/Icons/Classic/*.svg).

They follow a classic IDE's Solution Explorer: 16 px pixel drawings, a gray outline page with a small colored badge in its
lower-left corner (the page's outline cut around it), tan outline folders, and outline project boxes with the
language in color. Gray parts have no fill, so the shell draws them in Shell.Icon.Outline; colored parts are written in
the dark theme's palette colors and take each theme's nearest palette color (Icon.PaletteColor), folders its tan.

Run it after changing a drawing and commit the SVGs: python eng/icons/classic-icons.py
"""
import os

GRAY = None
GREEN, RED, ORANGE, PURPLE, YELLOW, BLUE = "#89D185", "#F48771", "#E8AB53", "#B180D7", "#FFCC00", "#75BEFF"
TAN = "#8A6320"  # Icon.PaletteColor: "Folder"

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Codalus.Shell", "Icons", "Classic")


def bitmap(rows, x=0, y=0):
    """The pixels of a drawing: '#' is on, anything else off."""
    return {(x + c, y + r) for r, row in enumerate(rows) for c, ch in enumerate(row) if ch == "#"}


def around(pixels):
    """The pixels' bounding box grown by one: what a badge cuts out of the drawing under it."""
    xs, ys = [x for x, _ in pixels], [y for _, y in pixels]
    return {(x, y) for x in range(min(xs) - 1, max(xs) + 2) for y in range(min(ys) - 1, max(ys) + 2)}


# A 5-pixel-high font for badges.
GLYPHS = {
    "C": ["###", "#.#", "#..", "#.#", "###"],
    "#": [".#.#.", "#####", ".#.#.", "#####", ".#.#."],
    "F": ["###", "#..", "##.", "#..", "#.."],
    "Y": ["#.#", "#.#", ".#.", ".#.", ".#."],
    "M": ["#...#", "##.##", "#.#.#", "#...#", "#...#"],
    "L": ["#..", "#..", "#..", "#..", "###"],
    "J": ["..#", "..#", "..#", "#.#", ".#."],
    "S": [".##", "#..", ".#.", "..#", "##."],
    "T": ["###", ".#.", ".#.", ".#.", ".#."],
    "P": ["##.", "#.#", "##.", "#..", "#.."],
    "D": ["##.", "#.#", "#.#", "#.#", "##."],
    "W": ["#...#", "#...#", "#.#.#", "#.#.#", ".#.#."],
    "0": ["###", "#.#", "#.#", "#.#", "###"],
    "1": [".#", "##", ".#", ".#", ".#"],
    "4": ["#.#", "#.#", "###", "..#", "..#"],
    "↓": ["..#..", "..#..", "#.#.#", ".###.", "..#.."],
    "<>": ["..#.#..", ".#...#.", "#.....#", ".#...#.", "..#.#.."],
    ">_": ["#....", ".#...", "..#..", ".#...", "#.###"],
    "→": ["..#..", "...#.", "#####", "...#.", "..#.."],
    "db": [".###.", "#####", "#...#", "#...#", ".###."],
    "lock": [".###.", ".#.#.", "#####", "##.##", "#####"],
    "key": [".##....", "#..####", "#..#.#.", ".##...."],
    "seal": [".###.", "#...#", "#...#", ".###.", ".#.#."],
    "box": ["#####", "#####", "#...#", "#...#", "#####"],
    "grid": ["#######", "#..#..#", "#######", "#..#..#", "#######"],
    "branch": ["#...#", "#...#", "#..#.", "###..", "#...."],
    "wrench": ["....#.#", "....###", "...##..", "..##...", ".##....", "##.....", "#......"],
    "whale": ["..#....", ".###...", "#######", "#######", ".#####."],
}


def text(s, x, y):
    """A badge's text: glyphs (or one named glyph) one pixel apart."""
    if s in GLYPHS:
        return bitmap(GLYPHS[s], x, y)
    pixels = set()
    for ch in s:
        pixels |= bitmap(GLYPHS[ch], x, y)
        x += len(GLYPHS[ch][0]) + 1
    return pixels


def width(s):
    return len(GLYPHS[s][0]) if s in GLYPHS else sum(len(GLYPHS[ch][0]) for ch in s) + len(s) - 1


# A page with a folded corner, 10 x 14, at x = 3 (4 when a wide badge needs the room).
PAGE = [
    ".#####....",
    "#....##...",
    "#....#.#..",
    "#....#..#.",
    "#....#####",
    "#........#",
    "#........#",
    "#........#",
    "#........#",
    "#........#",
    "#........#",
    "#........#",
    "#........#",
    ".########.",
]

FOLDER = [
    ".#####........",
    "##...##.......",
    "#.....#######.",
    "#....##.....##",
    "######.......#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "##..........##",
    ".############.",
]

FOLDER_OPEN = [
    ".#####.........",
    "##...##........",
    "#.....#####....",
    "#.........#....",
    "#...###########",
    "#..#.........#.",
    "#..#.........#.",
    "#.#.........#..",
    "#.#.........#..",
    "##.........#...",
    "##.........#...",
    ".##########....",
]

# A project: a rounded box, 13 x 11.
PROJECT = [
    ".###########.",
    "#...........#",
    "#...........#",
    "#...........#",
    "#...........#",
    "#...........#",
    "#...........#",
    "#...........#",
    "#...........#",
    "#...........#",
    ".###########.",
]

# The big C of a C# file, 5 x 8.
BIG_C = [".###.", "##.##", "#...#", "#....", "#....", "#...#", "##.##", ".###."]
BIG_F = ["#####", "#....", "#....", "####.", "#....", "#....", "#....", "#...."]
BIG_HASH = ["..#..#..", "..#..#..", "########", "..#..#..", "..#..#..", "########", "..#..#..", "..#..#.."]

# An open-end wrench, its head top right.
WRENCH = [
    "......#..#",
    "......#..#",
    "......####",
    ".....###..",
    "....##....",
    "...##.....",
    "..##......",
    ".##.......",
    "##........",
    "#.........",
]

# A cog.
COG = [
    "....#....",
    ".#.###.#.",
    "..#...#..",
    ".#.....#.",
    "##..#..##",
    ".#.....#.",
    "..#...#..",
    ".#.###.#.",
    "....#....",
]

IMAGE_FRAME = [
    "##############",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "#............#",
    "##############",
]
IMAGE_SUN = ["##", "##"]
IMAGE_HILLS = [
    "....#.......",
    "...###......",
    "..#####..#..",
    ".#######.##.",
    "############",
]

WINDOW = [
    ".##########.",
    "#..........#",
    "#..........#",
    "############",
    "#..........#",
    "#..........#",
    "#..........#",
    "#..........#",
    "#..........#",
    "#..........#",
    "#..........#",
    ".##########.",
]
DIAMOND = ["...#...", "..#.#..", ".#...#.", "#.....#", ".#...#.", "..#.#..", "...#..."]

FLASK = [
    "..######..",
    "...#..#...",
    "...#..#...",
    "...#..#...",
    "..#....#..",
    ".#......#.",
    ".#......#.",
    "#........#",
    "#........#",
    ".########.",
]
FLASK_LIQUID = [".######.", "########", "########"]

# An at sign, for Razor.
AT = [
    "..####..",
    ".#....#.",
    "#..##..#",
    "#.#..#.#",
    "#.#..#.#",
    "#..####.",
    ".#......",
    "..####..",
]

BRACES = [".##..##.", ".#....#.", ".#....#.", "##....##", ".#....#.", ".#....#.", ".##..##."]

LINES = ["######", "", "######", "", "######", "", "####"]


def globe(cx=7.5, cy=7.5, r=6.0):
    """A globe: its circle, a meridian and the equator, as pixels."""
    pixels = set()
    for y in range(16):
        for x in range(16):
            dx, dy = x + 0.5 - (cx + 0.5), y + 0.5 - (cy + 0.5)
            d = (dx * dx + dy * dy) ** 0.5
            e = ((dx / 2.6) ** 2 + (dy / r) ** 2) ** 0.5
            if abs(d - r) < 0.5 or (abs(e - 1) < 0.5 / 2.6 * 1.3 and d < r) or (y == round(cy) and d < r):
                pixels.add((x, y))
    return pixels


def page(badge=None, color=GRAY, page_x=3, extra=None):
    """A page; a badge in its lower-left corner (rows 10–14) cuts the outline around it."""
    layers = [(GRAY, bitmap(PAGE, page_x, 1))]
    if extra:
        layers.append(extra)
    if badge:
        pixels = text(badge, 0, 10)
        layers[0] = (GRAY, layers[0][1] - around(pixels))
        layers.append((color, pixels))
    return layers


def overlay(rows, x, y, page_x=3):
    """A page with a large gray glyph over its lower-left corner, cutting the outline around it."""
    pixels = bitmap(rows, x, y)
    return [(GRAY, (bitmap(PAGE, page_x, 1) - around(pixels)) | pixels)]


def badge_on(base, badge, color, x=0, y=10):
    """A badge on another drawing, cutting the drawing around it."""
    pixels = text(badge, x, y)
    cut = around(pixels)
    return [(c, p - cut) for c, p in base] + [(color, pixels)]


def project(glyph, color):
    """A project box with its language in color."""
    return [(GRAY, bitmap(PROJECT, 1, 3)), (color, text(glyph, 3, 6))]


ICONS = {
    "document": page(),
    "text": page(extra=(GRAY, bitmap(LINES, 5, 6))),
    "csharp": [(GREEN, bitmap(BIG_C, 1, 4) | bitmap(BIG_HASH, 7, 4))],
    "fsharp": [(PURPLE, bitmap(BIG_F, 1, 4) | bitmap(BIG_HASH, 7, 4))],
    "yaml": page("YML", RED, page_x=4),
    "json": overlay(BRACES, 1, 8, page_x=4),
    "markdown": page("M↓"),
    "msbuild": overlay(COG, 0, 7),
    "config": overlay(GLYPHS["wrench"], 0, 8),
    "xml": page("<>", ORANGE),
    "html": page("<>", RED),
    "xaml": page("<>", BLUE),
    "razor": [(GRAY, bitmap(PAGE, 3, 1) - around(bitmap(AT, 0, 7))), (GREEN, bitmap(AT, 0, 7))],
    "css": page("#", BLUE),
    "sass": page("S", PURPLE),
    "javascript": page("JS", YELLOW),
    "typescript": page("TS", BLUE),
    "powershell": page(">_", BLUE),
    "console": page(">_", GRAY),
    "database": page("db", YELLOW),
    "http": page("→", BLUE),
    "certificate": page("seal", ORANGE),
    "key": page("key", YELLOW),
    "lock": page("lock", YELLOW),
    "binary": page("01", BLUE),
    "package": page("box", ORANGE),
    "pdf": page("PDF", RED, page_x=4),
    "table": page("grid", GREEN),
    "word": page("W", BLUE),
    "template": page("T4", PURPLE),
    "git": page("branch", ORANGE),
    "docker": page("whale", BLUE),
    "image": [(GRAY, bitmap(IMAGE_FRAME, 1, 2)), (YELLOW, bitmap(IMAGE_SUN, 10, 4)), (GREEN, bitmap(IMAGE_HILLS, 2, 8))],
    "application": [(GRAY, bitmap(WINDOW, 2, 2)), (BLUE, bitmap(["##########", "##########"], 3, 3))],
    # A Windows Forms form or user control (M41a): a window with its title bar, and the C# badge.
    "form": badge_on([(GRAY, bitmap(WINDOW, 4, 1)), (BLUE, bitmap(["##########", "##########"], 5, 2))], "C#", GREEN),
    "solution": [(GRAY, bitmap(WINDOW, 4, 1) - around(bitmap(DIAMOND, 0, 8))), (PURPLE, bitmap(DIAMOND, 0, 8))],
    "folder": [(TAN, bitmap(FOLDER, 1, 2))],
    "folder-open": [(TAN, bitmap(FOLDER_OPEN, 1, 2))],
    "folder-properties": [(GRAY, bitmap(WRENCH, 3, 3))],
    "folder-web": [(BLUE, globe())],
    "dependencies": [(GRAY, bitmap(["####", "#..#", "#..#", "####"], 1, 1) | bitmap(["#", "#", "#", "#", "#", "#", "#"], 2, 5)
                      | bitmap(["####", "#..#", "#..#", "####"], 6, 6) | bitmap(["####", "#..#", "#..#", "####"], 6, 11)
                      | bitmap(["###"], 3, 7) | bitmap(["###"], 3, 12) | bitmap(["#"] * 5, 2, 8)),
                     (ORANGE, bitmap(["##", "##"], 7, 7) | bitmap(["##", "##"], 7, 12))],
    "project-csharp": project("C#", GREEN),
    "project-fsharp": project("F#", PURPLE),
    "project-web": badge_on([(GRAY, globe(8.5, 7.5))], "C#", GREEN),
    "project-test": badge_on([(GRAY, bitmap(FLASK, 5, 1)), (GREEN, bitmap(FLASK_LIQUID, 6, 7))], "C#", GREEN),
}


def runs(pixels):
    """The pixels as rectangles: runs of a row, merged with the same runs of the rows below."""
    rects = []
    open_runs = {}
    for y in range(-1, 18):
        row = sorted(x for x, yy in pixels if yy == y)
        current = []
        for x in row:
            if current and current[-1][1] == x:
                current[-1][1] = x + 1
            else:
                current.append([x, x + 1])
        keys = {(a, b) for a, b in current}
        for key in list(open_runs):
            if key not in keys:
                rects.append((key[0], open_runs.pop(key), key[1], y))
        for key in keys:
            open_runs.setdefault(key, y)
    return rects


def svg(layers):
    paths = []
    for color, pixels in layers:
        pixels = {(x, y) for x, y in pixels if 0 <= x < 16 and 0 <= y < 16}
        if not pixels:
            continue
        d = "".join(f"M{x0} {y0}H{x1}V{y1}H{x0}Z" for x0, y0, x1, y1 in sorted(runs(pixels), key=lambda r: (r[1], r[0])))
        fill = f' fill="{color}"' if color else ""
        paths.append(f'  <path{fill} d="{d}"/>')
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\n' + "\n".join(paths) + "\n</svg>\n"


def preview(layers):
    grid = [["." for _ in range(16)] for _ in range(16)]
    for color, pixels in layers:
        mark = {GRAY: "#", GREEN: "g", RED: "r", ORANGE: "o", PURPLE: "p", YELLOW: "y", BLUE: "b", TAN: "t"}[color]
        for x, y in pixels:
            if 0 <= x < 16 and 0 <= y < 16:
                grid[y][x] = mark
    return "\n".join("".join(row) for row in grid)


if __name__ == "__main__":
    import sys

    os.makedirs(OUT, exist_ok=True)
    for name in os.listdir(OUT):
        if name.endswith(".svg"):
            os.remove(os.path.join(OUT, name))
    for name, layers in ICONS.items():
        with open(os.path.join(OUT, f"classic-{name}.svg"), "w", newline="\n", encoding="utf-8") as f:
            f.write(svg(layers))
        if "--preview" in sys.argv:
            print(name)
            print(preview(layers))
            print()
