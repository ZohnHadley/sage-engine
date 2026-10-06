# -*- coding: utf-8 -*-
"""The Sandbox's normal-mapped wall (engine issue #410): a brick wall model and its three maps.

lit.fx draws a material's normal map, specular map, emissive and environment (issue #410); this is the
Sandbox's showcase of the first two. A slab of brick stands near the start with a lamp circling in front
of it (content/data/scene.json), so the bricks' bevels and the mortar's grooves catch the light as it
moves, and the bricks shine a little where the mortar does not.

Written out by hand like the bunny (make_placeholder_model.py): a box as a `.glb` with POSITION, NORMAL,
TEXCOORD_0 and TANGENT (so the engine's reading of a file's own tangents is exercised; a file without them
gets tangents worked out on load), and three PNGs made from one height field:

  brick_wall.png         the colour: brick red with a little variation per brick, grey mortar
  brick_wall_normal.png  the height field's slopes as a tangent-space normal map, glTF's way round:
                         red is +u (right), green is up the image, blue out of the surface
  brick_wall_spec.png    red: how shiny (bricks a little, mortar not at all); green: how glossy

Run:  python games/Sandbox/tools/make_brick_wall.py
"""
import json
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, '..', 'content', 'models')
TEXTURES = os.path.join(HERE, '..', 'content', 'textures')

SIZE = 128                 # pixels; the texture covers one metre square
COURSES = 8                # rows of bricks in it
BRICKS = 4                 # bricks a row
MORTAR = 2                 # pixels of mortar between bricks
BEVEL = 3                  # pixels over which a brick's edge rounds down to the mortar
DEPTH = 6.0                # how many pixels deep the mortar is, for the slopes

WALL = (3.0, 3.0, 0.3)     # width, height, thickness in metres; the origin is its middle


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
    print('%-24s %dx%d  %6d bytes' % (os.path.basename(path), width, height, len(png)))


