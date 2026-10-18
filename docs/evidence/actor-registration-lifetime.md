# Actor registration lifetime (L1)

**Status:** Focused lifetime, ordering, collection and native proofs complete. Final-head integrated gate results are
reported in the package report; this record preserves the focused workloads and measurements.

2026-10-18, Linux/CachyOS, Godot 4.7.2 .NET, Debug, GTX 1660, shared CPU/GPU load.
Branch: `wp/rf-l1-actor-registration`. Production parent: `2c3c374`; parent probe: `472c5f2` (same production).
Production changes: `dd5f0de`, `762fc0b`, `206feba`. Harness checkpoints: `472c5f2`, `a23ff76`, `daa9535`,
`8a589d7`, `0b240a6`, `fc4724b`. No release reference was refreshed.

## Ownership and reference census

Each Match actor has one session-indexed `ActorRegistration`. It holds the six owned-action unsubscriptions and
the actor cache retirement callback. Early removal removes its session unsubscription entry, detaches handlers,
retires caches, then frees the character. Disposal clears both callbacks and is idempotent, including reentrancy
and failures. Native callback continuations return when their actor runtime has retired.

| Reference/state | Boundary and result |
| --- | --- |
| Fired, Ragdolled, GotUp, MeleeStarted, MeleeHit, PushedBody closures | Moved from session-held closures into the actor registration; detached before native free. A captured delegate snapshot checks registration liveness. |
| BotServices actor/collider dictionary | Removes the departing character key before native free, for humans, remote actors and bots. Navigation, tank caches and budgets are unchanged. |
| OwnedBot/view/brain and other bots' peer lists | Removed at departure; peers lose the character immediately. Lobby brain disposal retains its original position before session roster removal. Session bot stopping retains its original order. |
| Banking target and combat/sight/cover/watcher references | Released by ForgetActor. A retired-target/watcher flag preserves the next original invalid-object decision without resetting clocks or drawing RNG. |
| King points target, rival and watched references | Released at the same boundary. Retired identity flags preserve null-vs-invalid hysteresis, changed-target reporting and watch refresh. |
| Received action and melee maps | Remove that actor's keys; a later relay cannot recreate them because actor lookup rejects it first. |
| Roaming profiles/scores | Removed with the attachment. Finite-match profiles/scores/standings remain for results, departed banks and late drops. |
| M0 selected-player cache | Existing synchronous ActorsChanged refresh retained. Primary removal and device-view collection are checked while the world survives. |
| Session/World views, device endpoint, joypad binding | Existing view closure before character free retained; World views unregister on tree exit. Primary LocalView is collectible. |
| LogicalAim explosion listener, blast receiver, skeleton binding and native actor children | Existing PlayerCharacter exit cleanup retained; native instance checks and surviving-world collection pass. |
| Snapshot/input/drop predictions and action counters | Remain runtime-owned; runtime retirement releases the actor graph. They did not move into session authority state. |
| B2 authority, body replication, mode/HUD/tag state | Authority retains numeric actor/life identities and tank/gather callbacks, not characters. BodySync holds tanks/cars. Mode/HUD/tag observations are values. Their transaction/history semantics remain unchanged. |
| Session retired IDs, lobby departure IDs, numeric blacklist/deadline history | Retained identity/policy history, not native-character references. Identity tombstones intentionally grow with departures and are not subject to the flat live-container assertion. |
| Offline LocalBank/prototype services | Their actors retire with their worlds; production early physical actor-removal callers are Match's lobby/peer departures. No B4b/B3 adapter work was performed. |

Whole-session ordering remains cancellation, detach, reverse unsubscription, reverse stopping (bots before
replication), reverse views, reverse actors, world resources. Actor unsubscriptions retain their original positions
among other session subscriptions. Snapshotting retiring actors and popping callbacks before invocation permits
removal during stop without skipping unrelated callbacks. Both attachment failures are retained and native cleanup
continues.

New allowed edges are Match/SessionContext to ActorRegistration within Composition, and IBotGoalPolicy to
PlayerCharacter (Bots to Simulation). No prohibited edge was added or removed: the same 79 baseline exceptions
remain. ActorRegistration has explicit file/type mappings in modules.json. Physical actors acquire no registration
or composition dependency. B4a tactical files were touched only for actor reference retirement; IBotWorld observations
and B3/C2 policy/extraction work were left intact.

## Lifetime and differential proof

[Container observations](actor-registration-counts.csv) contain identical inputs on parent and candidate: six cycles
each in roaming, Bank and King, alternating humans/bots and including remote actors. Match's real actor assembly,
subscription, bot preparation and removal paths are used. Finite matches do not admit real joins; their repeated
attachment fixture deliberately exercises that mechanical boundary without changing admission.

