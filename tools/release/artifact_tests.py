#!/usr/bin/env python3
"""Artifact script failure propagation, archive layout, and release-only workflow wiring."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
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

    def test_interruption_reaps_the_exported_child(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='artifact-interrupt-') as directory:
            root = Path(directory)
            child_pid = root / 'child.pid'
            # This exercises the real process/session boundary, not a mocked wait/kill sequence.
            code = ('import os,signal,sys\nfrom pathlib import Path\nimport check_artifacts as artifact\n'
                    'signal.signal(signal.SIGTERM, artifact.interrupted)\n'
                    'root=Path(sys.argv[1])\n'
                    'artifact.run(["bash", "-c", \'echo $$ > "$1"; exec sleep 30\', "bash", str(root / "child.pid")], '
                    'root / "child.log", root, os.environ)\n')
            for interrupt in (signal.SIGINT, signal.SIGTERM):
                child_pid.unlink(missing_ok=True)
                with subprocess.Popen([sys.executable, '-c', code, str(root)], cwd=ROOT / 'tools/release',
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as owner:
                    child_needs_cleanup = False
                    try:
                        deadline = time.monotonic() + 10
                        while (not child_pid.exists() or not child_pid.read_text().strip()) and owner.poll() is None and time.monotonic() < deadline:
                            time.sleep(.01)
                        self.assertTrue(child_pid.exists(), 'child did not start')
                        pid = int(child_pid.read_text())
                        child_needs_cleanup = True
                        owner.send_signal(interrupt)
                        self.assertNotEqual(owner.wait(timeout=15), 0)
                        with self.assertRaises(ProcessLookupError):
                            os.kill(pid, 0)
                        child_needs_cleanup = False
                    finally:
                        if owner.poll() is None:
                            owner.kill()
                            owner.wait()
                        if child_needs_cleanup:
                            try:
                                os.kill(pid, signal.SIGKILL)
                            except ProcessLookupError:
                                pass

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
