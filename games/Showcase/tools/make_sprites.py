# The Showcase's particle and decal pictures, from Kenney's Particle Pack (CC0, OpenGameArt; see
# THIRD_PARTY_NOTICES.md). Each output is white with the source's coverage as its alpha, so a particle or
# decal record tints it to any colour; several frames go into one sheet for a particle's `sheetColumns`.
#
#   blender --background --factory-startup --python games/Showcase/tools/make_sprites.py -- <Kenney PNG folder>
import os
import sys

import bpy
import numpy as np

SRC = sys.argv[sys.argv.index("--") + 1]
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "content", "textures")


def load(name, size):
    img = bpy.data.images.load(os.path.join(SRC, name + ".png"))
    img.scale(size, size)
    px = np.array(img.pixels[:], dtype=np.float32).reshape(size, size, 4)
    # Coverage: brightness times alpha, whichever way the source stores it.
    cov = np.clip(px[..., :3].max(axis=2) * px[..., 3], 0, 1)
    bpy.data.images.remove(img)
    return cov


def save(name, cov, rgb=False):
    h, w = cov.shape
    out = np.ones((h, w, 4), dtype=np.float32)
    if rgb:   # an emissive map: the picture is in the colour, black where nothing glows
        out[..., 0] = out[..., 1] = out[..., 2] = cov
    else:
        out[..., 3] = cov
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.pixels[:] = out.ravel()
    img.filepath_raw = os.path.join(OUT, name + ".png")
    img.file_format = "PNG"
    img.save()
    print("wrote", img.filepath_raw)


def sheet(name, frames, size):
    tiles = [load(f, size) for f in frames]
    # Blender's rows run bottom-up: the first frame goes top-left of the picture.
    top = np.concatenate([tiles[0], tiles[1]], axis=1)
    bottom = np.concatenate([tiles[2], tiles[3]], axis=1)
    save(name, np.concatenate([bottom, top], axis=0))


sheet("smoke_sheet", ["smoke_01", "smoke_02", "smoke_04", "smoke_05"], 128)
sheet("flame_sheet", ["flame_01", "flame_02", "flame_03", "flame_04"], 128)
save("spark", load("spark_01", 64))
save("scorch", load("scorch_01", 256))
save("scorch_b", load("scorch_02", 256))
save("rune", load("magic_03", 256), rgb=True)
