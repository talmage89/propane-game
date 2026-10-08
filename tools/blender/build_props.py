"""Build the procedural suburb props that the Kenney kits do not cover.

Run from the project root:
  blender -b --factory-startup --python tools/blender/build_props.py -- assets/props

Each prop is exported to its own GLB with the origin at the centre of its base. Blender -Y is the prop's front, so
after glTF export the front faces Godot +Z, like the Kenney models. Materials carry their colour as the base colour
factor; materials whose names start with "Tint_" take a per-instance colour in game.
"""
import bpy, bmesh, math, os, sys
from mathutils import Vector, Matrix

OUT = sys.argv[sys.argv.index("--") + 1:][0]
os.makedirs(OUT, exist_ok=True)

COLORS = {
    "Black": (0.03, 0.03, 0.035),
    "Charcoal": (0.09, 0.095, 0.1),
    "Steel": (0.62, 0.64, 0.66),
    "DarkSteel": (0.25, 0.26, 0.28),
    "White": (0.86, 0.86, 0.85),
    "Red": (0.72, 0.08, 0.06),
    "Yellow": (0.95, 0.72, 0.1),
    "Wood": (0.45, 0.3, 0.18),
    "Water": (0.25, 0.65, 0.85),
    "Rubber": (0.04, 0.04, 0.04),
    "Lamp": (1.0, 0.95, 0.8),
    "Tint_Body": (0.8, 0.8, 0.8),
    "Tint_Light": (0.92, 0.92, 0.92),
}
MATERIALS = {}


def mat(name):
    if name not in MATERIALS:
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        bsdf = m.node_tree.nodes["Principled BSDF"]
        c = COLORS[name]
        bsdf.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
        bsdf.inputs["Roughness"].default_value = 0.35 if "Steel" in name else 0.7
        bsdf.inputs["Metallic"].default_value = 0.8 if "Steel" in name else 0.0
        MATERIALS[name] = m
    return MATERIALS[name]


class Prop:
    def __init__(self, name):
        self.name = name
        self.parts = []

    def _finish(self, bm, material, bevel, segments=1):
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        if bevel > 0:
            bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=segments, affect="EDGES", profile=0.5)
        me = bpy.data.meshes.new(f"{self.name}_part")
        bm.to_mesh(me)
        bm.free()
        me.materials.append(mat(material))
        ob = bpy.data.objects.new(me.name, me)
        bpy.context.scene.collection.objects.link(ob)
        self.parts.append(ob)
        return ob

    def box(self, center, size, material, bevel=0.01, rot=None):
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
        if rot:
            bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(rot[1]), 3, rot[0]), verts=bm.verts)
        bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
        return self._finish(bm, material, bevel)

    def cyl(self, center, radius, height, material, segments=16, bevel=0.005, radius_top=None, axis="Z"):
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=radius,
                              radius2=radius if radius_top is None else radius_top, depth=height)
        if axis == "X":
            bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, "Y"), verts=bm.verts)
        elif axis == "Y":
            bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, "X"), verts=bm.verts)
        bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
        return self._finish(bm, material, bevel)

    def sphere(self, center, radius, material, scale=(1, 1, 1), half=False):
        bm = bmesh.new()
        bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=8, radius=radius)
        if half:
            for v in [v for v in bm.verts if v.co.z < -0.0001]:
                v.co.z = 0
        bmesh.ops.scale(bm, vec=Vector(scale), verts=bm.verts)
        bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
        return self._finish(bm, material, 0)

    def export(self):
        for ob in self.parts:
            for poly in ob.data.polygons:
                poly.use_smooth = False
        bpy.ops.object.select_all(action="DESELECT")
        for ob in self.parts:
            ob.select_set(True)
        bpy.context.view_layer.objects.active = self.parts[0]
        bpy.ops.object.join()
        ob = bpy.context.view_layer.objects.active
        ob.name = self.name
        bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f"{self.name}.glb"), export_format="GLB",
                                  use_selection=True, export_yup=True, export_animations=False)
        bpy.data.objects.remove(ob, do_unlink=True)
        print("EXPORTED", self.name)


def gas_grill():
    p = Prop("gas_grill")
    # Cart with an open bay underneath (where the tank lives), two side shelves, a domed lid.
    p.box((0, 0, 0.5), (0.62, 0.5, 0.05), "Charcoal")
    p.box((0, 0, 0.08), (0.62, 0.5, 0.04), "Charcoal")
    for x in (-0.29, 0.29):
        # Rear legs end at the wheel axle; the front ones reach the ground, so the cart stands level.
        p.box((x, 0.22, 0.29), (0.04, 0.04, 0.42), "DarkSteel", bevel=0.005)
        p.box((x, -0.22, 0.26), (0.04, 0.04, 0.52), "DarkSteel", bevel=0.005)
    p.box((0, 0.02, 0.68), (0.6, 0.46, 0.32), "Charcoal", bevel=0.02)
    p.box((0, -0.22, 0.6), (0.6, 0.04, 0.14), "Steel", bevel=0.008)
    for x in (-0.18, -0.06, 0.06, 0.18):
        p.cyl((x, -0.25, 0.62), 0.022, 0.03, "Black", segments=10, axis="Y")
    p.cyl((0, 0.02, 0.84), 0.235, 0.6, "Steel", segments=20, axis="X")
    p.box((0, -0.24, 0.98), (0.44, 0.03, 0.03), "Steel", bevel=0.008)
    for x in (-0.2, 0.2):
        p.box((x, -0.22, 0.95), (0.025, 0.06, 0.06), "Steel", bevel=0.005)
    for x in (-0.48, 0.48):
        p.box((x, 0.0, 0.83), (0.34, 0.42, 0.035), "Steel", bevel=0.006)
    for x in (-0.3, 0.3):
        p.cyl((x, 0.2, 0.06), 0.06, 0.04, "Rubber", segments=14, axis="X")
    p.export()


