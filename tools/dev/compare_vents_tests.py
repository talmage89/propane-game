#!/usr/bin/env python3
"""Regression checks for the puncture-race verifier's failure paths."""
import tempfile
import unittest
from pathlib import Path

from compare_vents import compare


class VentComparisonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="wpa1-vent-tests-")
        self.addCleanup(self.temp.cleanup)
        self.paths = [Path(self.temp.name) / f"bot{i}.log" for i in range(2)]
        self.write()

    def write(self, *, params=None, shift=0, complete=True, contact_tick=180, samples=120):
        for i, path in enumerate(self.paths):
            metadata = params if i and params else "owner 2 epoch 2 hole 0.1,0.2,0 normal 1,0,0 lean 0.8,0,0.6 swirl 1"
            lines = [f"race params {metadata}", f"race contact tick {contact_tick} hole {i},0,0 swirl 1"]
            lines += [f"race path time {tick / 10} pos {tick / 10 + (shift if i else 0)},0,0" for tick in range(samples)]
            if complete:
                lines.append("puncture-race: assertions passed")
            path.write_text('\n'.join(lines))

    def test_identical_parameters_and_aligned_trajectory(self):
        count, median, _, worst = compare(self.paths, 3)
        self.assertGreaterEqual(count, 100)
        self.assertLess(median, .001)
        self.assertLess(worst, .001)

    def test_canonical_disagreement_fails(self):
        self.write(params="owner 2 epoch 2 hole -0.1,0.2,0 normal 1,0,0 lean 0.8,0,0.6 swirl 1")
        with self.assertRaisesRegex(ValueError, "disagree"):
            compare(self.paths, 3)

    def test_divergent_trajectory_fails(self):
        self.write(shift=4)
        with self.assertRaisesRegex(ValueError, "drift"):
            compare(self.paths, 3)

    def test_missing_completion_fails(self):
        self.write(complete=False)
        with self.assertRaisesRegex(ValueError, "completion"):
            compare(self.paths, 3)

    def test_assertion_failure_after_completion_fails(self):
        with self.paths[1].open('a') as log:
            log.write('\nERROR: race: trajectory escaped owner corrections\n')
        with self.assertRaisesRegex(ValueError, "assertion failed"):
            compare(self.paths, 3)

    def test_non_simultaneous_contacts_fail(self):
        self.write(contact_tick=181)
        with self.assertRaisesRegex(ValueError, "tick 180"):
            compare(self.paths, 3)

    def test_missing_trajectory_fails(self):
        self.write(samples=90)
        with self.assertRaisesRegex(ValueError, "samples"):
            compare(self.paths, 3)

    def test_nonfinite_trajectory_fails(self):
        self.paths[1].write_text(self.paths[1].read_text().replace("pos 0.0,0,0", "pos nan,0,0"))
        with self.assertRaisesRegex(ValueError, "invalid trajectory"):
            compare(self.paths, 3)

    def test_observers_are_compared_with_each_other(self):
        self.write(shift=2)
        observer = Path(self.temp.name) / "observer.log"
        lines = ["race params owner 2 epoch 2 hole 0.1,0.2,0 normal 1,0,0 lean 0.8,0,0.6 swirl 1",
                 "puncture-race: assertions passed"]
        lines += [f"race path time {tick / 10} pos {tick / 10 - 2},0,0" for tick in range(120)]
        observer.write_text('\n'.join(lines))
        with self.assertRaisesRegex(ValueError, "drift"):
            compare([*self.paths, observer], 3)


if __name__ == "__main__":
    unittest.main()
