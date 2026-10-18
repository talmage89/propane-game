#!/usr/bin/env python3
"""Artifact script failure propagation, archive layout, and release-only workflow wiring."""
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile

from check_artifacts import ROOT, run, unpack


class ArtifactTests(unittest.TestCase):
    def test_runtime_failures_and_missing_pass_markers_fail_closed(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='artifact-negatives-') as directory:
            root = Path(directory)
            for script in ('echo ERROR: broken; echo PASS', 'echo PASS; exit 3', 'echo no-marker'):
                with self.assertRaisesRegex(RuntimeError, 'Artifact step failed'):
                    run(['bash', '-c', script], root / 'run.log', root, os.environ, marker='PASS')
            run(['bash', '-c', 'echo PASS'], root / 'run.log', root, os.environ, marker='PASS')
            with self.assertRaisesRegex(RuntimeError, 'Artifact step timed out'):
                run(['bash', '-c', 'sleep 10'], root / 'timeout.log', root, os.environ, timeout=.05)

    def test_archive_layout_requires_exactly_one_assembly_and_pack(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='artifact-layout-') as directory:
            root = Path(directory)
            for index, names in enumerate((['Propane.pck'], ['Propane.dll'], ['Propane.dll', 'Propane.pck', 'other/Propane.dll'],
                                           ['data/Propane.dll', 'Propane.pck'])):
                archive = root / 'export.zip'
                with zipfile.ZipFile(archive, 'w') as output:
                    for name in names:
                        output.writestr(name, b'fixture')
                if index < 3:
                    with self.assertRaisesRegex(ValueError, 'expected one game assembly and pack'):
                        unpack(archive, root / str(index))
                else:
                    assembly, pack = unpack(archive, root / str(index))
                    self.assertEqual(assembly.name, 'Propane.dll')
                    self.assertEqual(pack.name, 'Propane.pck')
            # tar extraction rejects archive entries that escape its owned output directory.
            archive = root / 'escape.tar.gz'
            with tarfile.open(archive, 'w:gz') as output:
                output.addfile(tarfile.TarInfo('../escaped'))
            with self.assertRaises(tarfile.FilterError):
                unpack(archive, root / 'escape')
            self.assertFalse((root / 'escaped').exists())

    def test_release_workflow_checks_artifacts_before_publication(self):
        workflow = (ROOT / '.github/workflows/release.yml').read_text()
        script = 'python3 -B tools/release/check_artifacts.py --output build/release-artifact-proof'
        self.assertEqual(workflow.count(script), 1)
        self.assertLess(workflow.index('run: tools/release/build.sh'), workflow.index(script))
        self.assertLess(workflow.index(script), workflow.index('- name: Publish release'))
        self.assertNotIn('check_artifacts.py', (ROOT / '.github/workflows/ci.yml').read_text())
        self.assertNotIn('--unsigned-macos', workflow)


if __name__ == '__main__':
    unittest.main()
