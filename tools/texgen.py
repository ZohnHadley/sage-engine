# -*- coding: utf-8 -*-
"""Tiling textures and texture sheets from JSON recipes (issue #627).

One tool for what the one-off generators each did with their own PNG writer and noise
(engine_content/tools/make_engine_textures.py, games/Sandbox/tools/make_*.py): a recipe names materials
of a few kinds (ground, grass, stone, cobble, brick, roof_tile, planks, bark, metal, water, cloud, sand,
snow, moss), and each is drawn from periodic noise and periodic patterns, so every map repeats with no
seam. The same height field gives the colour, an optional normal map and an optional specular map, in the
layout lit.fx reads (see games/Sandbox/tools/make_brick_wall.py):

  <name>.png         colour (RGBA; a cloud's alpha is its density)
  <name>_normal.png  tangent-space normal, glTF's way round: red +u, green up the image, blue out
  <name>_spec.png    red: how shiny; green: how glossy

A recipe:

  {
    "size": 128,                           // pixels a side, for every material unless one says otherwise
    "materials": [
      { "name": "grass", "kind": "grass", "seed": 3, "normal": true,
        "palette": ["#2e4a1e", "#4f7a2c", "#8fae4a"],   // dark to light: the colour ramp
        "bands": 6,                         // quantise the ramp into painted-looking steps (0: smooth)
        "scale": 1.0, "variants": 2 }      // scale: feature size; variants: more seeds of the same
    ],
    "sheet": { "name": "terrain_sheet", "columns": 4 }  // optional: every colour map packed into one atlas,
  }                                                     // with <name>.json listing each tile's rectangle

Every kind has a default palette, so "kind" alone is a valid material. Standard library only.

Run:      python tools/texgen.py tools/texgen_recipes/basics.json --out <folder>
Check:    python tools/texgen.py --selftest     (every kind tiles: its edges wrap like its insides)
"""
import argparse
import json
import math
import os
import struct
import sys
import zlib


# --- PNG -------------------------------------------------------------------------------------------------

def write_png(path, size_w, size_h, pixels):
    raw = bytearray()
    for y in range(size_h):
        raw.append(0)
        for x in range(size_w):
            raw += bytes(pixels[y * size_w + x])

    def chunk(tag, data):
        body = tag + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xFFFFFFFF)

    png = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', size_w, size_h, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(bytes(raw), 9))
           + chunk(b'IEND', b''))
    with open(path, 'wb') as f:
        f.write(png)


# --- Periodic noise --------------------------------------------------------------------------------------
# Every function takes u, v in texture space (0..1 across) and periods in whole cells, so it repeats
# exactly at the texture's edge.

def hash01(x, y, seed):
    n = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)
    n = (n ^ (n >> 13)) * 1274126177
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def smooth(t):
    return t * t * (3 - 2 * t)


def value_noise(u, v, px, py, seed):
    x, y = u * px, v * py
    ix, iy = math.floor(x), math.floor(y)
    fx, fy = smooth(x - ix), smooth(y - iy)
    x0, x1 = ix % px, (ix + 1) % px
    y0, y1 = iy % py, (iy + 1) % py
    a = hash01(x0, y0, seed) + (hash01(x1, y0, seed) - hash01(x0, y0, seed)) * fx
    b = hash01(x0, y1, seed) + (hash01(x1, y1, seed) - hash01(x0, y1, seed)) * fx
    return a + (b - a) * fy


def fbm(u, v, px, py, seed, octaves=5, gain=0.5):
    total, amp, norm = 0.0, 1.0, 0.0
    for o in range(octaves):
        total += amp * value_noise(u, v, px << o, py << o, seed + o * 101)
        norm += amp
        amp *= gain
    return total / norm


def worley(u, v, px, py, seed):
    """(F1, F2, cell hash): distances to the nearest two jittered points, in cells."""
    x, y = u * px, v * py
    ix, iy = math.floor(x), math.floor(y)
    f1 = f2 = 9.0
    cell = 0.0
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            cx, cy = (ix + dx) % px, (iy + dy) % py
            jx = ix + dx + hash01(cx, cy, seed)
            jy = iy + dy + hash01(cx, cy, seed + 7)
            d = math.hypot(x - jx, y - jy)
            if d < f1:
                f1, f2, cell = d, f1, hash01(cx, cy, seed + 13)
            elif d < f2:
                f2 = d
    return f1, f2, cell


