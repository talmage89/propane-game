# Bot scale verification

Sixteen in-process bots meet the frame-rate target in the final benchmark: rendered Release rises from 4.9 to
88.5 FPS (81.8–94.9 across the three branch runs), and Debug from 3.3 to 70.4 FPS (70.1–75.3). Branch physics
runs at approximately 120 ticks per wall second. Release rendered frame p50/p95/p99 is 10.80/14.19/19.05 ms,
average inclusive brain time is 0.477 ms per tick, and its worst observed brain tick is 1.550 ms. Headless
Release reaches 175.2 FPS and Debug 129.6 FPS.

The strict zero-allocation and 4 ms measured wall-time ceilings are not met across all runs: the sixteen-bot
Release rendered group has one 120-byte allocating tick, and longer loaded lobby soaks have larger wall-time
spikes. Lower bot counts also show FPS regressions in some loaded samples despite lower brain costs; eight-bot
Release rendered results range from 16.5 to 127.6 FPS. Keep the full table and ranges when assessing these
results. The lead should remeasure on a quiet machine.

Measured on Linux with Godot 4.7.2 .NET/Jolt, physics still at 120 Hz. This report distinguishes the large
reduction in recurring brain work from the requested absolute wall-time and allocation bounds, which the
longer runs do not establish.
The subsequent [King crowd verification](king-crowd.md) covers the perimeter policy and its new shared bot dials.

## Source and method

The measured bot/runtime implementation is `ccca098`. The later `4104bb1` changes only the Lab harness's
roster accounting assertions and diagnostic log; no bot/runtime code changes after measurement. Its baseline
is `dev` at `ac3a725`, copied into a private checkout and committed as `f1120b7` with only the identical
warmed-brain logger and the corrected Ring Out test assertion. Baseline brains and navigation are unchanged.
The baseline patch and verified commit bundle, native JSON, commands, revisions, `uptime`, logs and captures
are retained in `build/scale-bots/`; these local artifacts are not committed. Temporary baseline checkouts
have been removed. The inherited unfinished WIP was replaced after its first setup reported 16 compile errors.
Earlier finished history was preserved. The branch incorporates `dev`’s Dissolve Run, benchmark tooling,
hosted King and torso/gather test fixes.

The benchmark uses a fixed 254 m suburb, seed 23, 40 map tanks, 17 fixed starts and tank multiplier 1. Each
case warms for ten simulated seconds and measures ten more; the harness disturbs a quarter of the tanks
midway. There are three before/after pairs for each of 0/2/4/8/16 bots, Debug/Release and headless/rendered,
ordered AB/BA/AB. No other scenes or builds run in this worktree during measurement. Other agents use the
machine, so absolute timing remains noisy. Every run records system load. Rendered runs use the normal
invisible gamescope wrapper at 1280 × 720, with vsync, idle wait, FPS limits and gamescope WSI pacing
disabled. Captures happen after measurement. Release is the optimized managed assembly loaded by Godot,
verified by the harness, rather than an exported game.

The warmed brain logger surrounds shared observation refresh and all brains, starting after 1,200 ticks and
continuing until the Lab session stops; the raw log records its actual tick count, including any final
drain/capture ticks. Its allocations are managed bytes on that thread. The native frame/GC interval excludes
captures and records any physics-tick overshoot within the last frame. Whole-process allocation and GC
measurements include other systems. Status/HUD/log formatting and explosion-invalidation event callbacks are
outside the brain interval. Wall time includes scheduling and GC interruption; it is not isolated CPU time.
Navigation queries and expansions are bounded, but that does not guarantee a wall-time ceiling. The brain
histogram caps at 10 ms, so its baseline percentiles cannot describe slower ticks; the tables use uncapped
brain averages/maxima and native frame percentiles.

Reproduction (use separate private baseline and branch checkouts; do not switch an active checkout between
measurements):

