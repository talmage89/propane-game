"""Source-side asset checks and the owner's scaffold. Python standard library only; never executes asset data.

The .tres reader deliberately supports our small declarative resource format, not arbitrary Godot expressions.
Godot independently loads/validates the typed resources in assets-check. Runtime never scans directories.
"""
from pathlib import Path
import json
import math
import re
import struct

ROOT = Path(__file__).resolve().parents[2]
ID = re.compile(r'[a-z0-9][a-z0-9_-]{0,63}\Z', re.ASCII)


def contained(root, path):
    root = root.resolve()
    candidate = Path(path)
    if '..' in candidate.parts or candidate.is_absolute() or '\\' in str(path) or ':' in str(path):
        raise ValueError(f'path must be relative without traversal: {path}')
    result = root / candidate
    if not result.resolve().is_relative_to(root) or any(p.is_symlink() for p in [result, *result.parents] if p != root.parent):
        raise ValueError(f'path escapes asset root or uses a symlink: {path}')
    return result


def resource(path):
    """Read declarations without evaluating code. Reject duplicates and unknown expressions at their caller."""
    text = path.read_text(encoding='utf-8')
    sections = re.split(r'(?m)^\[([^\n]+)\]\s*$', text)
    if sections[0].strip() or len(sections) < 3 or not sections[1].startswith('gd_resource '):
        raise ValueError(f'{path}: expected a Godot Resource')
    external, properties = {}, {}
    for header, body in zip(sections[1::2], sections[2::2]):
        if header.startswith('gd_resource '):
            if body.strip(): raise ValueError(f'{path}: unexpected header content')
        elif header.startswith('ext_resource '):
            attributes = dict(re.findall(r'(\w+)=("(?:[^"\\]|\\.)*")', header))
            attributes = {key: json.loads(value) for key, value in attributes.items()}
            if set(attributes) - {'type', 'path', 'id', 'uid'} or not {'type', 'path', 'id'} <= attributes.keys():
                raise ValueError(f'{path}: invalid external declaration')
            if attributes['id'] in external: raise ValueError(f'{path}: duplicate external resource')
            external[attributes['id']] = attributes
        elif header == 'resource':
            # A property can contain a multiline array. Splitting at assignment starts preserves its contents.
            assignments = re.split(r'(?m)^(\w+)\s*=\s*', body)
            if assignments[0].strip(): raise ValueError(f'{path}: invalid resource content')
            for key, value in zip(assignments[1::2], assignments[2::2]):
                if key in properties: raise ValueError(f'{path}: duplicate {key}')
                properties[key] = value.strip()
        else:
            raise ValueError(f'{path}: unsupported section {header}')
    return external, properties


def string(properties, key, default=''):
    value = properties.get(key)
    if value is None: return default
    decoded = json.loads(value)
    if not isinstance(decoded, str): raise ValueError(f'{key} must be a string')
    return decoded


def reference(value):
    match = re.fullmatch(r'ExtResource\("([^"\n]+)"\)', value)
    if not match: raise ValueError(f'invalid resource reference {value}')
    return match[1]


def references(value):
    # Godot may write either a typed array or an ordinary Array for an exported Resource array.
    match = re.fullmatch(r'(?:Array\[(?:ExtResource\("[^"\n]+"\)|Resource)\]\()?\[([\s\S]*)\]\)?', value)
    if not match: raise ValueError('Hats must be an array of external resources')
    body = match[1].strip()
    return [] if not body else [reference(part.strip()) for part in body.split(',') if part.strip()]


def dependency(root, declaring, external):
    path = external['path']
    if not path.startswith('res://assets/'):
        raise ValueError(f'{declaring}: dependency outside assets: {path}')
    return contained(root, path.removeprefix('res://assets/'))


def glb(path):
    data = path.read_bytes()
    if len(data) < 20 or struct.unpack_from('<4sII', data) != (b'glTF', 2, len(data)):
        raise ValueError(f'{path}: invalid GLB header')
    size, kind = struct.unpack_from('<I4s', data, 12)
    if kind != b'JSON' or size > len(data) - 20: raise ValueError(f'{path}: invalid JSON chunk')
    doc = json.loads(data[20:20 + size])
    return doc, data


