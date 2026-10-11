#!/usr/bin/env python3
"""Prove world-check child failures retain their diagnostics and exit status."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class WorldCheckTests(unittest.TestCase):
    def run_check(self, failure):
        with tempfile.TemporaryDirectory(prefix="propane-world-test-") as tmp:
            scratch = Path(tmp)
            fake = scratch / "godot"
            fake.write_text(
                "#!/usr/bin/env bash\n"
                "for arg in \"$@\"; do\n"
                "  if [[ $arg == --$FAILURE-world-plan=* ]]; then\n"
                "    echo 'child shutdown backtrace sentinel' >&2\n"
                "    exit 134\n"
                "  fi\n"
                "done\n"
                "echo '[world] hash fixture'\n"
            )
            fake.chmod(0o755)
            env = dict(os.environ, GODOT=str(fake), TMPDIR=tmp, FAILURE=failure)
            result = subprocess.run(
                ["bash", str(Path(__file__).with_name("check_world.sh"))],
                env=env, capture_output=True, text=True, check=False,
            )
            logs = list(scratch.glob("propane-world-*"))
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


if __name__ == "__main__":
    unittest.main()