```sh
mkdir -p build/tmp
export TMPDIR="$PWD/build/tmp"
make setup
make bench BOTS=0,2,4,8,16 TANKS=1 CONFIG=Debug,Release RENDERED=0,1 SECONDS=10 WARMUP=10 REPEATS=3
# For paired revisions, run each individual case once in AB/BA/AB order, saving a distinct output:
make bench BOTS=16 TANKS=1 CONFIG=Release RENDERED=1 SECONDS=10 WARMUP=10 REPEATS=1 BENCH_ARGS="--capture --output=$PWD/build/bots-bench-example.json"
python3 -B tools/dev/bench_compare.py --merge build/scale-bots/paired-final/base-Release-r*-b*-*.json --output=build/bots-base-Release.json
python3 -B tools/dev/bench_compare.py --merge build/scale-bots/paired-final/branch-Release-r*-b*-*.json --output=build/bots-branch-Release.json
python3 -B tools/dev/bench_compare.py build/bots-base-Release.json build/bots-branch-Release.json
```

## Headless

Medians of three runs; FPS brackets show the minimum–maximum across those runs. Frame values are p50/p95/p99
in milliseconds; brain time includes shared observations.

| Build | Bots | FPS before → after [range] | Frame ms before → after |
| --- | ---: | ---: | --- |
| Debug | 0 | 3269.6 [3219.9–3314.8] → 3265.3 [3260.4–3372.7] | 0.28/0.43/0.92 → 0.28/0.43/0.93 |
| Debug | 2 | 849.5 [533.0–855.0] → 565.1 [345.7–921.5] | 0.71/1.81/11.28 → 1.13/3.75/9.38 |
| Debug | 4 | 146.4 [100.6–170.6] → 375.3 [270.1–405.3] | 1.81/33.34/135.49 → 2.09/4.96/5.71 |
| Debug | 8 | 9.4 [7.5–13.7] → 192.4 [157.7–213.1] | 112.74/269.56/329.72 → 5.20/8.52/12.08 |
| Debug | 16 | 2.9 [2.7–4.1] → 129.6 [108.7–161.5] | 304.74/743.99/923.55 → 7.71/11.02/15.86 |
| Release | 0 | 2817.6 [2678.0–3032.9] → 2803.9 [2558.1–2991.8] | 0.29/0.53/1.32 → 0.32/0.49/1.16 |
| Release | 2 | 988.0 [980.9–1036.6] → 1016.4 [781.7–1052.9] | 0.66/1.55/8.03 → 0.78/1.93/2.67 |
| Release | 4 | 203.2 [120.6–225.3] → 396.7 [395.3–508.0] | 1.50/16.98/97.17 → 1.97/5.01/5.76 |
| Release | 8 | 15.4 [14.6–20.6] → 242.8 [237.5–320.0] | 5.50/236.66/309.85 → 3.77/5.90/9.58 |
| Release | 16 | 2.8 [2.3–9.3] → 175.2 [161.0–175.8] | 319.50/562.60/890.22 → 5.91/7.62/12.02 |

Brain mean and worst observed tick are milliseconds; bytes are the total allocated inside warmed brain ticks
in the median run (maximum across repetitions in parentheses).

| Build | Bots | Brain mean before → after | Worst observed before → after | Brain bytes before → after (max) |
| --- | ---: | ---: | ---: | ---: |
| Debug | 0 | 0.000 → 0.000 | 0.001 → 0.005 | 0 → 0 (0) |
| Debug | 2 | 2.277 → 0.240 | 48.020 → 13.524 | 133,211,536 → 0 (0) |
| Debug | 4 | 5.931 → 0.198 | 47.223 → 6.209 | 328,540,776 → 0 (0) |
| Debug | 8 | 12.458 → 0.387 | 69.437 → 2.013 | 744,494,880 → 0 (0) |
| Debug | 16 | 24.965 → 0.592 | 242.690 → 4.444 | 1,324,694,136 → 120 (120) |
| Release | 0 | 0.000 → 0.000 | 0.001 → 0.005 | 0 → 0 (0) |
| Release | 2 | 1.995 → 0.128 | 35.896 → 2.946 | 130,769,784 → 0 (0) |
| Release | 4 | 5.411 → 0.169 | 48.972 → 1.036 | 323,510,544 → 0 (0) |
| Release | 8 | 11.415 → 0.251 | 56.917 → 1.888 | 745,729,344 → 0 (0) |
| Release | 16 | 26.223 → 0.463 | 181.746 → 2.213 | 938,260,968 → 0 (0) |

