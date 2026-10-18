#!/usr/bin/env python3
"""Deployment identity negatives run in private repositories; ssh/export are stubs, never remote actions."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class DeployTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='deploy-test-')
        self.addCleanup(self.directory.cleanup)
        scratch = Path(self.directory.name)
        self.project = scratch / 'project'
        (self.project / 'tools/server').mkdir(parents=True)
        for name in ('deploy.sh', 'Dockerfile', 'docker-compose.yml'):
            shutil.copy(ROOT / 'tools/server' / name, self.project / 'tools/server' / name)
        (self.project / 'project.godot').write_text('[application]\nconfig/version="1.2.3"\n')
        (self.project / '.gitignore').write_text('build/\nbuild_id.txt\n')
        fake = scratch / 'bin'
        fake.mkdir()
        self.log = scratch / 'commands.log'
        for name, script in {
            'godot': 'mkdir -p "${@: -1}"/../data_Propane_linuxbsd_x86_64\ntouch "${@: -1}" "${@: -1}"/../Propane.pck\n',
            'dotnet': 'if [[ "$*" == *" inventory "* && "${DEPLOY_TEST_FAIL_INVENTORY:-0}" == 1 ]]; then exit 1; fi\n',
            'ssh': 'cat >/dev/null\n',
        }.items():
            path = fake / name
            # Every external invocation is recorded; a rejected identity must not reach any of these.
            path.write_text('#!/bin/bash\nprintf "%s\\n" "$0 $*" >>"$DEPLOY_TEST_LOG"\n' + script)
            path.chmod(0o755)
        self.env = {**os.environ, 'PATH': str(fake) + ':' + os.environ['PATH'], 'DEPLOY_TEST_LOG': str(self.log),
                    'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_SYSTEM': os.devnull,
                    'GIT_AUTHOR_NAME': 'Test', 'GIT_AUTHOR_EMAIL': 'test@example.invalid',
                    'GIT_COMMITTER_NAME': 'Test', 'GIT_COMMITTER_EMAIL': 'test@example.invalid'}
        self.git('init', '-q', '-b', 'main')
        self.commit()
        self.git('tag', 'v1.2.3')
        self.git('switch', '-q', '-c', 'work')

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.project, env=self.env, check=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True).stdout

    def commit(self):
        self.git('add', '-A')
        self.git('commit', '-qm', 'Fixture')

    def deploy(self):
        return subprocess.run(['bash', 'tools/server/deploy.sh', 'example.invalid', 'server', 'godot'],
                              cwd=self.project, env=self.env, capture_output=True, text=True, timeout=30)

    def refuses(self):
        result = self.deploy()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('Refusing deployment', result.stderr)
        self.assertFalse(self.log.exists(), 'refusal invoked an exporter or ssh')
        self.assertFalse((self.project / 'build_id.txt').exists())

    def test_dirty_release_is_rejected(self):
        (self.project / 'untracked').touch()
        self.refuses()

    def test_unreleased_commit_is_rejected(self):
        (self.project / 'new').touch()
        self.commit()
        self.refuses()

    def test_tag_outside_main_is_rejected(self):
        (self.project / 'project.godot').write_text('[application]\nconfig/version="1.2.4"\n')
        self.commit()
        self.git('tag', 'v1.2.4')
        self.refuses()

    def test_clean_release_runs_inventory_before_ssh(self):
        result = self.deploy()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        commands = self.log.read_text()
        self.assertLess(commands.index(' inventory '), commands.index('/ssh '))
        self.assertIn('Deploying v1.2.3', result.stdout)
        self.assertFalse((self.project / 'build_id.txt').exists())

    def test_failed_inventory_never_reaches_ssh(self):
        self.env['DEPLOY_TEST_FAIL_INVENTORY'] = '1'
        result = self.deploy()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn(' inventory ', self.log.read_text())
        self.assertNotIn('/ssh ', self.log.read_text())
        self.assertFalse((self.project / 'build_id.txt').exists())


if __name__ == '__main__':
    unittest.main()
