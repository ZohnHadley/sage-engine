# -*- coding: utf-8 -*-
"""The engine's own font (docs/design/13 §3, TODO R12).

A 5x7 pixel font, drawn here and baked into an atlas — so the engine owns every byte of the text it
draws, ships no font it has not licensed, and needs no content pipeline to turn a `.spritefont` into an
`.xnb`. It is the last thing MGCB was doing.

The atlas is a fixed grid: 16 columns of 8x8 cells covering ASCII 32..127, so a glyph's place is
arithmetic (`code - 32`) and there is no metrics file to keep in step with it. Glyphs are white; the
colour comes from the tint at draw time, like every particle.

Run:  python engine_content/tools/make_font.py [--preview "text"]
"""
import os
import struct
import sys
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'textures')

CELL = 8          # the grid a glyph sits in
GLYPH_W = 5       # and how much of it a glyph uses
GLYPH_H = 7
COLUMNS = 16

# Each glyph is seven rows of five, '#' on and '.' off. Written out rather than compressed into bits:
# the whole point is that somebody can read a letter here and see the letter.
GLYPHS = {
    ' ': ("     ", "     ", "     ", "     ", "     ", "     ", "     "),
    '!': ("  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "     ", "  #  "),
    '"': (" # # ", " # # ", "     ", "     ", "     ", "     ", "     "),
    '#': (" # # ", " # # ", "#####", " # # ", "#####", " # # ", " # # "),
    '$': ("  #  ", " ####", "# #  ", " ### ", "  # #", "#### ", "  #  "),
    '%': ("##   ", "##  #", "   # ", "  #  ", " #   ", "#  ##", "   ##"),
    '&': (" ##  ", "#  # ", "#  # ", " ##  ", "#  ##", "#  # ", " ## #"),
    "'": ("  #  ", "  #  ", "     ", "     ", "     ", "     ", "     "),
    '(': ("   # ", "  #  ", " #   ", " #   ", " #   ", "  #  ", "   # "),
    ')': (" #   ", "  #  ", "   # ", "   # ", "   # ", "  #  ", " #   "),
    '*': ("     ", " # # ", "  #  ", "#####", "  #  ", " # # ", "     "),
    '+': ("     ", "  #  ", "  #  ", "#####", "  #  ", "  #  ", "     "),
    ',': ("     ", "     ", "     ", "     ", "  ## ", "  #  ", " #   "),
    '-': ("     ", "     ", "     ", "#####", "     ", "     ", "     "),
    '.': ("     ", "     ", "     ", "     ", "     ", " ##  ", " ##  "),
    '/': ("    #", "    #", "   # ", "  #  ", " #   ", "#    ", "#    "),
    '0': (" ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### "),
    '1': ("  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "),
    '2': (" ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####"),
    '3': ("#####", "   # ", "  #  ", "   # ", "    #", "#   #", " ### "),
    '4': ("   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # "),
    '5': ("#####", "#    ", "#### ", "    #", "    #", "#   #", " ### "),
    '6': ("  ## ", " #   ", "#    ", "#### ", "#   #", "#   #", " ### "),
    '7': ("#####", "    #", "   # ", "  #  ", " #   ", " #   ", " #   "),
    '8': (" ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### "),
    '9': (" ### ", "#   #", "#   #", " ####", "    #", "   # ", " ##  "),
    ':': ("     ", " ##  ", " ##  ", "     ", " ##  ", " ##  ", "     "),
    ';': ("     ", " ##  ", " ##  ", "     ", " ##  ", "  #  ", " #   "),
    '<': ("   # ", "  #  ", " #   ", "#    ", " #   ", "  #  ", "   # "),
    '=': ("     ", "     ", "#####", "     ", "#####", "     ", "     "),
    '>': (" #   ", "  #  ", "   # ", "    #", "   # ", "  #  ", " #   "),
    '?': (" ### ", "#   #", "    #", "   # ", "  #  ", "     ", "  #  "),
    '@': (" ### ", "#   #", "# ###", "# # #", "# ## ", "#    ", " ### "),
    'A': (" ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #"),
    'B': ("#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### "),
    'C': (" ### ", "#   #", "#    ", "#    ", "#    ", "#   #", " ### "),
    'D': ("#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### "),
    'E': ("#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####"),
    'F': ("#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#    "),
    'G': (" ### ", "#   #", "#    ", "#  ##", "#   #", "#   #", " ####"),
    'H': ("#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #"),
    'I': (" ### ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "),
    'J': ("  ###", "   # ", "   # ", "   # ", "   # ", "#  # ", " ##  "),
    'K': ("#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #"),
    'L': ("#    ", "#    ", "#    ", "#    ", "#    ", "#    ", "#####"),
    'M': ("#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #"),
    'N': ("#   #", "##  #", "# # #", "#  ##", "#   #", "#   #", "#   #"),
    'O': (" ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### "),
    'P': ("#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    "),
    'Q': (" ### ", "#   #", "#   #", "#   #", "# # #", "#  # ", " ## #"),
    'R': ("#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #"),
    'S': (" ####", "#    ", "#    ", " ### ", "    #", "    #", "#### "),
    'T': ("#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  "),
    'U': ("#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### "),
    'V': ("#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  "),
    'W': ("#   #", "#   #", "#   #", "# # #", "# # #", "## ##", "#   #"),
    'X': ("#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #"),
    'Y': ("#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  "),
    'Z': ("#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####"),
    '[': ("  ###", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  ###"),
    '\\': ("#    ", "#    ", " #   ", "  #  ", "   # ", "    #", "    #"),
    ']': ("###  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "###  "),
    '^': ("  #  ", " # # ", "#   #", "     ", "     ", "     ", "     "),
    '_': ("     ", "     ", "     ", "     ", "     ", "     ", "#####"),
    '`': (" #   ", "  #  ", "     ", "     ", "     ", "     ", "     "),
    'a': ("     ", "     ", " ### ", "    #", " ####", "#   #", " ####"),
    'b': ("#    ", "#    ", "#### ", "#   #", "#   #", "#   #", "#### "),
    'c': ("     ", "     ", " ### ", "#    ", "#    ", "#   #", " ### "),
    'd': ("    #", "    #", " ####", "#   #", "#   #", "#   #", " ####"),
    'e': ("     ", "     ", " ### ", "#   #", "#####", "#    ", " ### "),
    'f': ("  ## ", " #   ", " #   ", "#### ", " #   ", " #   ", " #   "),
    'g': ("     ", "     ", " ####", "#   #", " ####", "    #", "####"),
    'h': ("#    ", "#    ", "#### ", "#   #", "#   #", "#   #", "#   #"),
    'i': ("  #  ", "     ", " ##  ", "  #  ", "  #  ", "  #  ", " ### "),
    'j': ("   # ", "     ", "  ## ", "   # ", "   # ", "#  # ", " ##  "),
    'k': ("#    ", "#    ", "#  # ", "# #  ", "##   ", "# #  ", "#  # "),
    'l': (" ##  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "),
    'm': ("     ", "     ", "## # ", "# # #", "# # #", "#   #", "#   #"),
    'n': ("     ", "     ", "#### ", "#   #", "#   #", "#   #", "#   #"),
    'o': ("     ", "     ", " ### ", "#   #", "#   #", "#   #", " ### "),
    'p': ("     ", "     ", "#### ", "#   #", "#### ", "#    ", "#    "),
    'q': ("     ", "     ", " ####", "#   #", " ####", "    #", "    #"),
    'r': ("     ", "     ", "# ## ", "##   ", "#    ", "#    ", "#    "),
    's': ("     ", "     ", " ####", "#    ", " ### ", "    #", "#### "),
    't': (" #   ", " #   ", "#### ", " #   ", " #   ", " #  #", "  ## "),
    'u': ("     ", "     ", "#   #", "#   #", "#   #", "#   #", " ####"),
    'v': ("     ", "     ", "#   #", "#   #", "#   #", " # # ", "  #  "),
    'w': ("     ", "     ", "#   #", "#   #", "# # #", "# # #", " # # "),
    'x': ("     ", "     ", "#   #", " # # ", "  #  ", " # # ", "#   #"),
    'y': ("     ", "     ", "#   #", "#   #", " ####", "    #", "####"),
    'z': ("     ", "     ", "#####", "   # ", "  #  ", " #   ", "#####"),
    '{': ("   ##", "  #  ", "  #  ", " #   ", "  #  ", "  #  ", "   ##"),
    '|': ("  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  "),
    '}': ("##   ", "  #  ", "  #  ", "   # ", "  #  ", "  #  ", "##   "),
    '~': ("     ", "     ", " #  #", "# # #", "#  # ", "     ", "     "),
}

# Not ASCII, but the engine's own hint lines use them, so they get cells past 127.
EXTRA = {
    '↑': ("  #  ", " ### ", "# # #", "  #  ", "  #  ", "  #  ", "  #  "),   # up
    '↓': ("  #  ", "  #  ", "  #  ", "  #  ", "# # #", " ### ", "  #  "),   # down
    '•': ("     ", "     ", " ### ", " ### ", " ### ", "     ", "     "),   # bullet
    '×': ("     ", "     ", "#   #", " # # ", "  #  ", " # # ", "#   #"),   # times
    '…': ("     ", "     ", "     ", "     ", "     ", "     ", "# # #"),   # ellipsis
}


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
    print('%-16s %dx%d  %5d bytes  %d glyphs' % (os.path.basename(path), width, height, len(png), len(ORDER)))


# The order the atlas is in: ASCII 32..126 exactly where their codes say, then the extras after it.
ORDER = [chr(c) for c in range(32, 127)] + list(EXTRA.keys())


def build():
    rows = (len(ORDER) + COLUMNS - 1) // COLUMNS
    width, height = COLUMNS * CELL, rows * CELL
    pixels = [(255, 255, 255, 0)] * (width * height)

    for index, ch in enumerate(ORDER):
        art = GLYPHS.get(ch) or EXTRA.get(ch)
        if art is None:
            continue
        ox, oy = (index % COLUMNS) * CELL, (index // COLUMNS) * CELL
        for y, line in enumerate(art):
            for x, bit in enumerate(line):
                if bit == '#':
                    pixels[(oy + y) * width + ox + x] = (255, 255, 255, 255)
    return width, height, pixels


def preview(text):
    """Draws a line of text as ASCII, so a wrong glyph is obvious before anything is built."""
    for y in range(GLYPH_H):
        print(''.join((GLYPHS.get(ch) or EXTRA.get(ch) or GLYPHS[' '])[y] + ' ' for ch in text))


def main():
    if '--preview' in sys.argv:
        preview(sys.argv[sys.argv.index('--preview') + 1])
        return
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    width, height, pixels = build()
    write_png(os.path.join(OUT, 'font.png'), width, height, pixels)


if __name__ == '__main__':
    main()
