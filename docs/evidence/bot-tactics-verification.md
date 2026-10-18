# Tactical bot ownership verification

**Status:** Dated focused behavior, allocation and network evidence. Final paired costs and release-candidate gate
results accompany the branch submission; this record does not claim whole-game frame-time or cross-platform proof.

2026-10-17, Linux x86-64, Godot 4.7.2 .NET/Jolt, .NET SDK 10.0.113, GTX 1660.
Branch: `wp/rf-b4a-bot-tactics`. Production parent: `e718b6b`; production candidate: `151f8d6`.
Harness commits: `6692cc6`, `d9a42e8`, `dddaa46`. The subsequent evidence/runbook commit changes no C# source.

## Ownership

Physical lines, including comments and blank lines:

| Brain | Parent | Candidate | Extracted owners |
| --- | ---: | ---: | --- |
| Banking | 908 | 315 | `BankingTargets` (236), `BankingCombat` (267), `BankingDemolition` (189) |
| King, including unchanged `KingBotView` | 645 | 296 | `KingZoneTactics` (256), `KingPrizes` (186) |

Targets owns the objective, retarget/blacklist state, points estimator and Easy scouting leg. Combat owns fight,
strafe, cover and sight/shooter/watcher memory. Demolition owns its runway, candidate buffer and blocked-shot
progress. King's zone owner holds standing, intruder/watch, crowd, raid and recovery/perimeter state; its prize
owner holds prize/blocker/points selection, reusable scans, protection and blocked-shot retries.

Brains retain priority order, final intent assembly, RNG, navigation and their existing aim/motor ownership.
Owners borrow explicit observations, sampled positions, RNG and aim operations synchronously; none stores a brain
or a mutable facade context. Managed state and borrowed scene references expire with the brain. Timer domains and
reset subsets remain distinct. [The contract](../bot-navigation.md) documents these boundaries.

No production file outside `src/Bots` changed. `IBotWorld`, `BotServices`, `BotMotor`, `LocalBank` and
`PrototypeRunner` are unchanged. B7a's harness registry, independent inventory and lifecycle test were extended for
the opt-in `bot-cost` case; no Lab runner or existing scenario behavior was refactored.

## Frozen and parent behavior

A source archive of `e718b6b` in `build/b4a-parent` received identical probe sources and its own setup/build.
The original Banking and King sources in that archive stayed unchanged. No reference fixture was regenerated.

```sh
make setup
make build
python3 -B tools/dev/check_bot_tactics.py --baseline=build/b4a-parent --output=build/b4a-budget-pair
python3 -B tools/dev/check_equivalence.py --only=bank,king,spill-chain --baseline=build/b4a-parent --output=build/b4a-p0
python3 -B tools/dev/check_equivalence.py --only=bank,king,spill-chain --detail --output=build/b4a-p0-final
```

All 23,040 tactical observations agree byte for byte: 960 ticks in each of 24 Bank/King, difficulty and scenario
combinations. These include cover, combat, demolition runway changes, scouting, contested zones, knockdown
regrouping, equal-distance prizes, points protection and points combat. Per-tick observations retain intent, aim
bits, status, tactical fields, retry deadlines, RNG state, ray counts and navigation counters/diagnostics.
Missing/duplicate observation fields fail. An injected RNG-field change was rejected at case `bank/Easy/cover`,
tick 0, field `rng`. The first Hard prize assertion incorrectly assumed points selection must beat zone income;
the parent rejected it. The final assertion checks the actual selected prize, including the ordinary fallback,
without changing either policy.

Every case asserts consumed input edges and held intent. `TestRunner.AssertNoAllocation` passes warmed windows
with shared query/search budgets refilled before every tick. This exercises active searches instead of allowing
exhausted budgets to conceal allocations. The parent passes the same stronger checks.

Both equivalence invocations pass the immutable wire/schema/world/publication references and Bank/King/spill-chain
Lab references. The final detailed traces also match the immediate production parent's saved detail bytes:

| Trace | Identical uncompressed bytes | SHA256 |
| --- | ---: | --- |
| Bank | 142,175,393 | `84d060e7e767eb65a155f265db2ae1aba82e1a6da26a0f5fc1e2ebd9441f3b43` |
| King | 218,480,513 | `2937435389099c20581dba14ca7f8b82a2556a4e3a88f778181a9e8c21c8dc07` |
| Spill/chain | 15,530,343 | `3e8057270d0f5c4bdcbbf77a393f78b565cb31950184a420514ea5e8e0753c7e` |

