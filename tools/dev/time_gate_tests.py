#!/usr/bin/env python3
"""Exercise timing and failure propagation with tiny fake tools, without launching Godot."""
import json
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import unittest

PROFILER = pathlib.Path(__file__).with_name("time_gate.py").resolve()


class GateTimingTests(unittest.TestCase):
    def test_server_stop_reaches_the_wrapped_engine(self):
        with tempfile.TemporaryDirectory(prefix="wptest-server-stop-") as directory:
            root = pathlib.Path(directory)
            log = root / "steps.jsonl"
            child = 'import time; print("server ready", flush=True); time.sleep(30)'
            command = [sys.executable, str(PROFILER), "--probe", "godot", sys.executable, "-c", child]
            with subprocess.Popen(command, env={**os.environ, "PROPANE_TIMING_LOG": str(log)},
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as process:
                self.assertEqual(process.stdout.readline().strip(), "server ready")
                process.terminate()
                process.communicate(timeout=5)
                self.assertNotEqual(process.returncode, 0)
            row = json.loads(log.read_text())
            self.assertEqual(row["code"], -signal.SIGTERM)

    def test_parallel_steps_and_failures(self):
        with tempfile.TemporaryDirectory(prefix="wptest-timing-") as directory:
            root = pathlib.Path(directory)
            for name in ("godot", "dotnet"):
                tool = root / name
                tool.write_text("#!/bin/sh\nif [ \"${1:-}\" = --fail ]; then exit 7; fi\n"
                                "if [ \"${1:-}\" = --scene ]; then echo '[gate-timing] ready'; fi\n")
                tool.chmod(0o755)
            environment = dict(os.environ, PATH=f"{root}:{os.environ['PATH']}", GODOT=str(root / "godot"))
            (root / "Makefile").write_text(".PHONY: gate a b c\ngate: a b c\na:\n\t\"$(GODOT)\" --scene\n"
                                           "b:\n\tdotnet build\nc:\n\tpython3 -c 'print(123)'\n")
            output = root / "pass"
            result = subprocess.run([sys.executable, str(PROFILER), f"--output={output}", "--", "make", "-j4", "gate"],
                                    cwd=root, env=environment, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            rows = [json.loads(line) for line in (output / "steps.jsonl").read_text().splitlines()]
            self.assertCountEqual([row["command"][0] for row in rows], ["godot", "dotnet", "python3"])
            self.assertTrue(all(row["finished"] >= row["started"] for row in rows))
            total = json.loads((output / "total.json").read_text())
            self.assertGreaterEqual(total["finished"], total["started"])
            self.assertEqual(len(total["load"]), 3)
            scene = next(row for row in rows if row["command"][0] == "godot")
            self.assertIsNotNone(scene["startup"])
            self.assertGreaterEqual(scene["work_and_exit"], 0)
            self.assertTrue(all(row["startup"] is None for row in rows if row is not scene))
            self.assertEqual(json.loads((output / "total.json").read_text())["code"], 0)

            (root / "Makefile").write_text("gate:\n\t\"$(GODOT)\" --fail\n")
            output = root / "failure"
            result = subprocess.run([sys.executable, str(PROFILER), f"--output={output}", "--", "make", "gate"],
                                    cwd=root, env=environment, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            row = json.loads((output / "steps.jsonl").read_text())
            self.assertEqual(row["code"], 7)
            self.assertEqual(json.loads((output / "total.json").read_text())["code"], result.returncode)


if __name__ == "__main__":
    unittest.main()
