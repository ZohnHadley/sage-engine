"""Generates the Sandbox's placeholder sprite art (F1 test content).

Run it from anywhere with `python games/Sandbox/tools/make_placeholder_art.py`; it overwrites the two
PNGs beside it in ../content/textures. The art is deliberately crude, but it encodes the engine's
direction convention (docs/design/06 3.8), which is exactly what a placeholder is for:

creature.png: 8 view directions x 2 animation frames, 64x96 each (512x192).
  Direction d is the view from d*45 degrees around the creature, measured from its front toward its
  LEFT -- Doom's rotation order, where rotation 2 shows a thing "facing diagonally to the left of the
  player". Each cell draws the body, a "nose" whose horizontal offset follows the view angle, a pack
  when seen from behind, and d small bars at the feet, so a screenshot says which group was picked.
tree.png: one 96x128 frame, trunk + canopy.
sword.png: one 48x64 frame, the item F19 leaves lying in the grass.
hands.png / sword_fp.png: 3 frames of 128x128, the first-person view of your own hands (13 3).
"""
import math, zlib, struct, os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'content', 'textures')


class Canvas:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.px = bytearray(w * h * 4)          # transparent

    def set(self, x, y, rgba):
        if 0 <= x < self.w and 0 <= y < self.h:
            i = (y * self.w + x) * 4
            self.px[i:i + 4] = bytes(rgba)

    def rect(self, x0, y0, w, h, rgba):
        for y in range(int(y0), int(y0 + h)):
            for x in range(int(x0), int(x0 + w)):
                self.set(x, y, rgba)

    def ellipse(self, cx, cy, rx, ry, rgba):
        for y in range(int(cy - ry), int(cy + ry) + 1):
            for x in range(int(cx - rx), int(cx + rx) + 1):
                dx, dy = (x - cx) / rx, (y - cy) / ry
                if dx * dx + dy * dy <= 1.0:
                    self.set(x, y, rgba)

    def write(self, path):
        raw = b''.join(b'\x00' + bytes(self.px[y * self.w * 4:(y + 1) * self.w * 4]) for y in range(self.h))
        def chunk(t, d):
            return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
        png = (b'\x89PNG\r\n\x1a\n'
               + chunk(b'IHDR', struct.pack('>IIBBBBB', self.w, self.h, 8, 6, 0, 0, 0))
               + chunk(b'IDAT', zlib.compress(raw, 9))
               + chunk(b'IEND', b''))
        open(path, 'wb').write(png)


BODY = (92, 108, 132, 255)
BODY_DARK = (64, 76, 96, 255)
HEAD = (196, 170, 140, 255)
NOSE = (230, 90, 70, 255)
PACK = (120, 82, 48, 255)
MARK = (250, 230, 60, 255)
SKIN = (214, 176, 140, 255)
SKIN_DARK = (176, 138, 104, 255)
SLEEVE = (96, 104, 140, 255)
STEEL = (168, 172, 180, 255)
STEEL_LIGHT = (214, 218, 226, 255)
GUARD = (122, 96, 48, 255)
GRIP = (78, 54, 36, 255)
TRUNK = (96, 68, 44, 255)
LEAF = (64, 122, 58, 255)
LEAF_DARK = (44, 92, 44, 255)


def creature(path, fw=64, fh=96, directions=8, frames=2):
    c = Canvas(fw * directions, fh * frames)
    for f in range(frames):
        for d in range(directions):
            ox, oy = d * fw, f * fh
            bob = -2 if f == 1 else 0          # a small idle bob, so animation is visible
            angle = d * math.tau / directions  # 0 = seen from the front, 4 = from behind
            cx = ox + fw // 2

            # legs, body, head
            c.rect(cx - 10, oy + 74 + bob, 7, 18, BODY_DARK)
            c.rect(cx + 3, oy + 74 + bob, 7, 18, BODY_DARK)
            c.ellipse(cx, oy + 56 + bob, 16, 22, BODY)
            c.ellipse(cx, oy + 28 + bob, 12, 13, HEAD)

            # A nose that swings with the view angle, and a pack when seen from behind.
            #
            # Direction d is the view from `angle` around the creature, measured from its front toward
            # its LEFT (Doom's rotation order: rotation 2 shows a thing "facing diagonally to the left
            # of the player"). Project its facing onto the screen's right axis and you get -sin(angle),
            # so the nose swings the opposite way to the pack on its back, which gets +sin(angle).
            if math.cos(angle) > -0.25:
                nx = cx - int(math.sin(angle) * 11)
                c.ellipse(nx, oy + 30 + bob, 3, 3, NOSE)
            if math.cos(angle) < 0.25:
                c.rect(cx - 9 + int(math.sin(angle) * 6), oy + 44 + bob, 18, 20, PACK)

            # direction index as bars at the feet
            for b in range(d):
                c.rect(ox + 4 + b * 7, oy + fh - 5, 5, 3, MARK)
    c.write(path)


