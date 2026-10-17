# Participant integration review

**Status:** Dated record (2026-10-11, reverified 2026-10-12). It merged into dev over its own strict verdict; the
tank-agreement tails it lists were tracked as separate network work. The rules are in
[multiplayer.md](../multiplayer.md#participants-a6-protocol-2). Its "Protocol.Version remains 2" was true before 0.4.0
(the current protocol is 3).

Merged into `dev` at `3a204a8` over this review's strict verdict: its failing active-agreement tails (2.15 s and
2.05 s against the 2.0 s bound) also occur on stock `dev` (5.05 s on the same seed). The lead's paired faulted soaks
(match seeds 24–27) passed 8 of 8 with zero errors. The remaining tails are tracked as separate network work.

Reverification on October 12, 2026, after merging `dev` at `729c8c8` (including Lab `8516ad8`). The October 11 report is
preserved in commit `cdf422a` and its `build/va6-*` logs. The staged capacity gate passes, but the required
ordinary-play matrix and unstaged stress still violate unchanged tank bounds. The old terminal drift is not conclusively
classified.

## Summary by commit

- `2dc18de`: merge the rifle reach hotfix and Lab. Port actor-keyed scoring/retaliation into `BankingBot`, and give
  Lab's human and bot distinct actor ids on one owner peer. Its bank-rule assertion checks hit attribution by actor.
- `e943eaa`: ordinary sixteen-player destruction exhausted Godot's shader-instance uniform storage. Budget 131072 slots
  instead of 65536. The production-material regression allocates and retires 6000 meshes twice; the old budget fails,
  the new rendered and headless runs pass with zero engine errors. This is a renderer resource budget, not an
  effect/gameplay tuner change.
- `a78cafa`: the duel fixture sometimes shot the victim blocking a dropped tank. Both A6 and dev failed this scenario.
  Choose a clear road firing lane using a physics ray; preserve hit/grace, detonation and throw assertions. A
  deliberately blocked two-round regression fails before and passes after; it runs in `make test-full`.
- `79bf14c`: a zero-error 90-second capacity run recorded only 80.355 seconds of delayed play, below the required 81
  seconds. Capture 120 seconds, requiring 110 observed seconds under the unchanged ten-second startup ceiling. The
  regression rejects the old short capture and delays over that ceiling.
- `063aaa8`: catch life/revision exhaustion at round completion and revision exhaustion on departure. Retire the lobby
  and detach peers before rejection/disconnect callbacks. Seeded server regressions fail before (two then one
  overflows), pass after (268 tests, zero failures), and prove fresh lobbies remain usable.

Earlier verifier fixes remain: suppress every owned actor's unconfirmed drops, keep loading grace until the first
completed frame, handle synchronous session retirement, reject logged engine/protocol errors, and test production UI
with real device events. Actor authority and rotating reliable checkpoints retain both packages' intent. No gameplay
rules, correction bounds or tuner defaults changed.

## Verification commands and retained evidence

All scratch is under `build/`; TMPDIR is a worktree build directory. Native scenes use `godot-mono` or its wrappers. No
other checkout was modified, nothing was pushed, and all generated script UIDs are tracked.

The final commands are `git merge dev`, `make setup`, `make -j4 check`, `bash build/va6-r5-network-proof.sh`,
`tools/dev/capacity_acceptance.sh -n 16 -r 3 -w 1 -o "$PWD/build/va6-r5-capacity"`, and `make -j4 test-full`.
`build/va6-r5-final-proof.sh` serializes them with unstaged stress and rendered input; its status file records
individual exit codes. Network commands are recorded in `build/va6-r5-network-commands.txt`.

`make setup` and `make -j4 check` pass with zero build warnings; the final `make -j4 test-full` passes with 268 unit
tests, zero failures and zero logged engine errors. Deep generation passes 200 solo/two-player seeds, 100 seeds each at
8/16/24/32 actors and six seeds per forced formation/size. Navigation passes 700/700 courses, zero stuck events and 600
jumps. The nine-second readiness stall passes tank and score gates. Full Lab lifetime, bank rules, 168 dial sessions and
both three-minute duels pass with clean engine logs. The controlled blocked two-round duel regression and the mandatory
faulted rifle soak pass. Final exits are in `build/va6-r5-final-status.tsv`; two network failures remain recorded there.

