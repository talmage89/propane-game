# Client authority transaction verification

**Status:** Focused preservation and lifetime proof complete. Final-head gates run after this record; their results
are reported in the handoff.

2026-10-17, `wp/rf-b2-client-authority`, starting dev `f10e19d` (v0.4.2). Immediate production parent `efdd511`;
extraction `d3556b6`, allocation correction `c4d765c`, scenario/lifetime harness `0ebb1d2`, capture correction
`948a8d2`, allocation assertion `7602378`, final timing probe `48ec9fc`. Linux, GTX 1660, Godot 4.7.2 .NET,
SDK 10.0.113, Debug; Linux release export uses ExportRelease.

## Ownership and boundaries

`Match.cs` is 1,067 lines, down from 1,170. Its five partials total 1,797 lines, down from 1,899.
`ClientAuthorityTransactions.cs` is 212 lines including its synchronous replica interface.

The session owner holds `outgoingAuthority`, `pendingAuthority`, `pendingBodies`, `sentExplosions`, `nextRequest`,
`gatherRequests`, the request record and each request's `Expected` map, plus the last refusal value. Gather
membership now stores its registered callback so retirement can detach it. Match retains physical exploded /
predicted-explosion tracking, body reconstruction, actor lookup/action numbering, the public notification event
and wire sends. Actor action sequences and `DropPredictions` stay on each actor; BodySync keeps correction.
M0's cached nullable player field and roster refresh semantics are unchanged.

The owner is constructed in session configuration and disposed by the existing replication stop callback, after
inbox detachment and unsubscription. Disposal detaches owned gather listeners, clears all transactions, reservations
and roots, and releases the replica. An active synchronous resolution retains its collaborator through a retiring
send and still notifies; a pending gather callback in an event invocation snapshot observes retirement and stops.
There is no new processing node, event bus, timer, clock domain or deferred authority command.

Match no longer depends on the request record layout, expected-epoch maps, reservation maps or causal/gather
bookkeeping. The component has no ActorRuntime, NetClient, WorldContext, UI or concrete tank dependency; it borrows
BodySync entries through explicit synchronous operations. This does not remove Match's existing Net-to-Player /
World module edges: the remaining composition, actor lifetime and mode adapters belong to C1/C2, L1 and B3.

Resolution preserves finish/release → dependent epoch updates → canonical reconciliation → flush → notification →
gather removal. Proposal capture stays at send time, including after the preceding send and complete blast push.
The replica carries the original body entry through adoption so synchronous correction tracing cannot change which
entry reconstruction uses. No rule, RNG, traversal, clock, tuner, wire or generated-world change was introduced.

## Exact and lifetime proof

The frozen P0 fixtures were neither regenerated nor modified. `make test-equivalence` passes wire, tuner schema,
world plans/construction, publication and all five exact Lab cases: Bank, King, spill-chain, Short Fuse and Dissolve.
Final rerun: `build/b2-equivalence-final.log` and its printed evidence directory.

The same observer is injected only into isolated source copies. World/drop RNG is seeded only in observed copies;
production and ordinary tests retain their previous entropy. The immediate parent's networking source is also
identical to starting dev. The final candidate compares byte for byte with that parent:

| Suite | Tests | Records | Bytes | Notifications |
| --- | ---: | ---: | ---: | ---: |
| AuthorityTests | 58 | 391 | 1,661,489 | 30 |
| GatherTests | 13 | 21 | 41,341 | 1 |
| AuthorityLeaseTests | 1 | 12 | 54,517 | 1 |

These record exact sends, pending requests, reservations, expected epochs, sent roots, gather membership, physical
adoption/restoration boundaries and notification order. Existing suites assert vent motion/adoption, late/future
states, queued second shots, root/child refusal, unknown-body removal, departure and creation-vs-bank confirmation.
The new lease case checks public listener / gather callback ordering, membership through notification, released
reservations, physical reconciliation and rejection of a subsequently changed epoch.

`ClientTransactionTests` passes five cases: empty flush allocation; pending queue/gather/root retirement; six repeated
sessions and weak-reference collection; disposal by an earlier notification listener; and disposal during a queued
send while preserving the in-flight notification. All six retired owners become collectible. A separately retained
disposed owner does not retain its replica collaborator. Owned gather listeners detach while external listeners
retain their previous ownership.