def clamp01(t):
    return 0.0 if t < 0 else 1.0 if t > 1 else t


# --- Kinds -----------------------------------------------------------------------------------------------
# Each returns (tone, height, shine) at (u, v), all 0..1: tone picks from the palette, height makes the
# normal map, shine the specular map. `s` scales feature counts; counts stay whole so the map tiles.

def cells(n, s):
    return max(1, int(round(n * s)))


def k_ground(u, v, seed, s):
    n = fbm(u, v, cells(4, s), cells(4, s), seed)
    f1, _, c = worley(u, v, cells(12, s), cells(12, s), seed + 50)
    pebble = clamp01(1 - f1 * 3) * (c > 0.7)
    return clamp01(n * 0.8 + pebble * 0.35), n * 0.6 + pebble * 0.4, 0.05


def k_grass(u, v, seed, s):
    base = fbm(u, v, cells(4, s), cells(4, s), seed, 4)
    blades = value_noise(u, v, cells(64, s), cells(8, s), seed + 9)   # stretched up the image: blades
    t = clamp01(base * 0.55 + blades * 0.6 - 0.1)
    return t, blades, 0.1


def k_stone(u, v, seed, s):
    f1, f2, c = worley(u, v, cells(5, s), cells(5, s), seed)
    crack = clamp01((f2 - f1) * 6)
    n = fbm(u, v, cells(6, s), cells(6, s), seed + 3)
    t = clamp01(n * 0.6 + c * 0.25 + 0.15) * (0.35 + 0.65 * crack)
    return t, crack * 0.7 + n * 0.3, 0.15 * crack


def k_cobble(u, v, seed, s):
    f1, f2, c = worley(u, v, cells(6, s), cells(6, s), seed)
    gap = clamp01((f2 - f1) * 5)
    dome = clamp01(1 - f1 * 0.9)
    n = fbm(u, v, cells(8, s), cells(8, s), seed + 4, 3)
    t = (0.25 + 0.5 * c + 0.25 * n) * (0.3 + 0.7 * gap)
    return clamp01(t), gap * (0.5 + 0.5 * dome), 0.2 * gap


def k_brick(u, v, seed, s):
    rows, cols = 2 * cells(4, s), cells(4, s)      # an even count of courses keeps the stagger at the wrap
    y = v * rows + 0.5                             # joints off the texture's edge
    row = int(math.floor(y)) % rows
    x = u * cols + (0.75 if row % 2 else 0.25)
    col = int(math.floor(x)) % cols
    ex, ey = x - math.floor(x), y - math.floor(y)
    edge = min(ex * cols, (1 - ex) * cols, ey * rows / 2, (1 - ey) * rows / 2) * 4   # 0 at the joint
    mortar = edge < 0.12
    n = fbm(u, v, cells(16, s), cells(16, s), seed, 3)
    if mortar:
        return 0.15 + 0.1 * n, 0.05 * n, 0.0
    tone = 0.45 + 0.4 * hash01(row, col, seed) + 0.15 * n
    return clamp01(tone), clamp01(edge * 2) * (0.9 + 0.1 * n), 0.25


def k_roof_tile(u, v, seed, s):
    rows, cols = 2 * cells(4, s), cells(8, s)
    y = v * rows + 0.5                             # joints off the texture's edge
    row = int(math.floor(y)) % rows
    x = u * cols + (0.75 if row % 2 else 0.25)
    fx, fy = x - math.floor(x), y - math.floor(y)
    curve = math.sin(math.pi * fx)               # a tile's round back
    lip = clamp01(fy * 4)                        # the overlap at its top edge is in shadow
    n = fbm(u, v, cells(8, s), cells(8, s), seed, 3)
    col = int(math.floor(x)) % cols
    tone = (0.3 + 0.5 * curve * lip + 0.2 * hash01(row, col, seed)) * (0.85 + 0.15 * n)
    return clamp01(tone), curve * lip, 0.3 * curve