Whole-process managed allocation (MiB), GC generation 0/1/2 collection counts, and cumulative GC pause (ms)
are medians; they include systems outside bots.

| Build | Bots | MiB before → after | GC counts before → after | GC pause before → after |
| --- | ---: | ---: | --- | ---: |
| Debug | 0 | 23.4 → 25.9 | 3/3/0 → 4/4/0 | 68.35 → 89.65 |
| Debug | 2 | 147.0 → 16.2 | 24/24/0 → 4/3/1 | 457.74 → 108.76 |
| Debug | 4 | 323.0 → 18.5 | 55/55/1 → 3/3/0 | 1109.68 → 78.86 |
| Debug | 8 | 716.4 → 23.2 | 125/125/7 → 4/4/0 | 2399.22 → 92.98 |
| Debug | 16 | 1283.6 → 36.8 | 228/228/12 → 6/6/0 | 4531.88 → 125.80 |
| Release | 0 | 20.4 → 20.2 | 3/3/0 → 4/4/1 | 102.74 → 119.84 |
| Release | 2 | 147.3 → 24.5 | 24/24/0 → 4/4/0 | 455.22 → 91.35 |
| Release | 4 | 320.6 → 18.9 | 55/55/2 → 3/3/0 | 1077.79 → 83.33 |
| Release | 8 | 715.4 → 25.0 | 127/127/6 → 5/5/0 | 2231.45 → 99.95 |
| Release | 16 | 915.9 → 39.6 | 165/165/12 → 7/7/0 | 4868.56 → 126.54 |

## Rendered, 1280 × 720

Medians of three runs; FPS brackets show the minimum–maximum across those runs. Frame values are p50/p95/p99
in milliseconds; brain time includes shared observations.

| Build | Bots | FPS before → after [range] | Frame ms before → after |
| --- | ---: | ---: | --- |
| Debug | 0 | 129.8 [128.1–217.4] → 127.0 [84.4–219.1] | 7.30/11.72/15.53 → 7.61/11.56/14.51 |
| Debug | 2 | 62.3 [44.9–170.0] → 63.7 [27.5–155.1] | 7.69/74.31/171.27 → 13.48/29.88/43.16 |
| Debug | 4 | 70.7 [14.6–85.9] → 40.2 [19.2–170.2] | 6.21/60.96/133.32 → 23.01/40.93/52.74 |
| Debug | 8 | 3.3 [3.2–3.5] → 106.1 [69.8–114.9] | 292.41/512.28/561.03 → 8.64/14.43/18.49 |
| Debug | 16 | 3.3 [2.7–3.7] → 70.4 [70.1–75.3] | 299.79/496.04/612.33 → 13.77/18.74/25.31 |
| Release | 0 | 122.6 [121.0–218.0] → 168.8 [130.0–216.8] | 7.73/12.34/15.13 → 4.93/11.19/14.60 |
| Release | 2 | 68.6 [49.3–152.6] → 63.4 [60.8–192.1] | 7.93/66.50/147.32 → 14.76/26.86/37.81 |
| Release | 4 | 61.9 [5.8–63.6] → 41.4 [35.5–67.0] | 6.32/71.72/176.31 → 23.81/36.94/69.78 |
| Release | 8 | 9.7 [8.9–11.6] → 23.7 [16.5–127.6] | 79.34/259.34/280.44 → 29.39/113.44/133.50 |
| Release | 16 | 4.9 [3.5–5.2] → 88.5 [81.8–94.9] | 193.76/338.48/360.69 → 10.80/14.19/19.05 |

