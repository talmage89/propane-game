# King online verification

Linux, Godot 4.7.2 .NET/Jolt, 2026-10-14. The branch keeps Tank Bank as the default and promotes the existing King
prototype through the mode envelope. King's rules stay server-only; peers and lobby bots consume `KingState`.
The lobby's mint pace reaches the rules and every peer's tuning, and the Lab preserves its original suburb supply.
The wire and shared tuners require Protocol 3 and the next minor release after 0.3.0.

## Gates

- `make setup`, `make build`: zero warnings/errors.
- `godot-mono --headless res://scenes/dev/unit_tests.tscn`: 346 assertions, zero failures. King adds round trips,
  malformed payloads, settings/permissions, holds, contests, down/stale/departed actors, moves, pile registration,
  duplicate/refused/empty/closed placements, authority succession, conservation and snapshot-driven HUD/bots.
- `make check`: passed, zero compiler warnings. Includes the new 69-assertion King lobby probe and the extended
  real-device input probe, every formation and separate-process world/segment hashes.
- `python3 -B tools/dev/lint_tests.py`: passed all negative formatting/analyzer, gate-ordering, deep-shard, CI and
  discovery checks.
- `make -j4 test-full`: passed every deep shard and network fixture. King completed three 180 s void rounds and
  one 180 s suburb round with conserved banks and fresh-pile checks. All four deep generator shards, the navigation
  sweep, dial bounds and lifetime sweeps passed. The faulted Tank Bank soak, two repeated duel matches and native
  readiness-stall fixture passed their unchanged tank and score gates.

Scratch and logs use `TMPDIR="$PWD/build/king-scratch"`; captures and network output stay under `build/`.

## Network matrix

Every ordinary soak uses `tools/dev/net_bots.sh -o "$PWD/build/<output>" -n N -l 90 -s '--match-seed=23' --
--melee-start`, plus the options below. Clean rows use `-t clean`; faults use
`--net-lag=120 --net-jitter=30 --net-loss=3`. The launcher checks engine/protocol errors, continuous tank agreement,
server conservation, every actor's final bank and, for King, every actor's minted count.

| Output | Clients / actors | Mode and pace | Network | Result | Map + drops + minted − spills = banked | Final moving drift |
| --- | --- | --- | --- | --- | --- | --- |
| king-4-clean | 4 / 4 | `--mode=king --mint-seconds=0.5` | clean | PASS | 0 + 3 + 17 − 17 = 3 | 0.106 m |
| king-4-faults | 4 / 4 | `--mode=king` (2 s) | faults | PASS | 0 + 0 + 3 − 2 = 1 | 0 m |
| king-8-clean | 8 / 8 | `--mode=king` (2 s) | clean | PASS | 8 + 3 + 3 − 5 = 9 | 0.115 m |
| king-8-faults | 8 / 8 | `--mode=king` (2 s) | faults | PASS | 19 + 20 + 4 − 30 = 13 | 0.253 m |
| king-lobby-repeat | 2 / 8 | King, 0.5 s, `--lobby-bots=3 --human=idle --tank-motion-trace` | faults | PASS | 2 + 9 + 26 − 22 = 15 | 0.174 m |
| king-bank-clean | 4 / 4 | Tank Bank | clean | PASS | 36 + 38 + 0 − 46 = 28 | 0.050 m |
| rifle-soak-gate | 4 / 4 | Tank Bank (full gate) | faults | PASS | 31 + 39 + 0 − 44 = 26 | 0.292 m |

The full gate's `rifle-soak-gate` uses seed 25 and `--net-seed=2`, otherwise the same 90 s faulted melee-start soak.

The first 2 s lobby-bot run (`king-lobby-bots`) finished with one minted/spilled tank, identical scores and a passing
manual tank comparison. Editing the launcher's documentation while its shell was waiting caused a shell EOF after
its clients finished; the launcher was syntax-checked and the run repeated with the script left untouched.

The first faster lobby-bot run (`king-lobby-final`, same eight actors at 0.5 s) **failed** the unchanged tank gate on
one final moving vent, drop `20600003`, at 0.636 m against the 0.50 m limit. It had no engine/protocol errors or
score/mint disagreement: 9 + 13 + 42 − 53 = 11 banked; longest transient 0.35 s; final resting drift 0.001 m.
The repeat above passed. This resembles the terminal active-vent residual already recorded in
[network-corrections.md](network-corrections.md); it is not proof of the same cause. No tolerance or body correction
was changed. Keep this failed evidence when assessing chaotic matches at the fastest pace.

## Late loading

```sh
tools/dev/net_bots.sh -o "$PWD/build/king-late" -n 3 -l 110 -k -s '--match-seed=23' -- \
  --mode=king --mint-seconds=0.5 --scenario=king-late --tune.PlayerSendRate=20 \
  --delay-who=Bot3 --match-delay=70
python3 -B tools/dev/check_scores.py build/king-late/server.log build/king-late/bot{1,2,3}.log
python3 -B tools/dev/compare_tanks.py --profile clean build/king-late/bot{1,2}.log
```

PASS. Bot3 replayed zone 1 and 90 minted tanks **before readiness**, then all three peers finished at 217 banked.
The undelayed peers passed full continuous coverage. A separate assertion compared all three completion samples:
44 identical tank ids, lifecycles and resting poses, maximum drift 0 m. The late peer intentionally lacks full-play
coverage, so it is not put through the checker's unchanged full-duration requirement. The scripted holder and 20 Hz
poses keep the existing loading inbox within its packet bound. Lobby admission remains waiting-only; reconnecting
does not resume a running match.

## Rendered presentation and input

A real embedded host and a second rendered client each ran:

```sh
godot-mono --disable-vsync --resolution 1280x720 res://scenes/dev/net_test.tscn -- \
  --role=host --host-server --match-seed=23 --players=2 --length=55 --name=Bot1 --color=0 \
  --mode=king --scenario=king --port=24755 --tank-check --windowed \
  --capture-dir="$PWD/build/king-hosted/shots"
```

The joiner used `--role=join --name=Bot2 --color=1` and omitted `--host-server`. Both passed hold, contest, mint,
warning, move and fresh-pile assertions. `check_scores.py` (host log as server audit, both client logs) and
`compare_tanks.py --profile clean` passed: 23 banked for Bot1, 0 for Bot2, no tank drift or logged engine errors.
The harness widens its warning camera and captures a settled-pile close-up; production cameras are unchanged.

Reviewed both peers' `Bot{1,2}_king_{hold,contest,warning,moved_pile,settled_pile,results}.png` under
`build/king-hosted/shots`: matching holder tint and red contest, current ring and pale next beam, move warning,
six fresh upright tanks at the new spot, mint counter/progress and identical 23/0 results.

```sh
godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/menu_test.tscn -- \
  --scenario=king --port=24685 --capture-dir="$PWD/build/king-lobby"
godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/capacity_ui_tests.tscn -- \
  --players=16 --mode=king --capture-dir="$PWD/build/king-capacity"
```

PASS: 69 King lobby assertions, plus actual keyboard/mouse/gamepad delivery and panel bounds at 16 actors.
Reviewed `king_lobby_{mouse,keyboard,gamepad,guest}.png` and `king-capacity/lobby_16.png`: the creator's mode, pace and
round-length controls fit, preserve focus, clamp at 0.5/10 s and replicate to a guest without creator controls.
`menu_test --scenario=input` also passed within `make check`, including the new mode and mint controls before its
mouse look, jump, reload and melee gameplay checks.

No Windows/macOS runtime verification was performed. Bot navigation and think rates remain outside this package.
