# Mode client adapters — B3b1

**Status:** Focused verification complete; final-head gate outcomes are reported in the branch handoff.

2026-10-18, branch `wp/rf-b3b1-client-adapters`. Starting runtime `75e76b2`; immediate production parent `79e22c4`
adds only the preservation harness. Linux, Godot 4.7.2 .NET (`ed1daf0bf`), SDK 10.0.113, Debug, GTX 1660 Forward+.
All generated logs, JSONL, measurements and captures live under `build/b3b1-*`.

## Ownership and retirement

Bank scores/spill policy moved into `BankClientAdapter`; King composes Bank with its received snapshot and
`ClientModePiles`. Explicit `LobbyClientAdapter` owns roster decisions, departed identities, staging/panel state,
stock marking, magazine refill and pile policy. `SilentClientAdapter` explicitly handles Sandbox and supplies the
shared zero-bank roster/owner simulation-clock grace behavior. No client fallback remains: every network bundle
must provide its factory, and missing, unknown or null-returning integrations fail.

Match retains callback order, identity, envelope/schema/world/round/order checks, physical replication, bot/view
binding and lifetime coordination. `IClientModeReplica` and `IClientLobbyReplica` expose synchronous operations;
adapters never read Match fields or concrete HUD/front-end objects. The existing stop callback is registered before
factory invocation so partial assembly uses the same retirement. It disposes the adapter after inbox detach and
unsubscription. Disposal clears banks, received snapshot, departed identities and flags, drops borrowed collaborators
and removes Match's reference. Pile-center sampling remains inside the optional presentation call, preserving the
headless King's original short circuit and allocation behavior.

`Match.cs`: 1,068 → 931 lines. Match's partials: 1,825 → 1,612 (including the new 75-line synchronous binding partial;
excluding the independent `MatchInbox`). The adapter owns the stable live score container and immutable display
choices; Match borrows those feeds during configuration and releases them at retirement.
World/actor replication and bot/view ownership remain C2; the main file still exceeds the size review threshold.
No Lab/Bots/IBotWorld files changed. Generic input delivery stays in Match; Lobby's decisions use the same device path.

All new runtime types/files map explicitly to Client (the binding partial stays Composition). No baseline exception
was added or removed. Nine existing `bot-live-owner` entries were relabeled B3b → B3b2 with reasons naming local
Lab/IBotWorld observations; they cannot be resolved in this client-only half. Direct Match policy references to
TankBankRules and pile payload handling retired; the remaining King/Lobby references bind presentation/lifetime.
A generic native lookup projects resting intact authority bodies without adding a Client → Server enum dependency.

## Differential, HUD and lifetime proof

`tools/dev/mode_client_equivalence.py` creates isolated parent/candidate builds, seeds world/spill randomness only
there, and injects opt-in observations. Run the same suites with `--match-seed=23` and an absolute
`--mode-client-transcript` path: `ModeEnvelopeTests` (11), `KingOnlineTests` (9), `LobbyWorldTests` (13),
`ModeClientTests` (5). Their combined 207 observations match exactly, including 23 incoming envelopes, 18 accepted
adapter dispatches, rejected envelopes, ordered scores/spills/drops/piles/stock/roster/refill boundaries, outgoing
mode-event bytes, decoded view clocks and all HUD fields. Comparison rejects missing coverage, missing modes,
changed values/order, truncation and empty files (seven negative checker cases, also wired into test-infra).

Focused assertions compare client feeds with rule states: Bank loading/play/results, singular/plural banks and
objectives; King holder/contest/warning/minted counters, progress and read-time clocks; Lobby zero banks/empty mode
HUD. They also cover spill acceptance/refusal and duplicate/empty pile placement. Catalog: 19 cases, zero failures;
explicit/fresh adapter types, required factory, null/throwing factory failure, display values and scoped restoration.

Lifetime: three sessions each for Bank, King and Lobby in one process, with populated King snapshots and Lobby
departure/staging/panel state. Weak references cover adapters, composed owners, score containers and decoded state
while retired Match remains strongly reachable. State/bindings clear; disposal is idempotent and a captured late stock
callback is harmless. Real Match configuration followed by an injected failure proves rollback for all three modes.
The focused mode suites pass eight cases with zero failures.

