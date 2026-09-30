# -*- coding: utf-8 -*-
"""A pistol, a crossbow and their ammunition (engine issue #138: phase 4e's kit and Sandbox).

The same bargain as the mannequin (make_mannequin.py, whose box and .glb writer this reuses): no art the
repository does not own, so every model here is *generated* from boxes and written straight out as `.glb`.
Crude on purpose; real art replaces them file for file.

Each weapon is modelled once, **held**: the grip's top at the origin (the `hand_r` joint of the arms, where
the sword's grip also sits), the barrel or stock along +Y and "up" along +Z. The arms' `grip_r` socket
(content/data/viewmodel.json) turns +Y forward and +Z up, so the held model points where the eye looks.
The same mesh, turned to lie on the ground (barrel along -Z, up +Y, resting on y = 0), is the pickup.

  viewmodel_pistol.glb    the pistol in hand: a grip with a magazine's base plate, a slide over it and a
                          trigger guard
  pistol.glb              the pistol on the ground
  viewmodel_crossbow.glb  the crossbow in hand: a stock, a grip under it, the prod across its nose, the
                          string drawn back to the latch and a bolt in the groove
  crossbow.glb            the crossbow on the ground
  bolt.glb                a crossbow bolt in flight, along -Z (the engine's forward): the projectile the
                          crossbow's attack throws, and the bolts lying on the ground
  pistol_rounds.glb       a box of pistol rounds on the ground

Run:  python games/Sandbox/tools/make_weapons.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_mannequin as m  # noqa: E402  (the box helpers and the .glb writer)

m.GENERATOR = 'sage-engine make_weapons.py'
AXES = m.AXES


def pistol_mesh():
    mesh = ([], [], [], [])
    m.box(mesh, 0, (0, -0.012, -0.05), AXES, (0.014, 0.022, 0.05))     # grip, down from the hand
    m.box(mesh, 0, (0, -0.012, -0.104), AXES, (0.016, 0.026, 0.006))   # the magazine's base plate
    m.box(mesh, 0, (0, 0.045, 0.024), AXES, (0.016, 0.105, 0.02))      # slide and barrel, forward over it
    m.box(mesh, 0, (0, 0.04, -0.012), AXES, (0.004, 0.028, 0.004))     # trigger guard, under the slide
    m.box(mesh, 0, (0, 0.14, 0.046), AXES, (0.003, 0.004, 0.003))      # front sight
    return mesh


def crossbow_mesh():
    mesh = ([], [], [], [])
    m.box(mesh, 0, (0, 0.04, 0.03), AXES, (0.022, 0.30, 0.02))         # the stock, forward from behind the hand
    m.box(mesh, 0, (0, -0.01, -0.04), AXES, (0.016, 0.02, 0.05))       # grip, down from the stock
    m.box(mesh, 0, (0, 0.33, 0.03), AXES, (0.28, 0.018, 0.016))        # the prod, across the nose
    m.box(mesh, 0, (0.14, 0.19, 0.05), (m.norm((1, -1.05, 0)), m.norm((1.05, 1, 0)), (0, 0, 1)),
          (0.002, 0.2, 0.002))                                          # the string, drawn back to the latch
    m.box(mesh, 0, (-0.14, 0.19, 0.05), (m.norm((1, 1.05, 0)), m.norm((-1.05, 1, 0)), (0, 0, 1)),
          (0.002, 0.2, 0.002))
    m.box(mesh, 0, (0, 0.2, 0.054), AXES, (0.004, 0.17, 0.004))        # a bolt in the groove
    return mesh


def bolt_mesh():
    mesh = ([], [], [], [])
    m.box(mesh, 0, (0, 0, 0), AXES, (0.004, 0.004, 0.17))              # the shaft, along Z
    m.box(mesh, 0, (0, 0, -0.18), AXES, (0.008, 0.008, 0.012))         # the head, in front (-Z)
    m.box(mesh, 0, (0, 0, 0.15), AXES, (0.014, 0.001, 0.02))           # fletching
    m.box(mesh, 0, (0, 0, 0.15), AXES, (0.001, 0.014, 0.02))
    return mesh


def rounds_mesh():
    mesh = ([], [], [], [])
    m.box(mesh, 0, (0, 0.03, 0), AXES, (0.05, 0.03, 0.035))
    return mesh


def lying(mesh):
    """A held model (forward +Y, up +Z) turned to lie on the ground: forward -Z, up +Y, resting on y = 0."""
    positions, normals, joints, indices = mesh
    turn = lambda v: (v[0], v[2], -v[1])
    turned = [turn(p) for p in positions]
    floor = min(p[1] for p in turned)
    return ([(p[0], p[1] - floor, p[2]) for p in turned], [turn(n) for n in normals], joints, indices)


def main():
    models = m.SANDBOX_MODELS
    m.write_rigid(os.path.join(models, 'viewmodel_pistol.glb'), 'pistol', pistol_mesh())
    m.write_rigid(os.path.join(models, 'pistol.glb'), 'pistol', lying(pistol_mesh()))
    m.write_rigid(os.path.join(models, 'viewmodel_crossbow.glb'), 'crossbow', crossbow_mesh())
    m.write_rigid(os.path.join(models, 'crossbow.glb'), 'crossbow', lying(crossbow_mesh()))
    m.write_rigid(os.path.join(models, 'bolt.glb'), 'bolt', bolt_mesh())
    m.write_rigid(os.path.join(models, 'pistol_rounds.glb'), 'pistol rounds', rounds_mesh())


if __name__ == '__main__':
    main()
