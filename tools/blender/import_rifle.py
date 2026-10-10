"""Bring the rifle project's export into the game. Plain Python 3, no Blender needed.

Run from the project root:
  python3 tools/blender/import_rifle.py <rifle repo> assets/rifle

Reads <rifle repo>/export/rifle.glb and magazine.glb and writes them without their embedded textures, plus the 2K
texture set once, beside them:
  rifle.glb      skinned mesh "Rifle", armature, the clips and the Socket_* empties
  magazine.glb   the magazine on its own, for dropped magazines
  T_Rifle_BaseColor.png, T_Rifle_Normal.png, T_Rifle_ORM.png   (ORM: R occlusion, G roughness, B metallic)
The materials keep their names ("Rifle", "RifleLens") and factors; Rifle.cs builds the textured material, which
the rifle and every dropped magazine share.

Import settings that matter (the .import files beside them): the rifle's animations import at 60 fps, since at 30
Godot resamples the 67 ms firing cycle away, and keep their constant tracks, since every clip keys every bone so
that none inherits another's pose. The textures are VRAM compressed with mipmaps.
"""
import json, os, shutil, struct, sys

TEXTURES = ["T_Rifle_BaseColor.png", "T_Rifle_Normal.png", "T_Rifle_ORM.png"]


def read_glb(path):
    data = open(path, "rb").read()
    magic, version, _ = struct.unpack_from("<4sII", data, 0)
    assert magic == b"glTF" and version == 2, path
    offset, doc, binary = 12, None, b""
    while offset < len(data):
        length, kind = struct.unpack_from("<I4s", data, offset)
        chunk = data[offset + 8:offset + 8 + length]
        if kind == b"JSON":
            doc = json.loads(chunk)
        elif kind == b"BIN\0":
            binary = chunk
        offset += 8 + length
    return doc, binary


def strip_textures(doc, binary):
    """Drops images, textures and samplers, and the buffer views only images used, then repacks the buffer."""
    image_views = {image["bufferView"] for image in doc.get("images", []) if "bufferView" in image}
    for key in ("images", "textures", "samplers"):
        doc.pop(key, None)
    for material in doc.get("materials", []):
        for key in ("normalTexture", "occlusionTexture", "emissiveTexture"):
            material.pop(key, None)
        pbr = material.get("pbrMetallicRoughness", {})
        for key in ("baseColorTexture", "metallicRoughnessTexture"):
            pbr.pop(key, None)

    remap, views, packed = {}, [], bytearray()
    for index, view in enumerate(doc["bufferViews"]):
        if index in image_views:
            continue
        start = view.get("byteOffset", 0)
        chunk = binary[start:start + view["byteLength"]]
        packed += b"\0" * (-len(packed) % 4)
        view = dict(view, byteOffset=len(packed))
        packed += chunk
        remap[index] = len(views)
        views.append(view)
    for accessor in doc.get("accessors", []):
        if "bufferView" in accessor:
            accessor["bufferView"] = remap[accessor["bufferView"]]
        sparse = accessor.get("sparse")
        if sparse:
            sparse["indices"]["bufferView"] = remap[sparse["indices"]["bufferView"]]
            sparse["values"]["bufferView"] = remap[sparse["values"]["bufferView"]]
    doc["bufferViews"] = views
    packed += b"\0" * (-len(packed) % 4)
    doc["buffers"] = [{"byteLength": len(packed)}]
    return doc, bytes(packed)


def write_glb(path, doc, binary):
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    with open(path, "wb") as out:
        out.write(struct.pack("<4sII", b"glTF", 2, total))
        out.write(struct.pack("<I4s", len(text), b"JSON") + text)
        out.write(struct.pack("<I4s", len(binary), b"BIN\0") + binary)


def main():
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    for name in ("rifle.glb", "magazine.glb"):
        doc, binary = strip_textures(*read_glb(os.path.join(src, "export", name)))
        write_glb(os.path.join(out, name), doc, binary)
        print(f"{name}: {os.path.getsize(os.path.join(out, name)) / 1e6:.2f} MB")
    for name in TEXTURES:
        shutil.copyfile(os.path.join(src, "textures", "2k", name), os.path.join(out, name))
        print(f"{name}: {os.path.getsize(os.path.join(out, name)) / 1e6:.2f} MB")


if __name__ == "__main__":
    main()