The ordinary matrix has zero engine/protocol errors in all eight runs and every score audit agrees. It runs five faulted
and three clean four-bot 90-second `--melee-start` matches, map seeds 24–28 and fault seeds 1–5. Fault settings are 120
ms RTT, 30 ms jitter and 3% disposable loss. The script also runs rendered melee, two-round duel, gather and puncture
race, plus `compare_vents.py`. All scenario assertions, tank comparisons, score audits and vents pass, but those four
shell exits fail because I omitted the scratch TMPDIR directory needed by the rendering wrapper. Corrected reruns (`bash
build/va6-r6-scenes.sh`) pass all five exits with zero engine/protocol errors. Melee/duel/gather/race longest transients
are 0.20/0.30/0.20/0.30 seconds; final moving drift is 0.000/0.225/0.000/0.094 m. `compare_vents.py` passes 279 pairs,
median/p95/max 0.092/0.202/0.349 m. Reviewed `build/va6-r6-melee/shots/melee_host_2_06.png`,
`build/va6-r6-duel/shots/victim_throw_1.80.png` and `build/va6-r6-gather/shots/gather_join_settled.png`: strike/spills,
blast ragdoll and settled gather presentation. The scratch-directory mistake does not explain either ordinary tank
failure.

The rendered input command is `godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn
-- --scenario=input --port=24984 --capture-dir="$PWD/build/va6-r5-input"`. The final integrated run passes 163
assertions, with zero engine errors. Reviewed turn/jump images for single player, Lab and multiplayer.

Six rendered `capacity_ui_tests.tscn` runs use `--players=2|8|16`, `--resolution 1280x720|1920x1080`, `--fixed-fps 60`
and absolute capture directories `build/va6-r3-ui-COUNT-SIZE`. All pass real Tab/Enter, D-pad/A and scaled mouse
delivery. Personally reviewed all twelve lobby/results captures: controls, names, ranks, scores and departed markers
fit.

`python3 -B tools/dev/check_liveness.py --output build/va6-r3-liveness-client` passes in 4.523 seconds. The same command
with `--silent server --output build/va6-r3-liveness-server` passes in 3.156 seconds. Loading peers retain extended
grace; established peers retain short detection. The native nine-second readiness stall is part of the deep gate.

`python3 -B tools/dev/check_shader_capacity.py --rendered --output "$PWD/build/va6-r2-shader-after"` and its headless
invocation pass. Reviewed `shader_capacity.png`, a fully rendered 6000-mesh grid. The negative old-budget capture is
retained separately. `python3 -B tools/dev/compare_tanks_tests.py` passes 25 tests, including strict delayed coverage.
Exhaustion before/after unit logs are `build/va6-r4-exhaustion-*` and `build/va6-r4-departure-before*`.

## Cold review

Server actor ownership, lives, membership and sequences precede accepted actions. Regressions cover forged roster
updates, replay, capacity bypass, exhausted identities and stale revisions. Same-peer contacts use the ordinary actor
handler and deduplicate prediction/echoes; delayed chains retain actor credit. Departure removes every owned actor,
preserves standings and transfers body authority with new epochs and creator controls without original debug-tuning
permission. The server still trusts the plan's designated physical reporters and runs no physics.

## Tank failure classification and baseline limitations

Stock `dev` was archived into `build/va6-r2-dev-baseline`, set up and built without warnings. Eight four-bot comparisons
use the same map/fault seeds and melee start as A6. Its clean seed-24 run passes at 0.70 seconds longest divergence and
0.054 m final motion. A6's first and subsequent error-free clean seed-24 runs pass at 0.40/0.75 seconds and 0.108/0.025
m final motion. The old 1.75-second clean episode is not reproduced; its exact cause cannot be isolated from the old
error-heavy logs. There is no reproduced A6 actor/epoch regression. Engine-error load as a contributor is an inference.

Existing correction tails do reproduce: stock dev's fault seed-1/map-24 run fails at 5.05 seconds while A6's first rerun
fails at 2.75 seconds. Publications are fresh, epochs match, claims are resolved and traces show no barrier. The final
ordinary matrix has seven passes and one failure (fault-1: 2.15 seconds, 4.728 m episode peak, 0.185 m final motion).
Clean-1 passes at 1.45 seconds and 0.085 m final motion; failed exploratory runs remain failures. Stock dev fault-2 also
logs one incomplete StartMatch tuning snapshot after match completion; its tank pass does not make that launcher run
pass.

For sixteen-client dev comparisons only, the old eight-entry admission/decoder limits were widened to sixteen, with
color validation, wire shapes, physics and authority unchanged. The baseline checkout copy was removed after comparison;
logs and patches remain. The exact scratch delta is `build/va6-r2-dev-admission.patch`; later runs also use the renderer
budget fix. These are admission-adjusted dev comparisons, not untouched sixteen-client dev. All use map seed 23, fault
seed 1 and 120/30/3, with no staged scenario or melee start.

