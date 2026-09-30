"""The Sandbox's widget-screen art (docs/design/13 "As built (drawing)", issue #97).

content/textures/ui_frame.png: a 24x24 window frame for a nine-slice (a ui_style's `image` with
`"slice": 8`). The eight pixels round each edge are the corners and edges that keep their size — a
dark outer line, a light bevel two pixels wide with its corners cut, and a faint inner line — and the
middle is transparent, so the style's `background` colour shows through it and its `tint` colours the
frame (white here, so any tint is exact). The repository ships no art it did not make.

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


if __name__ == '__main__':
    png(OUT, SIZE, SIZE, frame())
    print(f'wrote {os.path.normpath(OUT)} ({SIZE}x{SIZE}, nine-slice 8)')
