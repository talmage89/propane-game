#!/usr/bin/env python3
"""Fast real-command smoke, plus percentile/comparison and assembly restoration assertions."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import bench
import bench_compare
import source_run


class BenchTests(unittest.TestCase):
    def test_percentiles(self):
        self.assertEqual(bench.percentile([10, 1, 5, 2], .5), 2)
        self.assertEqual(bench.percentile([10, 1, 5, 2], .99), 10)

    def test_vsync_fallback_is_rejected(self):
        bench.validate_log("[bench] PASS")
        with self.assertRaisesRegex(ValueError, "vsync"):
            bench.validate_log("WARNING: Falling back to V-Sync mode Enabled.\n[bench] PASS")
        with self.assertRaisesRegex(ValueError, "failed"):
            bench.validate_log("ERROR: scene failure\n[bench] PASS")

    def test_release_restoration_on_failure(self):
        with tempfile.TemporaryDirectory(dir=bench.ROOT / "build") as scratch:
            root = Path(scratch)
            debug = root / ".godot/mono/temp/bin/Debug"
            release = debug.parent / "Release"
            debug.mkdir(parents=True)
            release.mkdir()
            (debug / "assembly").write_text("Debug")
            (release / "assembly").write_text("Release")
            with patch.object(source_run, "ROOT", root), patch.object(source_run.subprocess, "run"):
                with self.assertRaisesRegex(RuntimeError, "probe"):
                    with source_run.assembly("Release"):
                        self.assertEqual((debug / "assembly").read_text(), "Release")
                        raise RuntimeError("probe")
            self.assertEqual((debug / "assembly").read_text(), "Debug")
            self.assertFalse((root / "build/source-run/lock").exists())

    def test_missing_release_restores_debug(self):
        previous_temp = os.environ.get("TMPDIR")
        with tempfile.TemporaryDirectory(dir=bench.ROOT / "build") as scratch:
            root = Path(scratch)
            debug = root / ".godot/mono/temp/bin/Debug"
            debug.mkdir(parents=True)
            (debug / "assembly").write_text("Debug")
            with patch.object(source_run, "ROOT", root):
                with self.assertRaises(FileNotFoundError):
                    with source_run.assembly("Release", build=False):
                        self.fail("missing Release output was accepted")
            self.assertEqual((debug / "assembly").read_text(), "Debug")
            self.assertFalse((root / "build/source-run/lock").exists())

        self.assertEqual(os.environ.get("TMPDIR"), previous_temp)

    def test_real_smoke(self):
        with tempfile.TemporaryDirectory(prefix="bench-smoke-", dir=bench.ROOT / "build") as scratch:
            path = Path(scratch) / "smoke.json"
            subprocess.run(["make", "--no-print-directory", "bench", "BOTS=2", "TANKS=1", "RENDERED=0",
                            "SECONDS=.5", "WARMUP=.2", "REPEATS=1", "CONFIG=Debug", f"BENCH_ARGS=--output={path} --bot-skill=0.2"], cwd=bench.ROOT, check=True)
            data = json.loads(path.read_text())
            case = data["cases"][0]
            run = case["runs"][0]
            self.assertEqual(run["assembly_config"], "Debug")
            self.assertFalse(run["assembly_optimized"])
            self.assertEqual((run["headless_idle_usec"], run["max_fps"], run["frame_delay_ms"]), (0, 0, 0))
            self.assertEqual(run["bots"], 2)
            self.assertAlmostEqual(run["bot_skill"], 0.2)
            self.assertEqual(data["bot_skill"], 0.2)
            self.assertFalse(run["replay_random"])
            self.assertFalse(data["replay_random"])
            self.assertGreater(run["tanks"], 0)
            self.assertEqual(set(run["rendering"]), set(bench.RENDER_METRICS))
            self.assertEqual(run["rendering"]["visible_draw_calls"], 0)
            self.assertGreater(run["actors"]["AimRay"]["calls"], 0)
            self.assertGreater(run["actors"]["Physics"]["ms_per_tick"], 0)
            self.assertGreater(run["actors"]["MoveAndSlide"]["calls"], 0)
            self.assertGreater(run["actors"]["AimQuery"]["allocated_bytes"], 0)
            self.assertGreater(run["brain_tick_ms"], 0)
            self.assertGreater(run["tank_tick_ms"], 0)
            self.assertGreater(run["jolt_step_upper_ms"], 0)
            self.assertGreater(run["rss_bytes"], 0)
            self.assertEqual(run["physics_ticks"], run["sampled_physics_ticks"])
            self.assertGreaterEqual(run["physics_ticks"], run["requested_physics_ticks"])
            self.assertLess(run["physics_ticks"] - run["requested_physics_ticks"], 12)
            self.assertEqual(set(run["stages"]), {"intact", "disturbed"})
            self.assertGreater(run["stages"]["disturbed"]["effects_ms_per_frame"]["VentProcess"], 0)
            compared = list(bench_compare.compare(path, path))
            self.assertEqual(compared[0][1], compared[0][2])
            subprocess.run(["python3", "-B", "tools/dev/bench_compare.py", str(path), str(path)], cwd=bench.ROOT,
                           stdout=subprocess.DEVNULL, check=True)
            merged = bench_compare.merge([path, path])
            self.assertEqual(len(merged["cases"][0]["runs"]), 2)
            self.assertEqual(merged["cases"][0]["median"], case["median"])
            other = Path(scratch) / "other.json"
            data["replay_random"] = True
            other.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "replay_random"):
                list(bench_compare.compare(path, other))
            with self.assertRaisesRegex(ValueError, "replay_random"):
                bench_compare.merge([path, other])
            data["replay_random"] = False
            data["bot_skill"] = 0.6
            other.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "bot_skill"):
                list(bench_compare.compare(path, other))
            with self.assertRaisesRegex(ValueError, "bot_skill"):
                bench_compare.merge([path, other])
            data["bot_skill"] = 0.2
            data["seconds"] += 1
            other.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "seconds"):
                list(bench_compare.compare(path, other))
            data["seconds"] -= 1
            data["omitted_render_feature"] = "shadows"
            other.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "omitted_render_feature"):
                list(bench_compare.compare(path, other))
            with self.assertRaisesRegex(ValueError, "omitted_render_feature"):
                bench_compare.merge([path, other])
            data["omitted_render_feature"] = "none"
            data["camera_mode"] = "near"
            other.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "camera_mode"):
                list(bench_compare.compare(path, other))
            with self.assertRaisesRegex(ValueError, "camera_mode"):
                bench_compare.merge([path, other])
            data["camera_mode"] = "overview"
            data["cases"][0]["runs"][0]["world_hash"] = "wrong"
            other.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "world_hash"):
                list(bench_compare.compare(path, other))

    def test_replay_smoke(self):
        with tempfile.TemporaryDirectory(prefix="bench-replay-smoke-", dir=bench.ROOT / "build") as scratch:
            path = Path(scratch) / "smoke.json"
            subprocess.run(["make", "--no-print-directory", "bench", "BOTS=2", "TANKS=1", "RENDERED=0",
                            "SECONDS=.5", "WARMUP=.2", "REPEATS=1", "CONFIG=Debug",
                            f"BENCH_ARGS=--output={path} --bot-skill=0.2 --replay-random"], cwd=bench.ROOT, check=True)
            data = json.loads(path.read_text())
            run = data["cases"][0]["runs"][0]
            self.assertTrue(run["replay_random"])
            self.assertTrue(data["replay_random"])
            self.assertEqual(run["frames"] * 2, run["physics_ticks"])
            directory = path.parent / run["evidence"]
            with self.assertRaisesRegex(ValueError, "replay mode"):
                bench.load_case(directory, 2, "Debug", 0.2)

    def test_local_release_keeps_source_benchmark(self):
        with tempfile.TemporaryDirectory(prefix="bench-release-smoke-", dir=bench.ROOT / "build") as scratch:
            path = Path(scratch) / "smoke.json"
            subprocess.run(["make", "--no-print-directory", "bench", "BOTS=0", "TANKS=1", "RENDERED=0",
                            "SECONDS=.5", "WARMUP=.2", "REPEATS=1", "CONFIG=Release", f"BENCH_ARGS=--output={path}"],
                           cwd=bench.ROOT, check=True)
            run = json.loads(path.read_text())["cases"][0]["runs"][0]
            self.assertEqual(run["assembly_config"], "Release")
            self.assertTrue(run["assembly_optimized"])


if __name__ == "__main__":
    (bench.ROOT / "build").mkdir(exist_ok=True)
    unittest.main()
