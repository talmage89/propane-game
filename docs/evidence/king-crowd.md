# King crowd verification

**Status:** Dated record (2026-10-15). Its "planned 0.4 minor release" shipped as 0.4.0. The policy is described in
[bot-navigation.md](../bot-navigation.md#king-crowd-tactics).

King bots wait at an occupied zone's perimeter, break off sustained contests, keep banking and shooting while
circling, and raid again on independent seeded clocks. The nearest challenger pounces on an empty zone.
The policy and its seven shared bot dials are described in [bot navigation](../bot-navigation.md#king-crowd-tactics).

Linux, Godot 4.7.2 .NET/Jolt, Debug, 120 Hz physics. Baseline runtime source is `39765cd`; 334 runtime files in the
prepared baseline copy were checked byte-for-byte against that revision. Only test logging/expectations differed.
A source check matched all 534 existing numeric/toggle initializer expressions; only the seven new bot dials
were added. The branch preserves all gameplay rules. All observations below use the same seeds
and configuration before and after; native physics outcomes vary between runs.

## Holds and mints

All 36 branch Lab rounds minted, versus six dry baseline rounds. Branch rounds minted 335 tanks versus 106;
34/36 minted at least three, and all 36 had multiple minters. Mean lone-holder time rose from 23.4% to 38.5%.
These are 90-second rounds, two seeds (23, 24), void/suburb, and 4/8/16 actors at every difficulty. Each table row
averages four rounds; minters means the number of different actors that minted in a round.

| Actors | Difficulty | Mints/min, dev | Mints/min, branch | Lone time, dev | Lone time, branch | Minters, dev | Minters, branch |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | Easy | 1.00 | 7.83 | 26.1% | 46.2% | 0.50 | 3.00 |
| 4 | Normal | 1.50 | 7.83 | 24.7% | 44.6% | 1.75 | 2.75 |
| 4 | Hard | 5.00 | 8.17 | 32.8% | 40.8% | 3.00 | 3.50 |
| 8 | Easy | 1.00 | 6.83 | 19.7% | 42.0% | 0.75 | 5.50 |
| 8 | Normal | 2.67 | 6.33 | 26.7% | 40.9% | 2.00 | 4.00 |
| 8 | Hard | 2.83 | 5.50 | 26.7% | 33.6% | 3.00 | 4.25 |
| 16 | Easy | 0.33 | 4.50 | 14.1% | 36.1% | 0.50 | 3.50 |
| 16 | Normal | 1.00 | 4.17 | 18.9% | 34.0% | 1.50 | 4.00 |
| 16 | Hard | 2.33 | 4.67 | 21.3% | 27.9% | 2.75 | 4.50 |

The 24 verified network cases minted 255 tanks versus 111. Every branch case minted at least four tanks;
23/24 had multiple minters. Mean lone-holder time rose from 24.5% to 39.8%. Four baseline cases were dry.
Every verified case passed the unmodified tank comparison and per-actor score/mint audit checks, with zero Godot
errors and zero tank disagreements. Four-client cases include clean and faulted runs at both supplies; eight and
sixteen actors use two clients plus six/fourteen Normal, Easy or Hard lobby bots at both supplies, clean.
Each row averages four cases at four actors and two cases at eight/sixteen actors.

| Actors | Difficulty | Mints/min, dev | Mints/min, branch | Lone time, dev | Lone time, branch | Minters, dev | Minters, branch |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | Easy | 0.67 | 7.33 | 13.4% | 46.7% | 0.75 | 3.50 |
| 4 | Normal | 0.33 | 8.17 | 15.5% | 45.2% | 0.50 | 2.75 |
| 4 | Hard | 6.67 | 7.17 | 32.7% | 33.8% | 2.75 | 2.75 |
| 8 | Easy | 5.33 | 7.00 | 37.5% | 39.9% | 3.50 | 3.50 |
| 8 | Normal | 2.00 | 6.33 | 30.6% | 39.9% | 2.50 | 3.00 |
| 8 | Hard | 7.00 | 8.67 | 36.6% | 36.7% | 3.00 | 3.00 |
| 16 | Easy | 1.33 | 6.00 | 18.6% | 37.8% | 1.50 | 3.50 |
| 16 | Normal | 3.00 | 4.67 | 27.9% | 36.3% | 3.00 | 4.50 |
| 16 | Hard | 3.00 | 7.00 | 19.1% | 35.1% | 2.00 | 4.00 |

[Every paired case](king-crowd-results.csv) records mints, mints per minute, lone-holder share and distinct minters.
Lab lone time is sampled on the simulation clock. Network lone time is the mean of client-observed snapshot time;
reliable snapshot delivery makes it an approximation of server occupancy. Mints and minters are checked against
the exact server audit. Network rounds last 90 seconds, world/brain seed 23, fault seed 2, `--melee-start`, supply
1×/4×. Faults are 120 ms lag, 30 ms jitter and 3% loss.

Two initial Normal 4× network samples failed: an eight-actor client disconnected during loading, and a sixteen-actor
run ended with two moving-vent position disagreements (0.533/0.666 m). These were not counted as verified cases.
The same cases passed quiet retries without changing synchronization code, comparison profiles or limits.
The failed logs remain in `build/wp-king-crowd/normal-net-rerun/`; successful retries are in `retry-net/`.

## Thinking cost

Crowd observation adds no physics queries and runs every 0.4 seconds. Navigation retains the shared 128-query and
160-expansion tick budgets. Prize searches cache positions within each synchronous search and retain candidate
distances; they preserve target order while removing repeated native transform reads. Tank crosshair checks skip
only the existing tap cooldown; Hard's blocked-shot observation stays live. Headless assertions cover target-choice
refresh, warm allocations and every trigger press with/without the cooldown guard.

Three alternating actual-dev/branch King runs at eight Normal actors in the seed-23 suburb measured inclusive
shared-perception plus brain time per actual think call. Median: 30.88 → 32.46 µs
(30.59–34.48 versus 31.77–34.81). At eight brains this is
0.247 → 0.260 ms per world tick. The ranges overlap; these samples do not
prove a strict no-increase bound. The new policy creates more mints, drops and live hazards. Exact think-call counts
avoid mistaking hitstop's additional calls for a slower individual call.

`make bench` does not support King, so its required eight-bot proxy uses default Tank Bank, 1× supply, Debug,
10-second samples after two-second warm-up, three alternating baseline/branch repetitions. Medians:

| Workload | Brain ms/tick, dev | Brain ms/tick, branch | FPS, dev | FPS, branch |
| --- | ---: | ---: | ---: | ---: |
| Headless | 0.238 | 0.220 | 1664.9 | 1794.7 |
| Rendered | 0.241 | 0.280 | 193.1 | 182.7 |

The rendered proxy regressed about 16% in brain time and 5% in FPS; the Tank Bank brain is unchanged. Its navigation
query workload varied across repetitions (dev 49.9–54.3, branch 48.3–66.5 queries/tick). This is not evidence of a
universal frame-time win. Raw JSON and frame samples are retained as `build/wp-king-crowd/bench-{base,branch}-N*`.
The prepared copy's benchmark Git metadata names the outer worktree; `baseline-source-check.json` identifies its
actual runtime source. A prior loaded benchmark and the intermediate King comparisons are retained as well.

## Verification and review

The five-seed, two-spawn-assignment strength sweep passed: summed mean final banks Hard 24.50 versus Normal 17.75
in Tank Bank, and 22.00 versus 11.25 in King; 106 King mints. Difficulty aim/reaction values and Hard's points policy
remain unchanged.

Rendered sixteen-Easy void captures were reviewed at 6, 18, 30, 65 seconds and round end. They show a contested
crowd, blasts scattering bodies, a blue lone holder, continued perimeter fighting, and a later purple holder.
That round minted eleven tanks across five actors. Captures are in `build/wp-king-crowd-crowd-break/`.

One loaded full run failed the unchanged Tank Bank idle gate (41% worst, 7.7% mean; limits 30%/12%). Standalone
baseline and branch repeats passed at 23.1% and 7.7% worst. One unit run failed the unchanged cached-navigation
zero-allocation assertion; a repeat passed 391/391. Those failure logs are retained. No limits were relaxed.
Final `make check PORT=25084` passed with zero compiler warnings/errors, 392 unit tests, 165 real-device input
assertions and both crowd idle checks. Final `make -j4 test-full PORT=25084` passed, including 1,044 generator
seeds, 100 navigation seeds, all 550 Lab cases (514 dial fixtures), menu lifetime checks, separate-process world
hashes and the final rifle, blocked-duel and loading-readiness network gates. All three final network gates had
agreeing audits and zero tank disagreements. `python3 -B tools/dev/lint_tests.py` passed. Logs are retained under
`build/wp-king-crowd/` as `check-final.log`, `test-full-final.log` and `lint-tests-final.log`.

Reproduction (run `make build` first; use a prepared baseline with the same test instrumentation for `--project`):

```sh
python3 -B tools/dev/check_king_crowd.py --seeds=2 --output=build/king-crowd-lab
python3 -B tools/dev/check_king_crowd.py --network --faults --output=build/king-crowd-net
# Baseline measurement permits dry rounds; the normal standalone outcome gate still requires a mint.
python3 -B tools/dev/check_king_crowd.py --project="$BASELINE" --seeds=2 --minimum-mints=0 --minimum-minters=0 --output=build/king-crowd-base
python3 -B tools/dev/check_king_crowd.py --project="$BASELINE" --network --faults --minimum-mints=0 --minimum-minters=0 --output=build/king-crowd-base-net
python3 -B tools/dev/check_bot_points.py
make bench BOTS=8 TANKS=1 RENDERED=0,1 SECONDS=10 WARMUP=2 REPEATS=3
# Exact King cost: repeat on baseline and branch, alternating three times.
godot-mono --headless --fixed-fps 60 res://scenes/dev/lab_test.tscn -- --scenario=prototype --mode=king --seeds=1 --first-seed=23 --bots=7 --bot-skill=0.6 --environment=suburb --tune.FixedSeed=23 --effects-seed=23 --tune.KingRoundSeconds=90 --min-king-mints=0
# Rendered proof; fixed FPS preserves simulation cadence under shared load.
godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/lab_test.tscn -- --scenario=prototype --mode=king --seeds=1 --first-seed=23 --bots=15 --bot-skill=0.2 --environment=void --tune.FixedSeed=23 --effects-seed=23 --tune.KingRoundSeconds=90 --overview --shots=6,12,18,25,30,40,55,65,75 --capture-dir="$PWD/build/king-crowd-shots"
make check
make -j4 test-full
python3 -B tools/dev/lint_tests.py
```

New exported bot tuners change the shared snapshot and require the planned 0.4 minor release. Messages, suburb
construction, tank behavior, mint timing, scoring, drops and blast effects are unchanged. `Protocol.Version` stays 3.
Verification was on Linux; Windows and Apple Silicon were not run.
