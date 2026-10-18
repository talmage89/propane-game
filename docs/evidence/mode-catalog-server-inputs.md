# Server mode inputs — B3a follow-up

**Status:** Targeted verification complete. The final deep sweep follows this evidence commit; its result belongs in the branch report.

2026-10-18. Immediate parent `5a6cc1b`; production `695451a` and `0444729`, harness `c1c6444` and `41e0dcc`.
Linux, Godot 4.7.2 .NET, SDK 10.0.113, Debug, GTX 1660. This supersedes the server-boundary and baseline
claims in [the original catalog evidence](mode-catalog-verification.md); that record's original verification is historical.

## Ownership and retirement

Composition now selects bundles through `ModeRegistry.CreateServer`. Each LobbyServer receives immutable Server-owned
`ServerModes`: default and roaming descriptors, ordered eligible lobby descriptors, schemas and rules/wire factories.
Each ServerRun receives its selected `ServerMode`, a bound supply callback returning SuburbSettings, and a rule-tuner
callback returning ModeTuners. These inputs carry no HUD/bot factories or ModeDefinition/ModeSupply/catalog references.
Supply callbacks retain only the selected supply policies; rule-tuner copying takes the descriptor's read-only names.
The descriptors and bindings live with the server; fresh rules/wires live with the run. Native tuning remains run-owned
and is disposed by the existing retirement path. No timer, node, scheduling system or new mutable global was added.

IModeHost now exposes live, allocation-free ModeWireTuners values. KingWire and LobbyWire read those plain values at
their original synchronous read points. Stable mode IDs and existing lobby numeric bounds moved into Foundation;
ModeSettings and MatchSetup are plain wire values. Composition/client owners resolve definitions and apply numeric
settings. Server rules initialization still precedes wire construction; seed, supply, generation and tuner sampling
retain their order. Phase fallback, debug edits, schemas, bytes, supply, tuner values/classification and exposure stay
unchanged. The earlier catalog work retired ModeWires, the independent definition/schema/wire registration mechanisms
and unscoped test registrations; that retirement remains intact.

The callback regression test deliberately fails against the previous shared compiler capture: the supply lambda's
C# closure retained the local catalog even though its expression only indexed a supply dictionary. `BindSupply` now
uses a separate capture scope. The negative run has four tests/one failure, exit 1; the corrected run has four/zero,
exit 0. This tests retained ownership as well as signatures. Additional assertions cover 192 server supply projections,
live tuner copies, immutable choices/names, scoped wire-factory retirement and prototype exclusion. The 1000-read
WireTuners probe allocates zero bytes and observes live edits.

## Dependency disposition

The production commit removes 15 of the 19 B3a exceptions and the additional LobbyServer → PacketTransports N1 edge
(composition now supplies the transport). Reviewed exception count falls **78 → 62**; no new exception was added,
and no baseline entry names B3a. Removed catalog edges cover ServerRun, LobbyServer, ModeRunner, ModeSettings and
MatchSetup; removed native Tuning edges cover IModeHost, KingWire, LobbyWire and ModeSettings. ModeSettings also no
longer depends on KingRules.

Four existing edges receive precise owners rather than an unnamed promise:

- **N1:** MessageCodec → ModeRegistry/ModeDefinition. Owner-scoped schema and eligibility resolution belongs with
  decode/admission/dispatch failure boundaries. Changing that API now would span every transport/client decoder;
  N1 must inject the owner resolver without a new process-global or changing malformed-packet rejection order.
- **A1:** LobbyServer → Tuning. Application ownership must replace implicit saved-template sampling and native
  snapshot conversion, preserving staging/defaults and resource ownership on failed run creation.
- **C1:** ServerRun → Tuning. Plain run tuning/snapshot/melee contracts must replace the native resource dependency,
  preserving live debug edits, synchronous reads and exactly-once disposal.

Existing ServerRun → KingRules helper placement is inherited C1 contract work; it is not catalog resolution. B3b
still owns client adapters and prototype live-rule views. No Bots or Session files changed. Match.Assembly changes
only its four mode-resolution expressions in session configuration/content construction, outside actor assembly/removal.

## Exact parent comparison

All scratch, logs and captures use `build/b3af-*`; TMPDIR is `build/tmp`.

```sh
python3 -B tools/dev/server_equivalence.py --revision=5a6cc1b --label=b3af-parent-observed --observe
python3 -B tools/dev/server_equivalence.py --revision=working --label=b3af-candidate-observed --observe
```

The latter isolated runtime was rebuilt with final source before the accepted comparisons. Run each with:
`godot-mono --headless --path PROJECT res://scenes/dev/unit_tests.tscn -- --suite=SUITE --match-seed=23 --server-transcript=ABSOLUTE_PATH`.
`cmp` proves identical packet bytes, destination, traffic class and synchronous order:

