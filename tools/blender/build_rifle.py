"""Build the stylized low-poly rifle.

Run from the project root:
  blender -b --factory-startup --python tools/blender/build_rifle.py -- assets/rifle/rifle.glb

Blender axes: +Y is the barrel direction (Godot -Z after glTF export), +Z up, +X to the right.
The origin is the center of the pistol grip, where the right hand holds it. Empties named Socket_* mark
the left-hand grip, the muzzle, the stock butt and the sight's eye point.
"""
import bpy, bmesh, math, sys
from mathutils import Vector, Matrix

OUT = sys.argv[sys.argv.index("--") + 1:][0]
bpy.ops.wm.read_factory_settings(use_empty=True)

MATERIALS = {name: bpy.data.materials.new(name) for name in ("RifleMetal", "RiflePolymer", "RifleAccent", "RifleLens")}
GRIP = Vector((0.0, -0.065, -0.085))
parts = []


def finish(bm, name, material, bevel=0.0025):
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=1, affect="EDGES", profile=0.5)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(MATERIALS[material])
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    parts.append(ob)
    return ob


def box(name, lo, hi, material, tilt_deg=0.0, pivot=None, bevel=0.0025):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    lo, hi = Vector(lo), Vector(hi)
    bmesh.ops.scale(bm, vec=hi - lo, verts=bm.verts)
    bmesh.ops.translate(bm, vec=(lo + hi) / 2, verts=bm.verts)
    if tilt_deg:
        p = Vector(pivot) if pivot else (lo + hi) / 2
        bmesh.ops.rotate(bm, cent=p, matrix=Matrix.Rotation(math.radians(tilt_deg), 3, "X"), verts=bm.verts)
    return finish(bm, name, material, bevel)


def tube(name, y0, y1, radius, z, material, sides=8, bevel=0.0015, x=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=sides, radius1=radius, radius2=radius, depth=y1 - y0)
    bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, "X"), verts=bm.verts)
    bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / sides, 3, "Y"), verts=bm.verts)
    bmesh.ops.translate(bm, vec=(x, (y0 + y1) / 2, z), verts=bm.verts)
    return finish(bm, name, material, bevel)


# Receiver, upper and lower
box("UpperReceiver", (-0.024, -0.13, 0.0), (0.024, 0.20, 0.068), "RifleMetal")
box("LowerReceiver", (-0.021, -0.11, -0.042), (0.021, 0.11, 0.002), "RifleMetal")
box("EjectionCover", (0.0235, -0.02, 0.022), (0.027, 0.07, 0.05), "RifleAccent", bevel=0.001)
box("TriggerGuard", (-0.008, -0.045, -0.062), (0.008, 0.04, -0.054), "RifleMetal", bevel=0.001)
# Pistol grip, raked back
box("PistolGrip", (-0.017, -0.095, -0.155), (0.017, -0.045, -0.03), "RiflePolymer", tilt_deg=-18, pivot=(0, -0.07, -0.03))
# Magazine, raked forward
box("Magazine", (-0.015, 0.03, -0.215), (0.015, 0.095, -0.03), "RiflePolymer", tilt_deg=12, pivot=(0, 0.06, -0.03))
box("MagazineBase", (-0.018, 0.022, -0.232), (0.018, 0.104, -0.212), "RifleAccent", tilt_deg=12, pivot=(0, 0.06, -0.03))
# Stock: buffer tube and a skeleton stock with butt pad
tube("BufferTube", -0.30, -0.12, 0.016, 0.03, "RifleMetal")
box("StockBody", (-0.02, -0.34, -0.035), (0.02, -0.20, 0.058), "RiflePolymer")
box("ButtPad", (-0.023, -0.365, -0.05), (0.023, -0.338, 0.065), "RifleAccent")
# Handguard and barrel
tube("Handguard", 0.195, 0.43, 0.031, 0.034, "RiflePolymer", sides=8, bevel=0.002)
tube("Barrel", 0.42, 0.555, 0.0095, 0.034, "RifleMetal", sides=10)
tube("MuzzleDevice", 0.535, 0.6, 0.0135, 0.034, "RifleMetal", sides=8)
tube("MuzzleRing", 0.543, 0.555, 0.0145, 0.034, "RifleAccent", sides=8, bevel=0.0)
# Top rail and holographic sight
box("TopRail", (-0.011, -0.12, 0.068), (0.011, 0.41, 0.079), "RifleMetal", bevel=0.001)
box("SightBase", (-0.02, 0.0, 0.079), (0.02, 0.085, 0.09), "RifleMetal", bevel=0.0015)
box("SightHoodL", (-0.021, 0.004, 0.09), (-0.014, 0.08, 0.135), "RifleMetal", bevel=0.0015)
box("SightHoodR", (0.014, 0.004, 0.09), (0.021, 0.08, 0.135), "RifleMetal", bevel=0.0015)
box("SightHoodTop", (-0.021, 0.004, 0.13), (0.021, 0.08, 0.138), "RifleMetal", bevel=0.0015)
box("SightLens", (-0.014, 0.05, 0.09), (0.014, 0.054, 0.13), "RifleLens", bevel=0.0)
box("FrontSight", (-0.004, 0.385, 0.079), (0.004, 0.4, 0.105), "RifleMetal", bevel=0.001)
# Vertical foregrip for the left hand
box("Foregrip", (-0.014, 0.2, -0.075), (0.014, 0.23, 0.005), "RiflePolymer", tilt_deg=8, pivot=(0, 0.215, 0.005))

for ob in parts:
    ob.data.transform(Matrix.Translation(-GRIP))
    for poly in ob.data.polygons:
        poly.use_smooth = False

bpy.ops.object.select_all(action="DESELECT")
for ob in parts:
    ob.select_set(True)
bpy.context.view_layer.objects.active = parts[0]
bpy.ops.object.join()
rifle = bpy.context.view_layer.objects.active
rifle.name = "Rifle"

SOCKETS = {
    "Socket_Foregrip": Vector((0.0, 0.215, -0.045)),
    "Socket_Muzzle": Vector((0.0, 0.6, 0.034)),
    "Socket_Butt": Vector((0.0, -0.365, 0.01)),
    "Socket_Eye": Vector((0.0, -0.07, 0.112)),
}
for name, pos in SOCKETS.items():
    e = bpy.data.objects.new(name, None)
    e.location = pos - GRIP
    bpy.context.scene.collection.objects.link(e)
    e.select_set(True)
rifle.select_set(True)
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True, export_yup=True,
                          export_materials="PLACEHOLDER", export_animations=False)
print("EXPORTED", OUT, len(rifle.data.polygons), "faces")