Brain mean and worst observed tick are milliseconds; bytes are the total allocated inside warmed brain ticks
in the median run (maximum across repetitions in parentheses).

| Build | Bots | Brain mean before → after | Worst observed before → after | Brain bytes before → after (max) |
| --- | ---: | ---: | ---: | ---: |
| Debug | 0 | 0.001 → 0.001 | 0.081 → 0.009 | 0 → 0 (0) |
| Debug | 2 | 3.748 → 0.317 | 60.210 → 4.513 | 133,210,576 → 0 (0) |
| Debug | 4 | 4.871 → 0.294 | 74.900 → 3.954 | 298,592,232 → 0 (0) |
| Debug | 8 | 19.923 → 0.338 | 154.005 → 3.212 | 711,319,792 → 0 (120) |
| Debug | 16 | 21.969 → 0.571 | 230.554 → 2.132 | 1,449,695,488 → 0 (0) |
| Release | 0 | 0.000 → 0.000 | 0.012 → 0.005 | 0 → 0 (0) |
| Release | 2 | 3.379 → 0.209 | 74.899 → 2.303 | 130,960,392 → 0 (0) |
| Release | 4 | 4.713 → 0.225 | 118.588 → 2.849 | 260,518,424 → 0 (0) |
| Release | 8 | 10.840 → 0.367 | 192.645 → 2.345 | 738,669,208 → 0 (0) |
| Release | 16 | 14.365 → 0.477 | 65.247 → 1.550 | 1,060,434,264 → 0 (120) |

Whole-process managed allocation (MiB), GC generation 0/1/2 collection counts, and cumulative GC pause (ms)
are medians; they include systems outside bots.

| Build | Bots | MiB before → after | GC counts before → after | GC pause before → after |
| --- | ---: | ---: | --- | ---: |
| Debug | 0 | 4.0 → 3.9 | 0/0/0 → 0/0/0 | 0.00 → 0.00 |
| Debug | 2 | 132.1 → 6.4 | 22/22/0 → 1/1/1 | 788.15 → 53.77 |
| Debug | 4 | 293.1 → 7.4 | 50/50/1 → 2/2/1 | 849.35 → 55.03 |
| Debug | 8 | 682.2 → 17.7 | 122/122/8 → 3/3/0 | 4171.99 → 61.02 |
| Debug | 16 | 1403.2 → 29.1 | 248/248/14 → 5/5/0 | 3871.77 → 84.22 |
| Release | 0 | 4.6 → 3.0 | 0/0/0 → 1/0/0 | 0.00 → 9.64 |
| Release | 2 | 130.1 → 6.3 | 22/22/0 → 1/1/0 | 795.57 → 46.50 |
| Release | 4 | 256.9 → 7.6 | 44/44/1 → 2/1/0 | 978.18 → 68.98 |
| Release | 8 | 707.0 → 12.8 | 125/125/7 → 3/2/1 | 1927.68 → 72.10 |
| Release | 16 | 1028.1 → 31.0 | 183/183/11 → 5/5/0 | 2656.84 → 84.28 |

One-minute system load ranged from 2.08 to 36.35; each raw run retains its full `uptime`.

## Behavior comparison

Forty-two paired headless matches, run labels 11–13, alternate baseline/branch order. Both use `--fixed-fps
60`; each asserts legal rules and accounting and exits without engine errors. The labels seed the world random
stream (and its derived brain seeds): Tank Bank uses `--effects-seed`, and prototype scenarios use
`--first-seed`. Tank Bank, Ring Out and King retain the harness's fixed suburb seed 23; Short Fuse derives its
map seed as `100 + 37 × label`, and Dissolve uses the label as its run seed. These are three random streams,
not three different Tank Bank maps. Physics and blast outcomes are not wholly determined by the brain seed, so
three samples do not establish identical strength.

