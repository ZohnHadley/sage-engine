# -*- coding: utf-8 -*-
"""The engine's own textures (docs/design/06 §3.12).

The engine ships almost no art — a white pixel, a ground check, and now the dot every particle draws
with. All of it is *generated*, like the Sandbox's placeholders: a few dozen lines of arithmetic, so the
repository owns everything in it and a change to how they look is a change to this file.

Run:  python engine_content/tools/make_engine_textures.py
"""
import math
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'textures')


def write_png(path, width, height, pixels):
    """One RGBA PNG, no compression cleverness."""
    raw = bytearray()
    for y in range(height):
        raw.append(0)                      # filter: none
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
    print('%-16s %dx%d  %6d bytes' % (os.path.basename(path), width, height, len(png)))


def particle(size=32):
    """A soft round dot: white in the middle, fading to nothing at the edge.

    White on purpose. Every particle is tinted per vertex by its record's colour (06 §3.12), so one dot
    serves sparks, embers, smoke and blood, and the additive material makes the bright middle glow.
    """
    pixels = []
    centre = (size - 1) / 2.0
    for y in range(size):
        for x in range(size):
            distance = math.hypot(x - centre, y - centre) / (size / 2.0)
            # Squared falloff, cut off at the edge: a hard circle looks like a coin, this looks like light.
            alpha = max(0.0, 1.0 - distance) ** 2
            pixels.append((255, 255, 255, int(alpha * 255)))
    return pixels


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    write_png(os.path.join(OUT, 'particle.png'), 32, 32, particle(32))


if __name__ == '__main__':
    main()