Three dev sixteen-bot runs fail active agreement: longest episodes 4.65/3.60/2.95 seconds, final motion
0.455/0.411/0.390 m; all score audits agree and engine logs are clean. None reproduces the terminal >0.5 m violation.
The old error-heavy A6 unstaged run ended at 0.643 m final motion against the 0.5 m bound. The first post-renderer-fix
A6 stress run has zero engine errors but fails with 0.889 m final motion, 2.40 seconds longest active divergence and a
0.113 m resting completion difference. Conservation is `56 map + 16 drops - 43 spills = 29 banked`, agreed by all
sixteen clients.

At that run's live final, vent `20a00002` has epoch 2 and owner 12. Observer motion targets have sequences 179, receipt
silence 0.003/0.014 seconds, no pending claim and no barrier; owner spin is about 30 rad/s. This matches the fresh
nonlinear vent-tracking residual already recorded on dev (0.531 m) in `docs/evidence/network-corrections.md`. BodySync correction
methods and vent-force callbacks are unchanged from dev; the existing-residual attribution is supported but remains an
inference, not deterministic replay. Fixed map/fault seeds do not fix physical timing or effect randomness.

The final frozen-tree unstaged run (`build/va6-r5-unstaged16`) has zero engine/protocol errors, but fails one vent
episode: body `10000017`, 2.05 seconds against the 2.0-second faulted bound, peak 3.487 m. Final motion is 0.486 m
against 0.5 m, so the old terminal violation does not reproduce here. Both copies use owner 12 / epoch 2; observer
targets are fresh (0.015–0.074 seconds receipt silence), with no pending claim or barrier. All sixteen score audits
agree: `41 map + 14 drops - 29 spills = 26 banked`. Upload mean/peak is 3.765/5.930 Mbit/s. Reviewed
`shots/Bot1_030.0.png`: normal player/suburb presentation.

The earlier resting completion mismatch occurs after the server retires replication. The owner remains at its settled
pose while an observer coasts during its later results interval. Dev has the same retirement path. No bounds were
loosened and no common correction/gameplay behavior was changed to hide either residual.

## Scale, risks and compatibility

Relay cost remains approximately bodies times recipients; full roster broadcasts are quadratic on lobby mutations, not
physics ticks. Final capacity acceptance passes three clean and three faulted 120-second matches: 248331299 strict
body-pair comparisons, zero disagreements. Longest transients are 0.60/0.45/0.30 seconds clean and 0.40/0.55/0.40
faulted; final moving drift is 0.001 m in every run. All audits conserve `128 initial + 1 map - 80 spills = 49 banked`.

Server payload upload means are 3.237–4.184 Mbit/s, peaks 7.314–8.959; download means 0.253–0.313, peaks up to 0.583.
Per-client upload means 0.017–0.021, peak 0.118; download means 0.213–0.274, peak 0.563. Peak RSS is 211.0 MiB server /
1222.3 MiB client. Client queues peak at 336 packets / 25228 bytes; loading inboxes at 2590 packets / 325362 bytes.
Client process means are 32.870–39.855 ms, p95 59.831–72.482 ms; server process includes an 889.254 ms outlier and
client physics a 3738.853 ms outlier. Detailed counters remain in each `acceptance.txt` and
`build/va6-r5-bandwidth.txt`. Payload rates exclude UDP headers and retransmissions; native ENet queue bytes are
unavailable. A ten-megabit uplink has little margin against the observed roughly nine-megabit payload peak. Twenty
megabits leaves more room; actual WAN congestion and distributed machines were not tested. One GPU and CPU were shared
with other worktrees, so functional capacity is not a sixty-FPS promise.

The staged fixture holds final owner poses and uses production gather interpolation for convergence. Unstaged physics
failures remain disclosed separately; the sixteen-player ceiling must not imply all ordinary trajectories meet every
strict tank bound. Earlier overlapping/cancelled `va6-r4` attempts are retained and are not counted as final proof.
Linux was tested; stock Ubuntu CI execution, Windows and macOS were not independently run here.

A6 changes shared message shapes, actor/life/roster identity, capacity settings and suburb construction from the actual
frozen actor count. `Protocol.Version` remains 2 by the brief's explicit unreleased-cycle instruction; a public minor
release is required. The verifier's renderer/fixture changes are local or dev-only; exhaustion handling reuses existing
rejection messages. No further protocol bump was made.