| Scenario | Baseline, seeds 11/12/13 | Branch, seeds 11/12/13 |
| --- | --- | --- |
| Tank Bank suburb, 180 s: total gains | 63 / 50 / 51 | 30 / 76 / 73 |
| Tank Bank suburb: hits | 46 / 15 / 40 | 13 / 44 / 45 |
| Tank Bank suburb: remaining combined banks | 17 / 28 / 7 | 17 / 27 / 27 |
| Tank Bank suburb: NPC lifetime gains | 20 / 26 / 22 | 20 / 46 / 30 |
| Tank Bank void, 180 s: total gains | 115 / 74 / 71 | 99 / 90 / 110 |
| Tank Bank void: hits | 73 / 55 / 53 | 70 / 75 / 79 |
| Tank Bank void: remaining combined banks | 23 / 18 / 13 | 21 / 13 / 26 |
| Ring Out: credited knockoffs | 1 / 0 / 1 | 2 / 2 / 1 |
| King quick void: minted | 1 / 3 / 7 | 3 / 1 / 3 |
| King full suburb: minted | 37 / 14 / 14 | 34 / 7 / 22 |
| King full suburb: remaining banks | 45 / 0 / 0 | 24 / 0 / 1 |
| Short Fuse: rounds | 3 / 2 / 3 | 4 / 2 / 3 |
| Short Fuse: deaths | 6 / 4 / 5 | 8 / 4 / 6 |
| Short Fuse: rounds at time limit | 0 / 0 / 1 | 0 / 0 / 0 |
| Dissolve Run: meters | 169 / 154 / 199 | 175 / 169 / 140 |
| Dissolve Run: tanks | 33 / 24 / 31 | 34 / 34 / 17 |
| Dissolve Run: score | 586 / 406 / 606 | 602 / 615 / 319 |

The first suburb banking match is quieter on the branch; the other two compensate in the combined totals. King
still has a lower peak bank and fewer minted units in one seed. Dissolve seed 13 is weaker despite a slightly
higher median score. These are behavior changes worth reviewing, rather than evidence of equal strength. Ring
Out and Short Fuse complete actual placements/rounds; no rule or assertion threshold was relaxed. Ring Out's
corrected assertion accepts the simultaneous final-fall tie already supported by the rules and checks exact
eliminated IDs, spectators and placements.

Eighteen additional sixteen-brain runs pass: Ring Out quick (three seeds), Short Fuse whole quick matches
(three), King quick void (three), King full void (three), and King full suburb (six). Full King's void mints
are 7/1/5; suburb runs mint 8/3/7 and 18/1/8. Two quick 30-second sixteen-brain King matches mint zero while
contested; the corresponding 180-second matches do mint. Short Fuse completes 7/8/2 rounds and 105/120/30
deaths, all without a round reaching the time limit. The crowd regroup policy improves lone-holder
opportunities without promising minting in every short round.

