#!/usr/bin/env python3
"""Prove headless wrappers retain child failure diagnostics."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class WorldCheckTests(unittest.TestCase):
    def run_check(self, failure):
        with tempfile.TemporaryDirectory(prefix="wptest-world-test-") as tmp:
            scratch = Path(tmp)
            fake = scratch / "godot"
            fake.write_text(
                "#!/usr/bin/env bash\n"
                "for arg in \"$@\"; do\n"
                "  if [[ $arg == --$FAILURE-world-plans=* ]]; then\n"
                "    echo 'child shutdown backtrace sentinel' >&2\n"
                "    exit 134\n"
                "  fi\n"
                "done\n"
                'for formation in Rows Square Triangular Cluster; do echo "[world] hash fixture fences 1 formation $formation"; done\n'
                'for segment in $(seq 1 60); do echo "[segments] hash run 1 segment $segment fixture"; done\n'
            )
            fake.chmod(0o755)
            env = dict(os.environ, GODOT=str(fake), TMPDIR=tmp, FAILURE=failure)
            result = subprocess.run(
                ["bash", str(Path(__file__).with_name("check_world.sh"))],
                env=env, capture_output=True, text=True, check=False,
            )
            logs = list(scratch.glob("wptest-world-*"))
            if failure:
                self.assertEqual(result.returncode, 134, result.stderr)
                self.assertIn("child shutdown backtrace sentinel", result.stderr)
                self.assertIn("status 134", result.stderr)
                self.assertEqual(len(logs), 1)
                self.assertIn(str(logs[0]), result.stderr)
                self.assertTrue(list(logs[0].glob("*.log")))
            else:
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("agree for all four formations", result.stdout)
                self.assertEqual(logs, [])

    def test_write_failure(self):
        self.run_check("write")

    def test_hash_failure(self):
        self.run_check("hash")

    def test_success_cleans_scratch(self):
        self.run_check("")


class DeployLogTests(unittest.TestCase):
    def test_export_failure_prints_tail_after_earlier_errors(self):
        with tempfile.TemporaryDirectory(prefix="wptest-deploy-test-") as tmp:
            project = Path(tmp)
            script = project / "tools" / "server" / "deploy.sh"
            script.parent.mkdir(parents=True)
            script.write_text((Path(__file__).parents[1] / "server" / "deploy.sh").read_text())
            commands = project / "commands"
            commands.mkdir()
            fake = commands / "godot"
            fake.write_text(
                "#!/usr/bin/env bash\n"
                "echo 'ERROR: earlier export error'\n"
                "for line in $(seq 1 100); do echo \"export output $line\"; done\n"
                "echo 'child shutdown backtrace sentinel' >&2\n"
                "exit 134\n"
            )
            git = commands / "git"
            git.write_text("#!/usr/bin/env bash\necho fixture\n")
            ssh = commands / "ssh"
            ssh.write_text("#!/usr/bin/env bash\ntouch \"$TMPDIR/ssh-called\"\nexit 99\n")
            for command in (fake, git, ssh):
                command.chmod(0o755)
            env = dict(os.environ, TMPDIR=tmp, PATH=f"{commands}{os.pathsep}{os.environ['PATH']}")
            result = subprocess.run(
                ["bash", str(script), "fixture.invalid", "unused", str(fake)],
                env=env, capture_output=True, text=True, check=False,
            )
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn("ERROR: earlier export error", result.stderr)
            self.assertIn("child shutdown backtrace sentinel", result.stderr)
            self.assertFalse((project / "ssh-called").exists(), "failed export attempted deployment")


if __name__ == "__main__":
    unittest.main()
