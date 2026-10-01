# -*- coding: utf-8 -*-
"""A box mannequin, first-person arms and a sword (engine issues #121 and #122: phase 4d's exit).

The same bargain as the bunny (make_placeholder_model.py): no art the repository does not own, so every
model here is *generated* — boxes, each skinned wholly to the joint it hangs from, with keyed clips,
written straight out as `.glb`. Crude on purpose, but a rig and clips a real character can replace
joint for joint and clip for clip.

**mannequin.glb** (tests/games/skeletal/content/models, tests/games/weapons/content/models): an 18-joint humanoid in the engine's model
space — +Y up, facing -Z, its right hand on +X, feet on y = 0 — every rest rotation identity:

  root
  └ pelvis (0, 0.95, 0)
    ├ spine (0, 1.05) └ chest (0, 1.30) ├ neck (0, 1.52) └ head (0, 1.60)
    │                                   ├ upper_arm_l (-0.21, 1.45) └ forearm_l (-0.23, 1.16) └ hand_l (-0.24, 0.90)
    │                                   └ upper_arm_r ( 0.21, 1.45) └ forearm_r ( 0.23, 1.16) └ hand_r ( 0.24, 0.90)
    ├ thigh_l (-0.10, 0.92) └ shin_l (-0.10, 0.50) └ foot_l (-0.10, 0.08)
    └ thigh_r ( 0.10, 0.92) └ shin_r ( 0.10, 0.50) └ foot_r ( 0.10, 0.08)

  The ankles are 0.08 m up (foot_ik's `footHeight`), so stood on the ground its soles touch it. A visor
  on the head's front shows which way it faces.

  clips  idle      2 s, loops   breathes: the chest lifts, the head nods, the arms hang a little out; the feet stay put
         walk      1 s, loops   a stride each second: legs ±25°, the knee bending through the swing, arms
                                swinging against the legs; the hips dip just enough to keep the stance
                                foot on the ground (`planted`), so foot IK has nothing to do on the flat
         run       0.7 s, loops legs 10 ± 40°, knees to 100°, elbows bent at 75°, a forward lean, planted too
                                (walk and run start on the same foot, so a phase-synced blend keeps them together)
         aim       1 s, loops   both arms forward, level with the chest: the right one holding, the left supporting
         aim_up    1 s, loops   the same, the arms 30° higher      (an aim layer blends the three over
         aim_down  1 s, loops   the same, the arms 30° lower        `aim_pitch`: -60, 0, 60 degrees)
         attack    0.8 s        from aim: arms up and back (0.25 s), a downward strike (0.4 s: `hit`),
                                back to aim (0.8 s)
         reload    1.4 s        from aim: the weapon tilts down, the left hand drops to the belt (0.3 s:
                                `mag_out`), comes back up under it (0.9 s: `mag_in`), back to aim

  Aim IK's split (the prefab's `aim_ik`): the chest ends up turned by half the pitch and the head by all
  of it, so the aim_up/aim_down clips add the other half (30° at 60°) to the arms, which hang from the
  chest. With both, arms and head point where the NPC aims.

**arms.glb** (games/Sandbox/content/models): the first-person arms — sleeves, forearms and hands curled
round a grip — in **view space**, the camera's own: the eye at the origin, x right, y up, looking down
-Z. The Sandbox's `viewmodel` record puts them there with no offset.

  joints   root → shoulder_r → elbow_r → hand_r, root → shoulder_l → elbow_l → hand_l
  clips    idle    (2 s, loops)  the root breathes and sways: 8 mm down and back, 4 mm across
           attack  (0.5 s)       the right arm draws back (0.12 s), swings down and across (0.28 s: `hit`)
                                 and recovers
           reload  (1.2 s)       the right hand tilts the weapon over; the left hand drops away and down
                                 (0.2–0.35 s: `mag_out` at 0.3), holds, comes back up (0.8–0.95 s: `mag_in`
                                 at 0.9) and pushes, then both settle

**viewmodel_sword.glb** (games/Sandbox/content/models): a rigid blade along +Y from a grip at the
origin; the `hand_r` socket (content/data/viewmodel.json) tilts it forward.

**slab.glb** (tests/games/skeletal/content/models, tests/games/weapons/content/models): the skeletal test game's floor, a 40 x 1 x 40 m box
centred on its origin, like the `body` it is drawn for.

Clip events are not in glTF: they are `anim_events` records beside the graphs (engine issue #119).

Run:  python games/Sandbox/tools/make_mannequin.py
"""
import json
import math
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
SANDBOX_MODELS = os.path.join(HERE, '..', 'content', 'models')
SKELETAL_MODELS = os.path.join(HERE, '..', '..', '..', 'tests', 'games', 'skeletal', 'content', 'models')
WEAPONS_MODELS = os.path.join(HERE, '..', '..', '..', 'tests', 'games', 'weapons', 'content', 'models')
GENERATOR = 'sage-engine make_mannequin.py'

