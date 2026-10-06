# -*- coding: utf-8 -*-
"""The Sandbox's sky textures (engine issue #320: moon, clouds).

  - `content/textures/moon.png`   — 128x128, the moon's surface: pale grey with darker maria and craters. The sky
    shader draws the disc and the phase over it, so the picture is the whole square, not a circle.
  - `content/textures/clouds.png` — 256x256, tileable cloud noise in the red channel (grey is fine): the sky's
    cloud layer thresholds it by the weather's coverage and scrolls it with the hour.

Run:  python games/Sandbox/tools/make_sky_art.py
"""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
TEXTURES = os.path.join(HERE, '..', 'content', 'textures')


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
    print('%-12s %dx%d  %5d bytes' % (os.path.basename(path), width, height, len(png)))


def hash01(x, y, seed):
    n = ((x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)) & 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def noise(x, y, period, seed):
    """Smooth value noise on a lattice of `period` cells that wraps (x, y in 0..1)."""
    gx, gy = x * period, y * period
    x0, y0 = int(gx), int(gy)
    tx, ty = gx - x0, gy - y0
    tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)

    def at(i, j):
        return hash01(i % period, j % period, seed)

    top = at(x0, y0) * (1 - tx) + at(x0 + 1, y0) * tx
    bottom = at(x0, y0 + 1) * (1 - tx) + at(x0 + 1, y0 + 1) * tx
    return top * (1 - ty) + bottom * ty


def clouds(size=256):
    out = []
    for y in range(size):
        for x in range(size):
            u, v = x / size, y / size
            total, amp, norm = 0.0, 1.0, 0.0
            for octave, period in enumerate((4, 8, 16, 32)):
                total += noise(u, v, period, 11 + octave) * amp
                norm += amp
                amp *= 0.5
            g = int(max(0.0, min(1.0, total / norm)) * 255)
            out.append((g, g, g, 255))
    write_png(os.path.join(TEXTURES, 'clouds.png'), size, size, out)


def moon(size=128):
    out = []
    craters = [(0.35, 0.40, 0.10), (0.62, 0.30, 0.07), (0.55, 0.65, 0.12), (0.30, 0.70, 0.06), (0.72, 0.58, 0.05)]
    for y in range(size):
        for x in range(size):
            u, v = x / size, y / size
            base = 0.80 + 0.12 * (noise(u, v, 8, 3) - 0.5) + 0.08 * (noise(u, v, 16, 4) - 0.5)
            maria = max(0.0, noise(u, v, 4, 7) - 0.5) * 0.6            # darker patches
            shade = base - maria
            for cx, cy, r in craters:
                d = math.hypot(u - cx, v - cy) / r
                if d < 1.0:
                    shade -= 0.12 * (1 - d * d)                         # a bowl
                if 0.9 < d < 1.1:
                    shade += 0.06                                       # its rim
            g = int(max(0.0, min(1.0, shade)) * 255)
            out.append((g, g, min(255, g + 6), 255))
    write_png(os.path.join(TEXTURES, 'moon.png'), size, size, out)


if __name__ == '__main__':
    moon()
    clouds()
