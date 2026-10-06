"""The Sandbox's widget-screen art (docs/design/13 "As built (drawing)", issue #97).

content/textures/ui_frame.png: a 24x24 window frame for a nine-slice (a ui_style's `image` with
`"slice": 8`). The eight pixels round each edge are the corners and edges that keep their size — a
dark outer line, a light bevel two pixels wide with its corners cut, and a faint inner line — and the
middle is transparent, so the style's `background` colour shows through it and its `tint` colours the
frame (white here, so any tint is exact). The repository ships no art it did not make.

content/textures/map_main.png: the main scene's map picture (issue #349, `area_map` sandbox:main), 128x128,
one pixel a metre from absolute (448, 448) to (576, 576): parchment, the wood to the north, the path up to
the hut and its yard, the fence, the campfire and the crypt's door, drawn by hand in code.

Run:  python games/Sandbox/tools/make_ui_art.py
"""
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'content', 'textures', 'ui_frame.png')
SIZE = 24


def png(path, width, height, pixels):
    """pixels: rows of (r, g, b, a) tuples."""
    raw = b''.join(b'\x00' + bytes(c for px in row for c in px) for row in pixels)

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)

    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)))
        f.write(chunk(b'IDAT', zlib.compress(raw, 9)))
        f.write(chunk(b'IEND', b''))


def frame():
    rows = []
    for y in range(SIZE):
        row = []
        for x in range(SIZE):
            d = min(x, y, SIZE - 1 - x, SIZE - 1 - y)          # distance to the nearest edge
            corner = min(x, SIZE - 1 - x) + min(y, SIZE - 1 - y)  # small near a corner
            if corner < 2:
                px = (0, 0, 0, 0)                               # the corner cut away
            elif d == 0:
                px = (24, 24, 32, 255)                          # outer line
            elif d in (1, 2):
                v = 255 if d == 1 else 200
                px = (v, v, v, 255)                             # the bevel the tint colours
            elif d == 5:
                px = (255, 255, 255, 70)                        # a faint inner line
            else:
                px = (0, 0, 0, 0)                               # the middle: the background shows
            row.append(px)
        rows.append(row)
    return rows


MAP_OUT = os.path.join(HERE, '..', 'content', 'textures', 'map_main.png')
MAP_SIZE = 128


def map_main():
    """The main scene round its origin (512, 512): local (x, z) is pixel (x + 64, z + 64), north up."""
    def noise(x, y):
        n = (x * 374761393 + y * 668265263) & 0xFFFFFFFF
        n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
        return ((n >> 16) & 0xFF) / 255.0

    def near_segment(px, py, ax, ay, bx, by, width):
        dx, dy = bx - ax, by - ay
        t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / float(dx * dx + dy * dy)))
        cx, cy = ax + t * dx, ay + t * dy
        return (px - cx) ** 2 + (py - cy) ** 2 <= width * width

    rows = []
    for y in range(MAP_SIZE):
        row = []
        for x in range(MAP_SIZE):
            lx, lz = x - 64, y - 64
            grain = int(noise(x, y) * 14)
            r, g, b = 222 - grain, 204 - grain, 160 - grain           # parchment
            if lz < -20 + int(noise(x // 4, 7) * 6):                  # the wood to the north
                r, g, b = r - 70, g - 40, b - 80
            if near_segment(lx, lz, 0, 6, 14, -9, 1.2) or near_segment(lx, lz, 14, -9, 14, -16, 1.2):
                r, g, b = 168, 132, 88                                # the path up to the hut
            if abs(lx - 14) <= 3 and -20 <= lz <= -14:
                r, g, b = 120, 84, 52                                 # the hut and its yard
            if lz == -9 and -5 <= lx <= 5:
                r, g, b = 90, 64, 40                                  # the fence
            if (lx - 3) ** 2 + (lz - 2) ** 2 <= 2:
                r, g, b = 200, 80, 40                                 # the campfire
            if abs(lx - 20) <= 1 and abs(lz + 24) <= 1:
                r, g, b = 60, 60, 70                                  # the crypt's door
            if x in (0, MAP_SIZE - 1) or y in (0, MAP_SIZE - 1):
                r, g, b = 90, 70, 50                                  # the edge
            row.append((max(r, 0), max(g, 0), max(b, 0), 255))
        rows.append(row)
    return rows


if __name__ == '__main__':
    png(OUT, SIZE, SIZE, frame())
    print(f'wrote {os.path.normpath(OUT)} ({SIZE}x{SIZE}, nine-slice 8)')
    png(MAP_OUT, MAP_SIZE, MAP_SIZE, map_main())
    print(f'wrote {os.path.normpath(MAP_OUT)} ({MAP_SIZE}x{MAP_SIZE}, the main scene\'s map)')
