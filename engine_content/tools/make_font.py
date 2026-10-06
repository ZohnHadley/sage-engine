# -*- coding: utf-8 -*-
"""The engine's own font (docs/design/13 §3, TODO R12).

A 5x7 pixel font, drawn here and baked into an atlas — so the engine owns every byte of the text it
draws, ships no font it has not licensed, and needs no content pipeline to turn a `.spritefont` into an
`.xnb`. It is the last thing MGCB was doing.

The atlas is a fixed grid: 16 columns of 8x8 cells covering ASCII 32..127, so a glyph's place is
arithmetic (`code - 32`) and there is no metrics file to keep in step with it. Glyphs are white; the
colour comes from the tint at draw time, like every particle.

It also writes the same letters as a TrueType font, `fonts/sage.ttf` (issue #338): each lit pixel a
square of outline, proportional (a glyph is as wide as its ink and a pixel of space), so a ui_style can
name a TTF and get the engine's own letters through the same path as any font a game ships. Its em is
nine pixels — seven of glyph, one of air above and one below — so at size 9 one font pixel is one
screen pixel, and its line height is its size.

Run:  python engine_content/tools/make_font.py [--preview "text"]
"""
import os
import struct
import sys
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'textures')
FONTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fonts')

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


# ---- the TrueType font --------------------------------------------------------------------------------

UNIT = 100          # font units per pixel
EM = 9 * UNIT       # seven rows of glyph, a row of air above and one below
ASCENT = 8 * UNIT   # the baseline is under the glyph's bottom row
DESCENT = -1 * UNIT
SPACE = 3           # a space's advance, in pixels


def glyph_outline(art):
    """Rectangles (x0, y0, x1, y1) in font units, one per horizontal run of lit pixels, and the advance."""
    if art is None:
        return [], SPACE * UNIT
    lit = [x for line in art for x, bit in enumerate(line) if bit == '#']
    if not lit:
        return [], SPACE * UNIT
    left, right = min(lit), max(lit)
    rects = []
    for y, line in enumerate(art):
        x = 0
        while x < len(line):
            if line[x] != '#':
                x += 1
                continue
            start = x
            while x < len(line) and line[x] == '#':
                x += 1
            top = (GLYPH_H - y) * UNIT   # rows count down from the top, font units up from the baseline
            rects.append(((start - left) * UNIT, top - UNIT, (x - left) * UNIT, top))
    return rects, (right - left + 2) * UNIT


def glyf_entry(rects):
    if not rects:
        return b''
    xs = [r[0] for r in rects] + [r[2] for r in rects]
    ys = [r[1] for r in rects] + [r[3] for r in rects]
    out = struct.pack('>hhhhh', len(rects), min(xs), min(ys), max(xs), max(ys))
    points = []
    ends = []
    for x0, y0, x1, y1 in rects:
        points += [(x0, y0), (x0, y1), (x1, y1), (x1, y0)]   # clockwise, y up: an outer contour
        ends.append(len(points) - 1)
    out += b''.join(struct.pack('>H', e) for e in ends)
    out += struct.pack('>H', 0)            # no instructions
    out += bytes([1] * len(points))        # every point on the curve, coordinates as shorts
    px = py = 0
    for x, y in points:
        out += struct.pack('>h', x - px)
        px = x
    for x, y in points:
        out += struct.pack('>h', y - py)
        py = y
    if len(out) % 2:
        out += b'\0'
    return out