```sh
python3 -B tools/dev/authority_equivalence.py --revision=efdd511 --label=b2-immediate-parent --observe
python3 -B tools/dev/authority_equivalence.py --revision=working --label=b2-fixed-observed --observe
# On both prepared projects, with fresh transcript paths:
godot-mono --headless --path build/b2-fixed-observed/project res://scenes/dev/unit_tests.tscn -- --suite=AuthorityTests --match-seed=23 --authority-transcript="$PWD/build/b2-fixed-AuthorityTests.jsonl"
# Repeat with GatherTests and AuthorityLeaseTests; cmp each against b2-immediate-parent-<suite>.jsonl.
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ClientTransactionTests --match-seed=23
```

## Whole client tick measurements

Three accepted alternating pairs use identical source instrumentation, the same two actors / 58 bodies (28 tanks,
30 cars), 20,000 warm-up ticks and 100,000 measured ticks per window. The client explicitly enters Playing. One
window has ordinary Match physics callbacks; the other adds a complete atomic claim, codec/server admission/reply,
canonical reconciliation and notification before each callback. Bodies are awake and fixed; Jolt and rendering do
not step. Transport polling and whole-engine/GPU time are outside this controlled measurement.

Every active window sends 100,000 requests / 7,900,000 bytes and receives 100,000 notifications. The parent and
candidate production sources were checked against `efdd511` and `c4d765c`; the measurement source is identical.
The child processes use CPUs 10–11, with a process monitor on CPU 0. Each accepted window records zero foreign
worktree load. The third attempted parent window detected another benchmark and was rejected; `3b` replaces the
whole pair after a fresh quiet interval. No timing samples are trimmed.

| Window | Bytes/tick parent → candidate | Median process mean µs | Median p95 µs | Median p99 µs |
| --- | --- | --- | --- | --- |
| Ordinary physics | 1862.75 → 1862.75 | 11.854 → 11.625 | 36.7 → 36.1 | 62.3 → 62.2 |
| Claim/reply + physics | 9020.23 → 8988.23 | 25.659 → 25.491 | 49.1 → 48.1 | 76.4 → 74.8 |

Allocation is identical in the ordinary window and exactly 32 bytes lower per authority tick in every pair.
The third authority mean is 1.2% higher (25.659 → 25.963 µs); the other two means and all authority p95/p99 values
improve. There is no repeatable whole-callback or tail regression. These results establish callback cost, not a
whole-game throughput claim. Raw accepted results: `build/b2-long-{1,2,3b}-{parent,candidate}.log`; process monitoring:
`build/b2-long-bench{,-last}.log`. The [tick CSV](client-authority-ticks.csv) retains every accepted process result.

```sh
python3 -B tools/dev/authority_equivalence.py --revision=efdd511 --label=b2-long-parent
python3 -B tools/dev/authority_equivalence.py --revision=working --label=b2-long-candidate
# Three pairs, parent/candidate then candidate/parent then parent/candidate, under a quiet CPU/GPU window:
taskset -c 10,11 godot-mono --headless --path build/b2-long-parent/project res://scenes/dev/unit_tests.tscn -- --suite=AuthorityBenchTests --authority-bench --match-seed=23
# Repeat with build/b2-long-candidate/project and retain each process's output.
```

## Diagnostic failures retained

The first observer build inserted a line inside a multiline condition; another encoded a diagnostic Authority with
invalid match id zero. Both were instrumentation-only failures and were corrected before accepted comparisons.
A few early scenes started before their observed assembly finished compiling; those outputs lack transcripts and
are excluded. Final observed scenes start only after the complete build succeeds.

Initial short timing probes exposed a compiler-generated 24-byte closure even in an empty Flush. The added assertion
fails on that owner with **24,000 bytes / 1,000 calls**, then passes with zero bytes after moving the claim selector
into a proposal helper. Its collaborator snapshot and sampling order are unchanged. These negative and corrected
logs remain `build/b2-allocation-{negative,fixed}.log`. Short/partly warmed timing runs and loading-phase probes are
retained as diagnostics; the final timing conclusion uses only the explicit Playing / long warm-up results above.

