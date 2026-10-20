#!/usr/bin/env python3
"""Negative proofs for source validation and scaffold rollback; no fixture art is retained."""
import json
import re
import os
import subprocess
import sys
from pathlib import Path
import shutil
import tempfile
import unittest
from hat import library_text, scaffold
from library import ROOT, validate, import_hint


class AssetTests(unittest.TestCase):
    def setUp(self):
        # Scratch stays inside the checkout. Reuse existing prop art; do not author a hat fixture.
        (ROOT / 'build/al-tmp').mkdir(parents=True, exist_ok=True)
        self.scratch = tempfile.TemporaryDirectory(dir=ROOT / 'build/al-tmp')
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name) / 'assets'
        shutil.copytree(ROOT / 'assets', self.root)
        self.inventory = json.loads((ROOT / 'tools/assets/inventory.json').read_text())
        self.budgets = json.loads((ROOT / 'tools/assets/budgets.json').read_text())
        self.model = next(p for p in self.root.rglob('cooler.glb'))

    def result(self): return validate(self.root, self.inventory, self.budgets)

    def clean(self): self.assertEqual(self.result()['errors'], [])

    def error(self, text):
        self.assertTrue(any(text in error for error in self.result()['errors']), self.result())

    def item(self, identity='probe'):
        return scaffold(self.root, identity, 'Probe', self.model, self.inventory)

    def change(self, old, new, identity='probe'):
        path = self.root / f'authored/hats/{identity}/item.tres'
        self.assertIn(old, path.read_text())
        path.write_text(path.read_text().replace(old, new))

    def test_native_import_hints_without_rejecting_ordinary_names(self):
        for name in ['mesh-col', 'mesh_CONVCOLONLY_001', 'mesh-col.001', 'mesh$col', 'mesh-vehicle', 'mesh_colonly', 'mesh-occ', 'mesh-noimp']:
            self.assertTrue(import_hint(name), name)
        for name in ['mesh-color', 'mesh-colors_001', 'cold_mesh', 'ordinary_001']:
            self.assertFalse(import_hint(name), name)

    def test_inventory_and_empty_catalog(self):
        self.clean()
        if not (self.root / 'library.tres').exists(): (self.root / 'library.tres').write_text(library_text([]))
        self.clean()
        self.assertEqual(self.result()['items'], [])

    def test_missing_known_file(self):
        self.model.unlink(); self.error('missing inventoried asset')

    def test_orphan(self):
        (self.root / 'stray.txt').write_text('stray'); self.error('orphan')

    def test_unlicensed_third_party(self):
        self.inventory['families']['quaternius']['license'] = ''; self.error('missing provenance/license')

    def test_unknown_license(self):
        self.item(); self.change('Scene = ExtResource("2")', 'License = "Unknown"\nScene = ExtResource("2")'); self.error("unknown license")

    def test_import_uid(self):
        sidecars = list(self.root.rglob('*.import'))
        a, b = sidecars[:2]
        import re
        uid = re.search(r'^uid="([^"]+)"', a.read_text(), re.M)[1]
        b.write_text(re.sub(r'^uid="[^"]+"', f'uid="{uid}"', b.read_text(), flags=re.M))
        self.error('duplicate UID')

    def test_stale_import(self):
        p = self.model.with_name(self.model.name + '.import')
        p.write_text(p.read_text().replace('source_file=', 'old_source_file=')); self.error('stale import')

    def test_scaffold_sorted_idempotent_preserves_overrides(self):
        self.item('z_probe'); self.item('a_probe')
        scaffold(self.root, 'z_probe', 'New name', None, self.inventory, author='An author', source='source label')
        before = (self.root / 'library.tres').read_bytes()
        scaffold(self.root, 'z_probe', 'New name', None, self.inventory)
        self.assertEqual(before, (self.root / 'library.tres').read_bytes())
        self.assertIn('Author = "An author"', (self.root / 'authored/hats/z_probe/item.tres').read_text())
        self.assertEqual([x['id'] for x in self.result()['items']], ['a_probe', 'z_probe'])
        self.clean()

    def test_registration_orders_ids_across_provenance(self):
        self.item('a_upstream')
        target = self.root / 'third_party/vendor/a_upstream'
        target.parent.mkdir(parents=True)
        shutil.move(str(self.root / 'authored/hats/a_upstream'), target)
        item = target / 'item.tres'
        item.write_text(item.read_text().replace('authored/hats/a_upstream', 'third_party/vendor/a_upstream').replace(
            'Scene = ExtResource("2")', 'Author = "Upstream"\nLicense = "CC0-1.0"\nSource = "Upstream source"\nScene = ExtResource("2")'))
        (self.root / 'library.tres').write_text(library_text(['third_party/vendor/a_upstream/item.tres']))
        self.item('z_owner')
        self.clean()
        text = (self.root / 'library.tres').read_text()
        self.assertLess(text.index('third_party/vendor/a_upstream/item.tres'), text.index('authored/hats/z_owner/item.tres'))

    def test_scaffold_rejects_invalid_ids_without_mutation(self):
        for identity in ['', 'UPPER', '../escape', 'a.b', 'a b', '帽子', 'x' * 65]:
            with self.subTest(identity=identity), self.assertRaises(ValueError): self.item(identity)
        self.assertEqual(self.result()['items'], []); self.clean()

    def test_resource_uids_survive_scaffold(self):
        self.item()
        paths = [self.root / 'library.tres', self.root / 'authored/hats/probe/item.tres']
        for index, path in enumerate(paths):
            text = path.read_text(); first, rest = text.split('\n', 1)
            path.write_text(first[:-1] + f' uid="uid://fixture{index}"]\n' + rest)
        scaffold(self.root, 'probe', 'New display name', None, self.inventory)
        for index, path in enumerate(paths):
            self.assertIn(f'uid="uid://fixture{index}"', path.read_text().splitlines()[0])
        self.clean()

    def test_resource_uids_cannot_collide_with_imports(self):
        self.item()
        uid = re.search(r'^uid="([^"]+)"', self.model.with_name(self.model.name + '.import').read_text(), re.M)[1]
        path = self.root / 'authored/hats/probe/item.tres'
        first, rest = path.read_text().split('\n', 1)
        path.write_text(first[:-1] + f' uid="{uid}"]\n' + rest)
        self.error('duplicate UID')

    def test_cli_treats_display_name_as_data(self):
        name = 'Name "quoted"; $(not_a_command) `literal`'
        command = ['make', '--no-print-directory', '-C', str(ROOT), 'hat', 'ASSET_ROOT=' + str(self.root), 'ID=cli_probe', 'NAME=' + name, 'MODEL=' + str(self.model)]
        result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn(json.dumps(name), (self.root / 'authored/hats/cli_probe/item.tres').read_text())

    def test_native_wrapper_rejects_engine_errors_and_missing_marker(self):
        stub = Path(self.scratch.name) / 'godot'
        for body, expected in [('echo "[assets] PASS native"; echo "[assets] PASS scaffold hats=1"', 0), ('echo "[assets] PASS native"', 1), ('echo "ERROR: fixture"; echo "[assets] PASS native"', 1), ('echo no-marker', 1)]:
            stub.write_text('#!/bin/sh\n' + body + '\n'); stub.chmod(0o755)
            result = subprocess.run([sys.executable, '-B', str(ROOT / 'tools/assets/native.py')], env={**os.environ, 'GODOT': str(stub)}, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            self.assertEqual(bool(result.returncode), bool(expected), result.stdout)

    def test_scaffold_rollback(self):
        self.item()
        original = (self.root / 'library.tres').read_bytes()
        (self.root / 'orphan.txt').write_text('x')
        with self.assertRaisesRegex(ValueError, 'orphan'): self.item('second')
        self.assertEqual((self.root / 'library.tres').read_bytes(), original)
        self.assertFalse((self.root / 'authored/hats/second').exists())

    def test_budget_is_warning_only(self):
        self.item()
        self.budgets['triangles'] = 1
        result = self.result()
        self.assertEqual(result['errors'], [])
        self.assertTrue(any('triangles=132' in w for w in result['warnings']))

    def test_unregistered_item(self):
        self.item(); (self.root / 'library.tres').write_text(library_text([])); self.error('unregistered')

    def test_bad_reference(self):
        self.item(); self.change('model.glb', 'missing.glb'); self.error('missing.glb')

    def test_id_folder_and_retirement(self):
        self.item(); self.change('Id = "probe"', 'Id = "other"'); self.error('folder mismatch')
        self.change('Id = "other"', 'Id = "probe"'); self.inventory['retired_ids'] = ['probe']; self.error('retired id')

    def test_duplicate_registration(self):
        self.item()
        p = self.root / 'library.tres'; p.write_text(p.read_text().replace('Hats = [ExtResource("2")]', 'Hats = [ExtResource("2"), ExtResource("2")]'))
        self.error('duplicate item')

    def test_duplicate_id(self):
        self.item()
        target = self.root / 'third_party/probe'
        shutil.copytree(self.root / 'authored/hats/probe', target)
        item = target / 'item.tres'
        item.write_text(item.read_text().replace('authored/hats/probe', 'third_party/probe'))
        (self.root / 'library.tres').write_text(library_text(['authored/hats/probe/item.tres', 'third_party/probe/item.tres']))
        self.error('duplicate id')

    def test_third_party_item_requires_provenance(self):
        self.item()
        target = self.root / 'third_party/probe'
        shutil.move(str(self.root / 'authored/hats/probe'), target)
        p = target / 'item.tres'; p.write_text(p.read_text().replace('authored/hats/probe', 'third_party/probe'))
        (self.root / 'library.tres').write_text(library_text(['third_party/probe/item.tres']))
        self.error('missing Author')

    def test_path_escape_and_symlink(self):
        self.item(); self.change('model.glb', '../probe/model.glb'); self.error('traversal')
        self.change('../probe/model.glb', 'model.glb')
        p = self.root / 'authored/hats/probe/model.glb'; p.unlink(); p.symlink_to(self.model)
        self.error('symlink')

    def test_invalid_glb_and_forbidden_content(self):
        self.item()
        p = self.root / 'authored/hats/probe/model.glb'; original = p.read_bytes()
        p.write_bytes(b'bad'); self.error('GLB header')
        # A same-size mutation of the JSON proves static-content rejection without generating new art.
        p.write_bytes(original.replace(b'"meshes":', b'"skins": ', 1)); self.error('static glTF')

    def test_duplicate_and_unknown_properties(self):
        self.item(); self.change('Id = "probe"', 'Id = "probe"\nId = "probe"'); self.error('duplicate Id')
        self.change('Id = "probe"\nId = "probe"', 'Id = "probe"\nUnexpected = true'); self.error('unknown item properties')


if __name__ == '__main__':
    unittest.main()