def wheelie_bin():
    p = Prop("wheelie_bin")
    # A four-sided frustum, turned so its faces line up with the axes.
    body = p.cyl((0, 0, 0.5), 0.3, 0.92, "Tint_Body", segments=4, radius_top=0.36, bevel=0.03)
    body.data.transform(Matrix.Rotation(math.radians(45), 4, "Z"))
    p.box((0, 0.02, 0.99), (0.56, 0.7, 0.05), "Tint_Body", bevel=0.015)
    p.box((0, 0.36, 0.96), (0.5, 0.06, 0.06), "Charcoal", bevel=0.01)
    for x in (-0.24, 0.24):
        p.cyl((x, 0.3, 0.1), 0.1, 0.05, "Rubber", segments=14, axis="X")
    p.cyl((0, 0.3, 0.1), 0.015, 0.5, "DarkSteel", segments=8, axis="X")
    # A moulded foot under the front, so the bin stands level on it and the wheels.
    p.box((0, -0.2, 0.025), (0.36, 0.08, 0.05), "Tint_Body", bevel=0.01)
    p.export()


def mailbox():
    p = Prop("mailbox")
    p.box((0, 0, 0.55), (0.09, 0.09, 1.1), "Wood", bevel=0.008)
    p.box((0, 0.0, 1.13), (0.24, 0.5, 0.18), "Tint_Light", bevel=0.01)
    p.cyl((0, 0.0, 1.22), 0.12, 0.5, "Tint_Light", segments=16, axis="Y")
    p.box((0.13, 0.08, 1.28), (0.015, 0.03, 0.2), "Red", bevel=0.003)
    p.box((0.13, 0.14, 1.36), (0.015, 0.12, 0.06), "Red", bevel=0.003)
    p.export()


def street_lamp():
    p = Prop("street_lamp")
    p.cyl((0, 0, 0.25), 0.16, 0.5, "Charcoal", segments=12)
    p.cyl((0, 0, 3.0), 0.07, 5.6, "Charcoal", segments=12, radius_top=0.05)
    p.box((0, -0.65, 5.75), (0.07, 1.4, 0.07), "Charcoal", bevel=0.01)
    p.box((0, -1.32, 5.66), (0.34, 0.6, 0.12), "Charcoal", bevel=0.02)
    p.box((0, -1.32, 5.59), (0.26, 0.5, 0.03), "Lamp", bevel=0.0)
    p.export()


def fire_hydrant():
    p = Prop("fire_hydrant")
    p.cyl((0, 0, 0.04), 0.17, 0.08, "Red", segments=14)
    p.cyl((0, 0, 0.32), 0.12, 0.5, "Red", segments=14)
    p.cyl((0, 0, 0.6), 0.15, 0.06, "Red", segments=14)
    p.sphere((0, 0, 0.63), 0.12, "Red", half=True)
    p.cyl((0, 0, 0.77), 0.035, 0.06, "Red", segments=6)
    for x in (-0.15, 0.15):
        p.cyl((x, 0, 0.42), 0.05, 0.1, "Red", segments=10, axis="X")
    p.cyl((0, -0.15, 0.42), 0.065, 0.1, "Red", segments=10, axis="Y")
    p.export()


def patio_heater():
    p = Prop("patio_heater")
    p.cyl((0, 0, 0.35), 0.24, 0.7, "Steel", segments=16, radius_top=0.2)
    p.cyl((0, 0, 1.3), 0.045, 1.3, "Steel", segments=12)
    p.cyl((0, 0, 2.0), 0.13, 0.22, "DarkSteel", segments=14)
    p.cyl((0, 0, 2.2), 0.45, 0.1, "Steel", segments=20, radius_top=0.04)
    p.export()


def cooler():
    p = Prop("cooler")
    p.box((0, 0, 0.19), (0.62, 0.38, 0.34), "Tint_Body", bevel=0.03)
    p.box((0, 0, 0.385), (0.64, 0.4, 0.06), "White", bevel=0.02)
    p.box((0, 0, 0.43), (0.36, 0.05, 0.03), "White", bevel=0.01)
    p.export()