Initial root/future screenshots read the preceding player-camera framebuffer during physics. The harness now waits
for FramePostDraw, checks its scenario lifetime and requests asynchronous readback. Final native runs below review
those corrected tank-camera captures. No production camera or input path changed.

## Final native matrix

All eleven final-code cases passed, with fourteen complete server audits, zero disagreements, zero unobserved
pair grids and zero engine/protocol errors in an independent scan. The four 90-second soaks each used four clients
and one rendered client. Final resting/moving drift and every accepted process result are retained in the adjacent
[native CSV](client-authority-native.csv). Departure compares the two survivors after a three-client start. Earlier pre-allocation-fix native runs
also passed, but the table below uses only final production code.

| Run | Audits | Grids | Body pairs | Longest episode s | Final moving drift m |
| --- | ---: | ---: | ---: | ---: | ---: |
| root-repeat | 2 | 998 | 27924 | 0.30 | 0.147 |
| future-repeat | 2 | 998 | 27944 | 0.00 | 0.000 |
| gather | 1 | 1100 | 32903 | 0.25 | 0.000 |
| puncture-race | 1 | 599 | 16772 | 0.15 | 0.033 |
| drop-refusal | 1 | 599 | 17333 | 0.25 | 0.000 |
| departure | 1 | 900 | 22328 | 0.40 | 0.436 |
| repeat | 2 | 1198 | 177456 | 0.55 | 0.174 |
| bank-clean | 1 | 2000 | 182593 | 1.45 | 0.177 |
| bank-faults | 1 | 1999 | 140033 | 0.45 | 0.344 |
| king-clean | 1 | 1999 | 419478 | 0.25 | 0.101 |
| king-faults | 1 | 1999 | 343369 | 0.95 | 0.246 |

Final puncture-race: 280 paired trajectories, median/p95/max drift **0.098/0.212/0.344 m**.
Root refusal and future-epoch each repeat twice on one connection. The latter holds a real decoded epoch-2 grant
until an actual cross-lane publication reaches BodySync, then proves that its sequence survives. No synthetic
authority or motion is introduced. Gather asserts shader readback, ownership, two shields, glides and stable intact
pile membership on both rendered clients. Drop refusal ends at banks 8/7 with exactly one accepted spill.

All runs keep launcher score/conservation, liveness, completion and tank checks enabled, at unchanged thresholds.
Use fresh output directories when repeating:

```sh
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-root-repeat" -n 2 -w 2 -l 15 -s '--match-seed=23' -- --scenario=root-refusal --matches=2 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-root-repeat/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-future-repeat" -n 2 -w 2 -l 15 -s '--match-seed=23' -- --scenario=future-epoch --matches=2 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-future-repeat/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-gather" -n 2 -w 2 -l 45 -s '--start-score=8 --match-seed=23' -- --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-gather/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-puncture-race" -n 2 -w 2 -l 20 -s '--match-seed=23' -- --scenario=puncture-race --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-puncture-race/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-drop-refusal" -n 2 -w 2 -l 20 -s '--start-score=8 --match-seed=23' -- --scenario=drop-refusal --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-drop-refusal/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-departure" -n 3 -w 1 -l 35 -s '--start-score=8 --match-seed=23' -- --lobby-roam=3 --lobby-bots=1 --leave-who=Bot1 --leave-at=15 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-departure/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-repeat" -n 4 -w 1 -l 20 -s '--match-seed=23' -- --lobby-roam=3 --matches=2 --lobby-bots=1 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-repeat/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-bank-clean" -n 4 -w 1 -l 90 -t clean -s '--match-seed=25' -- --melee-start --shot-every=30 --capture-dir="$PWD/build/b2-final-net-bank-clean/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-bank-faults" -n 4 -w 1 -l 90 -s '--match-seed=25' -- --melee-start --shot-every=30 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-bank-faults/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-king-clean" -n 4 -w 1 -l 90 -t clean -s '--match-seed=25' -- --mode=king --melee-start --shot-every=30 --capture-dir="$PWD/build/b2-final-net-king-clean/shots"
tools/dev/net_bots.sh -o "$PWD/build/b2-final-net-king-faults" -n 4 -w 1 -l 90 -s '--match-seed=25' -- --mode=king --melee-start --shot-every=30 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/b2-final-net-king-faults/shots"
python3 -B tools/dev/compare_vents.py build/b2-final-net-puncture-race/bot*.log
```