def k_planks(u, v, seed, s):
    boards = cells(5, s)
    x = u * boards + 0.5                          # joints off the texture's edge, so its wrap is mid-board
    board = int(math.floor(x)) % boards
    fx = x - math.floor(x)
    seam = min(fx, 1 - fx) * boards * 8
    grain = fbm(u, v + hash01(board, 0, seed), cells(40, s), cells(2, s), seed + board, 3)
    rings = 0.5 + 0.5 * math.sin((grain * 6 + fx * 3) * math.pi * 2)
    t = (0.35 + 0.35 * grain + 0.2 * rings) * (0.85 + 0.3 * hash01(board, 1, seed))
    h = clamp01(seam) * (0.8 + 0.2 * rings)
    return clamp01(t) * (0.4 + 0.6 * clamp01(seam)), h, 0.1


def k_bark(u, v, seed, s):
    f1, f2, _ = worley(u, v, cells(10, s), cells(3, s), seed)    # long plates up the trunk
    furrow = clamp01((f2 - f1) * 3)
    n = fbm(u, v, cells(16, s), cells(4, s), seed + 2, 4)
    return clamp01(furrow * 0.7 + n * 0.4 - 0.05), furrow * 0.8 + n * 0.2, 0.05


def k_metal(u, v, seed, s):
    brushed = fbm(u, v, cells(2, s), cells(64, s), seed, 3)      # streaks across the image
    blotch = fbm(u, v, cells(3, s), cells(3, s), seed + 5, 4)
    scratch = value_noise(u, v, cells(128, s), cells(4, s), seed + 9)
    t = clamp01(0.35 + 0.35 * brushed + 0.3 * blotch - 0.2 * (scratch > 0.93))
    return t, brushed * 0.3 + blotch * 0.2, 0.6 + 0.4 * blotch


def k_water(u, v, seed, s):
    w = 0.0
    for i, (a, b) in enumerate(((2, 1), (1, 3), (3, -2), (5, 4))):  # whole frequencies: they wrap
        ph = hash01(i, 0, seed) * math.tau
        w += math.sin(math.tau * (a * u + b * v) * s + ph) / (i + 1)
    n = fbm(u, v, cells(4, s), cells(4, s), seed + 3, 4)
    t = clamp01(0.5 + 0.22 * w + 0.35 * (n - 0.5))
    return t, t, 0.9


def k_cloud(u, v, seed, s):
    n = fbm(u, v, cells(3, s), cells(3, s), seed, 6, 0.55)
    return clamp01((n - 0.35) * 2.2), n, 0.0


def k_sand(u, v, seed, s):
    n = fbm(u, v, cells(4, s), cells(4, s), seed, 4)
    ripple = 0.5 + 0.5 * math.sin(math.tau * (cells(10, s) * v + 0.8 * n))
    grain = hash01(int(u * 4096), int(v * 4096), seed)
    return clamp01(0.35 + 0.3 * ripple + 0.25 * n + 0.1 * grain), ripple * 0.6 + n * 0.4, 0.05


def k_snow(u, v, seed, s):
    n = fbm(u, v, cells(4, s), cells(4, s), seed, 5)
    sparkle = hash01(int(u * 4096), int(v * 4096), seed + 1) > 0.995
    return clamp01(0.6 + 0.35 * n + 0.2 * sparkle), n, 0.3 + 0.7 * sparkle


def k_moss(u, v, seed, s):
    f1, _, c = worley(u, v, cells(8, s), cells(8, s), seed)
    clump = clamp01(1 - f1 * 1.2)
    n = fbm(u, v, cells(8, s), cells(8, s), seed + 2, 4)
    return clamp01(clump * 0.55 + n * 0.45 + 0.1 * c), clump * 0.7 + n * 0.3, 0.05


