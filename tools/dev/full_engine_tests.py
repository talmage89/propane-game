#!/usr/bin/env python3
"""Check that deep builds cannot overwrite normal Debug, and launches use the private project and native guard."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import full_engine


class FullEngineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (full_engine.ROOT / 'build').mkdir(exist_ok=True)

    def test_prepare_and_launch(self):
        with tempfile.TemporaryDirectory(dir=full_engine.ROOT / 'build') as directory:
            root = Path(directory)
            for name in ('assets', 'scenes', 'src', 'shaders', '.godot/imported'):
                (root / name).mkdir(parents=True)
            for name in ('icon.svg', 'icon.svg.import', 'tuning.tres', 'project.godot', 'override.cfg', 'Propane.csproj',
                         '.godot/uid_cache.bin', '.godot/global_script_class_cache.cfg'):
                (root / name).write_text(name)
            normal = root / '.godot/mono/temp/bin/Debug/Propane.dll'
            normal.parent.mkdir(parents=True)
            normal.write_text('normal Debug')
            project = root / 'build/test-full-engine/project'
            with patch.object(full_engine, 'ROOT', root), patch.object(full_engine, 'PROJECT', project), \
                    patch.object(full_engine.subprocess, 'run') as build:
                full_engine.prepare('/engine with spaces')
                command = build.call_args.args[0]
                self.assertIn('-p:Optimize=true', command)
                self.assertEqual(command[command.index('-c') + 1], 'Debug')
                self.assertIn('-warnaserror', command)
                for prefix in ('-p:BaseIntermediateOutputPath=', '-p:IntermediateOutputPath=', '-p:OutputPath='):
                    setting = next(argument.removeprefix(prefix) for argument in command if argument.startswith(prefix))
                    self.assertTrue(Path(setting).is_relative_to(project))
                self.assertEqual(normal.read_text(), 'normal Debug')
                self.assertFalse((project / '.godot/mono').is_symlink())
                self.assertEqual((project / 'override.cfg').read_bytes(), (root / 'override.cfg').read_bytes())
                self.assertEqual((project / '.godot/imported').resolve(), root / '.godot/imported')
                self.assertEqual((project / 'Propane.csproj').read_bytes(), (root / 'Propane.csproj').read_bytes())
                self.assertEqual(json.loads((project.parent / 'engine.json').read_text())['godot'], '/engine with spaces')
                with patch.object(full_engine.os, 'execvp') as launch:
                    full_engine.run(['--headless', '--path', '.', 'res://scene.tscn', '--', '--scenario=dials'])
                    self.assertEqual(launch.call_args.args, ('/engine with spaces', ['/engine with spaces', '--headless',
                        '--path', str(project), 'res://scene.tscn', '--', '--scenario=dials', '--full-engine-optimized']))
                    full_engine.run(['--headless', '--path', str(root), 'res://scene.tscn'])
                    self.assertEqual(launch.call_args.args[1][-2:], ['--', '--full-engine-optimized'])
                with self.assertRaises(ValueError):
                    full_engine.run(['res://scene.tscn'])


if __name__ == '__main__':
    unittest.main()
