# Network scenario ownership

Duel, drop-refusal, gather and pickup currently use extracted owners. The remaining registered cases still dispatch
through the host during the family-by-family migration described below. Inventory comparison currently reports
one extra generic completion expression: the temporary bridge for cases still in the host.

`NetTest` hosts the client connection, match construction/lifecycle, roaming lobby world, bot fallback, capture
service and telemetry. Plain scenario objects under `src/Dev/NetScenarios/` own case state, scripted input,
subscriptions, capture decisions and completion checks. They add no processing nodes. Register a case and its
completion requirement together in `NetScenarioRegistry`; unknown names fail before connecting.

The host calls the owner at the existing boundaries: lobby update / MatchStarting, construction-delay selection,
pre-assembly subscriptions, `BeforeInboxAttach`, post-assembly reset, the physics dispatch, completion checks and
retirement. Required assertions run before navigation telemetry; capacity's final checks follow that telemetry.
Retirement follows match removal/queued freeing. It detaches match subscriptions and invalidates pending timer
and render continuations, including when a replacement match supersedes the old one. Connection-scoped appearance
menus remain attached until the host exits. The roaming world remains a host service.

A selected scenario consumes its tick even while loading or showing results. Default clients retain the bot /
`--look-at-bots` / `--human=idle` fallback. `watch` consumes only the host's tick; joiners still play normally.
Clock domains, telemetry cadence, parsed lines and capture labels stay with their previous owners and call sites.
`body-stream` retains its original connection-scoped tank reference; repeated body-stream matches are outside the
existing documented proof. This extraction does not silently repair that preexisting reset omission.

## Scenario and option inventory

Before extraction, `128a664` has 122 command/reference sites across Makefile, tools, README and docs; 51 host option
names; 58 assertion invocations; 20 direct capture expressions; and 82 log expressions. The complete source and
command inventory can be reproduced with:

```sh
python3 -B tools/dev/net_scenario_inventory.py --revision=128a664 --output=build/rfp4-parent-inventory.json
python3 -B tools/dev/net_scenario_inventory.py --compare=128a664 --output=build/rfp4-current-inventory.json
```

This includes dated commands as evidence, without treating their numeric seeds, output paths, tuner overrides or
historical admission limits as current recommendations. Inventory comparison checks full assertion expressions,
CLI option names and log/capture expressions; runtime capture manifests are compared separately because native
network timings are not deterministic.