After six roaming cycles the parent has 9 unsubscriptions, 9 collider keys, 6 action entries, 6 melee entries and
9 profiles/scores, versus initial 5/3/0/0/3. The candidate stays 5/3/0/0/3 after every cycle. In Bank/King the
parent reaches 3 bot attachments and 11 peer entries, versus the candidate's unchanged 1 and 2. Actors/registry,
views and blast receivers remain 3/3, 6 and 3 on both sides: this isolates managed retention from native node count.
Finite score/profile history is deliberately excluded from flat live-cache counts.

Forced collection proves removed characters, runtimes and candidate registrations collect while the session stays
alive. Remaining characters/runtimes collect after session retirement. Primary actor/runtime/LocalView collection
and M0 fallback to actor 40 pass separately. Native instances are invalid immediately after removal. All six action
handlers are observed at TreeExiting, with assertions made after Free returns so failure exits 1 rather than being
swallowed by native signal delivery. The parent without its characterization flag fails all three worlds at this
unsubscription boundary; with the flag, it proves the expected retention. Candidate default assertions pass.

The 10-case gate additionally covers removal ahead of a captured Fired listener, real firing, damage/ragdoll and
get-up callbacks, another listener's explosion notification, removal during unsubscription/stopping, repeated
retirement, and two failing/reentrant attachment callbacks. Both injected errors survive aggregation; native free
still happens. The parent-compatible cycle suite is generated from the exact gate block, not a second oracle.

Parent/candidate packets compare byte-for-byte: LobbyWorldTests 449, ParticipantServerTests 728, MatchPlayerTests 80,
ServerRunTests 594 (1,851 total). Their 30 actor attachment/removal/retirement observations also match exactly.
The focused Banking combat/cover and King pending-points departure differential matches all 540 intent, status,
navigation, RNG and ray/query rows. Existing release wire/schema/world/publication and all five immutable Lab traces
passed before the final integrated run; its final result belongs to the package report.

Reproduction after make build (fresh labels and transcript paths):

```sh
python3 -B tools/dev/actor_equivalence.py --revision=472c5f2 --label=l1-parent
python3 -B tools/dev/actor_equivalence.py --revision=working --label=l1-candidate
godot-mono --headless --path build/l1-parent/project res://scenes/dev/unit_tests.tscn -- --suite=ActorLifetimeCycleProbe --actor-lifetime-parent --match-seed=23
godot-mono --headless --path build/l1-candidate/project res://scenes/dev/unit_tests.tscn -- --suite=ActorLifetimeCycleProbe --match-seed=23
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ActorLifetimeTests --match-seed=23
```

Run the four existing suites above on both prepared projects with `--actor-order --match-seed=23
--server-transcript=ABSOLUTE_PATH`; compare packet files and actor-order lines. Run ActorTacticsTests with
`--actor-tactics-transcript=ABSOLUTE_PATH`. Removing the parent's characterization flag is the expected-failure
control (exit 1). The tactical selector passes three named workloads and rejects an unknown selector with exit 1;
all 24 ordinary gate cases remain enabled by default.

## Callback cost

[Paired observations](actor-registration-cost.csv) retain allocation, mean/p95/p99, workload counts, start load
and other Godot counts (foreign counts identify explicit worktree paths). Compiler-overlap pairs were discarded and retaken; their logs/meta remain
under build/. These are controlled native callbacks without advancing Jolt, not native frame-rate promises.

ActorTickBenchTests measures the complete Match physics callback plus the owned character callback: 20,000 warm-up,
60,000 measured ticks, 2 actors/1 owned/58 bodies. Three headless and three rendered pairs allocate identically:
63,482,880 bytes headless and 126,845,760 rendered per window (existing allocation, no added steady allocation).
Headless parent-to-candidate mean/p95/p99 microseconds:
42.299/48.2/65.6 to 31.294/43.3/50.7;
27.992/38.4/46.7 to 25.951/33.5/42.3;
25.713/32.1/39.1 to 25.487/31.9/42.4.
Rendered means are 42.359 to 44.767, 47.354 to 42.846, 45.768 to 36.959; tails vary in both directions.
There is no repeatable whole-physics regression and no speedup claim under this load.

BotTacticsTests measures complete service/Think/Read callbacks after 120,000 warm-up ticks and over 60,000 measured
ticks. Three valid pairs each cover Normal Banking combat, Normal cover and Hard King points. Every window allocates
zero bytes and preserves ray/query counts. Small King increases in the first samples were investigated with six
more pairs on the same first available CPU, alternating which revision ran first. Five of six candidate means are
lower; the sixth is 4.9% higher, with essentially unchanged tails. This does not reproduce a structural regression.
It does not close the architecture ledger's existing absolute bot/native performance obligations.

