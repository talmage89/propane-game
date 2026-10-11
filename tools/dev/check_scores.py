#!/usr/bin/env python3
"""Verify the server's tank-bank conservation audit and every bot's final bank.
Usage: tools/dev/check_scores.py server.log bot1.log bot2.log [...]"""
import re
import sys


def check(paths):
    if len(paths) < 2:
        raise ValueError("provide a server log and at least one bot log")
    audits = {}
    for line in open(paths[0], errors="replace"):
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
    for path in paths[1:]:
        finished = {}
        for line in open(path, errors="replace"):
            match = re.search(r"finished match (\d+); final scores (.*)", line)
            if match:
                finished[int(match[1])] = sum(map(int, re.findall(r"\d+=(\d+)", match[2])))
        for match_id, banked in audits.items():
            if finished.get(match_id) != banked:
                raise ValueError(f"{path}: match {match_id} final bank {finished.get(match_id)}, server {banked}")
    print(f"All {len(paths) - 1} bots agree with {len(audits)} server audits.")


if __name__ == "__main__":
    try:
        check(sys.argv[1:])
    except (ValueError, OSError) as error:
        sys.exit(f"FAIL: {error}")