Raw comparisons, detail files and logs remain in `build/b4a-p0*` and `build/b4a-budget-pair`.
The scene lifecycle suite passes seven assertions; the independent Lab inventory lists 19 CLI cases,
148 assertion expressions, 48 log expressions and 35 capture expressions. The comparator's existing six
unit tests and Markdown link check pass. The early `make test` passes 519 unit tests and its remaining fast
partitions. `make lint` passes formatting/UID/link checks and warning-as-error Debug/ExportRelease builds.

## Rendered native behavior

All rendered commands use `godot-mono`, fixed 60 FPS and 1280x720, with absolute capture paths under this worktree's
`build/`. Existing scene assertions and idle thresholds were unchanged.

- `bot_navigation_course.tscn --seeds=5`: 35/35 courses, no stuck events, 30 jumps, mean 3.26 seconds. Reviewed
  `build/b4a-navigation/seed1_course1_privacy_air.png` and `seed1_course1_landed.png`: clearance over the privacy
  fence and an upright landing on the destination side.
- `lab_test.tscn --scenario=soak --environment=suburb --bots=8 --seconds=40 --replay-random
  --tune.HitstopDuration=0 --max-idle=0.12 --max-idle-worst=0.3`: nine brains, mean idle 4.8%, worst 20.5%, longest
  five seconds. Reviewed `build/b4a-bank-shots/duel_soak_end.png`: active rifle combat in the populated suburb.
- `lab_test.tscn --scenario=prototype --mode=king --environment=suburb --bots=15 --quick --seeds=1
  --first-seed=11 --tune.KingRoundSeconds=45 --replay-random --tune.HitstopDuration=0 --max-idle=0.15
  --max-idle-worst=0.4 --overview --shots=10,25`: 16 brains, four audited mints, two minters, 29.92% lone hold;
  mean/worst idle 4.4%/9.1%, longest three seconds. Reviewed the 10-second, 25-second and end captures in
  `build/b4a-king-shots`: zone warning, actors moving through the suburb and the correct results overlay.

## Owned online bots

Four fresh `tools/dev/net_bots.sh` runs used `-n 2 -w 0 -l 60 -s '--match-seed=23'`, the appropriate `-t` profile,
`--lobby-bots=2 --bot-log --net-seed=2`, and separate absolute `-o` directories under `build/b4a-net-*`.
King adds `--mode=king`. Faulted runs add `--net-lag=120 --net-jitter=30 --net-loss=3`.
Owned difficulty is Easy for clean Bank, Normal for clean King and Hard for both faulted runs.
Each match has six actors: two clients and four owned bots.

| Mode/profile | Audited bank | Minted | Compared body pairs | Final rest/moving residual (m) | Longest above-tolerance episode (s) |
| --- | ---: | ---: | ---: | --- | ---: |
| Bank clean | 8 | 0 | 47,700 | 0.001 / 0.084 | 0.05 |
| Bank faults | 25 | 0 | 31,692 | 0.001 / 0.246 | 0.60 |
| King clean | 31 | 12 | 49,039 | 0.000 / 0.142 | 0.35 |
| King faults | 27 | 4 | 42,226 | 0.001 / 0.199 | 0.65 |

Every run exits 0, completes conservation/score/mint audits, has both clients agree and reports zero tank
disagreements or unobserved pair grids. No engine errors or warnings appear in any server/client log. The
existing clean/faulted convergence bounds were not changed. Full logs and launcher summaries remain under
`build/b4a-net-*`.

## Cost method and limits

[The tactical probe](../bot-navigation.md#tactical-differential-probe) measures individual thinks and whole
controlled ticks at all difficulties. [The Lab cost case](../lab-scenarios.md) measures eight real brains,
shared budgets and turn rotation, plus the whole runner callback. Both use identical parent/candidate source,
2,000 Lab warm-up ticks and 6,000 measured ticks, with mean/p95/p99 and allocation/workload counters.
The controlled callback windows do not advance Jolt or the world clock; native moving scenes and online soaks
supply separate behavior evidence.

Early `build/b4a-costs*` attempts are diagnostic, not acceptance data: one was stopped during the sampled-position
review, a rendered run was rejected for a VSync fallback, and other windows overlapped new native jobs in other
worktrees. The final driver monitors other project Godot/build/format processes throughout each window and
retries whole affected pairs, retaining rejected attempts. Rendered measurements use the documented per-process
`DISABLE_GAMESCOPE_WSI=1` override, disabled VSync and 1280x720. Final accepted data and overlap metadata are in
`build/b4a-costs-accepted`; the submission reports those results and the final gate outcomes.

No native Windows/macOS run or whole-game frame-pacing acceptance is claimed. This package changes no rules,
shared tuners, wire shapes/IDs, world construction or protocol version (`Protocol.Version` remains 3).
