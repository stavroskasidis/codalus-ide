#!/usr/bin/env python3
"""Draws the code symbol icons of IntelliSense (src/Codalus.Shell/Icons/Symbols/symbol-*.svg).

The completion list, Quick Info and signature help show one per symbol kind (class, struct, interface, enum, delegate,
method, property, field, event, namespace, keyword, snippet, local, parameter...), in the spirit of a classic IDE's
IntelliSense glyphs but drawn here from scratch: 16 px pixel drawings. Parts without a fill are drawn in the kind's color
from CommandIcons' kind colors (Shell.Icon.Purple for methods, Orange for classes...); a part with a fill takes each
theme's nearest palette color (Icon.PaletteColor), like the classic file icons. High Contrast draws them in the
foreground. The build compiles the SVGs (eng/icons/CompileIcons.cs); nothing parses SVG at run time.

Run it after changing a drawing and commit the SVGs: python3 eng/icons/symbol-icons.py
"""
import os

BLUE = "#75BEFF"  # Icon.PaletteColor: "Blue" (the dark theme's palette color, as classic-icons.py writes them)

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Codalus.Shell", "Icons", "Symbols")


def pixels(rows):
    """The pixels of a 16 x 16 drawing: '#' is on, anything else off."""
    assert len(rows) == 16, len(rows)
    for row in rows:
        assert len(row) == 16, row
    return {(x, y) for y, row in enumerate(rows) for x, ch in enumerate(row) if ch == "#"}


def path(on):
    """The pixels as rectangles: each row's runs, merged with the same run on the rows below."""
    runs = {}
    for y in range(16):
        x = 0
        while x < 16:
            if (x, y) in on:
                start = x
                while x < 16 and (x, y) in on:
                    x += 1
                runs.setdefault((start, x), []).append(y)
            else:
                x += 1
    parts = []
    for (x0, x1), ys in sorted(runs.items(), key=lambda kv: (kv[1][0], kv[0][0])):
        ys = sorted(ys)
        start = prev = ys[0]
        for y in ys[1:] + [None]:
            if y is not None and y == prev + 1:
                prev = y
                continue
            parts.append(f"M{x0} {start}H{x1}V{prev + 1}H{x0}Z")
            if y is not None:
                start = prev = y
    return "".join(parts)


def svg(name, layers):
    body = []
    for rows, fill in layers:
        d = path(pixels(rows) if isinstance(rows, list) else rows)
        body.append(f'  <path fill="{fill}" d="{d}"/>' if fill else f'  <path d="{d}"/>')
    with open(os.path.join(OUT, f"symbol-{name}.svg"), "w", newline="\n") as f:
        f.write('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\n' + "\n".join(body) + "\n</svg>\n")


E = "................"

