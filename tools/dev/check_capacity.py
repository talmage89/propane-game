#!/usr/bin/env python3
"""Assert a complete capacity run and print its recorded resource measurements."""
import math
from pathlib import Path
import re
import statistics
import sys

from check_scores import check as check_scores
from compare_tanks import check as check_tanks


def check(directory, actors):
    root = Path(directory)
    server = root / "server.log"
    clients = [root / f"bot{i}.log" for i in range(1, actors + 1)]
    texts = [path.read_text(errors="replace") for path in clients]
    fingerprints = []
    for path, text in zip(clients, texts):
        if re.search(r"(?:ERROR:|FAIL|bad message|bad \w+ message|overflows=[1-9])", text):
            raise ValueError(f"{path}: engine, protocol, assertion or inbox failure")
        roster = re.search(r"capacity roster actors=(\d+) peers=(\d+).* starts=(\d+) tanks=(\d+) ammo=(\d+)", text)
        if not roster or tuple(map(int, roster.groups()[:3])) != (actors, actors, actors):
            raise ValueError(f"{path}: missing complete roster/start proof")
        fingerprint = re.search(r"capacity roster-map (.*)", text)
        if not fingerprint:
            raise ValueError(f"{path}: missing frozen roster map")
        entries = fingerprint[1].split(",")
        if len(entries) != actors or any(len(entry.split("/")) != 7 for entry in entries):
            raise ValueError(f"{path}: incomplete frozen actor map")
        identities = [tuple(map(int, entry.split("/")[:5])) for entry in entries]
        if (any(actor <= 0 or peer <= 0 or life <= 0 or slot < 0 or slot >= 256 for actor, peer, life, spawn, slot in identities)
                or any(len({entry[column] for entry in identities}) != actors for column in (0, 1, 4))
                or {entry[3] for entry in identities} != set(range(actors))):
            raise ValueError(f"{path}: invalid actor/peer/life/allocation/spawn mapping")
        fingerprints.append(fingerprint[1])
    if len(set(fingerprints)) != 1:
        raise ValueError("client frozen roster/spawn/appearance maps disagree")
    if "capacity creator departure" not in texts[0] or "capacity throw" not in texts[0]:
        raise ValueError("creator did not throw and depart")
    for path, text in zip(clients[1:], texts[1:]):
        if (f"capacity PASS actors={actors}" not in text or "capacity throw" not in text or "capacity transfer" not in text
                or "capacity quiescence" not in text or "capacity all actors loaded" not in text):
            raise ValueError(f"{path}: no complete departure/throw proof")
    if "delaying match" not in texts[-1]:
        raise ValueError("delayed loading not exercised")
    if sum("capacity contested claim" in text for text in texts) < actors:
        raise ValueError("not every actor contested body authority")
    if not any("capacity contested puncture" in text for text in texts) or not any("capacity root explosion" in text for text in texts):
        raise ValueError("contested lifecycle or root blast missing")
    server_text = server.read_text(errors="replace")
    disposable_rejections = re.findall(r"^.*bad BodyStates message from \d+: unknown body id$", server_text, re.MULTILINE)
    checked_server = re.sub(r"^.*bad BodyStates message from \d+: unknown body id$", "", server_text, flags=re.MULTILINE)
    if re.search(r"(?:ERROR:|FAIL|bad \w+ message)", checked_server):
        raise ValueError("server engine or protocol failure")
    standings = re.search(r"standings match \d+: (.*)", server_text)
    if not standings or len(standings[1].split()) != actors:
        raise ValueError("server lost departed per-actor standings")
    if {int(entry.split("=")[0]) for entry in standings[1].split()} != {entry[0] for entry in identities}:
        raise ValueError("server standings do not cover the frozen actor roster")
    print(f"expected disposable drop-before-registration snapshot rejections: {len(disposable_rejections)}")
    check_scores([str(server), *map(str, clients[1:])])
    check_tanks(list(map(str, clients[1:])))
    metrics = re.compile(r"metrics (server|client) process=(\S+)ms physics=(\S+)ms rss=(\d+)B in=(\d+)B out=(\d+)B queue=(\d+)/(\d+)B drops=(\d+) inbox=(\d+)/(\d+)B overflows=(\d+)")
    for role, logs in (("server", [server_text]), ("client", texts)):
        samples = [m for text in logs for m in metrics.finditer(text) if m[1] == role]
        if not samples:
            raise ValueError(f"no {role} resource measurements")
        process = sorted(float(m[2]) for m in samples)
        physics = sorted(float(m[3]) for m in samples)
        p95 = lambda values: values[math.ceil(len(values) * .95) - 1]
        print(f"{role}: samples={len(samples)} process mean/p95/max={statistics.mean(process):.3f}/{p95(process):.3f}/{max(process):.3f}ms "
              f"physics mean/p95/max={statistics.mean(physics):.3f}/{p95(physics):.3f}/{max(physics):.3f}ms "
              f"peak rss={max(int(m[4]) for m in samples)/2**20:.1f}MiB queue={max(int(m[7]) for m in samples)}/{max(int(m[8]) for m in samples)}B "
              f"inbox={max(int(m[10]) for m in samples)}/{max(int(m[11]) for m in samples)}B")
    # The server's existing per-peer diagnostics give actual relay bytes, including peers that departed.
    peers = {}
    for peer, incoming, outgoing in re.findall(r"net peer (\d+) in \d+/(\d+)B out \d+/(\d+)B", server_text):
        peers[int(peer)] = int(incoming), int(outgoing)
    if len(peers) != actors:
        raise ValueError("missing per-peer server bandwidth samples")
    print(f"server sampled bandwidth: {sum(value[0] for value in peers.values())}B in, {sum(value[1] for value in peers.values())}B out; "
          f"max per peer {max(value[0] for value in peers.values())}B in/{max(value[1] for value in peers.values())}B out")
    print(f"PASS capacity: {actors} actor-owning clients, scaled starts/supplies, roster/score agreement, conservation, loading, contested bodies, throws and departure")


if __name__ == "__main__":
    try:
        check(sys.argv[1], int(sys.argv[2]))
    except (ValueError, OSError, IndexError) as error:
        sys.exit(f"FAIL: {error}")
