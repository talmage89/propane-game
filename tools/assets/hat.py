#!/usr/bin/env python3
"""Register an owner's existing GLB. Idempotent; rollback files if validation fails. Never creates art."""
import argparse
import os
import json
import re
from pathlib import Path
import shutil
from library import ROOT, ID, contained, dependency, references, resource, validate


def library_text(paths, author='Propane project', license='LicenseRef-Project-All-Rights-Reserved', uid=None):
    lines = ['[gd_resource type="Resource" script_class="HatLibrary" load_steps=' + str(2 + len(paths)) + ' format=3' + (f' uid="{uid}"' if uid else '') + ']', '',
             '[ext_resource type="Script" path="res://src/Assets/HatLibrary.cs" id="1"]']
    for index, path in enumerate(sorted(paths), 2):
        lines.append(f'[ext_resource type="Resource" path={json.dumps("res://assets/" + path)} id="{index}"]')
    lines += ['', '[resource]', 'script = ExtResource("1")', 'DefaultAuthor = ' + json.dumps(author),
              'DefaultLicense = ' + json.dumps(license), 'Hats = [' + ', '.join(f'ExtResource("{i}")' for i in range(2, len(paths) + 2)) + ']', '']
    return '\n'.join(lines)


def scaffold(root, identity, name, model, inventory, author=None, license=None, source=None):
    if not ID.fullmatch(identity): raise ValueError('ID must be 1–64 lowercase letters, digits, underscores or hyphens, starting with a letter/digit')
    if not name.strip(): raise ValueError('NAME is required')
    folder = contained(root, f'authored/hats/{identity}')
    destination = folder / 'model.glb'
    model = Path(model).resolve() if model else destination
    if not model.is_file(): raise ValueError(f'drop model.glb in {folder} or pass MODEL=<existing GLB>')
    if model.suffix.lower() != '.glb': raise ValueError('MODEL must be a GLB')
    if destination.exists() and model != destination.resolve(): raise ValueError('model.glb exists; refusing to replace art')
    library = root / 'library.tres'
    paths = set()
    library_uid = None
    default_author, default_license = 'Propane project', 'LicenseRef-Project-All-Rights-Reserved'
    if library.exists():
        match = re.search(r'uid="(uid://[a-z0-9]+)"', library.read_text().splitlines()[0])
        library_uid = match[1] if match else None
        from library import string
        ext, props = resource(library)
        paths = {dependency(root, library, ext[key]).relative_to(root).as_posix() for key in references(props.get('Hats', '[]'))}
        default_author = string(props, 'DefaultAuthor', default_author)
        default_license = string(props, 'DefaultLicense', default_license)
    paths.add(f'authored/hats/{identity}/item.tres')
    lines = ['[gd_resource type="Resource" script_class="HatDefinition" load_steps=3 format=3]', '',
             '[ext_resource type="Script" path="res://src/Assets/HatDefinition.cs" id="1"]',
             f'[ext_resource type="PackedScene" path={json.dumps("res://assets/authored/hats/" + identity + "/model.glb")} id="2"]', '',
             '[resource]', 'script = ExtResource("1")', 'Id = ' + json.dumps(identity), 'DisplayName = ' + json.dumps(name)]
    for key, value in [('Author', author), ('License', license), ('Source', source)]:
        if value is not None: lines.append(key + ' = ' + json.dumps(value))
    lines += ['Scene = ExtResource("2")', '']
    item = folder / 'item.tres'
    # Existing overrides survive repeated registration unless explicitly replaced. Do not silently erase metadata.
    if item.exists():
        match = re.search(r'uid="(uid://[a-z0-9]+)"', item.read_text().splitlines()[0])
        if match: lines[0] = lines[0][:-1] + f' uid="{match[1]}"]'
        from library import string
        _, previous = resource(item)
        for key, value in [('Author', author), ('License', license), ('Source', source)]:
            if value is None and key in previous: lines.insert(-2, key + ' = ' + json.dumps(string(previous, key)))
    previous = {p: p.read_bytes() if p.exists() else None for p in [library, item, destination]}
    created = not folder.exists()
    folder.mkdir(parents=True, exist_ok=True)
    try:
        if model != destination.resolve(): shutil.copyfile(model, destination)
        item.write_text('\n'.join(lines))
        library.write_text(library_text(paths, default_author, default_license, library_uid))
        result = validate(root, inventory, json.loads((ROOT / 'tools/assets/budgets.json').read_text()))
        if result['errors']: raise ValueError('\n'.join(result['errors']))
        return result
    except BaseException:
        for path, data in previous.items():
            if data is None: path.unlink(missing_ok=True)
            else: path.write_bytes(data)
        if created and not any(folder.iterdir()): folder.rmdir()
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--id', default=os.environ.get('AL_HAT_ID', ''))
    parser.add_argument('--name', default=os.environ.get('AL_HAT_NAME', ''))
    parser.add_argument('--model', type=Path, default=Path(os.environ['AL_HAT_MODEL']) if os.environ.get('AL_HAT_MODEL') else None)
    parser.add_argument('--author', default=os.environ.get('AL_HAT_AUTHOR') or None)
    parser.add_argument('--license', default=os.environ.get('AL_HAT_LICENSE') or None)
    parser.add_argument('--source', default=os.environ.get('AL_HAT_SOURCE') or None)
    parser.add_argument('--root', type=Path, default=Path(os.environ.get('AL_HAT_ROOT') or ROOT / 'assets'))
    parser.add_argument('--inventory', type=Path, default=ROOT / 'tools/assets/inventory.json')
    args = parser.parse_args()
    try:
        result = scaffold(args.root.resolve(), args.id, args.name, args.model, json.loads(args.inventory.read_text()), args.author, args.license, args.source)
        for warning in result['warnings']: print('WARNING: ' + warning)
        print(f'PASS: registered {args.id}; {len(result["items"])} hats. Next: make assets-preview')
    except (ValueError, OSError, KeyError) as error:
        parser.exit(1, f'ERROR: {error}\n')


if __name__ == '__main__':
    main()
