# -*- coding: utf-8 -*-
"""The Sandbox's first-person arms and sword (engine issue #121: viewmodels).

The same bargain as the bunny (make_placeholder_model.py): no art the repository does not own, so the
arms are *generated* — boxes skinned to a seven-joint rig, with three clips, written straight out as a
`.glb` — and so is the sword they hold. Deliberately crude and meant to be replaced: issue #122 brings
a box mannequin and proper arms (make_mannequin.py).

`arms.glb` is modelled in **view space**, the camera's own: the eye at the origin, x right, y up,
looking down -Z. The Sandbox's `viewmodel` record puts it there with no offset, so what you see here is
what the player sees.

  joints   root → shoulder_r → elbow_r → hand_r, root → shoulder_l → elbow_l → hand_l
           (parents first, rest rotation identity; each box is skinned wholly to the joint it hangs from)
  clips    idle    (2 s, loops)  the root breathes: 8 mm down and back
           attack  (0.5 s)       the right arm draws back, then swings down and across
           reload  (1.2 s)       the left hand drops away (0.2–0.4 s: where `mag_out` goes), holds, comes
                                 back up (0.8–1.0 s: `mag_in`) while the right hand tilts the weapon over

`viewmodel_sword.glb` is a rigid blade along +Y from a grip at the origin: the `hand_r` socket
(content/data/viewmodel.json) tilts it forward.

Run:  python games/Sandbox/tools/make_viewmodel_arms.py
"""
import json
import math
import os
import struct

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'content', 'models')

# name, parent index, rest position in view space (a joint's local translation is this minus its parent's)
JOINTS = [
    ('root', -1, (0.0, 0.0, 0.0)),
    ('shoulder_r', 0, (0.26, -0.34, 0.08)),
    ('elbow_r', 1, (0.24, -0.30, -0.22)),
    ('hand_r', 2, (0.16, -0.18, -0.46)),
    ('shoulder_l', 0, (-0.26, -0.34, 0.08)),
    ('elbow_l', 4, (-0.22, -0.32, -0.20)),
    ('hand_l', 5, (-0.12, -0.20, -0.42)),
]

# Boxes: (joint, from, to, half width, half height) along a bone, or (joint, centre, half extents) for a hand.
SEGMENTS = [
    (1, JOINTS[1][2], JOINTS[2][2], 0.055, 0.055),   # right upper arm
    (2, JOINTS[2][2], JOINTS[3][2], 0.045, 0.045),   # right forearm
    (4, JOINTS[4][2], JOINTS[5][2], 0.055, 0.055),   # left upper arm
    (5, JOINTS[5][2], JOINTS[6][2], 0.045, 0.045),   # left forearm
]
HANDS = [
    (3, (0.16, -0.18, -0.51), (0.045, 0.03, 0.06)),
    (6, (-0.12, -0.20, -0.47), (0.045, 0.03, 0.06)),
]