Retained failed attempts: an early observer missed the parent's pile-send partial; fixing instrumentation produced
the exact transcript above. A collection fixture initially kept its own Debug local adapter reference; moving fixture
setup to a non-inlined helper removed that test root, retaining the same collection assertion. Early check invocations
also rejected formatting, a direct Server enum reference (replaced by the explicit native lookup) and missing diagnostic
clock approvals. Those logs remain; none counts as passing acceptance.

## Native and visual proof

The final six-run matrix uses `c7a8cec` (production `e595602`). Every run uses two native clients, one rendered,
`--match-seed=23 --net-seed=11`; faults add `--net-lag=120 --net-jitter=30 --net-loss=3`. Output names are
`build/b3b1-final-{bank,king,lobby}-{clean,fault}`; exact argument vectors are retained in sibling `-command.txt`
files and complete launcher/checker output in `-run.log`. Bank uses 20-second blocked duels, start score 8 and
zero DropScatter/DropToss. King runs its scripted 60-second zone/contest/move/pile scenario. Lobby runs two
20-second matches with one owned bot per client and 10 seconds roaming before/between/after matches.

| Mode/fault | Grids / body pairs | Final rest / moving residual (m) | Complete server audits |
|---|---:|---|---|
| Bank clean | 600 / 19,920 | 0.000 / 0.061 | 1: initial 16 + drop 1 − spills 7 = 10 |
| Bank faulted | 600 / 19,876 | 0.000 / 0.181 | 1: initial 16 + drop 1 − spills 7 = 10 |
| King clean | 1,399 / 42,160 | 0.000 / 0.000 | 1: minted 26 = 26 |
| King faulted | 1,400 / 42,164 | 0.000 / 0.000 | 1: minted 26 = 26 |
| Lobby clean | 1,198 / 16,142 | 0.001 / 0.118 | 2: map 34 + drops 3 − spills 10 = 27; map 32 + drops 2 − spills 8 = 26 |
| Lobby faulted | 1,199 / 11,521 | 0.000 / 0.141 | 2: map 34 = 34; map 35 + drop 1 − spills 6 = 30 |

All rows have zero unobserved pair grids, disagreements and engine/protocol errors; both bots agree with all eight
server audits. All 18 server/client logs independently scan clean for engine errors. Bank final standings are 9/1;
King 26/0. Faulted Lobby's longest above-tolerance episode is 0.60 seconds, within the unchanged checker bound.

The first Lobby clean run crossed the machine's roughly four-hour sleep; its exit 0 is invalid evidence. The first
Lobby fault run was incomplete. Both are retained and excluded (`b3b1-sleep-invalid.txt`). Fresh resumed Lobby
clean/faulted runs passed, then this entire final matrix was repeated on the committed extraction. The original
Bank/King native runs and six timing pairs finished before sleep and remain diagnostic evidence.

Reviewed final captures: Bank `shooter_03.2.png` shows bank 8, 0:17, leaderboard 8/6, remote score tag 6 and ammo 27/60;
King hold shows singular bank/minted 1, the holding banner and progress bar; contest shows bank/minted 2 and no bar;
warning shows bank/minted 17 and "THE ZONE MOVES IN 3". The moved/settled pile captures show the relocated ring and
standing fresh pile; results show matching 26/0 standings and the original column/counter labels.

Rendered `menu_test --scenario=input --classic-lobby` passes 165 real-device assertions, including online mouse/
gamepad look, fire, melee, jump, reload, pause and retirement. Reviewed match before/after-turn captures show the
camera turn with bank/clock/ammo intact. Rendered `--scenario=lobby` passes 60 assertions through panel controls,
roster changes, owned bots, canceled/restarted staging, results and a fresh returned world. Reviewed `01_arrival`,
`03_panel`, `05_bot_roaming`, `06_stage` and `10_return`: empty score HUD; name tags for Guest/Bot 1; docked settings;
stage lineup/countdown without world tags/crosshair; returned greeting, retained bot and piles. Captures live in
`b3b1-final-input` and `b3b1-final-menu-lobby` (1280×720).