# Per face: the normal, and the four corners as (x, y, z) sign multipliers, counter-clockwise from outside.
FACES = [
    ((0, 0, 1), [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]),
    ((0, 0, -1), [(1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1)]),
    ((1, 0, 0), [(1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)]),
    ((-1, 0, 0), [(-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)]),
    ((0, 1, 0), [(-1, 1, 1), (1, 1, 1), (1, 1, -1), (-1, 1, -1)]),
    ((0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
]
AXES = ((1, 0, 0), (0, 1, 0), (0, 0, 1))


def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def scale(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    length = math.sqrt(dot(a, a))
    return scale(a, 1.0 / length)


# ---- geometry ------------------------------------------------------------------------------------------

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


def bone_box(mesh, joint, a, b, hw, hh, overlap=0.02):
    """A box from `a` to `b` (a bone), `hw` by `hh` across, `overlap` longer than the bone at both ends."""
    z = norm(sub(b, a))
    x = cross((0.0, 1.0, 0.0), z)
    x = norm(x) if dot(x, x) > 1e-8 else (1.0, 0.0, 0.0)
    y = cross(z, x)
    length = math.sqrt(dot(sub(b, a), sub(b, a)))
    box(mesh, joint, scale(add(a, b), 0.5), (x, y, z), (hw, hh, length * 0.5 + overlap))


# ---- rotations -----------------------------------------------------------------------------------------

def quat(axis, degrees):
    half = math.radians(degrees) * 0.5
    s = math.sin(half)
    a = norm(axis)
    return (a[0] * s, a[1] * s, a[2] * s, math.cos(half))


def qmul(a, b):
    """a * b: b first, then a."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def rx(d): return quat((1, 0, 0), d)   # +: a limb hanging down swings forward (-Z); a spine tips back
def ry(d): return quat((0, 1, 0), d)   # +: turns left (from -Z towards -X)
def rz(d): return quat((0, 0, 1), d)   # +: a limb hanging down swings out to +X


def euler(x=0.0, y=0.0, z=0.0):
    """Pitch about X first, then roll about Z, then yaw about Y (each in the parent's frame)."""
    return qmul(ry(y), qmul(rz(z), rx(x)))


IDENTITY = (0.0, 0.0, 0.0, 1.0)


# ---- the .glb writer -----------------------------------------------------------------------------------

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
    folder = os.path.dirname(path)
    if not os.path.isdir(folder):
        os.makedirs(folder)
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


def write_rigid(path, name, mesh):
    positions, normals, _, indices = mesh
    buf = Buffer()
    acc = mesh_accessors(buf, positions, normals, indices)
    gltf = {
        'asset': {'version': '2.0', 'generator': GENERATOR},
        'scene': 0,
        'scenes': [{'nodes': [0]}],
        'nodes': [{'mesh': 0, 'name': name}],
        'meshes': [{'name': name, 'primitives': [{'attributes': {k: v for k, v in acc.items() if k != 'indices'},
                                                   'indices': acc['indices'], 'mode': 4}]}],
        'bufferViews': buf.views,
        'accessors': buf.accessors,
    }
    write_glb(path, gltf, buf.data, '%d triangles' % (len(indices) // 3))


def write_skinned(path, name, joints, mesh, clips):
    """`joints`: (name, parent index, rest position in model space), parents first, rest rotation identity.
    `clips`: name → list of (joint, path, [(time, value)]), LINEAR keys."""
    positions, normals, skin_joints, indices = mesh
    buf = Buffer()
    acc = mesh_accessors(buf, positions, normals, indices)
    acc['JOINTS_0'] = buf.add(b''.join(struct.pack('<BBBB', j, 0, 0, 0) for j in skin_joints), 5121, len(skin_joints), 'VEC4', 34962)
    acc['WEIGHTS_0'] = buf.add(b''.join(struct.pack('<ffff', 1, 0, 0, 0) for _ in skin_joints), 5126, len(skin_joints), 'VEC4', 34962)
    # Inverse bind matrices (column-major): each joint's rest pose is a translation, so its inverse is the negation.
    ibm = b''.join(struct.pack('<16f', 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -p[0], -p[1], -p[2], 1) for _, _, p in joints)
    inverse_bind = buf.add(ibm, 5126, len(joints), 'MAT4')

    nodes = []
    for i, (joint_name, parent, position) in enumerate(joints):
        local = position if parent < 0 else sub(position, joints[parent][2])
        node = {'name': joint_name, 'translation': list(local)}
        children = [c for c, (_, p, _) in enumerate(joints) if p == i]
        if children:
            node['children'] = children
        nodes.append(node)
    nodes.append({'name': name, 'mesh': 0, 'skin': 0})

    animations = []
    for clip, channels in clips.items():
        samplers, targets = [], []
        for joint, target_path, keys in channels:
            times = [t for t, _ in keys]
            inp = buf.add(b''.join(struct.pack('<f', t) for t in times), 5126, len(times), 'SCALAR', None, ([min(times)], [max(times)]))
            width = 4 if target_path == 'rotation' else 3
            out = buf.add(b''.join(struct.pack('<%df' % width, *v) for _, v in keys), 5126, len(keys), 'VEC4' if width == 4 else 'VEC3')
            samplers.append({'input': inp, 'output': out, 'interpolation': 'LINEAR'})
            targets.append({'sampler': len(samplers) - 1, 'target': {'node': joint, 'path': target_path}})
        animations.append({'name': clip, 'samplers': samplers, 'channels': targets})

    gltf = {
        'asset': {'version': '2.0', 'generator': GENERATOR},
        'scene': 0,
        'scenes': [{'nodes': [0, len(joints)]}],
        'nodes': nodes,
        'meshes': [{'name': name, 'primitives': [{'attributes': {k: v for k, v in acc.items() if k != 'indices'},
                                                   'indices': acc['indices'], 'mode': 4}]}],
        'skins': [{'name': name, 'joints': list(range(len(joints))), 'inverseBindMatrices': inverse_bind, 'skeleton': 0}],
        'animations': animations,
        'bufferViews': buf.views,
        'accessors': buf.accessors,
    }
    write_glb(path, gltf, buf.data, '%d joints, %d triangles, clips %s' % (len(joints), len(indices) // 3, ', '.join(clips)))


# ---- the mannequin -------------------------------------------------------------------------------------

MANNEQUIN = [
    ('root', -1, (0.0, 0.0, 0.0)),
    ('pelvis', 0, (0.0, 0.95, 0.0)),
    ('spine', 1, (0.0, 1.05, 0.0)),
    ('chest', 2, (0.0, 1.30, 0.0)),
    ('neck', 3, (0.0, 1.52, 0.0)),
    ('head', 4, (0.0, 1.60, 0.0)),
    ('upper_arm_l', 3, (-0.21, 1.45, 0.0)),
    ('forearm_l', 6, (-0.23, 1.16, 0.0)),
    ('hand_l', 7, (-0.24, 0.90, 0.0)),
    ('upper_arm_r', 3, (0.21, 1.45, 0.0)),
    ('forearm_r', 9, (0.23, 1.16, 0.0)),
    ('hand_r', 10, (0.24, 0.90, 0.0)),
    ('thigh_l', 1, (-0.10, 0.92, 0.0)),
    ('shin_l', 12, (-0.10, 0.50, 0.0)),
    ('foot_l', 13, (-0.10, 0.08, 0.0)),
    ('thigh_r', 1, (0.10, 0.92, 0.0)),
    ('shin_r', 15, (0.10, 0.50, 0.0)),
    ('foot_r', 16, (0.10, 0.08, 0.0)),
]
J = {name: i for i, (name, _, _) in enumerate(MANNEQUIN)}
P = {name: position for name, _, position in MANNEQUIN}
TWO_PI = 2 * math.pi


def mannequin_mesh():
    mesh = ([], [], [], [])
    box(mesh, J['pelvis'], (0, 0.95, 0), AXES, (0.16, 0.08, 0.10))
    bone_box(mesh, J['spine'], (0, 1.02, 0), (0, 1.28, 0), 0.14, 0.09, 0.0)
    box(mesh, J['chest'], (0, 1.40, 0), AXES, (0.19, 0.13, 0.11))
    box(mesh, J['neck'], (0, 1.56, 0), AXES, (0.05, 0.05, 0.05))
    box(mesh, J['head'], (0, 1.72, 0), AXES, (0.10, 0.12, 0.11))
    box(mesh, J['head'], (0, 1.75, -0.115), AXES, (0.075, 0.025, 0.015))      # the visor: its front
    for side in ('l', 'r'):
        bone_box(mesh, J['upper_arm_' + side], P['upper_arm_' + side], P['forearm_' + side], 0.055, 0.055)
        bone_box(mesh, J['forearm_' + side], P['forearm_' + side], P['hand_' + side], 0.045, 0.045)
        wrist = P['hand_' + side]
        box(mesh, J['hand_' + side], (wrist[0], wrist[1] - 0.07, wrist[2]), AXES, (0.03, 0.07, 0.045))
        bone_box(mesh, J['thigh_' + side], P['thigh_' + side], P['shin_' + side], 0.075, 0.075)
        bone_box(mesh, J['shin_' + side], P['shin_' + side], P['foot_' + side], 0.06, 0.06)
        ankle = P['foot_' + side]
        box(mesh, J['foot_' + side], (ankle[0], 0.04, -0.05), AXES, (0.05, 0.04, 0.12))
    return mesh


def cycle(duration, steps, fn):
    """Keys for a looping clip: fn(phase 0..1) at `steps` even steps, the last key equal to the first."""
    keys = [(duration * i / steps, fn(i / float(steps))) for i in range(steps)]
    keys.append((duration, fn(0.0)))
    return keys


def held(duration, value):
    return [(0.0, value), (duration, value)]


def swing(phase):
    """How far through its swing a leg is: 0 in stance (phase 0..0.5), up to 1 at mid-swing (0.75)."""
    return max(0.0, math.sin(TWO_PI * (phase - 0.5)))


def pelvis_at(dy):
    rest = P['pelvis']
    return (rest[0], rest[1] + dy, rest[2])


THIGH = P['thigh_l'][1] - P['shin_l'][1]     # 0.42 m
SHIN = P['shin_l'][1] - P['foot_l'][1]       # 0.42 m


def planted(thigh_l, shin_l, thigh_r, shin_r):
    """The pelvis's rise (negative: a dip) at a phase that puts the lower ankle where it rests: each leg's
    reach below the hip is thigh·cos(a) + shin·cos(a + b), for its thigh's and shin's pitches a and b.
    Keyed with the legs, it keeps the stance foot on flat ground, so foot IK has nothing to correct there."""
    def reach(a, b):
        return THIGH * math.cos(math.radians(a)) + SHIN * math.cos(math.radians(a + b))
    return max(reach(thigh_l, shin_l), reach(thigh_r, shin_r)) - (THIGH + SHIN)


def aim_arms(extra):
    """The aiming arms at `extra` degrees of pitch over level: the right one holding, the left supporting."""
    return {
        'upper_arm_r': euler(x=85 + extra, y=18),
        'forearm_r': rx(12),
        'hand_r': IDENTITY,
        'upper_arm_l': euler(x=70 + extra, y=-38),
        'forearm_l': rx(34),
        'hand_l': IDENTITY,
    }


def aim_clip(extra):
    return [(J[name], 'rotation', held(1.0, q)) for name, q in aim_arms(extra).items()]


def mannequin_clips():
    clips = {}

    clips['idle'] = [
        (J['chest'], 'rotation', [(0.0, IDENTITY), (1.0, rx(-2)), (2.0, IDENTITY)]),
        (J['head'], 'rotation', [(0.0, IDENTITY), (1.0, rx(2)), (2.0, IDENTITY)]),
        (J['upper_arm_l'], 'rotation', [(0.0, rz(-5)), (1.0, rz(-7)), (2.0, rz(-5))]),
        (J['upper_arm_r'], 'rotation', [(0.0, rz(5)), (1.0, rz(7)), (2.0, rz(5))]),
        (J['forearm_l'], 'rotation', held(2.0, rx(6))),
        (J['forearm_r'], 'rotation', held(2.0, rx(6))),
    ]

    # Walk: phase 0 is the left foot forward at heel strike; the right leg is half a cycle behind.
    def walk_thigh(p): return 25 * math.cos(TWO_PI * p)
    def walk_shin(p): return -5 - 45 * swing(p % 1.0)
    def walk_foot(offset): return lambda p: rx(20 * swing((p + offset) % 1.0) - 10 * math.cos(TWO_PI * (p + offset)))
    def walk_pelvis(p): return pelvis_at(planted(walk_thigh(p), walk_shin(p), walk_thigh(p + 0.5), walk_shin(p + 0.5)))
    clips['walk'] = [
        (J['pelvis'], 'translation', cycle(1.0, 16, walk_pelvis)),
        (J['pelvis'], 'rotation', cycle(1.0, 16, lambda p: ry(-6 * math.cos(TWO_PI * p)))),
        (J['chest'], 'rotation', cycle(1.0, 16, lambda p: ry(9 * math.cos(TWO_PI * p)))),
        (J['thigh_l'], 'rotation', cycle(1.0, 16, lambda p: rx(walk_thigh(p)))),
        (J['shin_l'], 'rotation', cycle(1.0, 16, lambda p: rx(walk_shin(p)))),
        (J['foot_l'], 'rotation', cycle(1.0, 16, walk_foot(0.0))),
        (J['thigh_r'], 'rotation', cycle(1.0, 16, lambda p: rx(walk_thigh(p + 0.5)))),
        (J['shin_r'], 'rotation', cycle(1.0, 16, lambda p: rx(walk_shin(p + 0.5)))),
        (J['foot_r'], 'rotation', cycle(1.0, 16, walk_foot(0.5))),
        (J['upper_arm_l'], 'rotation', cycle(1.0, 16, lambda p: euler(x=-20 * math.cos(TWO_PI * p), z=-5))),
        (J['upper_arm_r'], 'rotation', cycle(1.0, 16, lambda p: euler(x=20 * math.cos(TWO_PI * p), z=5))),
        (J['forearm_l'], 'rotation', cycle(1.0, 16, lambda p: rx(15 + 10 * max(0.0, -math.cos(TWO_PI * p))))),
        (J['forearm_r'], 'rotation', cycle(1.0, 16, lambda p: rx(15 + 10 * max(0.0, math.cos(TWO_PI * p))))),
    ]

    # Run: the same foot first, bigger and quicker, leaning in.
    run = 0.7
    def run_thigh(p): return 10 + 40 * math.cos(TWO_PI * p)
    def run_shin(p): return -15 - 85 * swing(p % 1.0)
    def run_foot(offset): return lambda p: rx(30 * swing((p + offset) % 1.0))
    def run_pelvis(p): return pelvis_at(planted(run_thigh(p), run_shin(p), run_thigh(p + 0.5), run_shin(p + 0.5)))
    clips['run'] = [
        (J['pelvis'], 'translation', cycle(run, 16, run_pelvis)),
        (J['pelvis'], 'rotation', cycle(run, 16, lambda p: ry(-8 * math.cos(TWO_PI * p)))),
        (J['spine'], 'rotation', held(run, rx(-12))),
        (J['chest'], 'rotation', cycle(run, 16, lambda p: ry(12 * math.cos(TWO_PI * p)))),
        (J['head'], 'rotation', held(run, rx(10))),
        (J['thigh_l'], 'rotation', cycle(run, 16, lambda p: rx(run_thigh(p)))),
        (J['shin_l'], 'rotation', cycle(run, 16, lambda p: rx(run_shin(p)))),
        (J['foot_l'], 'rotation', cycle(run, 16, run_foot(0.0))),
        (J['thigh_r'], 'rotation', cycle(run, 16, lambda p: rx(run_thigh(p + 0.5)))),
        (J['shin_r'], 'rotation', cycle(run, 16, lambda p: rx(run_shin(p + 0.5)))),
        (J['foot_r'], 'rotation', cycle(run, 16, run_foot(0.5))),
        (J['upper_arm_l'], 'rotation', cycle(run, 16, lambda p: euler(x=10 - 40 * math.cos(TWO_PI * p), z=-8))),
        (J['upper_arm_r'], 'rotation', cycle(run, 16, lambda p: euler(x=10 + 40 * math.cos(TWO_PI * p), z=8))),
        (J['forearm_l'], 'rotation', held(run, rx(75))),
        (J['forearm_r'], 'rotation', held(run, rx(75))),
    ]

    clips['aim'] = aim_clip(0)
    clips['aim_up'] = aim_clip(30)
    clips['aim_down'] = aim_clip(-30)

    # Attack: from aim, up and back, a strike down (hit at 0.4 s), back to aim.
    aim = aim_arms(0)
    clips['attack'] = [
        (J['upper_arm_r'], 'rotation', [(0.0, aim['upper_arm_r']), (0.25, euler(x=165, y=10)), (0.4, euler(x=45, y=25)),
                                         (0.55, euler(x=40, y=25)), (0.8, aim['upper_arm_r'])]),
        (J['forearm_r'], 'rotation', [(0.0, aim['forearm_r']), (0.25, rx(60)), (0.4, rx(5)), (0.8, aim['forearm_r'])]),
        (J['upper_arm_l'], 'rotation', [(0.0, aim['upper_arm_l']), (0.25, euler(x=150, y=-25)), (0.4, euler(x=40, y=-35)),
                                         (0.55, euler(x=38, y=-35)), (0.8, aim['upper_arm_l'])]),
        (J['forearm_l'], 'rotation', [(0.0, aim['forearm_l']), (0.25, rx(70)), (0.4, rx(15)), (0.8, aim['forearm_l'])]),
        (J['chest'], 'rotation', [(0.0, IDENTITY), (0.25, euler(x=12, y=15)), (0.4, euler(x=-22, y=-10)),
                                   (0.55, euler(x=-20, y=-8)), (0.8, IDENTITY)]),
        (J['spine'], 'rotation', [(0.0, IDENTITY), (0.25, rx(6)), (0.4, rx(-10)), (0.8, IDENTITY)]),
    ]

    # Reload: from aim, the weapon tilts down, the left hand goes to the belt (mag_out at 0.3 s) and back
    # up under it (mag_in at 0.9 s), with a push, then back to aim.
    clips['reload'] = [
        (J['upper_arm_r'], 'rotation', [(0.0, aim['upper_arm_r']), (0.2, euler(x=55, y=25, z=10)), (1.1, euler(x=55, y=25, z=10)),
                                         (1.4, aim['upper_arm_r'])]),
        (J['forearm_r'], 'rotation', [(0.0, aim['forearm_r']), (0.2, rx(45)), (1.1, rx(45)), (1.4, aim['forearm_r'])]),
        (J['hand_r'], 'rotation', [(0.0, IDENTITY), (0.2, rz(35)), (1.1, rz(35)), (1.4, IDENTITY)]),
        (J['upper_arm_l'], 'rotation', [(0.0, aim['upper_arm_l']), (0.3, euler(x=-10, z=-12)), (0.7, euler(x=-10, z=-12)),
                                         (0.9, euler(x=45, y=-30)), (1.0, euler(x=55, y=-30)), (1.1, euler(x=45, y=-30)),
                                         (1.4, aim['upper_arm_l'])]),
        (J['forearm_l'], 'rotation', [(0.0, aim['forearm_l']), (0.3, rx(20)), (0.7, rx(20)), (0.9, rx(60)), (1.4, aim['forearm_l'])]),
        (J['head'], 'rotation', [(0.0, IDENTITY), (0.2, rx(-25)), (1.1, rx(-25)), (1.4, IDENTITY)]),
    ]
    return clips


# ---- first-person arms ---------------------------------------------------------------------------------

# name, parent index, rest position in view space
ARMS = [
    ('root', -1, (0.0, 0.0, 0.0)),
    ('shoulder_r', 0, (0.26, -0.34, 0.08)),
    ('elbow_r', 1, (0.24, -0.30, -0.22)),
    ('hand_r', 2, (0.16, -0.18, -0.46)),
    ('shoulder_l', 0, (-0.26, -0.34, 0.08)),
    ('elbow_l', 4, (-0.22, -0.32, -0.20)),
    ('hand_l', 5, (-0.12, -0.20, -0.42)),
]


def arms_mesh():
    mesh = ([], [], [], [])
    for (s, e, h), sign in (((1, 2, 3), 1), ((4, 5, 6), -1)):
        shoulder, elbow, wrist = ARMS[s][2], ARMS[e][2], ARMS[h][2]
        bone_box(mesh, s, shoulder, elbow, 0.062, 0.062)                         # the sleeve
        bone_box(mesh, e, elbow, wrist, 0.047, 0.043)                            # the forearm
        cuff = add(elbow, scale(sub(wrist, elbow), 0.3))
        bone_box(mesh, e, elbow, cuff, 0.055, 0.052, 0.0)                        # the sleeve's cuff over it
        # The hand: a palm, fingers curled round a grip (a block in front of the palm), a thumb on top.
        box(mesh, h, add(wrist, (0.0, 0.0, -0.045)), AXES, (0.042, 0.028, 0.05))
        box(mesh, h, add(wrist, (0.0, -0.02, -0.075)), AXES, (0.04, 0.03, 0.022))
        box(mesh, h, add(wrist, (-0.035 * sign, 0.02, -0.06)), AXES, (0.012, 0.012, 0.035))
    return mesh


def arms_clips():
    return {
        'idle': [
            (0, 'translation', [(0.0, (0, 0, 0)), (0.5, (0.004, -0.004, 0.002)), (1.0, (0, -0.008, 0.004)),
                                (1.5, (-0.004, -0.004, 0.002)), (2.0, (0, 0, 0))]),
            (3, 'rotation', [(0.0, IDENTITY), (1.0, rx(-2)), (2.0, IDENTITY)]),
        ],
        # The right arm draws back, swings down and across (hit at 0.28 s), and recovers.
        'attack': [
            (1, 'rotation', [(0.0, IDENTITY), (0.12, rx(35)), (0.28, qmul(ry(25), rx(-45))), (0.36, qmul(ry(22), rx(-40))),
                             (0.5, IDENTITY)]),
            (2, 'rotation', [(0.0, IDENTITY), (0.12, rx(25)), (0.28, rx(-10)), (0.5, IDENTITY)]),
            (3, 'rotation', [(0.0, IDENTITY), (0.12, rx(15)), (0.28, rx(-20)), (0.5, IDENTITY)]),
        ],
        # The right hand tilts the weapon over; the left drops away (mag_out at 0.3 s), holds, comes back up
        # (mag_in at 0.9 s) and pushes; both settle.
        'reload': [
            (4, 'rotation', [(0.0, IDENTITY), (0.2, rx(-20)), (0.35, qmul(rz(15), rx(-60))), (0.8, qmul(rz(15), rx(-60))),
                             (0.95, rx(8)), (1.02, rx(-4)), (1.2, IDENTITY)]),
            (5, 'rotation', [(0.0, IDENTITY), (0.35, rx(-25)), (0.8, rx(-25)), (0.95, rx(10)), (1.2, IDENTITY)]),
            (3, 'rotation', [(0.0, IDENTITY), (0.2, rz(30)), (0.9, qmul(rx(8), rz(30))), (1.0, rz(28)), (1.2, IDENTITY)]),
            (1, 'rotation', [(0.0, IDENTITY), (0.2, rx(8)), (0.9, rx(8)), (0.98, rx(12)), (1.2, IDENTITY)]),
        ],
    }


# ---- rigid pieces --------------------------------------------------------------------------------------

def sword_mesh():
    mesh = ([], [], [], [])
    box(mesh, 0, (0, 0.02, 0), AXES, (0.018, 0.07, 0.018))      # grip
    box(mesh, 0, (0, 0.10, 0), AXES, (0.09, 0.012, 0.02))       # guard
    box(mesh, 0, (0, 0.46, 0), AXES, (0.028, 0.35, 0.006))      # blade
    return mesh


def slab_mesh():
    mesh = ([], [], [], [])
    box(mesh, 0, (0, 0, 0), AXES, (20.0, 0.5, 20.0))
    return mesh


def mannequin_ragdoll():
    """The mannequin's `ragdoll` record (engine issue #243): eleven bodies sized from the boxes above. Limbs hang
    down their joint's -Y, so their twist axis is -Y; the knees bend back (about -X) and the elbows forward (+X).
    The spine joint has no body: the chest body, which covers spine and chest, hangs from the pelvis."""
    def capsule(radius, length, bone):
        """A capsule filling a bone of `bone` metres hanging down -Y: its cylinder is what the caps leave."""
        return {'shape': 'Capsule', 'size': [radius, round(bone - 2 * radius, 3), 0], 'offset': [0, round(-bone / 2, 3), 0]}

    def ball(swing, twist, axis=(0, -1, 0)):
        return {'kind': 'Ball', 'swing': swing, 'twistMin': -twist, 'twistMax': twist, 'axis': list(axis)}

    def hinge(axis, high=140):
        return {'kind': 'Hinge', 'axis': list(axis), 'min': 0, 'max': high}

    bodies = {
        'pelvis': {'shape': 'Box', 'size': [0.32, 0.16, 0.20], 'mass': 10},
        'chest': {'shape': 'Box', 'size': [0.38, 0.52, 0.22], 'offset': [0, -0.02, 0], 'mass': 20,
                  'joint': ball(30, 25, (0, 1, 0))},
        'head': {'shape': 'Sphere', 'size': [0.12, 0, 0], 'offset': [0, 0.12, 0], 'mass': 5, 'joint': ball(40, 30, (0, 1, 0))},
    }
    for side in ('l', 'r'):
        bodies['upper_arm_' + side] = dict(capsule(0.055, 0.29, 0.29), mass=2.5, joint=ball(80, 45))
        bodies['forearm_' + side] = dict(capsule(0.045, 0.26, 0.26), mass=1.5, joint=hinge((1, 0, 0)))
        bodies['thigh_' + side] = dict(capsule(0.075, 0.42, 0.42), mass=8, joint=ball(70, 30))
        bodies['shin_' + side] = dict(capsule(0.06, 0.42, 0.42), mass=4, joint=hinge((-1, 0, 0)))
    return [{
        'type': 'ragdoll', 'id': 'mannequin', 'model': 'models/mannequin.glb',
        'friction': 0.6, 'damping': 0.1, 'settleSpeed': 0.15, 'settleTime': 1.0,
        'bodies': bodies,
    }]


def write_json(path, value, comment):
    with open(path, 'w') as f:
        f.write('// ' + comment + '\n')
        json.dump(value, f, indent=2)
        f.write('\n')
    print('%-22s ragdoll record' % os.path.basename(path))


def main():
    write_skinned(os.path.join(SKELETAL_MODELS, 'mannequin.glb'), 'mannequin', MANNEQUIN, mannequin_mesh(), mannequin_clips())
    write_rigid(os.path.join(SKELETAL_MODELS, 'slab.glb'), 'slab', slab_mesh())
    write_json(os.path.join(SKELETAL_MODELS, '..', 'data', 'ragdoll.json'), mannequin_ragdoll(),
               'The mannequin\'s ragdoll (engine issue #243), generated by games/Sandbox/tools/make_mannequin.py: see docs/design/12.')
    write_skinned(os.path.join(WEAPONS_MODELS, 'mannequin.glb'), 'mannequin', MANNEQUIN, mannequin_mesh(), mannequin_clips())
    write_rigid(os.path.join(WEAPONS_MODELS, 'slab.glb'), 'slab', slab_mesh())
    write_skinned(os.path.join(SANDBOX_MODELS, 'arms.glb'), 'arms', ARMS, arms_mesh(), arms_clips())
    write_rigid(os.path.join(SANDBOX_MODELS, 'viewmodel_sword.glb'), 'sword', sword_mesh())


if __name__ == '__main__':
    main()