def hash01(x, y, seed):
    """A repeatable hash to 0..1, so every run makes the same wall."""
    n = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)
    n = (n ^ (n >> 13)) * 1274126177
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def brick_at(x, y):
    """(course, brick, distance to the brick's nearest edge in pixels), the texture tiling both ways."""
    course_h = SIZE // COURSES
    brick_w = SIZE // BRICKS
    course = (y % SIZE) // course_h
    shift = (brick_w // 2) if course % 2 else 0         # every other course offset by half a brick
    bx = (x + shift) % SIZE
    brick = bx // brick_w
    inside_x = bx % brick_w
    inside_y = (y % SIZE) % course_h
    edge = min(inside_x, brick_w - 1 - inside_x, inside_y, course_h - 1 - inside_y)
    return course, brick, edge


def height(x, y):
    """0 in the mortar, rising over the bevel to 1 on a brick's face, with a little roughness."""
    course, brick, edge = brick_at(x, y)
    if edge < MORTAR // 2 + 0.5:
        return 0.06 * hash01(x, y, 3)
    t = min(1.0, (edge - MORTAR // 2 + 0.5) / BEVEL)
    return t * (0.94 + 0.06 * hash01(x // 2, y // 2, course * 7 + brick))


def maps():
    colour, normal, spec = [], [], []
    for y in range(SIZE):
        for x in range(SIZE):
            course, brick, edge = brick_at(x, y)
            mortar = edge < MORTAR // 2 + 0.5
            # Colour.
            if mortar:
                g = int(110 + 30 * hash01(x, y, 5))
                colour.append((g, g - 4, g - 10, 255))
            else:
                tone = 0.8 + 0.3 * hash01(course, brick, 11)
                speck = 0.9 + 0.2 * hash01(x, y, 13)
                colour.append((min(255, int(150 * tone * speck)), int(62 * tone * speck), int(44 * tone * speck), 255))
            # Normal: the height field's slopes. The image's y runs down and the map's green runs up.
            dx = (height(x + 1, y) - height(x - 1, y)) * 0.5 * DEPTH
            dy = (height(x, y + 1) - height(x, y - 1)) * 0.5 * DEPTH
            nx, ny, nz = -dx, dy, 1.0
            length = math.sqrt(nx * nx + ny * ny + nz * nz)
            normal.append(tuple(int(round((c / length * 0.5 + 0.5) * 255)) for c in (nx, ny, nz)) + (255,))
            # Specular: red is shine, green gloss.
            spec.append((20, 60, 0, 255) if mortar else (int(150 + 60 * hash01(x, y, 17)), 110, 0, 255))
    return colour, normal, spec


# Per face: its normal, the direction its texture's u runs (right) and the direction up the image, with
# normal = cross(right, up), so a face's tangent is `right` and its bitangent cross(normal, tangent) is `up`:
# w = +1 everywhere.
FACES = [
    ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
    ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
    ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
    ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
    ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
    ((0, -1, 0), (1, 0, 0), (0, 0, 1)),
]


def build():
    half = [s / 2 for s in WALL]
    positions, normals, uvs, tangents, indices = [], [], [], [], []
    for n, r, u in FACES:
        # How many metres the face is across and up: the texture is a metre square.
        across = sum(abs(r[i]) * WALL[i] for i in range(3))
        up = sum(abs(u[i]) * WALL[i] for i in range(3))
        base = len(positions)
        for sr, su in ((-1, -1), (1, -1), (1, 1), (-1, 1)):   # counter-clockwise seen from outside
            positions.append(tuple(n[i] * half[i] + sr * r[i] * half[i] + su * u[i] * half[i] for i in range(3)))
            normals.append(n)
            uvs.append(((sr + 1) / 2 * across, (1 - (su + 1) / 2) * up))   # v runs down the image
            tangents.append(r + (1.0,))
        indices += [base, base + 1, base + 2, base, base + 2, base + 3]
    return positions, normals, uvs, tangents, indices


def write_glb(path, positions, normals, uvs, tangents, indices):
    streams = [
        b''.join(struct.pack('<I', i) for i in indices),
        b''.join(struct.pack('<fff', *p) for p in positions),
        b''.join(struct.pack('<fff', *n) for n in normals),
        b''.join(struct.pack('<ff', *t) for t in uvs),
        b''.join(struct.pack('<ffff', *t) for t in tangents),
    ]
    views, offset = [], 0
    for data in streams:
        views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': len(data)})
        offset += len(data)
    binary = b''.join(streams)
    lo = [min(p[i] for p in positions) for i in range(3)]
    hi = [max(p[i] for p in positions) for i in range(3)]

    gltf = {
        'asset': {'version': '2.0', 'generator': 'sage-engine make_brick_wall.py'},
        'scene': 0,
        'scenes': [{'nodes': [0]}],
        'nodes': [{'mesh': 0, 'name': 'brick_wall'}],
        'meshes': [{'name': 'brick_wall', 'primitives': [
            {'attributes': {'POSITION': 1, 'NORMAL': 2, 'TEXCOORD_0': 3, 'TANGENT': 4}, 'indices': 0, 'mode': 4}]}],
        'buffers': [{'byteLength': len(binary)}],
        'bufferViews': views,
        'accessors': [
            {'bufferView': 0, 'componentType': 5125, 'count': len(indices), 'type': 'SCALAR'},
            {'bufferView': 1, 'componentType': 5126, 'count': len(positions), 'type': 'VEC3', 'min': lo, 'max': hi},
            {'bufferView': 2, 'componentType': 5126, 'count': len(normals), 'type': 'VEC3'},
            {'bufferView': 3, 'componentType': 5126, 'count': len(uvs), 'type': 'VEC2'},
            {'bufferView': 4, 'componentType': 5126, 'count': len(tangents), 'type': 'VEC4'},
        ],
    }
    json_bytes = json.dumps(gltf, separators=(',', ':')).encode('utf-8')
    json_bytes += b' ' * ((4 - len(json_bytes) % 4) % 4)
    binary += b'\x00' * ((4 - len(binary) % 4) % 4)
    glb = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(json_bytes) + 8 + len(binary))
    glb += struct.pack('<II', len(json_bytes), 0x4E4F534A) + json_bytes
    glb += struct.pack('<II', len(binary), 0x004E4942) + binary
    open(path, 'wb').write(glb)
    print('%-24s %d vertices, %d triangles, %d bytes'
          % (os.path.basename(path), len(positions), len(indices) // 3, len(glb)))


def main():
    for folder in (MODELS, TEXTURES):
        if not os.path.isdir(folder):
            os.makedirs(folder)
    write_glb(os.path.join(MODELS, 'brick_wall.glb'), *build())
    colour, normal, spec = maps()
    write_png(os.path.join(TEXTURES, 'brick_wall.png'), SIZE, SIZE, colour)
    write_png(os.path.join(TEXTURES, 'brick_wall_normal.png'), SIZE, SIZE, normal)
    write_png(os.path.join(TEXTURES, 'brick_wall_spec.png'), SIZE, SIZE, spec)


if __name__ == '__main__':
    main()
