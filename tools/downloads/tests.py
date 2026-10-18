#!/usr/bin/env python3
"""Offline integration tests: real installers/verifier, local archives and a fixture-only curl."""
import hashlib
import io
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
EDITOR = 'Godot_v4.7.2-stable_mono_linux_x86_64'
TEMPLATES = 'Godot_v4.7.2-stable_mono_export_templates.tpz'
SIGNER = 'apple-codesign-0.29.0-x86_64-unknown-linux-musl'


class Downloads(unittest.TestCase):
    def setUp(self):
        (ROOT / 'build').mkdir(exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix='s1-fixtures-', dir=ROOT / 'build')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.project = self.root / 'project'
        for name in ('tools/downloads', 'tools/release', '.github/actions/setup-godot'):
            (self.project / name).mkdir(parents=True)
        for name in ('tools/downloads/verify.py', 'tools/release/install_templates.sh',
                     'tools/release/install_signer.sh', '.github/actions/setup-godot/install.sh'):
            shutil.copyfile(ROOT / name, self.project / name)
        self.fixtures = self.root / 'fixtures'
        self.fixtures.mkdir()
        self.cache = self.root / 'cache'
        self.cache.mkdir()
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.env = {**os.environ, 'PATH': f"{self.bin}:{os.environ['PATH']}",
                    'GITHUB_ENV': str(self.root / 'github-env'), 'TMPDIR': str(self.root),
                    'XDG_DATA_HOME': str(self.root / 'data'), 'TEMPLATE_CACHE': str(self.cache)}
        # Never access the network: only known fixture basenames may be copied.
        self.script('curl', f'''#!/usr/bin/env python3
import pathlib, shutil, sys
args = sys.argv[1:]
with open({str(self.root / 'curl-calls')!r}, 'a') as log: log.write(args[-1] + '\\n')
source = pathlib.Path({str(self.fixtures)!r}) / args[-1].rsplit('/', 1)[-1]
if not source.exists(): sys.exit(23)
shutil.copyfile(source, args[args.index('-o') + 1])
''')
        # Exercise the CI/Linux layout even on a Mac; keep every installation inside this fixture.
        self.script('uname', '#!/bin/sh\necho Linux\n')
        self.script('lipo', '#!/usr/bin/env bash\ncp "$1" "$5"\n')
        with zipfile.ZipFile(self.fixtures / (EDITOR + '.zip'), 'w') as archive:
            info = zipfile.ZipInfo(EDITOR + '/Godot_v4.7.2-stable_mono_linux.x86_64')
            info.external_attr = 0o100755 << 16
            archive.writestr(info, b'#!/bin/sh\necho fixture-editor\n')
        mac = io.BytesIO()
        with zipfile.ZipFile(mac, 'w') as archive:
            for kind in ('debug', 'release'):
                archive.writestr(f'macos_template.app/Contents/MacOS/godot_macos_{kind}.universal', b'mac')
        with zipfile.ZipFile(self.fixtures / TEMPLATES, 'w') as archive:
            archive.writestr('templates/macos.zip', mac.getvalue())
            for name in ('version.txt', 'icudt_godot.dat', 'linux_release.x86_64', 'linux_debug.x86_64',
                         'windows_release_x86_64.exe', 'windows_debug_x86_64.exe',
                         'windows_release_x86_64_console.exe', 'windows_debug_x86_64_console.exe'):
                archive.writestr('templates/' + name, b'template')
        with tarfile.open(self.fixtures / (SIGNER + '.tar.gz'), 'w:gz') as archive:
            info = tarfile.TarInfo(SIGNER + '/rcodesign')
            data = b'#!/bin/sh\necho fixture-signer\n'
            info.size, info.mode = len(data), 0o755
            archive.addfile(info, io.BytesIO(data))
        for name in ('LICENSE.txt', 'COPYRIGHT.txt'):
            (self.fixtures / name).write_bytes(b'notice\n')
        self.pins = self.project / 'tools/downloads/pins.txt'
        self.lines = []
        for path in self.fixtures.iterdir():
            algorithm = 'sha256' if path.name.startswith(SIGNER) else 'sha512'
            version = '0.29.0' if algorithm == 'sha256' else '4.7.2'
            self.lines.append(f'{version} {path.name} {algorithm} {hashlib.new(algorithm, path.read_bytes()).hexdigest()}\n')
        self.pins.write_text(''.join(self.lines))

    def script(self, name, text):
        path = self.bin / name
        path.write_text(text)
        path.chmod(0o755)

    def run_command(self, *args):
        return subprocess.run(args, cwd=self.project, env=self.env, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)

    def install(self, kind):
        scripts = {'editor': ('.github/actions/setup-godot/install.sh', '4.7.2', str(self.cache)),
                   'templates': ('tools/release/install_templates.sh', '4.7.2'),
                   'signer': ('tools/release/install_signer.sh', '0.29.0', str(self.cache))}
        return self.run_command('bash', *scripts[kind])

    def verify(self, filename, path, url=None, version='4.7.2'):
        return self.run_command('python3', 'tools/downloads/verify.py', version, filename, str(path),
                                *([url] if url else []))

    def test_every_installer_rejects_missing_wrong_and_tampered_pins_before_use(self):
        for kind, filename in [('editor', EDITOR + '.zip'), ('templates', TEMPLATES), ('signer', SIGNER + '.tar.gz')]:
            with self.subTest(kind=kind):
                # No pin must fail before downloading anything.
                self.pins.write_text(''.join(line for line in self.lines if filename not in line))
                result = self.install(kind)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('Missing or duplicate pin for ' + filename, result.stdout)
                self.assertFalse((self.root / 'curl-calls').exists())
                self.pins.write_text(''.join(self.lines))
                # Fresh downloaded bytes must match before the cache can be published.
                fixture = self.fixtures / filename
                original = fixture.read_bytes()
                fixture.write_bytes(b'swapped download')
                result = self.install(kind)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('Checksum mismatch for ' + filename, result.stdout)
                self.assertIn('expected ', result.stdout)
                self.assertIn('actual ', result.stdout)
                self.assertFalse((self.cache / filename).exists())
                self.assertEqual(list(self.cache.glob('*.part')), [])
                fixture.write_bytes(original)
                result = self.install(kind)
                self.assertEqual(result.returncode, 0, result.stdout)
                self.assertIn('Verified ' + filename, result.stdout)
                (self.root / 'curl-calls').unlink()
                # Missing pins also fail with a valid archive and an existing installation.
                self.pins.write_text(''.join(line for line in self.lines if filename not in line))
                result = self.install(kind)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('Missing or duplicate pin for ' + filename, result.stdout)
                self.assertFalse((self.root / 'curl-calls').exists())
                self.pins.write_text(''.join(self.lines))
                # A poisoned archive is rejected even though its previous installation exists.
                (self.cache / filename).write_bytes(b'poisoned cache')
                result = self.install(kind)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('Checksum mismatch for ' + filename, result.stdout)
                self.assertFalse((self.root / 'curl-calls').exists())
                (self.cache / filename).unlink()

    def test_download_failures_propagate_without_exporting_tools_or_caching_partials(self):
        for kind, filename in [('editor', EDITOR + '.zip'), ('templates', TEMPLATES), ('signer', SIGNER + '.tar.gz')]:
            with self.subTest(kind=kind):
                fixture = self.fixtures / filename
                unavailable = fixture.with_suffix('.unavailable')
                fixture.rename(unavailable)
                try:
                    result = self.install(kind)
                    self.assertEqual(result.returncode, 23, result.stdout)
                    self.assertFalse((self.root / 'github-env').exists())
                    self.assertFalse((self.cache / filename).exists())
                    self.assertEqual(list(self.cache.glob('*.part')), [])
                finally:
                    unavailable.rename(fixture)

    def test_extracted_caches_are_recreated_from_verified_archives_without_network(self):
        for kind in ('editor', 'signer', 'templates'):
            result = self.install(kind)
            self.assertEqual(result.returncode, 0, result.stdout)
        installed = [self.cache / EDITOR / 'Godot_v4.7.2-stable_mono_linux.x86_64',
                     self.cache / SIGNER / 'rcodesign',
                     self.root / 'data/godot/export_templates/4.7.2.stable.mono/linux_release.x86_64',
                     self.root / 'data/godot/export_templates/4.7.2.stable.mono/macos.zip']
        original = [path.read_bytes() for path in installed[:3]]
        for path in installed:
            path.write_bytes(b'poisoned installed file')
        self.script('curl', '#!/bin/sh\nexit 99\n')
        for kind in ('editor', 'signer', 'templates'):
            result = self.install(kind)
            self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual([path.read_bytes() for path in installed[:3]], original)
        with zipfile.ZipFile(installed[3]) as archive:
            self.assertEqual(archive.read('macos_template.app/Contents/MacOS/godot_macos_release.arm64'), b'mac')
        exported = (self.root / 'github-env').read_text()
        self.assertIn('GODOT=' + str(installed[0]), exported)
        self.assertIn('RCODESIGN=' + str(installed[1]), exported)

    def test_notices_and_verifier_fail_closed(self):
        for filename in ('LICENSE.txt', 'COPYRIGHT.txt'):
            destination = self.root / ('download-' + filename)
            result = self.verify(filename, destination, 'file:///' + filename)
            self.assertEqual(result.returncode, 0, result.stdout)
            destination.write_bytes(b'tampered notice')
            result = self.verify(filename, destination)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(hashlib.sha512(b'notice\n').hexdigest(), result.stdout)
            self.assertIn(hashlib.sha512(b'tampered notice').hexdigest(), result.stdout)
            self.assertIn(filename, result.stdout)
        self.pins.unlink()
        result = self.verify('LICENSE.txt', self.fixtures / 'LICENSE.txt')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Missing or duplicate pin for LICENSE.txt', result.stdout)
        for lines in ('', next(line for line in self.lines if ' LICENSE.txt ' in line) * 2, '4.7.2 LICENSE.txt md5 deadbeef\n',
                      '4.7.2 LICENSE.txt sha512 invalid\n'):
            self.pins.write_text(lines)
            self.assertNotEqual(self.verify('LICENSE.txt', self.fixtures / 'LICENSE.txt').returncode, 0)
        self.pins.write_text(''.join(self.lines))
        self.assertNotEqual(self.verify('LICENSE.txt', self.fixtures / 'LICENSE.txt', version='future').returncode, 0)
        self.assertNotEqual(self.verify('LICENSE.txt', self.root / 'absent').returncode, 0)
        result = self.verify('LICENSE.txt', self.root / 'failed-download', 'file:///absent.txt')
        self.assertEqual(result.returncode, 23, result.stdout)
        self.assertFalse((self.root / 'failed-download').exists())
        self.assertEqual(list(self.root.glob('*.part')), [])

    def test_repository_versions_have_complete_pins_and_workflows_use_verified_archives(self):
        pins = {(v, f): (a, h) for v, f, a, h in
                (line.split() for line in (ROOT / 'tools/downloads/pins.txt').read_text().splitlines()
                 if line.strip() and not line.startswith('#'))}
        for workflow in ('ci', 'release'):
            text = (ROOT / f'.github/workflows/{workflow}.yml').read_text()
            version = re.search(r'^  GODOT_VERSION: (.+)$', text, re.M).group(1)
            for filename in (f'Godot_v{version}-stable_mono_linux_x86_64.zip',
                             f'Godot_v{version}-stable_mono_export_templates.tpz', 'LICENSE.txt', 'COPYRIGHT.txt'):
                self.assertEqual(pins[version, filename][0], 'sha512')
        version = re.search(r'^  APPLE_CODESIGN_VERSION: (.+)$', text, re.M).group(1)
        self.assertEqual(pins[version, f'apple-codesign-{version}-x86_64-unknown-linux-musl.tar.gz'][0], 'sha256')
        self.assertIn('tools/release/install_signer.sh "$APPLE_CODESIGN_VERSION" "$cache"', text)
        self.assertNotIn('curl ', text)
        action = (ROOT / '.github/actions/setup-godot/action.yml').read_text()
        self.assertIn('path: ~/.cache/propane-ci/Godot_v${{ inputs.godot-version }}-stable_mono_linux_x86_64.zip', action)


if __name__ == '__main__':
    unittest.main()