Use `--suite=ActorTickBenchTests --actor-tick-bench --match-seed=23` on both prepared projects; add
`--disable-vsync --resolution 960x540` for rendered measurements. Bot windows use `--suite=BotTacticsTests
--tactics-only=bank/Normal/combat --tactics-bench` (repeat for bank/Normal/cover and king/Hard/points).

## Native runs and reviewed captures

[Native audits](actor-registration-native.csv) record complete grids, zero persistent disagreements and zero Godot
engine errors for eight completed runs. All score/bank audits pass. Main examples, after make build:

```sh
tools/dev/net_bots.sh -o "$PWD/build/l1-roaming-metadata-final" -n 2 -w 1 -l 15 -t faults -s --match-seed=23 -- --matches=2 --lobby-roam=3 --lobby-bots=1 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --shot-every=7 --capture-dir="$PWD/build/l1-roaming-metadata-final/shots"
tools/dev/net_bots.sh -o "$PWD/build/l1-soak-clean" -n 4 -w 1 -l 90 -t clean -s --match-seed=23 -- --melee-start --lobby-bots=1 --digest-every=0.5 --shot-every=20 --capture-dir="$PWD/build/l1-soak-clean/shots"
tools/dev/net_bots.sh -o "$PWD/build/l1-soak-faulted" -n 4 -w 1 -l 90 -t faults -s --match-seed=23 -- --melee-start --lobby-bots=1 --digest-every=0.5 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --shot-every=20 --capture-dir="$PWD/build/l1-soak-faulted/shots"
tools/dev/net_bots.sh -o "$PWD/build/l1-soak-king-clean-final" -n 4 -w 1 -l 90 -t clean -s --match-seed=23 -- --mode=king --melee-start --lobby-bots=1 --digest-every=0.5 --shot-every=20 --capture-dir="$PWD/build/l1-soak-king-clean-final/shots"
tools/dev/net_bots.sh -o "$PWD/build/l1-king-fault-depart" -n 4 -w 1 -l 90 -t faults -s '--start-score=8 --match-seed=23' -- --mode=king --melee-start --lobby-bots=1 --leave-who=Bot1 --leave-at=40 --digest-every=0.5 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --shot-every=20 --capture-dir="$PWD/build/l1-king-fault-depart/shots"
```

Four clients each host one additional bot (eight initial actors). Bank clean/faulted runs check 410,864/386,353 body
pairs; King clean/faulted-departure check 565,297/359,597. Final moving errors are 0.227/0.138/0.102/0.342 m,
within unchanged limits. Final roaming cleanup passes two matches/15,606 pairs, plus the earlier three-match run
with 23,091 pairs. The Bank clean run predates only the final roaming metadata cleanup; final Bank faulted, King
clean/faulted and roaming runs cover the final production code.

Ordinary three-client roaming creator departures on parent/candidate pass both audits (30,215/26,890 pairs), but
concurrent lobby-bot arrivals assign different initial IDs, so they are not an exact-input ordering comparison.
The controlled two-client departure removes that input difference: `-n 2 -l 25 -s '--start-score=8 --match-seed=23'
-- --leave-who=Bot1 --leave-at=10 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 --actor-order` on each
prepared project's copied launcher yields identical departure, actor-removal and host-transfer rows. A single
survivor cannot supply pairwise convergence; the three/four-client runs supply that proof.

Rendered input uses menu_test, `--scenario=input --classic-lobby`, a fresh UDP port and an absolute capture directory
at 960x540/fixed 60 FPS: 165 real device-event assertions pass, as in the headless gate. Reviewed captures include
departure Bot1_010.0 (combat and standings), King Bot1_080.0 (zone, tags and active actors), roaming Bot1_024.8
(results/return), and input lab_03_jump (actual jump), under their l1-* build directories. One supplemental input
attempt was intentionally stopped to avoid sharing the gate's port, then rerun with a fresh port. The first lint
attempt found new harness import/braces formatting, corrected before complete passing checks. Expected unit capture
save failures and the explicit negative probes are not native-soak errors.

## Scope and compatibility

Patch-safe: no wire shape/bytes, Protocol.Version (3), tuner, world construction, roster/identity/ownership, rule,
bot RNG/traversal or clock-domain change. No B3/C2 extraction, navigation eviction or finite history pruning.
This is Linux source/native proof; exported artifacts and Windows/macOS execution are not established here.
