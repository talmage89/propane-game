# N1 decode, failure and transport boundary proof — 2026-10-18

**Status:** In progress. Targeted failure and exact decode/packet comparisons pass; final cost/native/gate results
are recorded below as they finish. This record does not predeclare the final deep gate.

Production parent: `5762e73` (only the initial proof harness above integrated `08d20ae`). Production candidate:
`54bd5e1` (initial boundary implementation `354d8ea`); expanded proof harness `6e51c69`. Godot 4.7.2 .NET, Linux, ordinary Debug. Frozen release references are unchanged. Raw artifacts stay in
worktree-local `build/n1-*` directories; commands below use the current worktree and `TMPDIR="$PWD/build/tmp"`.

## Ownership and scope

No gameplay state, RNG, clocks or authority model moved. The bounded parser/encoder moved from MessageCodec to
Wire's immutable PacketCodec. Composition captures schema resolution, lobby eligibility and native tuner validation
once per catalog; connection/server owners borrow those bindings. The captured schema dictionary retains no catalog
or definition/HUD/bot factories. MessageCodec remains a small Composition convenience facade for existing session
and harness callers, with its mapping explicit. NetReader receives the snapshot validator at its existing validation
point. A list-reading loop avoids adding a receiver-capturing closure when lobby eligibility becomes injected.

TrafficReliability owns lane indices/count and reliability; NetTransport maps them to ENet channels and
FaultPacketTransport uses only the neutral contract. TransportMetrics formats plain observations; CapacityMetrics
samples client observations in Composition. There is no new mutable runtime owner or generic exception framework.

Six N1 baseline edges were removed. The existing NetClient → PacketTransports edge is precisely reassigned to A1:
the compatibility default constructors still select application transport/catalog inputs. Fully explicit clients inject
both IPacketTransport and PacketCodec. Moving default creation into Main belongs to the parallel application package;
Main and Match/partials were not edited. No new baseline entry was added and none names N1. ServerRun and the
ModeCatalog/ModeRegistry composition inputs were necessarily touched; A1 must reconcile those constructor lines.

Expected decode failures are bounded-reader InvalidDataException, EndOfStreamException, FormatException and
DecoderFallbackException. Expected run admission rejections log at their old check points, including action-sequence
consumption before target rejection. Any exception escaping a handler is an owner failure, even those same types.
Queue overflow is an explicit admission result; a receiver throwing the overflow exception is a dispatch failure.

Client failure drops the connection/inbox through ordinary cleanup and notifies every offline observer despite a
broken observer or transport cleanup. Server receive/tick/departure failure detaches routes before run disposal,
clears staging, completes the creator/empty-lobby departure tail and returns the affected lobby to Waiting. It attempts
a notice/state update without disconnecting peers, retrying a partial transaction or creating a replacement run.
Construction preserves both its original and retirement failure; retirement observers release even when they throw.
Create/join carry an affected-lobby reference local to the receive call, so completed departure cannot make the
source the owner of a later construction/destination failure. A failed server run does not automatically retire
remote physical scenes; peers can leave or start another match.
There is no rollback or reconnect/resume feature.

## Failure and rejection proof

The initial harness (`5762e73`) ran `DispatchBoundaryTests --dispatch-parent`: two tests passed while explicitly
asserting the inherited bad-message classification and still-live mutated owners. The corrected default suite tests
real receive/tick/departure paths, ledger/native tuning retirement, handler/send failure before and after mutation,
accepted-before-throw sends, persistent send failures, cleanup failures and live/queued inbox delivery. Another lobby
continues publishing clocks/results; affected peers retain handles and receive subsequent Pong responses. Log capture
observes actual Godot diagnostics, including primary exception type/stack and secondary cleanup causes.

```sh
make setup
make build
for suite in TransitionBoundaryTests DispatchBoundaryTests DispatchTransportTests DecodeContextTests DecodeTranscriptTests ClientReceiveBenchTests TransportTests ProtocolTests; do
  godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite="$suite" --match-seed=23
done
```

Focused results: 2 / 6 / 10 / 3 / 1 / 1 / 35 / 67 assertions respectively, all passing. The TransportTests filter also
selects DispatchTransportTests, so these counts are not disjoint. The opt-in client benchmark additionally passes
its full workload assertions. The first expanded tick probes failed twice because the unrelated fixture lobby kept
its longer default length; explicitly setting both fixture lengths fixed the probe without changing production.
Those failed logs are retained as `build/n1-DispatchBoundaryTests.log`. A later line review found a genuine
containment defect in the first implementation: after successful departure, creation/admission failure could retire
the previous lobby. TransitionBoundaryTests first failed both cases on `354d8ea` (exit 1,
`build/n1-transition-negative.log`); `54bd5e1` fixes the receive-local owner tracking, and both now pass. The join
probe changes the destination roster inside a synchronous send callback before membership attachment.

## Exact immediate-parent comparisons

The helper creates private runtimes with identical probe sources and imported assets; it never changes another
checkout or release fixture. Prepare parent and candidate with `tools/dev/server_equivalence.py --revision=REV
--label=FRESH_NAME`, adding `--observe` only for packet transcripts. The recorded revisions are above.

