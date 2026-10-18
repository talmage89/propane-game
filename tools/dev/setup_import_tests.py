#!/usr/bin/env python3
"""Import diagnostics must fail closed, even when Godot returns success."""
from pathlib import Path
import subprocess
import tempfile
import unittest

from setup_import import BOOTSTRAP, failures

ROOT = Path(__file__).resolve().parents[2]


class ImportTests(unittest.TestCase):
    def test_only_exact_bootstrap_without_an_assembly_is_tolerated(self):
        self.assertEqual(failures(BOOTSTRAP, True), [])
        self.assertEqual(failures(BOOTSTRAP, False), [BOOTSTRAP])
        for line in ('ERROR: missing texture', 'SCRIPT ERROR: Parse Error: broken script',
                     BOOTSTRAP.replace('ShutdownGuard.cs', 'Other.cs')):
            self.assertEqual(failures(line, True), [line])
            self.assertEqual(failures(BOOTSTRAP + '\n' + line, True), [line])

    def test_process_and_log_failures_propagate_with_context(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='import-negative-') as directory:
            root = Path(directory)
            engine = root / 'godot'
            for output, status, expected in ((BOOTSTRAP, 0, 0), ('ERROR: injected bad resource\n  useful context', 0, 1),
                                             ('import crashed', 3, 1), (BOOTSTRAP, 1, 1)):
                engine.write_text('#!/bin/sh\ncat <<\'LOG\'\n' + output + '\nLOG\nexit ' + str(status) + '\n')
                engine.chmod(0o755)
                result = subprocess.run(['python3', '-B', str(ROOT / 'tools/dev/setup_import.py'), str(engine)],
                                        cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
                self.assertEqual(result.returncode, expected, result.stdout)
                self.assertIn(output, (root / 'build/setup-import.log').read_text())
                if expected:
                    self.assertIn(output, result.stdout)
            assembly = root / '.godot/mono/temp/bin/Debug/Propane.dll'
            assembly.parent.mkdir(parents=True)
            assembly.touch()
            engine.write_text('#!/bin/sh\ncat <<\'LOG\'\n' + BOOTSTRAP + '\nLOG\n')
            result = subprocess.run(['python3', '-B', str(ROOT / 'tools/dev/setup_import.py'), str(engine)],
                                    cwd=root, capture_output=True, text=True)
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
