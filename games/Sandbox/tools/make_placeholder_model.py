# -*- coding: utf-8 -*-
"""The Sandbox's placeholder model (docs/design/05 §3.4, TODO R12).

The same bargain as the sprites and the sounds: the repository ships no art it does not own, so the one
model in it is *generated* — a blocky rabbit made of five boxes, written straight out as a `.glb`.

A `.glb` is a twelve-byte header and two chunks: the glTF JSON, and the binary buffer its accessors point
into. Writing one by hand is about as much work as describing it, and it means the engine's runtime model
loader is exercised by content the repository made rather than by a file nobody can regenerate.

Run:  python games/Sandbox/tools/make_placeholder_model.py
"""
import json
import os
import struct

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'content', 'models')

# Each box is (centre, half-extents). A body, a head, two ears and a tail: enough of a rabbit that
# nobody has to be told what it is, and no more.
BOXES = [
    ((0.00, 0.22, 0.00), (0.22, 0.18, 0.32)),     # body
    ((0.00, 0.46, -0.26), (0.15, 0.15, 0.15)),    # head
    ((-0.08, 0.68, -0.26), (0.04, 0.12, 0.03)),   # left ear
    ((0.08, 0.68, -0.26), (0.04, 0.12, 0.03)),    # right ear
    ((0.00, 0.28, 0.34), (0.07, 0.07, 0.07)),     # tail
]

# Per face: the normal, and the four corners as (x, y, z) sign multipliers in winding order.
FACES = [
    ((0, 0, 1), [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]),
    ((0, 0, -1), [(1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1)]),
    ((1, 0, 0), [(1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)]),
    ((-1, 0, 0), [(-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)]),
    ((0, 1, 0), [(-1, 1, 1), (1, 1, 1), (1, 1, -1), (-1, 1, -1)]),
    ((0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
]


def build():
    positions, normals, uvs, indices = [], [], [], []
    for centre, half in BOXES:
        for normal, corners in FACES:
            base = len(positions)
            for i, (sx, sy, sz) in enumerate(corners):
                positions.append((centre[0] + sx * half[0],
                                  centre[1] + sy * half[1],
                                  centre[2] + sz * half[2]))
                normals.append(normal)
                uvs.append([(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)][i])
            indices += [base, base + 1, base + 2, base, base + 2, base + 3]
    return positions, normals, uvs, indices


def write_glb(path, positions, normals, uvs, indices):
    # One buffer, four views: indices, then the three vertex streams. Everything is little-endian and
    # four-byte aligned, which is all the format asks for.
    index_bytes = b''.join(struct.pack('<I', i) for i in indices)
    position_bytes = b''.join(struct.pack('<fff', *p) for p in positions)
    normal_bytes = b''.join(struct.pack('<fff', *n) for n in normals)
    uv_bytes = b''.join(struct.pack('<ff', *t) for t in uvs)

    chunks, offset, views = [], 0, []
    for data in (index_bytes, position_bytes, normal_bytes, uv_bytes):
        views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': len(data)})
        chunks.append(data)
        offset += len(data)
    binary = b''.join(chunks)

    lo = [min(p[i] for p in positions) for i in range(3)]
    hi = [max(p[i] for p in positions) for i in range(3)]

    gltf = {
        'asset': {'version': '2.0', 'generator': 'sage-engine make_placeholder_model.py'},
        'scene': 0,
        'scenes': [{'nodes': [0]}],
        'nodes': [{'mesh': 0, 'name': 'bunny'}],
        'meshes': [{'name': 'bunny', 'primitives': [
            {'attributes': {'POSITION': 1, 'NORMAL': 2, 'TEXCOORD_0': 3}, 'indices': 0, 'mode': 4}]}],
        'buffers': [{'byteLength': len(binary)}],
        'bufferViews': views,
        'accessors': [
            {'bufferView': 0, 'componentType': 5125, 'count': len(indices), 'type': 'SCALAR'},
            {'bufferView': 1, 'componentType': 5126, 'count': len(positions), 'type': 'VEC3',
             'min': lo, 'max': hi},
            {'bufferView': 2, 'componentType': 5126, 'count': len(normals), 'type': 'VEC3'},
            {'bufferView': 3, 'componentType': 5126, 'count': len(uvs), 'type': 'VEC2'},
        ],
    }

    json_bytes = json.dumps(gltf, separators=(',', ':')).encode('utf-8')
    json_bytes += b' ' * ((4 - len(json_bytes) % 4) % 4)          # chunks are four-byte aligned
    binary += b'\x00' * ((4 - len(binary) % 4) % 4)

    glb = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(json_bytes) + 8 + len(binary))
    glb += struct.pack('<II', len(json_bytes), 0x4E4F534A) + json_bytes
    glb += struct.pack('<II', len(binary), 0x004E4942) + binary
    open(path, 'wb').write(glb)
    print('%-18s %d vertices, %d triangles, %d bytes'
          % (os.path.basename(path), len(positions), len(indices) // 3, len(glb)))


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    positions, normals, uvs, indices = build()
    write_glb(os.path.join(OUT, 'bunny.glb'), positions, normals, uvs, indices)


if __name__ == '__main__':
    main()
