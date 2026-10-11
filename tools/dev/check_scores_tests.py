#!/usr/bin/env python3
"""Regression tests for conservation and agreement checks; no running game needed."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest

from check_scores import check


class ScoreChecks(unittest.TestCase):
    def run_check(self, server, *bots):
        with tempfile.TemporaryDirectory(prefix="vnet-scores-") as directory:
            paths = []
            for index, content in enumerate((server, *bots)):
                path = Path(directory) / f"{index}.log"
                path.write_text(content)
                paths.append(str(path))
            with contextlib.redirect_stdout(io.StringIO()):
                check(paths)

    audit = "match 1: bank audit initial=16 map=0 drops=1 spills=7 banked=10 expected=10 ok=True\n"
    final = "finished match 1; final scores 2=9 3=1\n"

    def test_same_scores_in_different_order(self):
        self.run_check(self.audit, self.final, "finished match 1; final scores 3=1 2=9\n")

    def test_equal_totals_with_different_players_fail(self):
        with self.assertRaisesRegex(ValueError, "individual scores disagree"):
            self.run_check(self.audit, self.final, "finished match 1; final scores 2=1 3=9\n")

    def test_missing_and_corrupt_audits_fail(self):
        for audit in ("", self.audit.replace("spills=7", "spills=6"), self.audit.replace("ok=True", "ok=False")):
            with self.subTest(audit=audit), self.assertRaises(ValueError):
                self.run_check(audit, self.final)

    def test_missing_or_malformed_standings_fail(self):
        for scores in ("", "2=10 2=0", "2=10 garbage", "2=-1 3=11", "0=10"):
            with self.subTest(scores=scores), self.assertRaises(ValueError):
                self.run_check(self.audit, self.final, f"finished match 1; final scores {scores}\n")
        with self.assertRaises(ValueError):
            self.run_check(self.audit, self.final, "")

    def test_every_consecutive_match_is_checked(self):
        second_audit = self.audit.replace("match 1", "match 2")
        second_final = "finished match 2; final scores 2=4 3=6\n"
        self.run_check(self.audit + second_audit, self.final + second_final, self.final + second_final)
        with self.assertRaises(ValueError):
            self.run_check(self.audit + second_audit, self.final + second_final, self.final)


if __name__ == "__main__":
    unittest.main()
