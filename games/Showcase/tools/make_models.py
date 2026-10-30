# The Showcase's models: simple shapes for the materials gallery and the instanced crystals, with UVs,
# tangents and (for one) vertex colours, written as .glb. Run with Blender (5.x):
#
#   blender --background --factory-startup --python games/Showcase/tools/make_models.py
#
# Each model sits on its origin (y = 0 at its base, after glTF's Y-up), so a placement's `at` is its foot.
import math
import os

import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "content", "models")


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def finish(obj, name, smooth=True, colours=False):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    if smooth:
        bpy.ops.object.shade_smooth()
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    path = os.path.join(OUT, name + ".glb")
    bpy.ops.export_scene.gltf(
        filepath=path, export_format="GLB", use_selection=True, export_tangents=True,
        export_normals=True, export_texcoords=True, export_materials="NONE",
        export_vertex_color="ACTIVE" if colours else "NONE", export_animations=False, export_yup=True)
    print("wrote", path)


def lift(obj, dz):
    for v in obj.data.vertices:
        v.co.z += dz


# A sphere 0.9 m across, standing on its base.
reset()
bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24, radius=0.45)
s = bpy.context.active_object
lift(s, 0.45)
finish(s, "sphere")

# A cube 0.8 m with rounded edges: the bevels catch the normal map's light at its corners.
reset()
bpy.ops.mesh.primitive_cube_add(size=0.8)
c = bpy.context.active_object
mod = c.modifiers.new("bevel", "BEVEL")
mod.width = 0.06
mod.segments = 4
bpy.ops.object.modifier_apply(modifier="bevel")
lift(c, 0.4)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.cube_project(cube_size=0.8)
bpy.ops.object.mode_set(mode="OBJECT")
finish(c, "rounded_cube")

# A torus standing on its rim, for the emissive plinth.
reset()
bpy.ops.mesh.primitive_torus_add(major_radius=0.36, minor_radius=0.12, major_segments=48, minor_segments=16)
t = bpy.context.active_object
t.rotation_euler = (math.radians(90), 0, 0)
bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
lift(t, 0.48)
finish(t, "torus")

# A tablet 1.0 x 1.4 x 0.12 m whose two big faces each carry the whole picture once.
reset()
bpy.ops.mesh.primitive_cube_add(size=1)
tab = bpy.context.active_object
tab.scale = (1.0, 0.12, 1.4)
bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
lift(tab, 0.7)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.cube_project(cube_size=1.0)
bpy.ops.object.mode_set(mode="OBJECT")
# The two big faces get the whole picture: u across x, v up z.
me = tab.data
uv = me.uv_layers.active.data
for poly in me.polygons:
    if abs(poly.normal.y) > 0.9:
        for li in poly.loop_indices:
            co = me.vertices[me.loops[li].vertex_index].co
            u = (co.x + 0.5) if poly.normal.y < 0 else (0.5 - co.x)
            uv[li].uv = (u, co.z / 1.4)
finish(tab, "tablet", smooth=False)

# A monkey head painted with vertex colours: a rainbow round it and white at the top.
reset()
bpy.ops.mesh.primitive_monkey_add(size=0.8)
m = bpy.context.active_object
sub = m.modifiers.new("sub", "SUBSURF")
sub.levels = 1
bpy.ops.object.modifier_apply(modifier="sub")
m.rotation_euler = (0, 0, math.radians(180))   # face -Y in Blender: +Z in the engine after Y-up, toward the viewer
bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
zmin = min(v.co.z for v in m.data.vertices)
lift(m, -zmin + 0.02)
attr = m.data.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")
m.data.color_attributes.active_color = attr
zmax = max(v.co.z for v in m.data.vertices)
for poly in m.data.polygons:
    for li in poly.loop_indices:
        co = m.data.vertices[m.data.loops[li].vertex_index].co
        hue = (math.atan2(co.y, co.x) / (2 * math.pi)) % 1.0
        k = co.z / zmax
        r = 0.5 + 0.5 * math.cos(2 * math.pi * (hue))
        g = 0.5 + 0.5 * math.cos(2 * math.pi * (hue - 1 / 3))
        b = 0.5 + 0.5 * math.cos(2 * math.pi * (hue - 2 / 3))
        w = max(0.0, (k - 0.75) * 4)
        attr.data[li].color = (r + (1 - r) * w, g + (1 - g) * w, b + (1 - b) * w, 1.0)
# UVs for the exporter (the material uses none).
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.smart_project()
bpy.ops.object.mode_set(mode="OBJECT")
finish(m, "monkey", colours=True)

# A crystal: a six-sided spire for the instanced field, flat shaded.
reset()
bpy.ops.mesh.primitive_cone_add(vertices=6, radius1=0.22, radius2=0.0, depth=1.2)
cr = bpy.context.active_object
lift(cr, 0.6)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.smart_project()
bpy.ops.object.mode_set(mode="OBJECT")
finish(cr, "crystal", smooth=False)
