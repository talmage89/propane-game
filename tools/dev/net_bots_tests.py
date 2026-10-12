#!/usr/bin/env python3
"""Prove the launcher propagates both checker failures, without launching Godot."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class LauncherChecks(unittest.TestCase):
    def run_launcher(self, fault, *flags):
        with tempfile.TemporaryDirectory(prefix="nas-launcher-") as directory:
            root = Path(directory)
            fake = root / "godot"
            fake.write_text('''#!/usr/bin/env python3
import os
import sys
if "--server" in sys.argv:
    print("match 1: bank audit initial=0 map=1 drops=0 spills=0 banked=1 expected=1 ok=True")
else:
    assert "--tank-check" in sys.argv
    second = "--name=Bot2" in sys.argv
    fault = os.environ["NAS_FAULT"]
    if fault == "process" and second:
        sys.exit(7)
    if fault == "engine" and second:
        print("ERROR: rifle contact failure")
    for tick in range(61):
        phase = "P" if tick < 40 else "E"
        x = 2 if fault == "tank" and second else 0
        print(f"tank-check match 1 time {tick / 10:.6f} phase {phase} final {tick == 60} length 4 n 1 a:I:R:{x},0,0")
    scores = "2=0 3=1" if fault == "score" and second else "2=1 3=0"
    print(f"finished match 1; final scores {scores}")
''')
            fake.chmod(0o755)
            # Only orchestrator startup sleeps are elided. The fake emits complete captured data immediately.
            command = ["bash", "-c", 'sleep() { :; }; export -f sleep; bash "$@"', "nas-tests",
                       str(Path(__file__).with_name("net_bots.sh")), "-g", str(fake), "-n", "2", "-l", "4",
                       "-o", str(root / "logs"), *flags]
            return subprocess.run(command, env={**os.environ, "NAS_FAULT": fault}, capture_output=True, text=True)

    def test_pass_and_both_independent_failures(self):
        for fault, status, output in (("none", 0, "PASS tanks"), ("tank", 1, "FAIL tanks"),
                                      ("score", 1, "individual scores disagree"), ("engine", 1, "FAIL engine or protocol"), ("process", 1, "no tank-check samples")):
            with self.subTest(fault=fault):
                result = self.run_launcher(fault)
                self.assertEqual(result.returncode, status, result.stdout + result.stderr)
                self.assertIn(output, result.stdout + result.stderr)

    def test_skip_flag_and_process_failure_preserved(self):
        self.assertEqual(self.run_launcher("tank", "-k").returncode, 0)
        self.assertEqual(self.run_launcher("process", "-k").returncode, 7)

    def test_remote_requires_separate_audit(self):
        result = self.run_launcher("none", "-S", "localhost:24680")
        self.assertEqual(result.returncode, 2)
        self.assertIn("remote server audit log", result.stderr)


if __name__ == "__main__":
    unittest.main()
