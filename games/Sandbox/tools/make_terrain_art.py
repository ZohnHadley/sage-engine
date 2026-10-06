# -*- coding: utf-8 -*-
"""The Sandbox's terrain textures (engine issue #307: splat terrain).

The ground's first layer is the engine's own `textures/ground.png`; this makes the rest, because the
repository ships no art it did not make:

  - `content/textures/terrain_rock.png` — bare, cracked rock: the second layer, where the hills are steep.
  - `content/textures/terrain_detail.png` — grey noise around mid-grey, tiled finely over every layer so
    the ground is not flat colour close up (mid-grey changes nothing; lighter brightens, darker darkens).

Both are 64x64 and tile: the noise is periodic, so a repeat has no edge.

Run:  python games/Sandbox/tools/make_terrain_art.py
"""
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
TEXTURES = os.path.join(HERE, '..', 'content', 'textures')
SIZE = 64


def write_png(path, width, height, pixels):
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        for x in range(width):
            raw += bytes(pixels[y * width + x])

    def chunk(tag, data):
        body = tag + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xFFFFFFFF)

    png = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(bytes(raw), 9))
           + chunk(b'IEND', b''))
    open(path, 'wb').write(png)
    print('%-20s %dx%d  %5d bytes' % (os.path.basename(path), width, height, len(png)))


def hash01(x, y, seed):
    """A repeatable hash to 0..1, so the same texture comes out of every run."""
    n = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)
    n &= 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((n ^ (n >> 16)) & 0xFF) / 255.0


def tiling_noise(x, y, cell, seed):
    """Smooth value noise on a lattice `cell` pixels apart that wraps at SIZE, so the texture tiles."""
    period = SIZE // cell
    gx, gy = x / cell, y / cell
    x0, y0 = int(gx), int(gy)
    tx, ty = gx - x0, gy - y0
    tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)

    def at(i, j):
        return hash01(i % period, j % period, seed)

    top = at(x0, y0) * (1 - tx) + at(x0 + 1, y0) * tx
    bottom = at(x0, y0 + 1) * (1 - tx) + at(x0 + 1, y0 + 1) * tx
    return top * (1 - ty) + bottom * ty


def rock():
    pixels = []
    for y in range(SIZE):
        for x in range(SIZE):
            n = 0.55 * tiling_noise(x, y, 16, 3) + 0.3 * tiling_noise(x, y, 8, 4) + 0.15 * tiling_noise(x, y, 4, 5)
            crack = tiling_noise(x, y, 8, 6)
            shade = 0.55 + 0.45 * n
            if abs(crack - 0.5) < 0.035:
                shade *= 0.55   # thin dark cracks where the noise crosses its middle
            pixels.append((int(128 * shade) + 20, int(118 * shade) + 18, int(104 * shade) + 16, 255))
    return pixels


def detail():
    pixels = []
    for y in range(SIZE):
        for x in range(SIZE):
            n = 0.6 * tiling_noise(x, y, 8, 11) + 0.4 * tiling_noise(x, y, 2, 12)
            grey = max(0, min(255, int(128 + (n - 0.5) * 120)))
            pixels.append((grey, grey, grey, 255))
    return pixels


def main():
    write_png(os.path.join(TEXTURES, 'terrain_rock.png'), SIZE, SIZE, rock())
    write_png(os.path.join(TEXTURES, 'terrain_detail.png'), SIZE, SIZE, detail())


if __name__ == '__main__':
    main()