| Suite | Tests per revision | Packet records |
| --- | ---: | ---: |
| ServerTests | 34 | 1500 |
| ParticipantServerTests | 17 | 728 |
| KingOnlineTests | 9 | 549 |
| LobbyWorldTests | 13 | 449 |
| LobbySupplyTests | 7 | 398 |
| ServerRunTests | 18 | 594 |

Total: **98 tests per revision, 4218 identical packet records**. The shared ModeInventoryTests inventory also compares
byte-identically to parent: all seven definitions, registration/lobby/prototype order, IDs, eligibility, tuner order,
factory types, pickup/ammo policy and 336 supply cases. SHA256:
`61efa2ebaf88c7b35e86a1bf12bd4b3ccb71de7e208dd36c76586785c362e48e`.
No frozen reference was regenerated.

## Final targeted gates

```sh
make setup
make build
make check
make test-equivalence
# Final root inventory after the completed gates:
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeInventoryTests --mode-inventory=ABSOLUTE_PATH
cmp build/b3af-parent-inventory.json build/b3af-final-inventory.json
git diff --exit-code 5a6cc1b -- src/Dev/Equivalence/Fixtures
```

The final complete `make check` exits 0 (`build/b3af-check.log`): Debug/ExportRelease zero warnings/errors;
architecture 2171/1023 types, 62 reviewed exceptions, zero failures; 82 source sites, zero failures; 543 unit tests,
movement/platform/body/actor/menu/native behavior, Lab, bounded generator/forced formations and separate-process
world/segment hashes pass. Infrastructure self-tests pass (including 51 compiled and 16 source architecture negatives).
The real device-event `menu_test --scenario=input --classic-lobby --port=24681` gate passes all 32 assertions.

The final `make test-equivalence` exits 0 (`build/b3af-equivalence-final.log`): frozen wire/schema/world/publication
and Bank/King/spill-chain/Short Fuse/Dissolve Lab traces pass. No references changed. Standalone final ServerModeTests
passes four tests; its deliberate capture-negative run fails one of four with exit 1, as described above.

`make -j4 test-full` runs once after this evidence commit on the final head. This record does not predeclare that
result or require a post-sweep commit; the final branch report records the exit status and depth.

## Whole server ticks

Uninstrumented isolated parent `b3af-parent-bench/project` and exact final candidate `b3af-final-native/project` use
identical existing ServerRunBenchTests instrumentation. Command:
`godot-mono --headless --path PROJECT res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench
--match-seed=23`; rendered substitutes `--disable-vsync --resolution 960x540`. Each mode warms 20000 ticks then measures
30000 complete server ticks, each with one actor pose and eight bodies. Counts match: Bank 60500 packets/19024500
payload bytes, King 60518/19025886, roaming 60000/19020000. No new steady-state allocation; the separate 1000-read
WireTuners assertion also allocates zero bytes.

[Paired raw data](mode-catalog-server-benchmark.csv) retains every completed compiler-free pair, with allocation,
mean/p95/p99, workloads, order, one-minute load and other active worktrees. Pairs before 10 run parent/candidate;
thereafter even pairs run parent/candidate and odd pairs candidate/parent. Process/load samples every 250 ms identify
actual dotnet build/MSBuild processes; any overlap discards the entire pair. Logs retain the excluded compile-overlap
pairs and the rendered launcher failure. The machine remained shared; no quiet-window waiting or precise frame-time
claim is made. The rendered environment warns V-Sync is unavailable and falls back to Enabled; settings are unchanged.

Default-runtime retained pairs: headless **1, 2, 4, 13–15, 33–34, 37–38** (10); rendered **1–2, 7–15** (11).
All other completed pairs are excluded for sampled compile overlap; rendered pair 3 failed in the launcher.
No pair is excluded for its measured cost. Median parent → candidate (times in microseconds):

| Display/mode | Bytes/tick | Mean | p95 | p99 |
| --- | --- | --- | --- | --- |
| headless Tank Bank | 6868.120 → 6868.120 | 19.666 → 18.741 | 24.200 → 24.600 | 29.200 → 29.650 |
| headless King of the Cul-de-sac | 7070.420 → 7070.430 | 19.978 → 18.157 | 27.400 → 26.000 | 30.950 → 29.450 |
| headless roaming | 6813.650 → 6813.650 | 18.681 → 18.779 | 23.850 → 24.000 | 31.350 → 34.400 |
| rendered Tank Bank | 6868.120 → 6868.120 | 22.654 → 22.587 | 24.800 → 24.700 | 31.400 → 32.000 |
| rendered King of the Cul-de-sac | 7070.420 → 7070.420 | 22.733 → 23.749 | 28.100 → 28.100 | 32.200 → 32.800 |
| rendered roaming | 6813.650 → 6813.650 | 21.716 → 22.778 | 25.000 → 25.100 | 42.000 → 42.200 |

