#!/usr/bin/env python3
"""Aligned samples must tolerate bounded replication and fail on actual disagreement."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from compare_tanks import Limits, Sample, compare, load


LIMITS = Limits(settle=.3, resting=.1, moving=1, gap=.15)


def log(offset=0, transform=None, shift=0, length=4):
    samples = []
    last_tick = (length + 3) * 10
    for tick in range(last_tick + 1):
        time = tick * .1 + offset
        phase = "P" if tick < length * 10 else "E"
        tanks = {"a": ("I", "R", (shift, 0, 0)), "b": ("V", "M", (time * 20, 0, 0))}
        if transform:
            tanks = transform(time, tanks)
        samples.append(Sample(time, phase, tick == last_tick, length, tanks))
    return {1: samples}


class TankChecks(unittest.TestCase):
    def check(self, *logs):
        return compare(list(logs), LIMITS)

    def test_fast_asynchronous_motion_aligns(self):
        result = self.check(log(), log(.025))
        self.assertEqual(result["failures"], [])
        self.assertLess(result["moving_peak"], 1e-10)
        self.assertGreater(result["body_pairs"], 100)

    def test_delayed_capacity_keeps_the_ten_second_startup_ceiling(self):
        def delayed(length, start):
            early = log(length=length)
            late = {1: [sample for sample in early[1] if sample.time >= start]}
            return [early, late]
        # The measured delayed builder missed 9.6 s: a 90 s match requires 81 s of play.
        with self.assertRaisesRegex(ValueError, 'missing play coverage'):
            compare(delayed(90, 9.6))
        # A longer capture requires 110 s and still rejects starts beyond the existing 10 s ceiling.
        self.assertFalse(compare(delayed(120, 9.6))["failures"])
        with self.assertRaisesRegex(ValueError, 'missing play coverage'):
            compare(delayed(120, 10.1))

    def test_short_lifecycle_and_position_transients_pass(self):
        def transient(time, tanks):
            if 1 <= time < 1.2:
                del tanks["a"]
                tanks["b"] = ("I", "M", (0, 0, 0))
            if 2 <= time < 2.2:
                tanks["a"] = ("I", "R", (2, 0, 0))
            return tanks
        with tempfile.TemporaryDirectory(prefix="nas-tanks-") as directory:
            paths = [Path(directory) / f"{peer}.log" for peer in range(2)]
            self.write(paths[0], log()[1])
            self.write(paths[1], log(transform=transient)[1])
            result = compare([load(path) for path in paths], LIMITS)
        self.assertEqual(result["failures"], [])
        self.assertGreater(len(result["episodes"]), 0)

    def test_persistent_identity_state_rest_and_motion_fail(self):
        for kind in ("identity", "state", "rest", "motion"):
            def disagree(time, tanks):
                if 1 <= time < 2:
                    if kind == "identity":
                        del tanks["a"]
                    elif kind == "state":
                        tanks["a"] = ("V", "R", (0, 0, 0))
                    elif kind == "rest":
                        tanks["a"] = ("I", "R", (2, 0, 0))
                    else:
                        tanks["b"] = ("V", "M", (time * 20 + 2, 0, 0))
                return tanks
            with self.subTest(kind=kind):
                result = self.check(log(), log(transform=disagree))
                self.assertTrue(result["failures"])
                self.assertGreater(max(e[-1] for e in result["episodes"]), LIMITS.settle)

    def test_rest_motion_switch_does_not_restart_disagreement_window(self):
        def alternating(time, tanks):
            if 1 <= time < 2:
                rest = "R" if round(time * 10) % 2 else "M"
                tanks["a"] = ("I", rest, (2, 0, 0))
            return tanks
        self.assertTrue(self.check(log(), log(transform=alternating))["failures"])

    def test_lifecycle_position_switch_cannot_hide_continuous_disagreement(self):
        def alternating(time, tanks):
            if 1 <= time < 2:
                if round(time * 10) % 2:
                    tanks.pop("a")
                else:
                    tanks["a"] = ("I", "R", (2, 0, 0))
            return tanks
        result = self.check(log(), log(transform=alternating))
        self.assertTrue(result["failures"])
        self.assertTrue(any(e[4] == "mixed" and e[-1] > LIMITS.settle for e in result["episodes"]))

    def test_final_difference_fails_without_waiting_for_window(self):
        def disagree(time, tanks):
            if time >= 6.9:
                tanks.pop("a")
            return tanks
        self.assertTrue(any("final" in f for f in self.check(log(), log(transform=disagree))["failures"]))

    def test_final_motion_has_a_tighter_limit_than_flight(self):
        def disagree(time, tanks):
            if time >= 6.9:
                tanks["b"] = ("V", "M", (time * 20 + .75, 0, 0))
            return tanks
        self.assertTrue(any("final" in f for f in self.check(log(), log(transform=disagree))["failures"]))

    def test_last_completion_lifecycle_checked_even_after_common_grid(self):
        other = log(.025)
        other[1][-1].tanks.pop("a")
        self.assertTrue(any("final identities" in f for f in self.check(log(), other)["failures"]))

    def test_all_pairs_not_just_first_peer(self):
        # Each peer is <0.1m from the baseline; peers two and three are >0.1m apart.
        result = self.check(log(), log(shift=.075), log(shift=-.075))
        self.assertTrue(any("peers 2/3" in f for f in result["failures"]))

    def test_empty_tank_world_can_pass(self):
        self.assertFalse(self.check(log(transform=lambda t, tanks: {}), log(transform=lambda t, tanks: {}))["failures"])

    def test_gaps_truncation_short_coverage_and_match_mismatch_fail(self):
        cases = []
        gap = log()
        del gap[1][10:15]
        cases.append(gap)
        truncated = log()
        truncated[1].pop()
        cases.append(truncated)
        short = log()
        short[1] = short[1][20:]
        cases.append(short)
        cases.append({2: log()[1]})
        for broken in cases:
            with self.assertRaises(ValueError):
                self.check(log(), broken)
        with self.assertRaises(ValueError):
            self.check(log())

    def test_loading_gap_is_outside_the_comparison_interval(self):
        other = log()
        other[1].insert(0, Sample(-2, "L", False, 4, {}))
        self.assertFalse(self.check(log(), other)["failures"])

    def test_bounded_stall_is_unobserved_instead_of_an_invented_path(self):
        def bounce(time, tanks):
            if 2 < time < 4.8:
                tanks["b"] = ("V", "M", (time * 20 + 100, 0, 0))
            return tanks
        a, b = log(length=30, transform=bounce), log(length=30)
        del b[1][21:48]
        result = compare([a, b])
        self.assertFalse(result["failures"])
        self.assertGreater(result["unobserved_pairs"], 50)
        self.assertLess(result["moving_peak"], 1e-10)

    def test_gap_pauses_but_does_not_reset_a_real_disagreement(self):
        def disagree(time, tanks):
            if 1 < time < 8:
                tanks["a"] = ("I", "R", (2, 0, 0))
            return tanks
        a, b = log(length=30), log(length=30, transform=disagree)
        del b[1][21:48]
        result = compare([a, b])
        self.assertTrue(result["failures"])
        duration = max(e[-1] for e in result["episodes"])
        self.assertGreater(duration, 4)
        self.assertLess(duration, 5)

    def test_capture_stalls_and_startup_have_explicit_bounds(self):
        late = log(length=90)
        late[1] = late[1][73:]
        self.assertFalse(compare([log(length=90), late])["failures"])
        too_late = log(length=90)
        too_late[1] = too_late[1][120:]
        excessive_gap = log(length=90)
        del excessive_gap[1][20:125]
        excessive_total = log(length=30)
        del excessive_total[1][20:65]
        for other in (too_late, excessive_gap, excessive_total):
            with self.assertRaises(ValueError):
                compare([log(length=other[1][0].length), other])

    def test_loaded_six_second_stall_retains_dense_capture_coverage(self):
        other = log(length=90)
        del other[1][200:260]
        result = compare([log(length=90), other])
        self.assertFalse(result["failures"])
        self.assertGreater(result["unobserved_pairs"], 100)

    def test_late_completion_resting_position_is_checked_directly(self):
        other = log(.025)
        other[1][-1].tanks["a"] = ("I", "R", (2, 0, 0))
        self.assertTrue(any("final completion" in f for f in self.check(log(), other)["failures"]))

    def test_every_match_checked(self):
        first, second = log(), log()
        first[2] = log()[1]
        second[2] = log(shift=2)[1]
        self.assertTrue(any("match 2" in f for f in self.check(first, second)["failures"]))

    def retired(self, offset=0):
        samples = log(offset)[1][:61]
        samples[-1].final = True
        samples[-1].kind = "final"
        samples.append(Sample(8 + offset, "E", True, 4,
                              {"a": ("I", "R", (0, 0, 0)), "b": ("V", "M", (1000 + offset * 1000, 0, 0))}, "completion"))
        return {1: samples}

    def test_retirement_ends_shared_motion_but_checks_exact_completion(self):
        first, second = self.retired(), self.retired(.025)
        with tempfile.TemporaryDirectory(prefix="nas-tanks-") as directory:
            paths = [Path(directory) / f"{peer}.log" for peer in range(2)]
            for path, capture in zip(paths, (first, second)):
                self.write(path, capture[1])
            self.assertFalse(self.check(*(load(path) for path in paths))["failures"])
        second[1][-1].tanks["a"] = ("I", "R", (2, 0, 0))
        self.assertTrue(any("final completion" in f for f in self.check(first, second)["failures"]))
        second[1][-1].tanks.pop("a")
        self.assertTrue(any("final identities" in f for f in self.check(first, second)["failures"]))

    def test_retirement_preserves_the_live_final_motion_limit(self):
        first, second = self.retired(), self.retired()
        final = second[1][-2]
        final.tanks["b"] = ("V", "M", (final.time * 20 + .75, 0, 0))
        self.assertTrue(any("final" in f for f in self.check(first, second)["failures"]))

    def test_retirement_preserves_intact_moving_completion_limit(self):
        for rest in ("R", "M"):
            first, second = self.retired(), self.retired(.025)
            first[1][-1].tanks["a"] = ("I", rest, (0, 0, 0))
            second[1][-1].tanks["a"] = ("I", "M", (.75, 0, 0))
            self.assertTrue(any("final completion" in f for f in self.check(first, second)["failures"]))

    def test_retirement_requires_completion_and_rejects_later_trajectory_rows(self):
        samples = self.retired()[1]
        for broken in (samples[:-1], samples[:-2] + samples[-1:], samples + [samples[-1]],
                       samples[:-1] + [Sample(7, "E", False, 4, {})] + samples[-1:]):
            with tempfile.TemporaryDirectory(prefix="nas-tanks-") as directory:
                path = Path(directory) / "bot.log"
                self.write(path, broken)
                with self.assertRaises(ValueError):
                    load(path)

    def write(self, path, samples):
        path.write_text("\n".join(f"tank-{s.kind} match 1 time {s.time:.6f} phase {s.phase} final {s.final} length {s.length} "
                                  f"n {len(s.tanks)}" + "".join(f" {id_}:{state}:{rest}:" + ",".join(map(str, pos))
                                                              for id_, (state, rest, pos) in s.tanks.items())
                                  for s in samples))

    def test_cli_exit_status_and_synthetic_logs(self):
        with tempfile.TemporaryDirectory(prefix="nas-tanks-") as directory:
            a, b = [Path(directory) / name for name in ("a.log", "b.log")]
            self.write(a, log()[1])
            self.write(b, log(.025)[1])
            self.assertFalse(compare([load(a), load(b)])["failures"])
            command = [sys.executable, "-B", str(Path(__file__).with_name("compare_tanks.py")), str(a), str(b)]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.write(b, log(shift=2)[1])
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)
            self.assertIn("FAIL", result.stdout)

    def test_invalid_and_old_samples_fail_closed(self):
        prefix = "tank-check match 1 time 1 phase P final False length 4 "
        for content in ("tanks clock 4 n 0", prefix + "n 1 a:I:R:nan,0,0", prefix + "n 2 a:I:R:0,0,0",
                        prefix + "n 1 a:X:R:0,0,0", prefix + "n 2 a:I:R:0,0,0 a:I:R:0,0,0",
                        prefix + "n 0\n" + prefix + "n 0", prefix + "n 1 broken"):
            with tempfile.TemporaryDirectory(prefix="nas-tanks-") as directory:
                path = Path(directory) / "bot.log"
                path.write_text(content)
                with self.subTest(content=content), self.assertRaises(ValueError):
                    load(path)


if __name__ == "__main__":
    unittest.main()
