#!/usr/bin/env python3
"""Verify server tank-bank conservation and every bot's per-player final standings.
Usage: tools/dev/check_scores.py server.log bot1.log bot2.log [...]"""
import re
import sys


def check(paths):
    if len(paths) < 2:
        raise ValueError("provide a server log and at least one bot log")
    audits = {}
    with open(paths[0], errors="replace") as source:
        for line in source:
            match = re.search(r"match (\d+): bank audit initial=(\d+) map=(\d+) drops=(\d+) spills=(\d+) banked=(\d+) expected=(-?\d+) ok=(True|False)", line)
            if not match:
                continue
            match_id, initial, original, drops, spills, banked, expected = map(int, match.groups()[:-1])
            if initial + original + drops - spills != banked or expected != banked or match[8] != "True":
                raise ValueError(f"match {match_id}: bank does not conserve tanks")
            audits[match_id] = banked
            print(f"match {match_id}: {initial} initial + {original} map + {drops} drops - {spills} spills = {banked} banked")
    if not audits:
        raise ValueError("no completed server bank audit")
    agreed = {}
    for path in paths[1:]:
        finished = {}
        with open(path, errors="replace") as source:
            for line in source:
                match = re.search(r"finished match (\d+); final scores (.*)", line)
                if match:
                    scores = {}
                    for entry in match[2].split():
                        score = re.fullmatch(r"([1-9]\d*)=(\d+)", entry)
                        if not score or int(score[1]) in scores:
                            raise ValueError(f"{path}: malformed or duplicate final score {entry}")
                        scores[int(score[1])] = int(score[2])
                    if not scores:
                        raise ValueError(f"{path}: empty final scores")
                    finished[int(match[1])] = scores
        for match_id, banked in audits.items():
            scores = finished.get(match_id)
            if scores is None or sum(scores.values()) != banked:
                raise ValueError(f"{path}: match {match_id} final scores {scores}, server bank {banked}")
            if match_id in agreed and scores != agreed[match_id]:
                raise ValueError(f"{path}: match {match_id} individual scores disagree: {scores}, expected {agreed[match_id]}")
            agreed[match_id] = scores
    print(f"All {len(paths) - 1} bots agree with {len(audits)} server audits.")


if __name__ == "__main__":
    try:
        check(sys.argv[1:])
    except (ValueError, OSError) as error:
        sys.exit(f"FAIL: {error}")