def kiddie_pool():
    p = Prop("kiddie_pool")
    p.cyl((0, 0, 0.14), 0.9, 0.28, "Tint_Body", segments=28, bevel=0.0)
    p.cyl((0, 0, 0.22), 0.82, 0.04, "Water", segments=28, bevel=0.0)
    p.export()


def patio_umbrella():
    p = Prop("patio_umbrella")
    p.cyl((0, 0, 0.05), 0.3, 0.1, "Charcoal", segments=16)
    p.cyl((0, 0, 1.2), 0.025, 2.3, "White", segments=10)
    p.cyl((0, 0, 2.25), 1.35, 0.35, "Tint_Body", segments=8, radius_top=0.05, bevel=0.0)
    p.export()


def shed():
    p = Prop("shed")
    p.box((0, 0, 1.0), (2.6, 2.0, 2.0), "Tint_Light", bevel=0.02)
    bm = bmesh.new()
    verts = [bm.verts.new(v) for v in ((-1.45, -1.15, 1.95), (1.45, -1.15, 1.95), (1.45, 1.15, 1.95), (-1.45, 1.15, 1.95),
                                       (-1.45, 0, 2.7), (1.45, 0, 2.7))]
    for f in ((0, 1, 5, 4), (2, 3, 4, 5), (0, 4, 3), (1, 2, 5), (0, 3, 2, 1)):
        bm.faces.new([verts[i] for i in f])
    p._finish(bm, "Charcoal", 0)
    p.box((0.3, -1.01, 0.9), (1.1, 0.04, 1.75), "Wood", bevel=0.01)
    p.export()


def stop_sign():
    p = Prop("stop_sign")
    p.cyl((0, 0, 1.2), 0.035, 2.4, "Steel", segments=8)
    p.cyl((0, -0.05, 2.3), 0.38, 0.03, "Red", segments=8, axis="Y", bevel=0.0)
    p.cyl((0, -0.07, 2.3), 0.33, 0.01, "White", segments=8, axis="Y", bevel=0.0)
    p.export()


def fence_privacy():
    """2.4 m privacy panel: vertical boards between two rails, one post at the -X end. Spans x 0..2.4."""
    p = Prop("fence_privacy")
    p.box((0.05, 0, 0.85), (0.1, 0.1, 1.7), "Tint_Body", bevel=0.01)
    p.box((0.05, 0, 1.72), (0.13, 0.13, 0.04), "Tint_Body", bevel=0.008)
    for z in (0.35, 1.35):
        p.box((1.25, 0.035, z), (2.3, 0.04, 0.09), "Tint_Body", bevel=0.005)
    boards = 16
    for i in range(boards):
        x = 0.1 + (i + 0.5) * 2.3 / boards
        height = 1.6 + (0.015 if i % 2 else 0.0)
        p.box((x, -0.012, height / 2 + 0.03), (2.3 / boards - 0.012, 0.02, height), "Tint_Body", bevel=0.003)
    p.export()


def fence_picket():
    """2.4 m white picket panel with pointed pickets, one post at the -X end. Spans x 0..2.4."""
    p = Prop("fence_picket")
    p.box((0.05, 0, 0.55), (0.09, 0.09, 1.1), "Tint_Light", bevel=0.01)
    p.box((0.05, 0, 1.12), (0.12, 0.12, 0.04), "Tint_Light", bevel=0.008)
    for z in (0.3, 0.8):
        p.box((1.25, 0.03, z), (2.3, 0.03, 0.06), "Tint_Light", bevel=0.004)
    pickets = 14
    for i in range(pickets):
        x = 0.1 + (i + 0.5) * 2.3 / pickets
        bm = bmesh.new()
        w, d, h = 0.075, 0.018, 0.95
        verts = [bm.verts.new(v) for v in ((-w / 2, -d / 2, 0), (w / 2, -d / 2, 0), (w / 2, d / 2, 0), (-w / 2, d / 2, 0),
                                           (-w / 2, -d / 2, h), (w / 2, -d / 2, h), (w / 2, d / 2, h), (-w / 2, d / 2, h),
                                           (0, -d / 2, h + 0.07), (0, d / 2, h + 0.07))]
        for f in ((0, 3, 2, 1), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7), (4, 5, 8), (6, 7, 9), (5, 6, 9, 8), (7, 4, 8, 9)):
            bm.faces.new([verts[k] for k in f])
        bmesh.ops.translate(bm, vec=(x, -0.01, 0.05), verts=bm.verts)
        p._finish(bm, "Tint_Light", 0)
    p.export()


def fence_post():
    """Free-standing end post for a fence run (privacy height)."""
    p = Prop("fence_post")
    p.box((0, 0, 0.85), (0.1, 0.1, 1.7), "Tint_Body", bevel=0.01)
    p.box((0, 0, 1.72), (0.13, 0.13, 0.04), "Tint_Body", bevel=0.008)
    p.export()


bpy.ops.wm.read_factory_settings(use_empty=True)
for build in (fence_privacy, fence_picket, fence_post, gas_grill, wheelie_bin, mailbox, street_lamp, fire_hydrant, patio_heater, cooler, kiddie_pool,
              patio_umbrella, shed, stop_sign):
    build()