Run DecodeTranscriptTests on both unobserved projects with `--decode-transcript=ABSOLUTE_PATH`, then `cmp` their
outputs: **24,047 identical prefix/first-rejection records**, including truncated fields, invalid UTF-8 and 7-bit
lengths, duplicate tuners and doubly invalid schema/settings inputs. DecodeContextTests separately verifies injected
resolver call order and retention across scoped registration restoration.

On observed projects, run each suite below with `--match-seed=23 --server-transcript=ABSOLUTE_PATH`, then `cmp`
parent/candidate files. Every record contains direction, peer, traffic class and exact bytes in call order.

| Suite filter | Identical records |
| --- | ---: |
| ServerTests (also selects ParticipantServerTests) | 1,500 |
| ParticipantServerTests | 728 |
| KingOnlineTests | 549 |
| LobbyWorldTests | 449 |
| LobbySupplyTests | 398 |
| Total, including the explicitly repeated participant coverage | 3,624 |

## Controlled whole network ticks

Final samples use ordinary Debug runtimes prepared from `5762e73` and `6e51c69`, with the same current probe
sources and no packet observation. ServerRunBenchTests covers two receives plus actual whole run ticks for Bank, King
and roaming; ClientReceiveBenchTests covers three decodes, two deliveries to the attached receiver, a lobby-list
observer and both existing client polls. Each uses 20,000 warm-up and 30,000 measured ticks; preparation is excluded.
These are connection/run callback workloads, not physical Match work or GPU frame-time claims. Final samples
use `--network-bench-long`: 300,000 measured ticks after 20,000 warm-up ticks. `cpu_us` records process CPU time
(including runtime/GC threads), separately from wall mean/p95/p99. CPU observations are outside allocation accounting.

Run each workload on each revision, alternating candidate/parent, parent/candidate, candidate/parent. Use
`godot-mono --headless` for headless samples and `godot-mono --disable-vsync --resolution 960x540` for rendered
samples, with the private project's `--path` and `res://scenes/dev/unit_tests.tscn`:

```sh
# Each revision, each pair; include --headless only for that phase:
godot-mono --headless --disable-vsync --resolution 960x540 --path PROJECT res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench --network-bench-long --match-seed=23
godot-mono --headless --disable-vsync --resolution 960x540 --path PROJECT res://scenes/dev/unit_tests.tscn -- --suite=ClientReceiveBenchTests --client-receive-bench --network-bench-long
```

`uptime` is retained before, between and after each pair. No own heavy work overlaps the final pairs. The initial
short and long groups remain in `build/n1-pairs/`; pairs 4–6 overlapped our transcript run and are excluded. Large
machine-load/cost spikes in the early headless groups were retained and retaken without waiting for a quiet window.
Other processes are hidden, so compilation attribution is inferred from load/cost spikes, not established. Final
rendered pairs are 16–18; the declared headless replacement set is 19–21. These data do not establish an absolute
quiet-machine frame budget. Gamescope reports a V-Sync fallback in rendered startup despite the requested flag;
this does not turn the synchronous callback measurements into GPU/frame-time proof.

Pending final numeric table.

## Native matrix and captures

Every net_bots output directory is fresh; complete audits retain their original thresholds. Each case runs once
with `-t clean` and once with `-t faults` plus client `--net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3`.
Every run uses `-w 1`, server `--match-seed=23`, and `--capture-dir="$PWD/build/n1-net-CASE-PROFILE/shots"`.
The reproducible command form is `tools/dev/net_bots.sh -o "$PWD/build/n1-net-CASE-PROFILE" -t PROFILE
-n N -w 1 -l LENGTH -s 'SERVER_ARGS' -- CASE_ARGS FAULT_ARGS --capture-dir=ABSOLUTE_PATH`.

| Case | N / length | Additional server args | Case args | Result |
| --- | --- | --- | --- | --- |
| Bank | 2 / 20 | `--start-score=8` | `--scenario=duel --matches=2 --duel-blocked --tune.DropScatter=0 --tune.DropToss=0` | Pending |
| King | 2 / 55 | — | `--scenario=king --mode=king` | Pending |
| Roaming | 2 / 20 | — | `--lobby-roam=5 --matches=2 --lobby-bots=1` | Pending |
| Departure | 3 / 30 | `--start-score=8` | `--lobby-roam=5 --lobby-bots=1 --leave-who=Bot1 --leave-at=12` | Pending |

## Final gates and compatibility

An intermediate `make test` passed (629 unit assertions plus core/behavior/generator/world gates) before the final
expanded harness. Final `make check`, `make test-equivalence` and final-head `make -j4 test-full` remain pending here.
No shared message shape, wire bytes, worlds, tank behavior, tuner value/classification/order or Protocol.Version
changed (still 3). Valid traffic ordering and rejection order are compared explicitly; clocks remain monotonic network
time and existing server phase time, with no new timers or random draws. Patch-safe failure containment only.