Paired differences reverse direction, and allocations/workloads match. Initial headless Bank medians were higher;
the expanded default-runtime set reverses that mean direction while keeping the entire compiler-free set. This does
not reproduce a steady regression across the alternating runs. The large outliers remain in the CSV; this shared-load
sample cannot establish a tight sub-microsecond bound. A tiering-disabled diagnostic repeated the same six headless
pairs with `DOTNET_TieredCompilation=0`; pairs 3–5 are compiler-free and marked `tiering-disabled` in the same CSV. Allocations
still match. Their p99 outliers reach 2218.5 microseconds on the parent, illustrating the shared-load limitation; these
diagnostic results do not replace the default-runtime comparison.

## Native network and gates

| Case (`build/b3af-final-` suffix) | Grids/body pairs | Final moving difference | Audits |
| --- | --- | --- | --- |
| bank-clean | 1099 / 15244 | 0.000 m | 1, banked 25 |
| bank-faults | 1099 / 21094 | 0.111 m | 1, banked 15 |
| king-clean-r3 | 1400 / 42188 | 0.000 m | 1, minted 26 |
| king-faults | 1400 / 42176 | 0.000 m | 1, minted 27 |
| roam-clean | 1400 / 28171 | 0.000 m | 2, banked 10 and 15 |
| roam-faults | 1400 / 24839 | 0.171 m | 2, banked 16 and 13 |

All six accepted runs exit 0 with complete server audits, zero engine/protocol errors, zero unobserved pair grids
and zero disagreements. Both King peers prove hold/contest/mint/warning/move/fresh pile. Roaming peers enter/retire
three worlds and finish both matches. The tank comparison covers finite matches; waiting-world lifetime/stock is
covered by unit/parent comparisons and native logs, not a new continuous waiting-world convergence assertion.

The rendered lobby command is `build/b3af-native-godot --fixed-fps 60 --resolution 1280x720 --path .
res://scenes/dev/menu_test.tscn -- --scenario=lobby --port=24691 --capture-dir=ABSOLUTE_PATH`: 60 assertions pass,
exit 0, zero engine errors. Captures remain in `build/b3af-final-lobby-menu`. Reviewed `02_roaming`, `03_panel`,
`06_stage`, `08_match`, `09_results`, `10_return`: intact mode order, defaults, staged start, HUD/results and return.
Also reviewed Bank `Bot1_024.0.png`/`Bot1_048.0.png`, all six `Bot1_king_*.png` from the accepted King run, and roaming
`Bot1_008.9.png`/`Bot1_024.0.png`: unchanged ammo/score/clock, zone states, pile placement and match presentation.

The accepted six-case network matrix uses `tools/dev/net_bots.sh -g build/b3af-native-godot -n 2 -s '--match-seed=23'`
and fresh `-o "$PWD/build/b3af-final-CASE"` directories. The ignored launcher only replaces `--path` with an isolated,
exact-source runtime and execs **godot-mono by name**. This prevents concurrent verification builds from replacing a
live server DLL. Bank runs use `-l 45`, King `-l 60 -- --mode=king --scenario=king`, roaming `-l 25 -- --lobby-roam=8
--matches=2`. Clean runs add `-w 1 -t clean` and absolute capture directories; faulted runs add `-t faults` and
`--net-lag=120 --net-jitter=30 --net-loss=3`. No checks are skipped.

A provisional root-runtime Bank run failed startup while our own make check replaced its DLL (recorded build/error
mtimes, Main/ShutdownGuard script lookup errors). We changed the validation setup, not product behavior. Three
subsequent provisional cases passed before the capture fix; the fourth was intentionally stopped when that fix was
identified. None is counted as final proof. Their logs remain alongside the fresh final matrix. Two later King clean
runs completed the game/audits without engine errors but exited 2 when the externally maintained launcher changed
while its shell was reading it; both preserve the shutdown parse errors. The accepted King clean run is `-r3`.
Remaining rendered checks use an unchanged, syntax-checked worktree-local snapshot of that same launcher, invoked as
`godot-mono` through PATH. No graphics/system settings or external files were changed. A preliminary make check was
superseded after the capture fix and interrupted at its infrastructure phase; the final complete gate is separate.

This record does not claim Windows/macOS execution, release-export smoke, exact frame-time bounds under shared load,
general round-world replacement/online streaming, or closure of inherited FU2 debt. Protocol.Version remains 3.
The change is patch-safe: no shared message/schema/world construction/tank/tuner behavior changes; HUD and bots stay
unchanged. Remaining obligations belong to the named N1/A1/C1/B3b owners above.