Reviewed final-code captures (under `build/b2-final-net-<run>/shots/`):

- `root-repeat/Bot1_root_refused.png`: recreated canonical vent, jet and surviving prediction effects.
- `future-repeat/Bot2_future_epoch.png`: intact canonical tank stack after the overtaking publication/grant.
- `gather/gather_host_shimmer.png` and `gather_host_settled.png`: the two intact drops and the resulting stable pile.
- `puncture-race/Bot1_race_600.png`: canonical vent/jet and the moving shared copy.
- `drop-refusal/victim_drop_refused.png`: bank 7 and one surviving intact drop.
- `bank-clean/Bot1_060.0.png`: local bank/highlight/popup agreement, remote tags and blast/debris.
- `bank-faults/Bot1_060.0.png`: thrown owner and coherent bank/HUD while remote actors continue.
- `king-clean/Bot1_060.0.png`: owned actor, spilled tank and mint HUD.
- `king-faults/Bot1_060.0.png`: moving owner, shared zone/holder, bank and hit feedback.

Screenshots support scene/presentation review; their single frames do not establish sequence ordering or trajectory
convergence. Those are asserted by the scenarios, transcripts and cross-client checkers.

## Gates, export and compatibility

The initial `make check` passed 520 unit tests, all behavior/generator/world checks and infrastructure negatives,
with zero compiler warnings. Its real device input checks passed 165 assertions. The subsequent empty-flush
assertion raises the final unit inventory by one. Final-head `make check` and `make -j4 test-full` run after this
record; their exact outcomes are in the handoff rather than a premature claim here.

```sh
make setup
make build
dotnet format Propane.sln --verify-no-changes --no-restore
make test-equivalence
mkdir -p build/b2-linux-final
godot-mono --headless --path . --export-release Linux "$PWD/build/b2-linux-final/Propane.x86_64"
build/b2-linux-final/Propane.x86_64 --headless -- --release-test
# Actual exported server and smoke client, with an editor NetTest guest:
build/b2-linux-final/Propane.x86_64 --headless -- --server --port=24681
build/b2-linux-final/Propane.x86_64 --headless -- --release-test --scenario=network --connect=127.0.0.1:24681 --length=20
godot-mono --headless --path . res://scenes/dev/net_test.tscn -- --role=join --players=2 --name=ExportGuest --server=127.0.0.1 --port=24681 --length=20
# After the evidence commit:
make check
make -j4 test-full
```

The actual Linux ExportRelease executable passes menu/single-player construction (40 tanks) and a client/server
match with one editor guest. The network smoke covers its initial lobby, match, results and a fresh lobby return.
Both clients and the server agree on final banks **4/11**; the complete bank audit passes. Independent scans of all
three process logs find zero engine/protocol errors. The server and both clients are stopped/reaped by the smoke
runner. These are Linux checks despite the smoke's historical “cross-platform” log label. Export evidence is
`build/b2-export-linux-final.log`, `b2-release-smoke-final.log`, `b2-export-network-result.log` and
`b2-export-{server,network-smoke,guest}.log`.


The expected capture-save failures in the ordinary unit gate remain deliberate negative probes, not native soak
errors. No threshold, assertion, reference or bot behavior was relaxed. No other package's production implementation
was edited. Shared harness integration touches NetScenarioRegistry and NetScenarioTests; contract/index updates touch
docs/multiplayer.md, docs/net-scenarios.md, docs/release-reference.md and docs/README.md.

Windows and macOS execution were not verified. Actor registration lifetime, mode adapters, the rest of Match and
FU2's moving-vent investigation remain their existing packages. Messages, protocol bytes, worlds, tanks, tuner values
and shared/local classification are unchanged. Protocol.Version remains **3**; this is patch-safe. No push, merge or
release was performed.