def hat_metrics(path):
    doc, data = glb(path)
    if doc.get('skins') or doc.get('animations') or doc.get('extensionsRequired') or doc.get('extensionsUsed'):
        raise ValueError(f'{path}: hats must be static glTF without extensions')
    if len(doc.get('buffers', [])) != 1 or 'uri' in doc['buffers'][0]:
        raise ValueError(f'{path}: expected one embedded GLB buffer')
    chunk_end = 20 + struct.unpack_from('<I', data, 12)[0]
    if chunk_end + 8 > len(data): raise ValueError(f'{path}: missing binary chunk')
    binary_size, binary_kind = struct.unpack_from('<I4s', data, chunk_end)
    if binary_kind != b'BIN\0' or chunk_end + 8 + binary_size != len(data):
        raise ValueError(f'{path}: invalid binary chunk')
    binary = data[chunk_end + 8:]
    if doc['buffers'][0].get('byteLength', 0) > len(binary): raise ValueError(f'{path}: truncated buffer')
    views = doc.get('bufferViews', [])
    for view in views:
        if view.get('buffer', 0) != 0 or view.get('byteOffset', 0) < 0 or view.get('byteLength', 0) <= 0 or view.get('byteOffset', 0) + view['byteLength'] > len(binary):
            raise ValueError(f'{path}: buffer view out of bounds')
    accessors = doc.get('accessors', [])
    for accessor in accessors:
        if accessor.get('count', 0) <= 0 or 'sparse' in accessor: raise ValueError(f'{path}: invalid/sparse accessor')
        view = views[accessor['bufferView']]
        width = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}.get(accessor['type'])
        size = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}.get(accessor['componentType'])
        if not width or not size: raise ValueError(f'{path}: unsupported accessor format')
        end = accessor.get('byteOffset', 0) + (accessor['count'] - 1) * view.get('byteStride', width * size) + width * size
        if accessor.get('byteOffset', 0) < 0 or end > view['byteLength']: raise ValueError(f'{path}: accessor out of bounds')
    triangles = surfaces = 0
    meshes = doc.get('meshes', [])
    # Count instances, not only distinct mesh definitions.
    for node in doc.get('nodes', []):
        if any(key in node for key in ('camera', 'skin', 'extensions')) or any(suffix in node.get('name', '').lower() for suffix in ('-col', '-convcol', '-rigid', '-navmesh')):
            raise ValueError(f'{path}: unsupported scene node')
        for values in [node.get('translation', []), node.get('rotation', []), node.get('scale', []), node.get('matrix', [])]:
            if any(not isinstance(x, (int, float)) or not math.isfinite(x) for x in values): raise ValueError(f'{path}: non-finite transform')
        scale = node.get('scale', [1, 1, 1])
        if len(scale) != 3 or any(abs(s - 1) > 0.00001 for s in scale): raise ValueError(f'{path}: apply object scale before export')
        if 'mesh' not in node: continue
        for primitive in meshes[node['mesh']]['primitives']:
            if primitive.get('mode', 4) != 4 or primitive.get('targets'): raise ValueError(f'{path}: only static triangles are supported')
            position = accessors[primitive['attributes']['POSITION']]
            if position['type'] != 'VEC3' or position['componentType'] != 5126: raise ValueError(f'{path}: invalid positions')
            view = views[position['bufferView']]
            for i in range(position['count']):
                at = view.get('byteOffset', 0) + position.get('byteOffset', 0) + i * view.get('byteStride', 12)
                if not all(math.isfinite(v) for v in struct.unpack_from('<3f', binary, at)): raise ValueError(f'{path}: non-finite vertex')
            count = accessors[primitive['indices']]['count'] if 'indices' in primitive else position['count']
            if count % 3: raise ValueError(f'{path}: incomplete triangles')
            triangles += count // 3
            surfaces += 1
    if not surfaces: raise ValueError(f'{path}: no mesh surfaces')
    textures, decoded, dimensions = set(), 0, []
    for material in doc.get('materials', []):
        if material.get('alphaMode', 'OPAQUE') not in ('OPAQUE', 'MASK') or material.get('extensions'):
            raise ValueError(f'{path}: only opaque/cutout standard PBR materials are supported')
        pbr = material.get('pbrMetallicRoughness', {})
        for owner, key in [(material, 'normalTexture'), (material, 'occlusionTexture'), (material, 'emissiveTexture'), (pbr, 'baseColorTexture'), (pbr, 'metallicRoughnessTexture')]:
            if key in owner: textures.add(doc['textures'][owner[key]['index']]['source'])
    dependencies = []
    for index, image in enumerate(doc.get('images', [])):
        if 'uri' in image:
            uri = image['uri']
            image_path = contained(path.parent, uri)
            if image_path.suffix.lower() != '.png': raise ValueError(f'{path}: textures must be PNG')
            dependencies.append(image_path)
            png = image_path.read_bytes()
        else:
            if image.get('mimeType') != 'image/png': raise ValueError(f'{path}: embedded textures must be PNG')
            view = views[image['bufferView']]; start = view.get('byteOffset', 0); png = binary[start:start + view['byteLength']]
        if len(png) < 24 or png[:8] != b'\x89PNG\r\n\x1a\n' or png[12:16] != b'IHDR': raise ValueError(f'{path}: invalid PNG header')
        width, height = struct.unpack_from('>II', png, 16)
        if not width or not height: raise ValueError(f'{path}: empty image')
        # Count all images, including unused ones, to avoid concealing oversized embedded payloads.
        dimensions.append([width, height]); w, h = width, height
        while True:
            decoded += w * h * 4
            if w == h == 1: break
            w, h = max(1, w // 2), max(1, h // 2)
    return {'triangles': triangles, 'surfaces': surfaces, 'textures': len(dimensions), 'dimensions': dimensions, 'decoded_bytes': decoded}, dependencies


def validate(root, inventory, budgets):
    errors, warnings, items, accounted = [], [], [], set(inventory['files'])
    for relative, family in inventory['files'].items():
        try:
            path = contained(root, relative)
            if not path.is_file(): raise ValueError(f'missing inventoried asset: {relative}')
            metadata = inventory['families'][family]
            if not all(metadata.get(key) for key in ('author', 'license', 'source')):
                raise ValueError(f'{relative}: missing provenance/license')
            if not inventory['licenses'].get(metadata['license']): raise ValueError(f'{relative}: unknown license identifier')
            if path.suffix == '.import':
                text = path.read_text(); match = re.search(r'^uid="(uid://[a-z0-9]+)"', text, re.M)
                if not match: raise ValueError(f'{relative}: missing import UID')
                if not re.search(r'^source_file="' + re.escape('res://assets/' + relative.removesuffix('.import')) + r'"$', text, re.M): raise ValueError(f'{relative}: stale import source')
        except (ValueError, KeyError, OSError) as error: errors.append(str(error))
    library = root / 'library.tres'
    registered = []
    author, license = 'Propane project', 'LicenseRef-Project-All-Rights-Reserved'
    try:
        if library.exists():
            accounted.add('library.tres')
            ext, props = resource(library)
            if set(props) - {'script', 'DefaultAuthor', 'DefaultLicense', 'Hats'}: raise ValueError('library: unknown properties')
            script = ext[reference(props['script'])]
            if script['path'] != 'res://src/Assets/HatLibrary.cs' or script['type'] != 'Script': raise ValueError('library: wrong script')
            author, license = string(props, 'DefaultAuthor', author), string(props, 'DefaultLicense', license)
            if not author or not license: raise ValueError('library: empty authored defaults')
            if not inventory['licenses'].get(license): raise ValueError('library: unknown default license')
            registered_keys = references(props.get('Hats', '[]'))
            for key, entry in ext.items():
                if key in registered_keys:
                    if entry['type'] != 'Resource': raise ValueError('library: entries must be Resources')
                elif entry['type'] != 'Script' or entry['path'] not in ('res://src/Assets/HatLibrary.cs', 'res://src/Assets/HatDefinition.cs'):
                    raise ValueError('library: unexpected external resource')
            registered = [dependency(root, library, ext[key]) for key in registered_keys]
            if len(registered) != len(set(registered)): raise ValueError('library: duplicate item references')
    except (ValueError, KeyError, OSError, TypeError) as error: errors.append(str(error))
    ids = set()
    for path in registered:
        try:
            relative = path.relative_to(root).as_posix(); accounted.add(relative)
            if path.name != 'item.tres': raise ValueError(f'{relative}: expected item.tres')
            if not relative.startswith(('authored/hats/', 'third_party/')): raise ValueError(f'{relative}: invalid item location')
            ext, props = resource(path)
            if set(props) - {'script', 'Id', 'DisplayName', 'Author', 'License', 'Source', 'Scene'}: raise ValueError(f'{relative}: unknown item properties')
            if ext[reference(props['script'])]['path'] != 'res://src/Assets/HatDefinition.cs': raise ValueError(f'{relative}: wrong item script')
            identity, name = string(props, 'Id'), string(props, 'DisplayName')
            if not ID.fullmatch(identity) or identity != path.parent.name: raise ValueError(f'{relative}: invalid/missing id or folder mismatch')
            if identity.lower() in ids: raise ValueError(f'{relative}: duplicate id {identity}')
            if identity in inventory.get('retired_ids', []): raise ValueError(f'{relative}: retired id {identity}')
            ids.add(identity.lower())
            if not name.strip(): raise ValueError(f'{relative}: missing display name')
            authored = relative.startswith('authored/')
            for key, default in [('Author', author if authored else ''), ('License', license if authored else ''), ('Source', 'owner export' if authored else '')]:
                if not (string(props, key) or default).strip(): raise ValueError(f'{relative}: missing {key}')
            if set(ext) != {reference(props['script']), reference(props['Scene'])}: raise ValueError(f'{relative}: unexpected external resource')
            if not inventory['licenses'].get(string(props, 'License') or (license if authored else '')): raise ValueError(f'{relative}: unknown license identifier')
            scene_entry = ext[reference(props['Scene'])]
            if scene_entry['type'] != 'PackedScene': raise ValueError(f'{relative}: Scene must be a PackedScene')
            scene = dependency(root, path, scene_entry)
            if scene.parent != path.parent or scene.suffix != '.glb': raise ValueError(f'{relative}: scene must be a GLB inside the item folder')
            metrics, dependencies = hat_metrics(scene)
            for asset in [scene, *dependencies]:
                accounted.add(asset.relative_to(root).as_posix())
                sidecar = asset.with_name(asset.name + '.import')
                if sidecar.exists(): accounted.add(sidecar.relative_to(root).as_posix())
            for key in ['triangles', 'surfaces', 'textures', 'decoded_bytes']:
                if metrics[key] > budgets[key]: warnings.append(f'{identity}: {key}={metrics[key]} exceeds recommended {budgets[key]}')
            if any(max(d) > budgets['texture_edge'] for d in metrics['dimensions']): warnings.append(f'{identity}: texture dimensions={metrics["dimensions"]} exceed recommended {budgets["texture_edge"]}')
            items.append({'id':identity,'item':relative,'scene':scene.relative_to(root).as_posix(),'dependencies':[p.relative_to(root).as_posix() for p in [scene,*dependencies]],**metrics})
        except (ValueError, KeyError, IndexError, OSError, TypeError, struct.error) as error: errors.append(f'{path}: {error}')
    uids = {}
    for path in root.rglob('*'):
        if not path.is_file() and not path.is_symlink(): continue
        relative = path.relative_to(root).as_posix()
        if path.is_symlink(): errors.append(f'{relative}: symlinks are not allowed')
        if relative not in accounted: errors.append(f'{relative}: unregistered item or orphan asset')
        if path.suffix == '.import':
            match = re.search(r'^uid="([^"]+)"', path.read_text(), re.M)
            if match:
                if match[1] in uids: errors.append(f'{relative}: duplicate UID with {uids[match[1]]}')
                uids[match[1]] = relative
            elif relative in accounted: errors.append(f'{relative}: missing import UID')
            if not re.search(r'^source_file="' + re.escape('res://assets/' + relative.removesuffix('.import')) + r'"$', path.read_text(), re.M):
                errors.append(f'{relative}: stale import source')
    return {'errors': errors, 'warnings': warnings, 'items': sorted(items, key=lambda item:item['id'])}