def sword(path, w=48, h=64):
    """A sword lying in the grass: the placeholder item for F19 (docs/design/16 3.2)."""
    c = Canvas(w, h)
    cx = w // 2
    c.rect(cx - 2, 6, 4, 40, STEEL)            # blade
    c.rect(cx - 1, 6, 1, 40, STEEL_LIGHT)      # a highlight down one edge
    c.rect(cx - 8, 46, 16, 3, GUARD)           # crossguard
    c.rect(cx - 2, 49, 4, 10, GRIP)            # grip
    c.rect(cx - 3, 59, 6, 3, GUARD)            # pommel
    c.set(cx, 4, STEEL_LIGHT)                  # tip
    c.write(path)


def viewmodel(path, w=128, h=128, frames=3, weapon=None):
    """A first-person view of your own hands (docs/design/13 3): three frames of a swing.

    Frame 0 is at rest, 1 is drawn back, 2 is the strike. The art fills its frame: a viewmodel is
    scaled against the window height, so empty space around it is wasted screen.
    """
    c = Canvas(w * frames, h)
    # Fist centre and how far the blade leans, per frame.
    poses = ((70, 74, 0.0), (86, 52, -0.35), (44, 44, 0.55))
    for f in range(frames):
        ox = f * w
        fx, fy, lean = poses[f]

        if weapon:
            # A blade from the guard up and away, leaning with the swing.
            tip = (fx + lean * 70 - 18, fy - 92)
            for i in range(90):
                t = i / 89.0
                bx = int(fx + (tip[0] - fx) * t)
                by = int(fy - 14 - (fy - 14 - tip[1]) * t)
                c.rect(ox + bx - 5, by, 9, 2, STEEL)
                c.rect(ox + bx - 5, by, 3, 2, STEEL_LIGHT)
            c.rect(ox + fx - 20, fy - 20, 40, 7, GUARD)
            c.rect(ox + fx - 5, fy - 14, 10, 16, GRIP)

        # Forearm to the bottom edge, then the fist over it.
        c.rect(ox + fx - 19, fy + 14, 38, h - (fy + 14), SLEEVE)
        c.rect(ox + fx - 19, fy + 14, 6, h - (fy + 14), BODY_DARK)
        c.ellipse(ox + fx, fy + 6, 26, 24, SKIN)
        c.ellipse(ox + fx - 8, fy - 2, 9, 8, SKIN_DARK)
        for k in range(3):
            c.rect(ox + fx - 16 + k * 12, fy + 2, 9, 5, SKIN_DARK)
    c.write(path)


def tree(path, w=96, h=128):
    c = Canvas(w, h)
    c.rect(w // 2 - 6, h - 42, 12, 42, TRUNK)
    for i, (cy, r) in enumerate(((h - 96, 34), (h - 70, 28), (h - 48, 20))):
        c.ellipse(w // 2, cy, r, r * 0.8, LEAF if i % 2 == 0 else LEAF_DARK)
    c.write(path)


def fireball(path, w=32, h=32, frames=2):
    """A little burning ball, two frames so it flickers on its way (F21)."""
    c = Canvas(w * frames, h)
    for f in range(frames):
        ox = f * w
        r = 11 if f == 0 else 13
        c.ellipse(ox + w // 2, h // 2, r, r, (255, 140, 20, 190))
        c.ellipse(ox + w // 2, h // 2, r - 4, r - 4, (255, 214, 10, 255))
        c.ellipse(ox + w // 2, h // 2, r - 8, r - 8, (255, 255, 235, 255))
    c.write(path)


os.makedirs(OUT, exist_ok=True)
creature(os.path.join(OUT, 'creature.png'))
fireball(os.path.join(OUT, 'fireball.png'))
tree(os.path.join(OUT, 'tree.png'))
sword(os.path.join(OUT, 'sword.png'))
viewmodel(os.path.join(OUT, 'hands.png'))
viewmodel(os.path.join(OUT, 'sword_fp.png'), weapon=True)
print('wrote creature.png, tree.png, sword.png, fireball.png, hands.png and sword_fp.png')