ICONS = {
    # A type: a box with its header filled, and two member lines.
    "class": [
        E, E,
        "..############..",
        "..############..",
        "..############..",
        "..#..........#..",
        "..#.#######..#..",
        "..#..........#..",
        "..#.#####....#..",
        "..#..........#..",
        "..#..........#..",
        "..############..",
        E, E, E, E,
    ],
    # A value type: four blocks.
    "struct": [
        E,
        "..#####..#####..",
        "..#####..#####..",
        "..#####..#####..",
        "..#####..#####..",
        "..#####..#####..",
        E, E,
        "..#####..#####..",
        "..#####..#####..",
        "..#####..#####..",
        "..#####..#####..",
        "..#####..#####..",
        E, E, E,
    ],
    # A contract: a lollipop.
    "interface": [
        E, E, E,
        "........####....",
        ".......#....#...",
        "......#......#..",
        "######.......#..",
        "######.......#..",
        "......#......#..",
        ".......#....#...",
        "........####....",
        E, E, E, E, E,
    ],
    # Named values: a list of items.
    "enum": [
        E,
        "..###.########..",
        "..###.########..",
        "..###.########..",
        E,
        "..###.########..",
        "..###.########..",
        "..###.########..",
        E,
        "..###.########..",
        "..###.########..",
        "..###.########..",
        E, E, E, E,
    ],
    # One named value: one item.
    "enum-member": [
        E, E, E, E,
        "...#####........",
        "...#####.######.",
        "...#####.######.",
        "...#####........",
        "...#####........",
        E, E, E, E, E, E, E,
    ],
    # A method as a value: an arrow.
    "delegate": [
        E, E, E,
        ".........#......",
        ".........##.....",
        ".........###....",
        "..###########...",
        "..############..",
        "..###########...",
        ".........###....",
        ".........##.....",
        ".........#......",
        E, E, E, E,
    ],
    # Code: a cube.
    "method": [
        ".......##.......",
        ".....##..##.....",
        "...##......##...",
        ".##..........##.",
        ".#.##......##.#.",
        ".#...##..##...#.",
        ".#.....##.....#.",
        ".#.....##.....#.",
        ".#.....##.....#.",
        ".#.....##.....#.",
        ".##....##....##.",
        "...##..##..##...",
        ".....##..##.....",
        ".......##.......",
        E, E,
    ],
    # A setting: a wrench.
    "property": [
        E,
        ".........##..##.",
        ".........##..##.",
        ".........######.",
        ".........######.",
        "........#####...",
        ".......####.....",
        "......####......",
        ".....####.......",
        "....####........",
        "...####.........",
        "..####..........",
        ".####...........",
        ".###............",
        E, E,
    ],
    # Data: a brick.
    "field": [
        E, E, E,
        ".....#########..",
        "....#........##.",
        "...##########.#.",
        "...##########.#.",
        "...##########.#.",
        "...##########.#.",
        "...###########..",
        "...##########...",
        E, E, E, E, E,
    ],
    # Something that happens: a bolt.
    "event": [
        ".......######...",
        "......######....",
        ".....######.....",
        "....######......",
        "...##########...",
        ".......######...",
        "......######....",
        ".....#####......",
        "....####........",
        "...###..........",
        "..##............",
        E, E, E, E, E,
    ],
    # A value that doesn't change: a box with two bars.
    "constant": [
        E, E, E,
        "..############..",
        "..#..........#..",
        "..#.########.#..",
        "..#..........#..",
        "..#..........#..",
        "..#.########.#..",
        "..#..........#..",
        "..############..",
        E, E, E, E, E,
    ],
    # A variable: a block in brackets.
    "local": [
        E, E,
        "..###......###..",
        "..#..........#..",
        "..#..........#..",
        "..#..######..#..",
        "..#..######..#..",
        "..#..######..#..",
        "..#..######..#..",
        "..#..........#..",
        "..#..........#..",
        "..###......###..",
        E, E, E, E,
    ],
    # An argument: an open block in brackets.
    "parameter": [
        E, E,
        "..###......###..",
        "..#..........#..",
        "..#..........#..",
        "..#..######..#..",
        "..#..#....#..#..",
        "..#..#....#..#..",
        "..#..######..#..",
        "..#..........#..",
        "..#..........#..",
        "..###......###..",
        E, E, E, E,
    ],
    # A scope: braces.
    "namespace": [
        E,
        "....##....##....",
        "...#........#...",
        "...#........#...",
        "...#........#...",
        "...#........#...",
        "..#....##....#..",
        "..#....##....#..",
        "...#........#...",
        "...#........#...",
        "...#........#...",
        "...#........#...",
        "....##....##....",
        E, E, E,
    ],
    # A word of the language: a key.
    "keyword": [
        E, E,
        "...##########...",
        "..#..........#..",
        "..#..........#..",
        "..#..........#..",
        "..#..........#..",
        "..#...####...#..",
        "..#..........#..",
        "...##########...",
        "....########....",
        E, E, E, E, E,
    ],
    # A piece of code to insert: a dashed sheet with lines.
    "snippet": [
        E,
        "..##.##.##.##...",
        E,
        "..#.........#...",
        "..#..######.....",
        "............#...",
        "..#..####...#...",
        "..#.............",
        "............#...",
        "..#..#####..#...",
        "..#.............",
        "............#...",
        "..##.##.##.##...",
        E, E, E,
    ],
    # A type's parameter: T.
    "type-parameter": [
        E, E,
        "..############..",
        "..############..",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        E, E, E, E,
    ],
    # An operator: plus and minus.
    "operator": [
        E, E,
        ".......##.......",
        ".......##.......",
        "....########....",
        "....########....",
        ".......##.......",
        ".......##.......",
        E,
        "....########....",
        "....########....",
        E, E, E, E, E,
    ],
    # A module: a box in a box.
    "module": [
        E, E,
        "..############..",
        "..#..........#..",
        "..#..........#..",
        "..#..######..#..",
        "..#..######..#..",
        "..#..######..#..",
        "..#..######..#..",
        "..#..........#..",
        "..#..........#..",
        "..############..",
        E, E, E, E,
    ],
    # A label: a tag.
    "label": [
        E, E, E,
        "..########......",
        "..#.......#.....",
        "..#..##....#....",
        "..#..##.....#...",
        "..#.........#...",
        "..#........#....",
        "..#.......#.....",
        "..########......",
        E, E, E, E, E,
    ],
    # A word from the document: lines of text.
    "text": [
        E, E, E,
        "..############..",
        "..############..",
        E,
        "..##########....",
        "..##########....",
        E,
        "..#######.......",
        "..#######.......",
        E, E, E, E, E,
    ],
    # The data tip's Raw View (D44): a box in perspective, an object as it is.
    "raw-view": [
        E,
        "....##########..",
        "...#.........##.",
        "..#.........#.#.",
        ".###########..#.",
        ".#.........#..#.",
        ".#.........#..#.",
        ".#.........#..#.",
        ".#.........#..#.",
        ".#.........#..#.",
        ".#.........#.#..",
        ".#.........##...",
        ".###########....",
        E, E, E,
    ],
    # A target type match (the completion list's filter): a target.
    "target-type": [
        E,
        ".....######.....",
        "...##......##...",
        "..#..........#..",
        ".#....####....#.",
        ".#...#....#...#.",
        "#...#......#...#",
        "#...#..##..#...#",
        "#...#..##..#...#",
        "#...#......#...#",
        ".#...#....#...#.",
        ".#....####....#.",
        "..#..........#..",
        "...##......##...",
        ".....######.....",
        E,
    ],
}


def extension_method():
    """A method with an arrow in its corner (an extension method), the arrow in blue."""
    cube = pixels(ICONS["method"])
    arrow = pixels([
        E, E, E, E, E, E, E, E, E,
        "...........###..",
        "...........###..",
        "...........###..",
        ".........#######",
        "..........#####.",
        "...........###..",
        "............#...",
    ])
    cut = {(x + dx, y + dy) for x, y in arrow for dx in (-1, 0, 1) for dy in (-1, 0, 1)}
    return [(cube - cut, None), (arrow, BLUE)]


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, rows in ICONS.items():
        svg(name, [(rows, None)])
    svg("extension-method", extension_method())


if __name__ == "__main__":
    main()
