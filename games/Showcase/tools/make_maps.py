# -*- coding: utf-8 -*-
"""The Showcase's brush levels, in TrenchBroom's standard format (32 units a metre, z up, map north is
engine -Z). Each is written from boxes in engine metres, relative to the level's own `at` (its floor's top
is y = 0), so the map record in content/data/showcase.json says where it stands.

  plaza.map   the hub: a cobbled square on flattened ground, four stone blocks at its corners
  hall.map    the lights hall: walls, a roof and a doorway to the west; pillars and crates to cast shadows
  court.map   the weather court: paving and a small roofed shelter that stays dry
  deck.map    the terrain terrace: a plank deck on the hillside, the ground under it laid flat
  yard.map    the yard: a flat floor for the walking mannequins and the instanced crystal field

Run:  python games/Showcase/tools/make_maps.py
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
MAPS = os.path.join(HERE, '..', 'content', 'maps')


def box(x0, y0, z0, x1, y1, z1, texture):
    """A brush from engine metres (x east, y up, z south) to Quake units, with the canonical plane order."""
    q = lambda v: int(round(v * 32))
    lx, ly, lz = q(x0), q(-z1), q(y0)
    hx, hy, hz = q(x1), q(-z0), q(y1)
    faces = [
        ((lx, ly, lz), (lx, ly + 1, lz), (lx, ly, lz + 1)),
        ((lx, ly, lz), (lx, ly, lz + 1), (lx + 1, ly, lz)),
        ((lx, ly, lz), (lx + 1, ly, lz), (lx, ly + 1, lz)),
        ((hx, hy, hz), (hx, hy + 1, hz), (hx + 1, hy, hz)),
        ((hx, hy, hz), (hx + 1, hy, hz), (hx, hy, hz + 1)),
        ((hx, hy, hz), (hx, hy, hz + 1), (hx, hy + 1, hz)),
    ]
    lines = ['{']
    for p1, p2, p3 in faces:
        lines.append('( %d %d %d ) ( %d %d %d ) ( %d %d %d ) %s 0 0 0 1 1' % (p1 + p2 + p3 + (texture,)))
    lines.append('}')
    return lines


def write(name, message, brushes):
    lines = ['// %s (games/Showcase/tools/make_maps.py; regenerate rather than edit by hand)' % message,
             '{', '"classname" "worldspawn"', '"message" "%s"' % message]
    for b in brushes:
        lines += b
    lines.append('}')
    path = os.path.join(MAPS, name)
    with open(path, 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print('wrote', os.path.normpath(path))


def plaza():
    h = 8.0
    b = [box(-h, -0.3, -h, h, 0, h, 'cobble')]
    for sx in (-1, 1):
        for sz in (-1, 1):
            cx, cz = sx * (h - 0.6), sz * (h - 0.6)
            b.append(box(cx - 0.6, 0, cz - 0.6, cx + 0.6, 0.8, cz + 0.6, 'stone'))
    return b


def hall():
    # Inside 12 m (x) by 10 m (z), 4.5 m high; walls 0.4 m; a doorway 2.4 m wide and 3 m high in the west wall.
    w, d, ht, t = 6.0, 5.0, 4.5, 0.4
    b = [box(-w - t, -0.3, -d - t, w + t, 0, d + t, 'planks'),                  # floor
         box(-w - t, ht, -d - t, w + t, ht + 0.4, d + t, 'roof'),             # roof
         box(-w - t, 0, -d - t, w + t, ht, -d, 'brick'),                      # north wall
         box(-w - t, 0, d, w + t, ht, d + t, 'brick'),                        # south wall
         box(w, 0, -d, w + t, ht, d, 'brick'),                                # east wall
         box(-w - t, 0, -d, -w, ht, -1.2, 'brick'),                           # west wall, north of the door
         box(-w - t, 0, 1.2, -w, ht, d, 'brick'),                             # west wall, south of the door
         box(-w - t, 3.0, -1.2, -w, ht, 1.2, 'brick')]                        # over the door
    # Two pillars and a few crates for the lamps to throw shadows from.
    for px in (-1.5, 2.5):
        b.append(box(px - 0.35, 0, -0.35, px + 0.35, ht, 0.35, 'stone'))
    for (cx, cz, s) in ((4.2, -3.4, 0.9), (4.0, 3.2, 1.1), (-3.8, 3.6, 0.8), (0.6, -3.8, 0.7)):
        b.append(box(cx - s / 2, 0, cz - s / 2, cx + s / 2, s, cz + s / 2, 'planks'))
    return b


def court():
    h = 7.0
    b = [box(-h, -0.3, -h, h, 0, h, 'stone')]
    # The shelter in the court's north-east corner: four posts and a roof, 4 m square, 2.8 m up.
    sx, sz = 3.5, -3.5
    for ox in (-1.9, 1.9):
        for oz in (-1.9, 1.9):
            b.append(box(sx + ox - 0.15, 0, sz + oz - 0.15, sx + ox + 0.15, 2.8, sz + oz + 0.15, 'planks'))
    b.append(box(sx - 2.3, 2.8, sz - 2.3, sx + 2.3, 3.05, sz + 2.3, 'roof'))
    return b


def deck():
    b = [box(-4, -0.3, -3, 4, 0, 3, 'planks')]
    for x in (-3.9, -1.3, 1.3, 3.9):
        b.append(box(x - 0.08, 0, 2.84, x + 0.08, 1.0, 3.0, 'planks'))
    b.append(box(-4, 1.0, 2.84, 4, 1.1, 3.0, 'planks'))
    return b


def yard():
    return [box(-8, -0.3, -6, 8, 0, 6, 'stone')]


if __name__ == '__main__':
    os.makedirs(MAPS, exist_ok=True)
    write('plaza.map', 'The Showcase hub', plaza())
    write('hall.map', 'The lights hall', hall())
    write('court.map', 'The weather court', court())
    write('deck.map', 'The terrain terrace', deck())
    write('yard.map', 'The yard', yard())