Commands for the paired scenario families (repeat each for seeds 11–13 and both revisions):

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- --scenario=soak --environment=suburb --seconds=180 --first-seed=11 --seeds=1 --effects-seed=11
# Repeat soak with --environment=void.
# Other scenario arguments: --scenario=prototype --mode=ringout --quick;
# --scenario=prototype --mode=king --quick;
# --scenario=prototype --mode=king --environment=suburb;
# --scenario=shortfuse --quick; --scenario=dissolve --seconds=45.
# Add --bots=15 for the separate sixteen-brain prototype runs; full King also uses void.
```

## Gates and network

At `4104bb1`, `make check` passes with zero build warnings, including formatting, 361 unit tests, real device
input delivery (163 input assertions), the fast scene suites and separate-process world hashes. `make -j4
test-full` passes all shards: navigation 700/700, mean 3.30 s, zero stuck events, 600 planned jumps; 1,044
generator seeds; 534 Lab slider sessions built and reset; 53 piles/738 tanks; menu lifetimes; full
prototype/Dissolve scenes; and clean audited network tails. The final dials job takes 468.4 seconds under its
unchanged 600-second limit (the earlier `ccca098` full pass took 547.1 seconds). `python3 -B
tools/dev/lint_tests.py` passes. `make setup`, `make build` and standalone `dotnet format Propane.sln
--verify-no-changes` also pass.

Twenty-four 90-second lobby soaks use three seeds per clean/fault profile. Tank Bank covers 2 actors (one
client plus one owned bot), 3 actors (two clients plus one owned bot, adding observer comparison) and 16
actors (two clients, fourteen bots co-owned by the first). King covers sixteen actors with the same ownership.
Faults are 120 ms lag, 30 ms jitter and 3% loss. All score audits agree and all engine logs are error-free.
Observer comparison is unavailable in the one-client two-actor case; the three-actor cases cover that gap.

Twenty-three original soaks pass. The first faulted sixteen-actor Tank Bank soak fails its existing
synchronization checker: moving vent drop `20100000` has 3.745 m final drift and a 3.00-second episode,
producing two disagreements. Its exact command/seed replay passes (107,325 body pairs, zero disagreements,
longest episode 0.70 s), without source or tolerance changes. Keep both results; the failure is unresolved.
All six King observer comparisons pass. Sixteen-actor Tank Bank final banks are 37/35/35 clean and 20/33/37
faulted (the failed case replay banks 29). King's clean mints are 17/3/7 and faulted mints 10/8/3. Full-suite
network tails independently compare 287,556, 38,622 and 16,772 body pairs with zero disagreements.

```sh
tools/dev/net_bots.sh -n 1 -l 90 -t clean -o "$PWD/build/bots-net-example-2" -s '--match-seed=23' -- --lobby-bots=1 --human=idle --melee-start --net-seed=1
tools/dev/net_bots.sh -n 2 -l 90 -t clean -o "$PWD/build/bots-net-example-16" -s '--match-seed=23' -- --lobby-bots=14,0 --human=idle --melee-start --net-seed=1
# Also use --lobby-bots=1,0 with two clients, and --mode=king with fourteen owned bots.
# Repeat seeds 23–25/net-seeds 1–3 and profile faults, adding:
# --net-lag=120 --net-jitter=30 --net-loss=3
```

Other failed development runs remain in the raw evidence: an earlier full Dissolve run caught the actor but
later observed it on ground (Y=0.2), and passed an unchanged replay and the final full suite. Momentum
crossing the fixed caught boundary is an inference, not a proven cause; neither game rules nor test assertions
were changed. Earlier machine contention also caused dials timeouts. The torso/gather fixes were merged from
`dev`; no private workaround changes those assertions.

## Rendered review

The final five-seed rendered course passes 35/35, mean 3.26 s, zero stuck events and 30 jumps. Reviewed
`build/bots-navigation-final/seed1_course0_privacy_air.png`, `seed1_course0_landed.png` and
`seed5_course0_picket_air.png`: both fence styles are crossed, and the privacy landing is supported on the
opposite side.

Two final thirty-second suburb captures use fifteen NPCs. The scripted-player duel passes with 96 gains, 85
NPC gains, 70 spills and 52 hits. The soak drives the camera actor with a sixteenth brain and passes with 105
gains, 97 NPC gains, 61 spills and 55 hits, ending with banks 8 for the camera actor and 10 for the strongest
opponent, Bot 7. Reviewed its 5/15/25-second frames and round-end image in `build/bots-bank-bots-view/`: the
actor moves from road to fence to yard, fights amid blasts and loose props, and the correct strongest-bot
result appears. The sixteen-bot Debug and Release benchmark overviews also show bots spread across the suburb,
with explosions at separate locations. Local clustering occurs near combat; no persistent jam is visible in
these samples. Neither these images nor aggregate gains prove that every bot avoids idling or becomes
productive in thirty seconds.

The first scripted-player capture exposed a harness error: it rejected 105 total gains (94 from NPCs) because
the first NPC alone had zero gains. `4104bb1` checks all rival gains and sums the full soak roster, preserving
both requirements for the original one-NPC case. It also logs total NPC gains. The failed log/captures remain
in `build/scale-bots/visual-first-rival-failure.log` and `build/bots-bank-first-rival-failure/`; neither
gameplay nor the minimum total-gain threshold changed. The successful duel rerun is in
`build/bots-bank-final/`. The fixed-60-FPS captures are functional/visual evidence; only the uncapped
benchmark establishes the FPS numbers.

```sh
make build
godot-mono --resolution 1280x720 --disable-vsync --fixed-fps 60 --path . res://scenes/dev/bot_navigation_course.tscn -- --seeds=5 --capture-dir="$PWD/build/bots-navigation-final"
DISABLE_GAMESCOPE_WSI=1 godot-mono --resolution 1280x720 --disable-vsync --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- --scenario=soak --environment=suburb --bots=15 --seconds=30 --duel-seconds=30 --shots=5,15,25 --tune.FixedSeed=23 --effects-seed=23 --capture-dir="$PWD/build/bots-bank-bots-view"
# Repeat with --scenario=duel and a different capture directory for the scripted camera actor.
```

## Limits and compatibility

Long lobby soaks have 14 co-owned brains averaging at most 1.578 ms per tick, but individual wall-time maxima
reach 24.250 ms. Most larger Tank Bank soaks record one allocating tick of 120 bytes. The source of that
residual allocation is unproven. Unit checks establish allocation-free stable paths, including cached height
layers and actor collider ownership; they do not establish zero allocation for every changing world. Reserved
ground layers, map chunks and dynamic population buffers can grow when their reserve is exceeded. Cached
static geometry can be stale between disturbance/progress invalidations, and bone RID ownership assumes the
actor keeps its prepared collision bodies. These limits, and the requested universal 4 ms bound, need
follow-up rather than an unconditional success claim.

Only Linux was exercised. Windows, macOS and exported Release binaries were not tested. A human's subjective
responsiveness cannot be proven by headless runs; captures are sampled visual evidence, not a manual input
playthrough.

Bot policies, cached navigation, buffers, scheduling, local Lab starts and unexported C# bot tuners change.
The Lab permits fifteen NPCs in banking, Ring Out, King and Short Fuse sessions. Shared movement/physics,
tanks, puncture/vent/chain/detonation, scoring, drops, blast effects, network payloads, exported tuning schema
and suburb construction are unchanged by this package. `Protocol.Version` remains 3, inherited from hosted
King on `dev`; no protocol bump is added here. Short Fuse widens only its local crowded-roster start
assignment using existing minimum spacing; its generated plan and game rules stay intact.

## Idle bots

A playtest found most lobby bots standing still. `BotActivity` ([bot navigation](../bot-navigation.md#idle-bots)) measures
the share of one-second windows of play in which a bot neither moved a meter, fired, meleed nor held King's zone. Lobby
matches (`net_bots.sh -n 1 -l 60 -k -s --match-seed=S -- --lobby-bots=K`, Normal unless noted) on `01cc0c6` and with
the fixes, under other agents' load; mean / worst bot:

| Mode, bots, seed | `01cc0c6` | Fixed |
| --- | ---: | ---: |
| Tank Bank 8, s21 | 8.5% / 19% | 9.7% / 17% |
| Tank Bank 15, s21 | 34% / 64% | 19% / 29% |
| Tank Bank 15, s22 | 47% / 88% | 6.9% / 25% |
| King 8 Hard, s21 | 35% / 63% | 7.1% / 12% |
| King 8, s22 | 41% / 63% | 7.8% / 12% |
| King 15, s21 | 55% / 93% | 11% / 14% |

The Lab gates (`check_lab.py --only=idle-bank,idle-king`) measure 18.5% / 44% and 39% / 66% on `01cc0c6` and
6% / 13% and 6% / 25% fixed. Before scale-bots (`fe50c1b`) unseeded lobby runs idled about as much in Tank Bank
(20/25/31% at 4/8/15 bots) and less in King (19/18/27%): King's crowd regroup stood bots outside the zone for up to
30 s, and stall recovery restarted multi-tick searches until far ones never finished. The older causes were goals no
route could reach (vantage points inside clearances, tanks beyond the search budget, bots between overlapping
clearances or on car roofs) and goals that moved with the bot.
