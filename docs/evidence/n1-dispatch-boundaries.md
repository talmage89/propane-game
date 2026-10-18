# N1 decode, failure and transport boundary proof — 2026-10-18

**Status:** Targeted correctness, immutable references, paired cost and clean/faulted native matrix proof passed.
The final-head deep gate runs after this evidence commit; its result is in the final branch handoff, not predeclared here.

Production parent: `5762e73` (only the initial proof harness above integrated `08d20ae`). Production candidate:
`418ca81` (initial boundaries `354d8ea`, transition ownership `54bd5e1`); expanded proof harness `6e51c69`
with formatting/source-site declarations in `1961446` and `a530b40`. Godot 4.7.2 .NET, Linux, ordinary Debug. Frozen release references are unchanged. Raw artifacts stay in
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
Main and Match/partials were not edited. No new dependency baseline entry was added and none names N1. Two new Dev-only monotonic benchmark clock sites
are explicitly declared in source-sites.tsv; they are diagnostic observations, not gameplay deadlines. ServerRun and the
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

The exploratory samples use ordinary Debug runtimes prepared from `5762e73` and `6e51c69`, with the same
current probe sources and no packet observation. Their 208-byte client allocation reduction held, but the headless
client CPU direction was consistently higher in one balanced set despite opposite rendered results. `418ca81`
removes the newly added polling/body-admission wrapper calls, retaining the guards in the original methods.
Final paired proof repeats the same source harness against that runtime after the functional gate. Its sample set
is declared in advance as pairs 22–24 in both headless and rendered phases. ServerRunBenchTests covers two receives plus actual whole run ticks for Bank, King
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
Other processes are hidden, so compilation attribution is inferred from load/cost spikes, not established. Exploratory
rendered pairs 16–18 and headless 19–21 are retained, not claimed as acceptance of the final inline guards. These
data do not establish an absolute
quiet-machine frame budget. Gamescope reports a V-Sync fallback in rendered startup despite the requested flag;
this does not turn the synchronous callback measurements into GPU/frame-time proof.

Final 22–24 results (median of the three measurements for each column; raw per-pair fields remain in
`build/n1-pairs/accept.json` and individual logs):

| Phase / workload | Bytes/tick parent → candidate | CPU µs parent → candidate | Wall mean / p95 / p99 µs parent → candidate |
| --- | ---: | ---: | --- |
| headless / Tank Bank | 6868.11 → 6868.11 | 16.262 → 14.958 | 16.727/23.1/27.6 → 14.906/15.5/22.5 |
| headless / King of the Cul-de-sac | 7092.35 → 7092.34 | 16.069 → 15.767 | 15.755/17.4/29.6 → 15.458/16.9/23.1 |
| headless / roaming | 6813.65 → 6813.65 | 17.193 → 14.532 | 18.238/24.1/31.3 → 14.478/15.3/20.6 |
| headless / client | 3067.92 → 2859.91 | 7.271 → 5.717 | 7.053/8.3/9.8 → 5.353/7.7/8.5 |
| rendered / Tank Bank | 6868.11 → 6868.11 | 16.644 → 15.522 | 16.957/23.1/24.8 → 15.361/17.9/24.0 |
| rendered / King of the Cul-de-sac | 7092.35 → 7092.35 | 16.874 → 16.816 | 16.437/22.5/27.6 → 16.379/22.3/27.9 |
| rendered / roaming | 6813.65 → 6813.65 | 15.800 → 15.487 | 15.656/21.8/25.3 → 15.330/20.2/24.4 |
| rendered / client | 3067.92 → 2859.91 | 8.248 → 6.053 | 7.756/12.0/13.4 → 5.635/8.1/11.2 |

Each server case measures 300,000 ticks, 600,000 receives and eight bodies per state batch. Bank emits
605,000 packets / 190,245,000 bytes; King 605,168 / 190,257,936; roaming 600,000 / 190,200,000.
Client measures 900,000 decodes / 198,900,000 bytes and 600,000 deliveries to its attached receiver, plus
300,000 lobby-list observer calls. Counts match exactly in every parent/candidate pair. Bank/roaming allocations
match; King's one-hundredth-byte variation is below 3 KB over 300,000 ticks, not a repeatable per-tick increase.
Client allocation falls by about 208 bytes/tick. No repeatable CPU/whole-tick/tail regression appears in the final
pairs. Shared-load noise prevents treating the apparent improvements as a portable speedup claim.

The final accepted runtimes use production `418ca81` / harness/policy through `a530b40`, compared with `5762e73`.
Valid packet and decode records also compare exactly with `6e51c69`, the immediate parent of the hot-path cleanup.

## Native matrix and captures