Linux export inventory: 1,055 types, 511 resources, zero failures (including Dev exclusion). Exported ordinary menu,
classic menu, headless server and rendered `--release-test` all pass without engine errors. Reviewed its main menu
and populated 40-tank single-player captures (960×540); the counter reads 40. Executed from the export directory
through the invisible wrapper with `PROPANE_REAL_GODOT`, without source fallback. No Windows/macOS runtime claim.

## Performance and limits

Three alternating headless pairs and six renderer-enabled pairs, identical instrumentation on parent/candidate, warm
20,000 and measure 120,000 complete Match physics, owned-character physics and Match render callbacks per mode.
The final candidate uses the committed production extraction (`e595602`); benchmark instrumentation is `c7a8cec`.
Counts are 2 actors/1 owned/58 bodies for Bank/King and 1 actor/1 owned/0 bodies for Lobby, on stationary native bodies.
All 27 mode pairs have exactly equal allocation totals, including the per-tick inventories (asserted to sum to the
whole window). Inventory bucket counts can differ when equal-cost operations coincide on different ticks. Byte
comparison verified all 216 parent and 223 candidate production C# sources against their respective revisions.

| Workload | Bytes per revision, each pair | Median paired change: mean / p95 / p99 |
|---|---:|---|
| Headless Bank | 267,196,096 | −0.77% / −0.08% / +2.17% |
| Headless King | 306,558,112 | −1.58% / −0.46% / −0.45% |
| Headless Lobby | 167,319,776 | −0.63% / +0.80% / +6.88% |
| Rendered Bank | 393,922,144 | +0.65% / +1.29% / +2.83% |
| Rendered King | 433,283,872 | +3.48% / +4.19% / +4.18% |
| Rendered Lobby | 294,045,536 | +3.20% / +4.49% / +4.58% |

The positive rendered medians warranted the three additional pairs, all retained above. Rendered Bank's second
pair is +12.78% mean/+32.17% p95 and its third −14.37%/−19.27%. Rendered Lobby's fourth pair is +71.26% mean,
with unchanged actor cost rising 26.982 → 44.760 µs while machine load rose 3.01 → 5.06; that sample is unsuitable
for attributing a code cost. It is retained rather than silently removed. Raw costs, component means, allocation
inventories and loads: `b3b1-finalpair-*`; summary `b3b1-finalperformance.json`.

Three further renderer-enabled pairs restricted both processes to CPUs 0/1 through the launcher's process affinity
(no machine configuration change). Same binaries, instrumentation and workload; allocations exactly equal again.

| Affinity-controlled workload | Median paired change: mean / p95 / p99 |
|---|---|
| Bank | −10.11% / −23.94% / −21.45% |
| King | −4.86% / −7.69% / −2.16% |
| Lobby | +1.67% / −1.41% / +3.52% |

King means are lower in all three affinity pairs (−23.16%, −4.17%, −4.86%); Lobby means swing +92.98%, −56.57%,
+1.67%. Machine load rose 3.24 → 8.66 in the first pair, with large changes in unchanged actor callbacks; its timing
is diagnostic only. No speedup is claimed. Raw data: `b3b1-affinitypair-*`, `b3b1-affinityperformance.json`.
Across all 36 mode pairs there is no added steady-state allocation. Timing does not establish a repeatable slowdown
across the CPU-placement cohorts, but shared-load variance prevents a tight regression bound; the positive
unrestricted rendered medians remain a review limitation. Uptime is recorded before each pair and each process;
no local compile overlapped. No quiet-window or native capacity claim is made.

Retained diagnostics: the first three pairs per workload (`b3b1-pair-*`, 3,000 warm/12,000 measured) had identical
Bank/Lobby allocations and small King one-off variations in both revisions, with headless King mean/Lobby rendered
tails increased. Reversed pairs with stage timing (`b3b1-counterpair-*`) removed those increases. The final longer
windows above include the stable score/display feed cache. All earlier data remains available; no reference or
threshold was weakened. This measures these callbacks, not engine/Jolt frame time or capacity. Native online runs
separately exercise populated worlds.

