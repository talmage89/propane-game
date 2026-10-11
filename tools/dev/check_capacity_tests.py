#!/usr/bin/env python3
"""Capacity evidence cannot pass when actor, departure or convergence proofs are incomplete."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from check_capacity import check

class CapacityChecks(unittest.TestCase):
    def run_check(self, change=lambda logs: None):
        with tempfile.TemporaryDirectory(prefix="capacity-check-") as directory:
            logs = {"server": "match 1: bank audit initial=32 map=0 drops=0 spills=0 banked=32 expected=32 ok=True\n"
                    "standings match 1: 1=8 2=8 3=8 4=8\n"
                    "metrics server process=1ms physics=1ms rss=100B in=0B out=0B queue=0/0B drops=0 inbox=0/0B overflows=0\n"}
            for i in range(1, 5):
                logs["server"] += f"net peer {i} in 1/10B out 1/20B\n"
                mapping = ",".join(f"{actor}/{actor+100}/1/{actor-1}/{actor-1}/Bot{actor}/abcdef" for actor in range(1, 5))
                logs[f"bot{i}"] = f"capacity roster actors=4 peers=4 revision=4 starts=4 tanks=36 ammo=7\ncapacity roster-map {mapping}\n"
                logs[f"bot{i}"] += "capacity all actors loaded\ncapacity quiescence\n"
                logs[f"bot{i}"] += "capacity throw\ncapacity contested claim\nmetrics client process=1ms physics=1ms rss=100B in=0B out=0B queue=0/0B drops=0 inbox=1/10B overflows=0\n"
                if i == 1:
                    logs[f"bot{i}"] += "capacity creator departure\ncapacity contested puncture\ncapacity root explosion\n"
                else:
                    logs[f"bot{i}"] += "capacity PASS actors=4\ncapacity transfer\nfinished match 1; final scores 1=8 2=8 3=8 4=8\ntanks clock 0.0 n 1 1:I:0,0,0\n"
                if i == 4:
                    logs[f"bot{i}"] += "delaying match\n"
            change(logs)
            for name, text in logs.items():
                (Path(directory) / f"{name}.log").write_text(text)
            with contextlib.redirect_stdout(io.StringIO()):
                check(directory, 4)
    def test_complete_evidence_passes(self):
        self.run_check()
    def test_known_disposable_snapshot_race_is_counted(self):
        self.run_check(lambda logs: logs.update(server=logs["server"] + "bad BodyStates message from 2: unknown body id\n"))
    def test_standings_must_cover_the_actual_frozen_actor_ids(self):
        with self.assertRaisesRegex(ValueError, 'frozen actor roster'):
            self.run_check(lambda logs: logs.update({f"bot{i}": logs[f"bot{i}"].replace('1/101/1/0/0', '1001/101/1/0/0') for i in range(1, 5)}))
    def test_other_protocol_failures_are_rejected(self):
        for line in ("bad BodyStates message from 2: invalid epoch", "bad Shot message from 2: unknown shot target", "ERROR: failure"):
            with self.subTest(line=line), self.assertRaises(ValueError):
                self.run_check(lambda logs: logs.update(server=logs["server"] + line + "\n"))
    def test_incomplete_actor_or_departure_evidence_is_rejected(self):
        for old, new in (("roster actors=4", "roster actors=3"), ("/Bot2/abcdef", "/Changed/abcdef"),
                         ("2/102/1/1/1", "1/101/1/1/0"), ("2/102/1/1/1", "2/102/0/1/1"),
                         ("capacity all actors loaded", ""), ("capacity quiescence", ""),
                         ("capacity transfer", ""), ("capacity throw", ""), ("overflows=0", "overflows=1"),
                         ("final scores 1=8 2=8", "final scores 1=7 2=9"), ("1:I:0,0,0", "2:I:0,0,0")):
            with self.subTest(old=old), self.assertRaises(ValueError):
                self.run_check(lambda logs: logs.update(bot2=logs["bot2"].replace(old, new)))
if __name__ == "__main__":
    unittest.main()