# Per face: the normal, and the four corners as (x, y, z) sign multipliers, counter-clockwise from outside.
FACES = [
    ((0, 0, 1), [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]),
    ((0, 0, -1), [(1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1)]),
    ((1, 0, 0), [(1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)]),
    ((-1, 0, 0), [(-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)]),
    ((0, 1, 0), [(-1, 1, 1), (1, 1, 1), (1, 1, -1), (-1, 1, -1)]),
    ((0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
]


def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def scale(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    length = math.sqrt(dot(a, a))
    return scale(a, 1.0 / length)


def box(mesh, joint, centre, axes, half):
    """A box at `centre` whose local x, y, z are `axes` (orthonormal, right-handed), skinned to `joint`."""
    positions, normals, joints, indices = mesh
    for normal, corners in FACES:
        base = len(positions)
        n = add(add(scale(axes[0], normal[0]), scale(axes[1], normal[1])), scale(axes[2], normal[2]))
        for sx, sy, sz in corners:
            offset = add(add(scale(axes[0], sx * half[0]), scale(axes[1], sy * half[1])), scale(axes[2], sz * half[2]))
            positions.append(add(centre, offset))
            normals.append(n)
            joints.append(joint)
        indices += [base, base + 1, base + 2, base, base + 2, base + 3]


def bone_box(mesh, joint, a, b, hw, hh):
    z = norm(sub(b, a))
    x = cross((0.0, 1.0, 0.0), z)
    x = norm(x) if dot(x, x) > 1e-8 else (1.0, 0.0, 0.0)
    y = cross(z, x)
    length = math.sqrt(dot(sub(b, a), sub(b, a)))
    box(mesh, joint, scale(add(a, b), 0.5), (x, y, z), (hw, hh, length * 0.5 + 0.02))


def quat(axis, degrees):
    half = math.radians(degrees) * 0.5
    s = math.sin(half)
    a = norm(axis)
    return (a[0] * s, a[1] * s, a[2] * s, math.cos(half))


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


IDENTITY = (0.0, 0.0, 0.0, 1.0)

# clip name → list of (joint, path, [(time, value)]): LINEAR keys.
CLIPS = {
    'idle': [
        (0, 'translation', [(0.0, (0, 0, 0)), (1.0, (0, -0.008, 0.004)), (2.0, (0, 0, 0))]),
    ],
    'attack': [
        (1, 'rotation', [(0.0, IDENTITY), (0.12, quat((1, 0, 0), 35)), (0.28, qmul(quat((0, 1, 0), 25), quat((1, 0, 0), -45))),
                         (0.5, IDENTITY)]),
        (2, 'rotation', [(0.0, IDENTITY), (0.12, quat((1, 0, 0), 25)), (0.28, quat((1, 0, 0), -10)), (0.5, IDENTITY)]),
    ],
    'reload': [
        (4, 'rotation', [(0.0, IDENTITY), (0.2, IDENTITY), (0.4, quat((1, 0, 0), -55)), (0.8, quat((1, 0, 0), -55)),
                         (1.0, IDENTITY), (1.2, IDENTITY)]),
        (3, 'rotation', [(0.0, IDENTITY), (0.2, quat((0, 0, 1), 30)), (1.0, quat((0, 0, 1), 30)), (1.2, IDENTITY)]),
    ],
}


class Buffer:
    """One binary buffer, its views and accessors, four-byte aligned."""

    def __init__(self):
        self.data = bytearray()
        self.views = []
        self.accessors = []

    def add(self, raw, component, count, kind, target=None, bounds=None):
        while len(self.data) % 4:
            self.data.append(0)
        view = {'buffer': 0, 'byteOffset': len(self.data), 'byteLength': len(raw)}
        if target:
            view['target'] = target
        self.views.append(view)
        self.data += raw
        accessor = {'bufferView': len(self.views) - 1, 'componentType': component, 'count': count, 'type': kind}
        if bounds:
            accessor['min'], accessor['max'] = bounds
        self.accessors.append(accessor)
        return len(self.accessors) - 1


def write_glb(path, gltf, binary, name):
    gltf['buffers'] = [{'byteLength': len(binary)}]
    json_bytes = json.dumps(gltf, separators=(',', ':')).encode('utf-8')
    json_bytes += b' ' * ((4 - len(json_bytes) % 4) % 4)
    binary = bytes(binary) + b'\x00' * ((4 - len(binary) % 4) % 4)
    glb = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(json_bytes) + 8 + len(binary))
    glb += struct.pack('<II', len(json_bytes), 0x4E4F534A) + json_bytes
    glb += struct.pack('<II', len(binary), 0x004E4942) + binary
    with open(path, 'wb') as f:
        f.write(glb)
    print('%-22s %s, %d bytes' % (os.path.basename(path), name, len(glb)))


def mesh_accessors(buf, positions, normals, indices):
    lo = [min(p[i] for p in positions) for i in range(3)]
    hi = [max(p[i] for p in positions) for i in range(3)]
    return {
        'indices': buf.add(b''.join(struct.pack('<H', i) for i in indices), 5123, len(indices), 'SCALAR', 34963),
        'POSITION': buf.add(b''.join(struct.pack('<fff', *p) for p in positions), 5126, len(positions), 'VEC3', 34962, (lo, hi)),
        'NORMAL': buf.add(b''.join(struct.pack('<fff', *n) for n in normals), 5126, len(normals), 'VEC3', 34962),
    }


def arms():
    mesh = ([], [], [], [])
    for joint, a, b, hw, hh in SEGMENTS:
        bone_box(mesh, joint, a, b, hw, hh)
    for joint, centre, half in HANDS:
        box(mesh, joint, centre, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), half)
    positions, normals, joints, indices = mesh

    buf = Buffer()
    acc = mesh_accessors(buf, positions, normals, indices)
    acc['JOINTS_0'] = buf.add(b''.join(struct.pack('<BBBB', j, 0, 0, 0) for j in joints), 5121, len(joints), 'VEC4', 34962)
    acc['WEIGHTS_0'] = buf.add(b''.join(struct.pack('<ffff', 1, 0, 0, 0) for _ in joints), 5126, len(joints), 'VEC4', 34962)
    # Inverse bind matrices (column-major): each joint's rest pose is a translation, so its inverse is the negation.
    ibm = b''.join(struct.pack('<16f', 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -p[0], -p[1], -p[2], 1) for _, _, p in JOINTS)
    inverse_bind = buf.add(ibm, 5126, len(JOINTS), 'MAT4')

    nodes = []
    for i, (name, parent, position) in enumerate(JOINTS):
        local = position if parent < 0 else sub(position, JOINTS[parent][2])
        node = {'name': name, 'translation': list(local)}
        children = [c for c, (_, p, _) in enumerate(JOINTS) if p == i]
        if children:
            node['children'] = children
        nodes.append(node)
    nodes.append({'name': 'arms', 'mesh': 0, 'skin': 0})

    animations = []
    for clip, channels in CLIPS.items():
        samplers, targets = [], []
        for joint, path, keys in channels:
            times = [t for t, _ in keys]
            inp = buf.add(b''.join(struct.pack('<f', t) for t in times), 5126, len(times), 'SCALAR', None, ([min(times)], [max(times)]))
            width = 4 if path == 'rotation' else 3
            out = buf.add(b''.join(struct.pack('<%df' % width, *v) for _, v in keys), 5126, len(keys), 'VEC4' if width == 4 else 'VEC3')
            samplers.append({'input': inp, 'output': out, 'interpolation': 'LINEAR'})
            targets.append({'sampler': len(samplers) - 1, 'target': {'node': joint, 'path': path}})
        animations.append({'name': clip, 'samplers': samplers, 'channels': targets})

    gltf = {
        'asset': {'version': '2.0', 'generator': 'sage-engine make_viewmodel_arms.py'},
        'scene': 0,
        'scenes': [{'nodes': [0, len(JOINTS)]}],
        'nodes': nodes,
        'meshes': [{'name': 'arms', 'primitives': [{'attributes': {k: v for k, v in acc.items() if k != 'indices'},
                                                     'indices': acc['indices'], 'mode': 4}]}],
        'skins': [{'name': 'arms', 'joints': list(range(len(JOINTS))), 'inverseBindMatrices': inverse_bind, 'skeleton': 0}],
        'animations': animations,
        'bufferViews': buf.views,
        'accessors': buf.accessors,
    }
    write_glb(os.path.join(OUT, 'arms.glb'), gltf, buf.data,
              '%d joints, %d triangles, clips %s' % (len(JOINTS), len(indices) // 3, ', '.join(CLIPS)))


def sword():
    mesh = ([], [], [], [])
    axes = ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    box(mesh, 0, (0, 0.02, 0), axes, (0.018, 0.07, 0.018))      # grip
    box(mesh, 0, (0, 0.10, 0), axes, (0.09, 0.012, 0.02))       # guard
    box(mesh, 0, (0, 0.46, 0), axes, (0.028, 0.35, 0.006))      # blade
    positions, normals, _, indices = mesh
    buf = Buffer()
    acc = mesh_accessors(buf, positions, normals, indices)
    gltf = {
        'asset': {'version': '2.0', 'generator': 'sage-engine make_viewmodel_arms.py'},
        'scene': 0,
        'scenes': [{'nodes': [0]}],
        'nodes': [{'mesh': 0, 'name': 'sword'}],
        'meshes': [{'name': 'sword', 'primitives': [{'attributes': {k: v for k, v in acc.items() if k != 'indices'},
                                                      'indices': acc['indices'], 'mode': 4}]}],
        'bufferViews': buf.views,
        'accessors': buf.accessors,
    }
    write_glb(os.path.join(OUT, 'viewmodel_sword.glb'), gltf, buf.data, '%d triangles' % (len(indices) // 3))


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    arms()
    sword()


if __name__ == '__main__':
    main()
