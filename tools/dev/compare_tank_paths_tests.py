#!/usr/bin/env python3
"""Trajectory checks must remove sampling skew without concealing physical drift."""
import tempfile
import unittest
from pathlib import Path
from compare_tank_paths import compare, interpolate, load


def trajectory(times, offset=0):
    samples = [(time, True, {"a": ("V", (time * 20 + offset, 0, 0))}) for time in times]
    samples.append((1.5, False, {"a": ("V", (30 + offset, 0, 0))}))
    return samples


class PathsTests(unittest.TestCase):
    def test_asynchronous_fast_motion_aligns(self):
        first = trajectory([i / 20 for i in range(21)])
        other = trajectory([.025 + i / 20 for i in range(21)])
        _, _, vent = compare([first, other])
        self.assertGreater(vent["pairs"], 15)
        self.assertLess(vent["max"], 1e-10)

    def test_real_offset_remains_visible(self):
        _, _, vent = compare([trajectory([i / 20 for i in range(21)]),
                              trajectory([.025 + i / 20 for i in range(21)], offset=2)])
        self.assertAlmostEqual(vent["p95"], 2)

    def test_gaps_and_lifecycle_changes_are_not_interpolated(self):
        samples = [(0, True, {"a": ("I", (0, 0, 0))}), (.1, True, {"a": ("V", (1, 0, 0))}),
                   (1, True, {"a": ("V", (10, 0, 0))})]
        self.assertEqual(interpolate(samples, [0, .1, 1], .05, .2), {})
        self.assertEqual(interpolate(samples, [0, .1, 1], .5, .2), {})

    def test_final_disagreement_and_missing_completion_fail(self):
        first = trajectory([0, .1])
        other = trajectory([0, .1])
        other[-1] = (1.5, False, {})
        with self.assertRaises(ValueError):
            compare([first, other])
        with self.assertRaises(ValueError):
            compare([first[:-1], first])

    def test_empty_and_duplicate_wall_samples(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bot.log"
            path.write_text("tank-sample time 1 playing True clock 5 n 1 a:V:0,0,0\n"
                            "tank-sample time 1 playing True clock 5 n 1 a:V:2,0,0\n"
                            "tank-sample time 2 playing False clock 0 n 0\n")
            samples = load(path)
            self.assertEqual(samples[0][2]["a"][1], (2, 0, 0))
            self.assertEqual(samples[1][2], {})

    def test_malformed_samples_fail(self):
        for suffix in ("n 2 a:V:0,0,0", "n 1 a:V:nan,0,0", "n 1 a:X:0,0,0", "n 2 a:V:0,0,0 a:V:1,0,0"):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "bot.log"
                path.write_text(f"tank-sample time 1 playing True clock 5 {suffix}\n")
                with self.assertRaises(ValueError):
                    load(path)


if __name__ == "__main__":
    unittest.main()