KINDS = {
    'ground': (k_ground, ['#3b2e22', '#5e4a35', '#86704f', '#a8926b']),
    'grass': (k_grass, ['#22361a', '#3e5e24', '#5f8a30', '#94b052']),
    'stone': (k_stone, ['#2f2f33', '#57575c', '#808084', '#a9a8a3']),
    'cobble': (k_cobble, ['#26231f', '#4d4a45', '#77736b', '#9f9a8f']),
    'brick': (k_brick, ['#5a5550', '#6e2f22', '#934535', '#b5674e']),
    'roof_tile': (k_roof_tile, ['#1c2328', '#33424b', '#4f6470', '#7a8f99']),
    'planks': (k_planks, ['#2b1a0f', '#5a3a20', '#80562f', '#a8784a']),
    'bark': (k_bark, ['#1d140d', '#3b2a1c', '#5b4430', '#7d6448']),
    'metal': (k_metal, ['#3a3d40', '#6b7075', '#9aa0a5', '#cfd3d6']),
    'water': (k_water, ['#0c2a3a', '#16465c', '#2b6f85', '#5ea2b3']),
    'cloud': (k_cloud, ['#c9ced6', '#e4e7ec', '#ffffff']),
    'sand': (k_sand, ['#8a7046', '#b39461', '#d1b583', '#e8d4a6']),
    'snow': (k_snow, ['#a9b6c6', '#d3dce6', '#eef2f6', '#ffffff']),
    'moss': (k_moss, ['#1b2a12', '#334d1d', '#56742c', '#7f9a44']),
}


# --- Drawing ---------------------------------------------------------------------------------------------

def parse_colour(c):
    c = c.lstrip('#')
    return tuple(int(c[i:i + 2], 16) for i in (0, 2, 4))


def ramp(palette, t, bands):
    if bands > 1:
        t = min(bands - 1, int(t * bands)) / (bands - 1)
    t *= len(palette) - 1
    i = min(len(palette) - 2, int(t))
    f = t - i
    a, b = palette[i], palette[i + 1]
    return tuple(int(round(a[k] + (b[k] - a[k]) * f)) for k in range(3))


def draw(material, size):
    """The material's maps as rows of RGBA tuples: (colour, normal or None, spec or None)."""
    kind = material.get('kind', material.get('name'))
    if kind not in KINDS:
        raise ValueError(f"material '{material.get('name')}': unknown kind '{kind}' ({', '.join(sorted(KINDS))})")
    fn, default_palette = KINDS[kind]
    palette = [parse_colour(c) for c in material.get('palette', default_palette)]
    if len(palette) < 2:
        raise ValueError(f"material '{material.get('name')}': a palette needs at least two colours")
    seed, scale, bands = int(material.get('seed', 1)), float(material.get('scale', 1.0)), int(material.get('bands', 0))
    tone = [0.0] * (size * size)
    height = [0.0] * (size * size)
    shine = [0.0] * (size * size)
    for y in range(size):
        v = y / size
        for x in range(size):
            t, h, sh = fn(x / size, v, seed, scale)
            i = y * size + x
            tone[i], height[i], shine[i] = t, h, sh

    alpha = kind == 'cloud'
    colour = [ramp(palette, tone[i], bands) + ((int(tone[i] * 255),) if alpha else (255,)) for i in range(size * size)]

    normal = None
    if material.get('normal'):
        depth = float(material.get('depth', 4.0))
        normal = []
        for y in range(size):
            for x in range(size):
                # The height field's slopes, wrapping at the edges; image y runs down, green runs up.
                dx = (height[y * size + (x + 1) % size] - height[y * size + (x - 1) % size]) * 0.5 * depth
                dy = (height[((y + 1) % size) * size + x] - height[((y - 1) % size) * size + x]) * 0.5 * depth
                nx, ny, nz = -dx, dy, 1.0
                length = math.sqrt(nx * nx + ny * ny + nz * nz)
                normal.append(tuple(int(round((c / length * 0.5 + 0.5) * 255)) for c in (nx, ny, nz)) + (255,))

    spec = None
    if material.get('spec'):
        spec = [(int(shine[i] * 255), int(shine[i] * 200), 0, 255) for i in range(size * size)]
    return colour, normal, spec


