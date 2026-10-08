"""Convert the Unreal export of the propane tank into glTF for Godot.

Run from the project root:
  blender -b --factory-startup --python tools/blender/export_tank.py -- <propane-tank repo>/unreal assets/tank

Writes:
  tank.glb    mesh "Tank" plus convex collision mesh "TankCollision"
  debris.glb  25 debris pieces "Debris_<Part>" and a convex hull "Debris_<Part>_Hull" for each
Materials are named slots only; Godot assigns the real materials at runtime.
"""
import bpy, bmesh, sys, os

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, OUT = argv[0], argv[1]
HULL_MAX_VERTS = 48

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.join(SRC, "SM_PropaneTank.fbx"))
bpy.ops.import_scene.fbx(filepath=os.path.join(SRC, "SM_PropaneTank_Debris.fbx"))

outer = bpy.data.materials.new("TankOuter")
inner = bpy.data.materials.new("TankInterior")


def retarget_materials(ob):
    for slot in ob.material_slots:
        slot.material = inner if slot.material and "Interior" in slot.material.name else outer


def farthest_points(points, count):
    """Greedy farthest-point sampling: a well-spread subset that keeps the hull's extremes."""
    chosen = [max(points, key=lambda p: p.length)]
    dist = [(p - chosen[0]).length for p in points]
    while len(chosen) < min(count, len(points)):
        i = max(range(len(points)), key=dist.__getitem__)
        chosen.append(points[i])
        dist = [min(d, (p - points[i]).length) for d, p in zip(dist, points)]
    return chosen


def hull_object(src, name):
    points = [v.co.copy() for v in src.data.vertices]
    bm = bmesh.new()
    for p in farthest_points(points, HULL_MAX_VERTS * 3):
        bm.verts.new(p)
    result = bmesh.ops.convex_hull(bm, input=bm.verts, use_existing_faces=False)
    for geom in result["geom_interior"] + result["geom_unused"]:
        if isinstance(geom, bmesh.types.BMVert) and geom.is_valid:
            bm.verts.remove(geom)
    if len(bm.verts) > HULL_MAX_VERTS:
        keep = farthest_points([v.co.copy() for v in bm.verts], HULL_MAX_VERTS)
        bm.free()
        bm = bmesh.new()
        for p in keep:
            bm.verts.new(p)
        bmesh.ops.convex_hull(bm, input=bm.verts, use_existing_faces=False)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def export(objs, path):
    bpy.ops.object.select_all(action="DESELECT")
    for ob in objs:
        ob.select_set(True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_yup=True,
                              export_tangents=True, export_normals=True, export_materials="PLACEHOLDER",
                              export_apply=True, export_animations=False)


tank = bpy.data.objects["SM_PropaneTank"]
tank.name = "Tank"
retarget_materials(tank)
col = bpy.data.objects["UCX_SM_PropaneTank_00"]
col.name = "TankCollision"
col.data.materials.clear()
export([tank, col], os.path.join(OUT, "tank.glb"))

pieces = []
for ob in list(bpy.data.objects):
    if ob.name.startswith("SM_PropaneTank_Debris_"):
        part = ob.name.replace("SM_PropaneTank_Debris_", "")
        ob.name = f"Debris_{part}"
        retarget_materials(ob)
        hull = hull_object(ob, f"Debris_{part}_Hull")
        pieces += [ob, hull]
        print(f"piece {ob.name}: {len(ob.data.polygons)} faces, hull {len(hull.data.vertices)} verts")
export(pieces, os.path.join(OUT, "debris.glb"))
