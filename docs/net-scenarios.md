# Network scenario ownership

All registered scenario families use plain owners; the host contains no scenario-selection chain or case state.

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
| `ballistic` | Two clients; 20 s; `--tune.VentThrust=0 --ballistic-gravity=0` or `1`; optional `--ballistic-spin` and residual observer | Native zero-contact/selected-spin/authority/gravity assertions; ordinary ending and tank checker. Diagnostic parent gravity-on runs can fail convergence. |
| `vent-contact` | Two clients; 30 s; `--vent-contact-axis=mixed`, fixed seed 23; optional motion traces | Every client requires eight canonical vents/epochs; no scenario captures. Punctures carry the owner's actor ID (and Match resolves its life); the historical parent failure is recorded in the verification report. |
| `king` | Two clients, `--mode=king`, at least 55 s | Every client requires hold/contest/mint/warning/move/fresh pile; `<name>_king_{hold,contest,warning,moved_pile,settled_pile,results}`. |
| `king-late` | Three clients; `--mode=king --mint-seconds=0.5 --tune.PlayerSendRate=20 --delay-who=Bot3 --match-delay=70`; 110 s | Every client requires moved zone/holder/mints; delayed peer checks catch-up before readiness. Use `-k`, check server scores/mints, full tank coverage on undelayed peers, and all completion samples. |
| `appearance` | Three clients; server `--start-score=8`; `-c 12ABEF,12ABEF,F6F6F6` or `000000,0000FF,FFFFFF`; render all three | Every client requires exact accepted lobby/setup/body/drop/results colors; `<name>_appearance_{lobby,tags_glow,results}`. |
| `capacity` | `make test-capacity`: 16 clients, server `--start-score=8 --capacity-metrics`, 120 s; last builder delayed 26 s | Creator proves throw then departs; survivors require transfer/final actor scores/loading inbox; `capacity PASS`; dedicated capacity checker. |

Shared combinations include `--host-server`, `--match-delay`/`--delay-who`, `--matches`, `--lobby-roam`, scalar/list
`--lobby-bots` with difficulty, named `--leave-at`/`--leave-who`, Bank/King mode/mint/supply, `--melee-start`,
`--send-tune`, capture/frame metrics and shot intervals, tank-check loading/readiness stalls, divergence nudges,
authority/body/gather traces and ordinary transport fault overrides. `tools/dev/net_bots.sh` owns startup and final
cross-process audits; its flags and Make targets are unchanged.

## Verification

Run the fake-context suite after `make build`:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=NetScenarioTests
```

The suite independently enumerates supported selections and required completion, constructs every named factory,
and checks phase dispatch, incomplete completion, repeated resets, replacement/disposal, late callbacks, the
connection lifetime of lobby callbacks, watch joiner fallback, movement host obligations and warmed allocations.
Use the native commands above and in [multiplayer.md](multiplayer.md) for scene/transport proof.
[The P4 verification report](evidence/net-scenarios-verification.md) records the original inventories, migrations,
failures, native comparisons, file sizes and paired measurements.

The server run's retirement probes live in the shared unit fixture owners (`ServerRunFixture` and
`ServerNativeFixture`), alongside the existing server driver. After `make build`, run:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ServerRunTests --server-native --match-seed=23 --port=24681
```

The normal headless gate checks all six retirement paths and repeated disposal. The opt-in native probes use real
ENet sockets, two clients, manual run time and the real codec/receive/delivery path to exhaust actor lives after
results and roster revision on a roaming departure. They assert native tuning release, detached routes and ordered
MatchEnd/Rejected delivery; they do not construct client worlds or replace the separate-process scenario matrix.
Use this checkout's unused server port and do not overlap another server on that port.

## Client authority transaction probes

`root-refusal` and `future-epoch` each require two clients and at least 15 seconds. Both use ordinary match assembly,
ENet delivery and the production codec; they retain the normal final score/conservation and tank audits. They can
repeat with `--matches=2`. `root-refusal` punctures two owned vents, sends its root explosion with the preceding
canonical epoch, then asserts native refusal, physical reconstruction and cancellation of the already owned child's
scheduled fuse. It invokes the captured canceled fuse to prove it cannot detonate the canonical vent.

`future-epoch` holds the observer's first real epoch-2 grant in a scenario-local inbox wrapper until an actual body
publication has reached BodySync's future-epoch slot. It then delivers that same grant and asserts that the newer
sequence survives. The wrapper uses reflection inside this Dev owner and restores the previous receiver on
retirement; it adds no production packet interception or scheduling path. No synthetic grant or publication is used.
Rendered runs capture `<name>_root_refused` and `<name>_future_epoch` after drawing the resolved tank camera.
Pending render continuations retire with their scenario.

```sh
tools/dev/net_bots.sh -n 2 -w 2 -l 15 -s --match-seed=23 -o "$PWD/build/authority-root" -- --scenario=root-refusal --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/authority-root/shots"
tools/dev/net_bots.sh -n 2 -w 2 -l 15 -s --match-seed=23 -o "$PWD/build/authority-future" -- --scenario=future-epoch --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/authority-future/shots"
```

The opt-in [moving-vent observer](runbooks/moving-vent-residual.md) instruments isolated parent builds without changing
these scenarios or their completion conditions. It retains correction decisions and the exact final comparison brackets.

## Mode client preservation probes

`tools/dev/mode_client_equivalence.py --revision=<commit|working> --label=<fresh-name> --observe` builds an isolated
copy under `build/` with opt-in synchronous observations. Run `ModeEnvelopeTests`, `KingOnlineTests`, `LobbyWorldTests`
and `ModeClientTests` there with `--match-seed=23 --mode-client-transcript=<absolute-jsonl-path>`. Compare parent and
candidate files byte for byte. Observations include accepted/rejected envelopes, score/spill/pile/stock/roster call
boundaries, decoded view clocks and all HUD values. The copied native RNG is fixed identically; shipping sources,
ordinary RNG and immutable release references are untouched. `--compare <parent-jsonl> <candidate-jsonl>` requires
all mode/operation coverage and reports the first changed row; its negative tests run in `make test-infra`. Delete the JSONL before repeating a run (it appends).

`ModeClientTests` asserts rule-state-to-client HUD values for Bank, King and Lobby, including singular/plural banks,
loading/results, mint progress, contest, warning and explicit read time. `ModeClientBenchTests --mode-client-bench`
is opt-in: it warms 20,000 complete physics/owned-character/render callbacks and measures 12,000 per mode by default
(`--mode-client-bench-ticks=120000` selects longer windows). It reports whole-callback allocations, mean/p95/p99,
component means, per-tick allocation sizes/counts and actor/body counts. Prepare copies without `--observe` for performance. Alternate at
least three parent/candidate pairs under the same headless/rendered workload and record machine load per pair.

`ModeClientLifetimeTests` repeatedly creates and retires Bank, King and roaming sessions, populating King's snapshot
and Lobby's departure/staging/panel state. It checks empty state/released collaborators, idempotent disposal and
collection of adapters, composed owners and snapshots while the retired Match remains reachable.