def cmap_format4(mapping):
    """mapping: codepoint -> glyph index, every codepoint under 0x10000."""
    codes = sorted(mapping)
    segments = []   # (start, end, delta) for runs whose glyph ids are consecutive
    for c in codes:
        g = mapping[c]
        if segments and segments[-1][1] == c - 1 and mapping[c - 1] == g - 1:
            segments[-1][1] = c
        else:
            segments.append([c, c, (g - c) & 0xFFFF])
    segments.append([0xFFFF, 0xFFFF, 1])
    n = len(segments)
    search = 2 ** (n.bit_length() - 1) * 2
    body = struct.pack('>HHHH', n * 2, search, (search // 2).bit_length() - 1, n * 2 - search)
    body += b''.join(struct.pack('>H', s[1]) for s in segments) + b'\0\0'
    body += b''.join(struct.pack('>H', s[0]) for s in segments)
    body += b''.join(struct.pack('>H', s[2]) for s in segments)
    body += b''.join(struct.pack('>H', 0) for s in segments)
    sub = struct.pack('>HHH', 4, 6 + len(body), 0) + body
    return struct.pack('>HH', 0, 1) + struct.pack('>HHI', 3, 1, 12) + sub


def name_table(strings):
    records, data = b'', b''
    for name_id, text in strings:
        raw = text.encode('utf-16-be')
        records += struct.pack('>HHHHHH', 3, 1, 0x409, name_id, len(raw), len(data))
        data += raw
    return struct.pack('>HHH', 0, len(strings), 6 + 12 * len(strings)) + records + data


def checksum(data):
    data += b'\0' * (-len(data) % 4)
    return sum(struct.unpack('>%dI' % (len(data) // 4), data)) & 0xFFFFFFFF


def build_ttf():
    chars = [c for c in ORDER if c != ' ']
    notdef = [(0, 0, 4 * UNIT, UNIT), (0, 6 * UNIT, 4 * UNIT, 7 * UNIT), (0, UNIT, UNIT, 6 * UNIT), (3 * UNIT, UNIT, 4 * UNIT, 6 * UNIT)]
    glyphs = [(notdef, 5 * UNIT), ([], SPACE * UNIT)]   # .notdef (a hollow box), then space
    mapping = {32: 1}
    for ch in chars:
        mapping[ord(ch)] = len(glyphs)
        glyphs.append(glyph_outline(GLYPHS.get(ch) or EXTRA.get(ch)))

    glyf, loca = b'', [0]
    for rects, _ in glyphs:
        glyf += glyf_entry(rects)
        loca.append(len(glyf))
    max_points = max(len(r) * 4 for r, _ in glyphs)
    max_contours = max(len(r) for r, _ in glyphs)
    widths = [a for _, a in glyphs]
    all_x = [v for r, _ in glyphs for q in r for v in (q[0], q[2])]
    all_y = [v for r, _ in glyphs for q in r for v in (q[1], q[3])]

    tables = {
        b'head': struct.pack('>IIIIHHqqhhhhHHhhh', 0x00010000, 0x00010000, 0, 0x5F0F3CF5, 0x000B, EM, 0, 0,
                             min(all_x), min(all_y), max(all_x), max(all_y), 0, 8, 2, 1, 0),
        b'hhea': struct.pack('>IhhhHhhhhhhhhhhhH', 0x00010000, ASCENT, DESCENT, 0, max(widths), 0, 0, max(all_x),
                             1, 0, 0, 0, 0, 0, 0, 0, len(glyphs)),
        b'maxp': struct.pack('>IHHHHHHHHHHHHHH', 0x00010000, len(glyphs), max_points, max_contours, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0),
        b'OS/2': struct.pack('>HhHHHhhhhhhhhhhh10sIIIIIHHHhhhHHIIhhHHH', 4, sum(widths) // len(widths), 400, 5, 0,
                             EM // 2, EM // 2, 0, 0, EM // 2, EM // 2, 0, EM // 2, UNIT, 4 * UNIT, 0, b'\0' * 10, 1, 0, 0, 0,
                             b'SAGE'[0] << 24 | b'SAGE'[1] << 16 | b'SAGE'[2] << 8 | b'SAGE'[3], 0x40, 32, 0x2026,
                             ASCENT, DESCENT, 0, ASCENT, -DESCENT, 1, 0, 5 * UNIT, 7 * UNIT, 0, 32, 0),
        b'hmtx': b''.join(struct.pack('>Hh', a, 0) for a in widths),
        b'cmap': cmap_format4(mapping),
        b'loca': b''.join(struct.pack('>I', o) for o in loca),
        b'glyf': glyf,
        b'name': name_table([(1, 'Sage'), (2, 'Regular'), (3, 'Sage Regular'), (4, 'Sage Regular'), (5, 'Version 1.0'), (6, 'Sage-Regular')]),
        b'post': struct.pack('>IIhhIIIII', 0x00030000, 0, -UNIT, UNIT // 2, 0, 0, 0, 0, 0),
    }

    tags = sorted(tables)
    n = len(tags)
    search = 2 ** (n.bit_length() - 1)
    header = struct.pack('>IHHHH', 0x00010000, n, search * 16, search.bit_length() - 1, n * 16 - search * 16)
    offset = 12 + 16 * n
    directory, body = b'', b''
    for tag in tags:
        data = tables[tag]
        directory += struct.pack('>4sIII', tag, checksum(data), offset + len(body), len(data))
        body += data + b'\0' * (-len(data) % 4)
    font = bytearray(header + directory + body)
    adjustment = (0xB1B0AFBA - checksum(bytes(font))) & 0xFFFFFFFF
    for i, tag in enumerate(tags):
        if tag == b'head':
            head = struct.unpack('>I', directory[16 * i + 8:16 * i + 12])[0]
    font[head + 8:head + 12] = struct.pack('>I', adjustment)
    return bytes(font), len(glyphs)


def write_ttf(path):
    data, count = build_ttf()
    open(path, 'wb').write(data)
    print('%-16s %5d bytes  %d glyphs' % (os.path.basename(path), len(data), count))


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
    if not os.path.isdir(FONTS):
        os.makedirs(FONTS)
    write_ttf(os.path.join(FONTS, 'sage.ttf'))


if __name__ == '__main__':
    main()