def build(recipe, out):
    os.makedirs(out, exist_ok=True)
    size_all = int(recipe.get('size', 128))
    tiles = []
    for material in recipe.get('materials', []):
        size = int(material.get('size', size_all))
        if size < 8 or size & (size - 1):
            raise ValueError(f"material '{material.get('name')}': size {size} is not a power of two of at least 8")
        variants = int(material.get('variants', 1))
        for n in range(variants):
            m = dict(material, seed=int(material.get('seed', 1)) + n * 1000)
            name = material.get('name', material.get('kind')) + (f'_{n + 1}' if variants > 1 else '')
            colour, normal, spec = draw(m, size)
            write_png(os.path.join(out, name + '.png'), size, size, colour)
            if normal:
                write_png(os.path.join(out, name + '_normal.png'), size, size, normal)
            if spec:
                write_png(os.path.join(out, name + '_spec.png'), size, size, spec)
            tiles.append((name, size, colour))
            print(f'{name:<20} {size}x{size}' + (' +normal' if normal else '') + (' +spec' if spec else ''))

    sheet = recipe.get('sheet')
    if sheet and tiles:
        cols = max(1, int(sheet.get('columns', 4)))
        cell = max(t[1] for t in tiles)
        rows = (len(tiles) + cols - 1) // cols
        w, h = cols * cell, rows * cell
        pixels = [(0, 0, 0, 0)] * (w * h)
        rects = {}
        for i, (name, size, colour) in enumerate(tiles):
            ox, oy = (i % cols) * cell, (i // cols) * cell
            for y in range(size):
                pixels[(oy + y) * w + ox:(oy + y) * w + ox + size] = colour[y * size:(y + 1) * size]
            rects[name] = {'x': ox, 'y': oy, 'w': size, 'h': size}
        write_png(os.path.join(out, sheet['name'] + '.png'), w, h, pixels)
        with open(os.path.join(out, sheet['name'] + '.json'), 'w', encoding='utf-8') as f:
            json.dump({'width': w, 'height': h, 'tiles': rects}, f, indent=2)
        print(f"{sheet['name']:<20} {w}x{h} sheet, {len(tiles)} tiles")


# --- Self-test -------------------------------------------------------------------------------------------

def seam_ratio(colour, size):
    """How much the colour jumps across the wrap, against how much it changes between neighbours inside:
    about 1 for a map that tiles; far more where an edge does not meet its opposite."""
    def diff(a, b):
        return sum(abs(a[k] - b[k]) for k in range(3))
    inside = across = 0
    for y in range(size):
        row = colour[y * size:(y + 1) * size]
        inside += sum(diff(row[x], row[x - 1]) for x in range(1, size)) / (size - 1)
        across += diff(row[0], row[size - 1])
    for x in range(size):
        col = colour[x::size]
        inside += sum(diff(col[y], col[y - 1]) for y in range(1, size)) / (size - 1)
        across += diff(col[0], col[size - 1])
    return across / max(1, inside)


def selftest():
    size, failed = 64, 0
    for kind in sorted(KINDS):
        colour, normal, _ = draw({'name': kind, 'kind': kind, 'normal': True, 'seed': 7}, size)
        ratio = max(seam_ratio(colour, size), seam_ratio(normal, size))
        ok = ratio < 2.0
        failed += not ok
        print(f'{kind:<10} seam/inside {ratio:4.2f}  {"ok" if ok else "SEAM"}')
    shifted = [(0, 0, 0, 255)] * (size * size)
    for i in range(size * size):
        shifted[i] = (int(255 * (i % size) / size),) * 3 + (255,)   # a ramp: its edges do not meet
    control = seam_ratio(shifted, size)
    print(f'control    seam/inside {control:4.2f}  (a ramp that does not tile: must be large)')
    if control < 10:
        failed += 1
    print('ok' if not failed else f'{failed} failure(s)')
    return 1 if failed else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('recipe', nargs='?', help='a recipe JSON file')
    ap.add_argument('--out', default='.', help='the folder to write into')
    ap.add_argument('--selftest', action='store_true', help='check every kind tiles, and exit')
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    if not a.recipe:
        ap.error('a recipe is needed (or --selftest)')
    with open(a.recipe, encoding='utf-8') as f:
        recipe = json.load(f)
    build(recipe, a.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