## Reproduction and gate handoff

Initial `make setup` and repeated `make build` passed; the last focused build is warning/error-free in
`b3b1-final-build.log`. Production is `e595602`; harness completion is `c7a8cec`. Shared harness files touched outside
the new mode probes: `ActorLifetimeFixture` now reads the public score feed, authority observation preparation finds
the moved spill RNG, and `lint_tests.py` runs the comparator negatives. No other package's runtime owners changed.

Focused commands (paths are worktree-local; use fresh labels/output directories):

```sh
python3 -B tools/dev/mode_client_equivalence.py --revision 79e22c4 --label b3b1-parent-proof --observe
python3 -B tools/dev/mode_client_equivalence.py --revision working --label b3b1-cached-proof --observe
# On each prepared project, append the four suites to a new JSONL, in the order listed above:
godot-mono --headless --path build/LABEL/project res://scenes/dev/unit_tests.tscn -- \
  --suite=SUITE --match-seed=23 --mode-client-transcript="$PWD/build/LABEL.jsonl"
python3 -B tools/dev/mode_client_equivalence.py --compare build/b3b1-parent-proof.jsonl build/b3b1-cached-proof.jsonl
python3 -B tools/dev/mode_client_equivalence_tests.py
python3 -B tools/architecture/source_check.py
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- \
  --suite=ModeClientTests,ModeClientLifetimeTests,ActorLifetimeTests
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeCatalogTests
```

The focused commands pass: exact 207-row comparison; seven negative comparator cases; 92 source approvals and zero
failures; eight mode/lifetime cases; 19 catalog cases. Performance uses the same preparer without `--observe`, with
the identical benchmark copied to both revisions. Run alternating orders at least three times, recording `uptime`:

```sh
godot-mono --headless --disable-vsync --resolution 960x540 --path build/LABEL/project \
  res://scenes/dev/unit_tests.tscn -- --suite=ModeClientBenchTests --mode-client-bench \
  --mode-client-bench-ticks=120000 --match-seed=23
# Repeat without --headless for the renderer-enabled cohort.
tools/dev/net_bots.sh -n 2 -w 1 -l 20 -t clean -s '--match-seed=23 --start-score=8' \
  -o "$PWD/build/b3b1-final-bank-clean" -- --net-seed=11 --capture-dir="$PWD/build/b3b1-final-bank-clean/shots" \
  --scenario=duel --duel-blocked --tune.DropScatter=0 --tune.DropToss=0
# King: -l 60, no --start-score, --mode=king --scenario=king instead of the duel options.
# Lobby: no --start-score, --lobby-roam=10 --matches=2 --lobby-bots=1 instead of the duel options.
# Faulted counterparts: fresh output, -t faults, plus --net-lag=120 --net-jitter=30 --net-loss=3.
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- \
  --scenario=input --classic-lobby --port=24683 --capture-dir="$PWD/build/b3b1-final-input"
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- \
  --scenario=lobby --port=24683 --capture-dir="$PWD/build/b3b1-final-menu-lobby"
godot-mono --headless --path . --export-release Linux "$PWD/build/b3b1-export/Propane.x86_64"
```

The export uses `Architecture.dll inventory` on its actual DLL/PCK, then the existing artifact checker's bounded
runner for exported `--quit-after 120`, `--classic-lobby`, `--server --port=24683` and rendered `--release-test`.
Exact commands and logs remain under `build/b3b1-*`. The final `make check`, `make test-equivalence` and
`make -j4 test-full` run after this record is committed; their results belong to the final-head handoff, not an
advance pass claim here. Earlier failed checks are described above. Immutable references were never regenerated.

## Compatibility

Patch-safe structural extraction: no rules, tuners/classification, worlds, traversal/RNG draw order, clocks,
processing order, online eligibility, wire shape/bytes, health finalization or round replacement changes.
`Protocol.Version` remains 3. Immutable release references are untouched. B3b2 and inherited FU2/capacity obligations
remain separate; final gate results must be reported without treating inherited failures as passes.