Every net_bots output directory is fresh; complete audits retain their original thresholds. The first matrix on
`6e51c69` passed clean Bank (two complete audits) and was stopped during clean King after the hot-path follow-up;
its process group was terminated and verified to hold no live processes. These superseded/interrupted logs remain
under `build/n1-net-*`; final acceptance uses new `build/n1-net-final-*` directories. Each case runs once
with `-t clean` and once with `-t faults` plus client `--net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3`.
Every run uses `-w 1`, server `--match-seed=23`, and `--capture-dir="$PWD/build/n1-net-final-CASE-PROFILE/shots"`.
The reproducible command form is `tools/dev/net_bots.sh -o "$PWD/build/n1-net-final-CASE-PROFILE" -t PROFILE
-n N -w 1 -l LENGTH -s 'SERVER_ARGS' -- CASE_ARGS FAULT_ARGS --capture-dir=ABSOLUTE_PATH`.

| Case | N / length | Additional server args | Case args | Result |
| --- | --- | --- | --- | --- |
| Bank | 2 / 20 | `--start-score=8` | `--scenario=duel --matches=2 --duel-blocked --tune.DropScatter=0 --tune.DropToss=0` | Clean/faults PASS |
| King | 2 / 55 | — | `--scenario=king --mode=king` | Clean/faults PASS |
| Roaming | 2 / 20 | — | `--lobby-roam=5 --matches=2 --lobby-bots=1` | Clean/faults PASS |
| Departure | 3 / 30 | `--start-score=8` | `--lobby-roam=5 --lobby-bots=1 --leave-who=Bot1 --leave-at=12` | Clean/faults PASS |

Native results (`build/n1-native-final-matrix.log`, all eight exit 0):

| Case / profile | Grids / body pairs | Final rest / moving error (m) | Conservation |
| --- | ---: | ---: | --- |
| bank / clean | 1198 / 40270 | 0.001 / 0.048 | All 2 bots agree with 2 server audits. |
| king / clean | 1300 / 38788 | 0.000 / 0.000 | All 2 bots agree with 1 server audits. |
| roam / clean | 1201 / 16315 | 0.000 / 0.081 | All 2 bots agree with 2 server audits. |
| departure / clean | 801 / 18502 | 0.000 / 0.060 | All 2 bots agree with 1 server audits. |
| bank / faults | 1199 / 39213 | 0.001 / 0.144 | All 2 bots agree with 2 server audits. |
| king / faults | 1300 / 38776 | 0.000 / 0.000 | All 2 bots agree with 1 server audits. |
| roam / faults | 1200 / 12079 | 0.001 / 0.445 | All 2 bots agree with 2 server audits. |
| departure / faults | 800 / 26182 | 0.001 / 0.224 | All 2 bots agree with 1 server audits. |

Every case has zero unobserved pair grids, zero disagreements and zero engine/protocol/owner-failure errors.
The ordinary checker thresholds are unchanged; transient peaks remain in the raw audit logs above their final
errors. Creator departure retains frozen standings while survivors continue; roaming runs include two owned bots,
two matches and repeated waiting worlds. This is targeted coverage, not ordinary sixteen-client capacity proof.

Reviewed final 960×540 captures: clean Bank `shooter_06.5.png`; faulted Bank `shooter_blast_1.80.png`; clean King
`Bot1_king_contest.png` and `Bot1_king_moved_pile.png`; faulted King `Bot1_king_settled_pile.png` and
`Bot1_king_results.png`. Bank actors/tags/ammo/blast and scores are present; King contest/hold, moved zone, upright
fresh pile, mint progress and 23–0 results agree with the scripted states. The default roaming/departure owners
produce no scenario screenshots; their rendered processes and full audits pass, without a claim of reviewed images
for those cases. These samples do not prove continuous animation or subjective feel.

## Final gates and compatibility

An intermediate `make test` passed (629 unit assertions plus core/behavior/generator/world gates) before the final
expanded harness. The complete `make check` passed (`build/n1-check-4.log`): 650 unit assertions, core/behavior/device input,
bounded generation/formations, separate-process world/segment hashes and gate self-tests. Debug/ExportRelease
builds have zero warnings/errors; compiled architecture checks report 2,375/1,074 types, 17 reviewed exceptions
and no failures; source checks report 94 declared sites and no failures. Early check attempts found untracked
evidence, formatting and missing diagnostic clock declarations; these were corrected without weakening the gates.

`make test-equivalence` passed (`build/n1-equivalence.log`, `build/rfp0-equivalence-18`): frozen
wire/schema/world/publication and Bank, King, spill-chain, Short Fuse and Dissolve traces. No reference was
regenerated. Final decode and packet files (`build/n1-accept-*-decode.txt`, `build/n1-transcripts/accept-*.txt`)
again compare byte-identically to the parent. Final-head `make -j4 test-full` remains pending here.
No shared message shape, wire bytes, worlds, tank behavior, tuner value/classification/order or Protocol.Version
changed (still 3). Valid traffic ordering and rejection order are compared explicitly; clocks remain monotonic network
time and existing server phase time, with no new timers or random draws. Patch-safe failure containment only.
