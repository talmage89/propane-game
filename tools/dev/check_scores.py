#!/usr/bin/env python3
"""Verify Tank Bank/King conservation and every bot's per-player final standings.
Usage: tools/dev/check_scores.py server.log bot1.log bot2.log [...]"""
import re
import sys


def parse_scores(text):
    scores = {}
    for entry in text.split():
        parsed = re.fullmatch(r"([1-9]\d*)=(\d+)", entry)
        if not parsed or int(parsed[1]) in scores:
            raise ValueError("malformed or duplicate minted standing")
        scores[int(parsed[1])] = int(parsed[2])
    if not scores:
        raise ValueError("empty minted standing")
    return scores


def check(paths):
    if len(paths) < 2:
        raise ValueError("provide a server log and at least one bot log")
    audits = {}
    server_standings = {}
    server_minted = {}
    king_matches = set()
    with open(paths[0], errors="replace") as source:
        for line in source:
            match = re.search(r"match (\d+): (bank|king) audit initial=(\d+) map=(\d+) drops=(\d+)(?: minted=(\d+))? spills=(\d+) banked=(\d+) expected=(-?\d+) ok=(True|False)", line)
            if not match:
                minted = re.search(r"match (\d+): king minted (.*)", line)
                if minted:
                    server_minted[int(minted[1])] = parse_scores(minted[2])
                standing = re.search(r"standings match (\d+): (.*)", line)
                if standing:
                    scores = {}
                    for entry in standing[2].split():
                        parsed = re.fullmatch(r"([1-9]\d*)=(\d+)", entry)
                        if not parsed or int(parsed[1]) in scores:
                            raise ValueError("malformed or duplicate server standing")
                        scores[int(parsed[1])] = int(parsed[2])
                    server_standings[int(standing[1])] = scores
                continue
            match_id = int(match[1])
            initial, original, drops, minted, spills, banked, expected = (int(match[i] or 0) for i in range(3, 10))
            if match[2] == "king":
                if match[6] is None:
                    raise ValueError("King audit missing minted count")
                king_matches.add(match_id)
            elif match[6] is not None:
                raise ValueError("Tank Bank audit includes minted tanks")
            if initial + original + drops + minted - spills != banked or expected != banked or match[10] != "True":
                raise ValueError(f"match {match_id}: bank does not conserve tanks")
            audits[match_id] = (banked, minted)
            print(f"match {match_id}: {initial} initial + {original} map + {drops} drops + {minted} minted - {spills} spills = {banked} banked")
    if not audits:
        raise ValueError("no completed server bank audit")
    for match_id in king_matches:
        if match_id not in server_standings or set(server_minted.get(match_id, {})) != set(server_standings[match_id]):
            raise ValueError(f"match {match_id}: missing King server standings or minted actors")
        if match_id not in server_minted or sum(server_minted[match_id].values()) != audits[match_id][1]:
            raise ValueError(f"match {match_id}: missing or incorrect King minted audit")
    agreed = {}
    for path in paths[1:]:
        finished = {}
        minted_by_actor = {}
        with open(path, errors="replace") as source:
            for line in source:
                mint = re.search(r"finished king (\d+); minted (.*)", line)
                if mint:
                    minted_by_actor[int(mint[1])] = parse_scores(mint[2])
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
        for match_id, (banked, _) in audits.items():
            if match_id in king_matches and minted_by_actor.get(match_id) != server_minted[match_id]:
                raise ValueError(f"{path}: match {match_id} minted tanks disagree with server")
            scores = finished.get(match_id)
            if scores is None or sum(scores.values()) != banked:
                raise ValueError(f"{path}: match {match_id} final scores {scores}, server bank {banked}")
            if match_id in server_standings and scores != server_standings[match_id]:
                raise ValueError(f"{path}: match {match_id} individual scores disagree with server: {scores}, expected {server_standings[match_id]}")
            if match_id in agreed and scores != agreed[match_id]:
                raise ValueError(f"{path}: match {match_id} individual scores disagree: {scores}, expected {agreed[match_id]}")
            agreed[match_id] = scores
    print(f"All {len(paths) - 1} bots agree with {len(audits)} server audits.")


if __name__ == "__main__":
    try:
        check(sys.argv[1:])
    except (ValueError, OSError) as error:
        sys.exit(f"FAIL: {error}")
