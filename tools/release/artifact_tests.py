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

from check_artifacts import ROOT, native_entry, run, unpack


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
            dist = root / 'dist'
            dist.mkdir()
            game = root / 'game'
            (game / 'data').mkdir(parents=True)
            (game / 'data/Propane.dll').write_bytes(b'fixture assembly')
            (game / 'Propane.pck').write_bytes(b'fixture pack')
            executable = game / 'Propane.x86_64'
            executable.write_text('#!/bin/bash\necho $$ > "$CHILD_PID"\nexec sleep 30\n')
            executable.chmod(0o755)
            with tarfile.open(dist / 'Propane-fixture-linux-x86_64.tar.gz', 'w:gz') as archive:
                archive.add(game, arcname='Propane-fixture-linux-x86_64')
            for platform, prefix, entry in [('windows-x86_64', '', 'Propane.exe'),
                                             ('macos-arm64', 'Propane.app/Contents/Resources/', 'Propane.app/Contents/MacOS/Propane')]:
                with zipfile.ZipFile(dist / f'Propane-fixture-{platform}.zip', 'w') as archive:
                    archive.writestr(prefix + 'data/Propane.dll', b'fixture assembly')
                    archive.writestr(prefix + 'Propane.pck', b'fixture pack')
                    archive.writestr(entry, b'fixture executable')
            fake_bin = root / 'bin'
            fake_bin.mkdir()
            dotnet = fake_bin / 'dotnet'
            dotnet.write_text('#!/bin/sh\nexit 0\n')
            dotnet.chmod(0o755)
            env = {**os.environ, 'PATH': str(fake_bin) + os.pathsep + os.environ['PATH'], 'CHILD_PID': str(child_pid)}
            # Run the actual CLI so removing its signal registration also fails this test. Only metadata
            # inspection is stubbed; the native executable/process/session boundary is real.
            for interrupt in (signal.SIGINT, signal.SIGTERM):
                child_pid.unlink(missing_ok=True)
                command = [sys.executable, str(ROOT / 'tools/release/check_artifacts.py'), '--dist', str(dist),
                           '--output', str(root / f'proof-{interrupt}')]
                with subprocess.Popen(command, cwd=root, env=env,
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

    def test_native_entries_must_exist_and_be_nonempty(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='artifact-entries-') as directory:
            root = Path(directory)
            for platform, pack_path, executable_path in [('linux', 'linux/Propane.pck', 'linux/Propane.x86_64'),
                                                       ('windows', 'windows/Propane.pck', 'windows/Propane.exe'),
                                                       ('macos', 'macos/Contents/Resources/Propane.pck', 'macos/Contents/MacOS/Propane')]:
                pack, executable = root / pack_path, root / executable_path
                executable.parent.mkdir(parents=True, exist_ok=True)
                with self.assertRaisesRegex(ValueError, 'Missing or empty ' + platform):
                    native_entry(platform, pack)
                executable.touch()
                with self.assertRaisesRegex(ValueError, 'Missing or empty ' + platform):
                    native_entry(platform, pack)
                executable.write_bytes(b'entry fixture')
                self.assertEqual(native_entry(platform, pack), executable)

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