| Selection | Required prerequisites / options | Completion and captures |
| --- | --- | --- |
| default | `--brain=smart` (default) or `simple`; optional `--bot-skill`, `--bot-log`, `--human=idle`, `--look-at-bots` | Host navigation/activity telemetry; no scripted completion obligation. |
| `watch` | At least two actors; host follows remote actor; joiners use ordinary brains | `watch_<time>`. |
| `duel` | Two clients, server `--start-score=8`; `--duel-blocked` exercises lane recovery; documented `--matches=2` | Both clients require all hit/grace/drop/throw assertions; `shooter_*`, `victim_*`. |
| `drop-refusal` | Two clients, server `--start-score=8`, client lag/jitter/loss 120/30/3 | Both require rollback/7-bank/one-drop assertions; `*_drop_refused`. |
| `gather` | Two clients, server `--start-score=8`, `--tune.DropScatter=0.2 --tune.DropToss=1`; 45 s | Both require two shields, glides, authority acquisition and stable intact pile; `gather_*`. Rendered runs read shader uniforms; headless runs retain gameplay assertions. |
| `pickup` | One client; enough time for `PickupRespawnTime` and `PickupRevealTime` | Take/once-only ammo/respawn assertions; `pickup_{0,1,2}_*`. |
| `melee` | Two clients, server `--start-score=5`, at least 25 s; `--assert-grip --assert-motion`; `--view=side|player` | Both knockdowns/get-ups and five/zero-bank spills; `melee_*`, `remote_{down,getup,getup_handoff}`. |
| `movement` | Two clients; headless at least 18 s, rendered 35 s; `--assert-grip --assert-motion --assert-feet`; `--view=player|rifleleft|rifleright` | Host must finish observed remote gait/support/reload/recoil proof; `remote_00` through `remote_10` labels. |
| `puncture-race` | Two or three clients; 20 s; lag/jitter/loss 120/30/3; color indices 0/1 contend | Every client requires canonical vent, lean, bounded corrections and 100 path samples; `<name>_race_<tick>`; run `compare_vents.py`. |
| `body-stream` | Two clients; server `--net-loss=100 --net-body-stall=2000`; 20 s | Every client requires owner motion; no scenario captures. Also used with readiness stall. |
| `vent-contact` | Two clients; 30 s; `--vent-contact-axis=mixed`, fixed seed 23; optional motion traces | Every client requires eight canonical vents/epochs; no scenario captures. Parent fixture failure is recorded below. |
| `king` | Two clients, `--mode=king`, at least 55 s | Every client requires hold/contest/mint/warning/move/fresh pile; `<name>_king_{hold,contest,warning,moved_pile,settled_pile,results}`. |
| `king-late` | Three clients; `--mode=king --mint-seconds=0.5 --tune.PlayerSendRate=20 --delay-who=Bot3 --match-delay=70`; 110 s | Every client requires moved zone/holder/mints; delayed peer checks catch-up before readiness. Use `-k`, check server scores/mints, full tank coverage on undelayed peers, and all completion samples. |
| `appearance` | Three clients; server `--start-score=8`; `-c 12ABEF,12ABEF,F6F6F6` or `000000,0000FF,FFFFFF`; render all three | Every client requires exact accepted lobby/setup/body/drop/results colors; `<name>_appearance_{lobby,tags_glow,results}`. |
| `capacity` | `make test-capacity`: 16 clients, server `--start-score=8 --capacity-metrics`, 120 s; last builder delayed 26 s | Creator proves throw then departs; survivors require transfer/final actor scores/loading inbox; `capacity PASS`; dedicated capacity checker. |

Shared combinations include `--host-server`, `--match-delay`/`--delay-who`, `--matches`, `--lobby-roam`, scalar/list
`--lobby-bots` with difficulty, named `--leave-at`/`--leave-who`, Bank/King mode/mint/supply, `--melee-start`,
`--send-tune`, capture/frame metrics and shot intervals, tank-check loading/readiness stalls, divergence nudges,
authority/body/gather traces and ordinary transport fault overrides. `tools/dev/net_bots.sh` owns startup and final
cross-process audits; its flags and Make targets are unchanged.

## P4 evidence — October 16, 2026

Parent: `128a664`, Godot 4.7.2 .NET/Jolt, Linux Debug. Evidence lives under the worktree's `build/rfp4*` directories.
All rendered invocations use `godot-mono` through the headless gamescope wrapper. Scratch uses `build/rfp4/tmp`.
The parent matrix runs before source changes and retains command arrays, exit codes, assertion/PASS/FAIL lines and
capture manifests. Failed runs are retained alongside successful ones.

The parent `vent-contact` fixture deterministically fails: its `TakeShot` call omits `current.Player.ActorId`.
`Match.OnTankPunctured` forwards the resulting zero actor/life IDs, and the server rejects all eight punctures with
`integer out of range: 0`; both clients then fail the existing canonical-vent assertion. No checker limit or
production code is changed to conceal this failure.

### Duel / refusal / gather migration

`make setup`, `make build` and `make test` passed (451 unit assertions, zero failures). Focused
`unit_tests.tscn -- --suite=NetScenarioTests` passed all five lifecycle checks after the completion-table update.
The candidate repeated the parent matrix's exact duel/refusal/gather arguments; all three passed, with zero engine
or protocol errors, complete tank coverage and agreeing server audits. Reviewed the candidate gather shimmer
and settled-pile captures against the parent. Native output: `build/rfp4-candidate/{duel,refusal,gather}`.
The scaffold's generic completion bridge is the sole temporary source-inventory addition.

### Pickup migration

`make setup`, warning-free `make build`, the five fake-context lifecycle tests and the source-inventory comparison
passed (only the temporary completion bridge differs). The single rendered client repeated the parent's 25 s
pickup command and passed take, exact ammo, absence and return assertions with an agreeing server audit.
Evidence: `build/rfp4/pickup-*.log`, `build/rfp4-candidate/pickup`.
