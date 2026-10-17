# Lab idle replay and Easy's empty-map fallback

**Status:** Dated record (written 2026-10-16). What it established lives in [lab.md](../lab.md#verification) (the idle
checks and `--replay-random`) and [bot-navigation.md](../bot-navigation.md#idle-bots).

A fixed-FPS Lab run did not control all of its physics randomness. Its seed fixed `World.Random`, but
`Blast.Detonate`, `Ragdoll.Begin` and offline `DebrisQueue.Enqueue` each constructed a fresh, unseeded generator.
[Godot 4.7.2's RNG constructor](https://github.com/godotengine/godot/blob/4.7.2-stable/core/math/random_number_generator.h)
automatically randomizes every instance. Different impulses, limb flail and colliding fragments change the
obstructions and actors' recovery positions that the brains observe.

Hitstop also releases on `Time.GetTicksUsec()`. At fixed FPS, CPU load changes how many process frames fit inside
its real-time lease. Physics advances with scaled delta, and debris drains per process frame; changing that cadence
changes the physical trajectory. Lab navigation and brain timers themselves use physics delta or the world clock.

The two idle fixtures now inject a world-local factory for fresh physical-event generators, taking each seed from
that world's seeded random stream. They disable hitstop in their own fixture arguments. The original single
fixtures, durations, actor counts and 12%/30% (Bank), 15%/40% (King) mean/worst limits remain intact. Ordinary worlds
and every other Lab check retain freshly randomized event generators and hitstop. Network fragment seeding by
network id is preserved. Generator ownership is explicit; blast and ragdoll callers now dispose their generators.
No impulse, flail, fragment, tank, scoring or shared tuning value changes.

A scratch diagnostic first held all three event-generator seeds at 23 and disabled hitstop. Five repeats each
produced identical Bank mean/worst/longest **4.6%/12.8%/4 s** and King **3.4%/9.1%/2 s** despite concurrent Lab/network
verification. The committed fixture varies event seeds through the world's stream rather than repeating the same
scatter at every event. This investigation found no remaining need to change Jolt threading or the navigator.
It does not establish deterministic replay across engine, architecture or build changes.

Unit assertions exercise all three physical callers, check the ordinary factory leaves the shared random stream
alone and prove one world's injected factory does not affect another world. The original idle limits still reject
the old 64–93% stranded bots.

Easy's Tank Bank fallback uses an upright rival as a hint and wanders a short leg roughly toward it, rather than
routing to its precise location. `BotEasyRoamRadius` (12 m), `BotEasyRoamSpreadDegrees` (90 degrees) and
`BotEasyRoamSeconds` (4 s) are local dials, alongside `BotEasySeekRivals`.
An unreachable scouting point is replaced without ruling out its anchor rival. Ammo, tanks and profitable fights
still take priority, and ordinary melee applies at reach. Aim, reaction, navigation budgets, Normal/Hard goals,
scoring, shared tuning and messages are unchanged.

## Reproducing the measurements

Build normal Debug with `make build`; prepare optimized Debug with `make test-full-engine`. For each engine,
repeat each of these five times alone and five times with four `python3 -c 'while True: pass'` processes and another
600-second eight-bot suburb Lab soak (`--seconds=600 --duel-seconds=600`) running alongside it:

```sh
python3 -B tools/dev/check_lab.py --full --only=idle-bank
python3 -B tools/dev/check_lab.py --full --only=idle-king
env GODOT="$PWD/tools/dev/full_engine.py" python3 -B tools/dev/check_lab.py --full --only=idle-bank
env GODOT="$PWD/tools/dev/full_engine.py" python3 -B tools/dev/check_lab.py --full --only=idle-king
```

Kill the load processes after each measured check; retain each output and engine log. The baseline loaded trials
used a 180-second companion soak, which outlasted each single-fixture measurement. Check the machine hold before
each build, make target or engine launch. Keep `TMPDIR`, logs and captures in `build/`.

For 4/8-actor Easy/Normal Tank Bank, run each difficulty (0/1) against the same match seeds, changing capacity and
lobby-bot count together:

```sh
tools/dev/net_bots.sh -n 1 -w 0 -l 90 -t clean \
  -s '--tune.MaxPlayers=4 --match-seed=23' -o "$PWD/build/idle-easy-4" \
  -- --melee-start --lobby-bots=3 --lobby-bot-difficulty=0
```

Read `bot idle:` and the final server standings; compare only lobby bots' retained banks, since the primary actor
uses its own Hard brain. Baseline/candidate lobby soaks ran in parallel, on separate local servers. For cost,
alternate baseline/candidate `make bench` per case/repetition, headless/rendered, with 4/8/16 bots, 1× supply,
ten measured seconds, two warm-up seconds, three repetitions, and `BENCH_ARGS='--bot-skill=0.2 --replay-random'` or
`BENCH_ARGS='--bot-skill=0.6 --replay-random'`. The replay mode also fixes simulation FPS at 60: free-running process
cadence otherwise changes when fragments enter physics, even with seeded scatter. It measures CPU cost per tick;
its synthetic-frame FPS is not real-time gameplay FPS. The baseline receives the same replay plumbing and
`ScaleBench` arguments/telemetry; its production brains remain at the baseline commit. Keep all JSON and raw samples.

## Measured results (Linux, Godot 4.7.2, .NET 10)

The baseline is `964b408`. Each cell below contains five independent invocations, in run order.
Normal Debug is “fast”; optimized Debug is “full”. Loaded means four CPU hogs plus a separate Lab soak.
The baseline used the original single seed and hitstop settings. Values are percentages.

| Baseline engine/load | Check | Mean idle, five runs | Worst bot, five runs | Maximum wait | Failures |
| --- | --- | --- | --- | ---: | ---: |
| fast/alone | idle-bank | 6.8, 3.7, 2.8, 6.6, 6.0 | 15.4, 10.3, 7.7, 28.2, 10.3 | 5 s | 0/5 |
| fast/alone | idle-king | 5.8, 4.5, 4.4, 6.1, 3.3 | 25.0, 9.1, 29.5, 27.3, 9.1 | 10 s | 0/5 |
| fast/loaded | idle-bank | 4.3, 4.8, 6.0, 3.4, 4.0 | 12.8, 10.3, 15.4, 10.3, 12.8 | 3 s | 0/5 |
| fast/loaded | idle-king | 3.4, 7.1, 3.4, 4.7, 3.1 | 15.9, 22.7, 9.1, 36.4, 6.8 | 13 s | 0/5 |
| full/alone | idle-bank | 10.0, 6.8, 8.0, 6.6, 3.7 | 41.0, 28.2, 38.5, 12.8, 12.8 | 8 s | 2/5 |
| full/alone | idle-king | 9.9, 4.0, 8.2, 5.4, 3.3 | 29.5, 11.4, 20.5, 11.4, 13.6 | 12 s | 0/5 |
| full/loaded | idle-bank | 6.6, 3.7, 6.8, 3.7, 2.8 | 15.4, 12.8, 15.4, 5.1, 7.7 | 4 s | 0/5 |
| full/loaded | idle-king | 3.7, 3.4, 4.4, 3.3, 4.3 | 6.8, 9.1, 13.6, 6.8, 13.6 | 4 s | 0/5 |

A separate initial fast King invocation also failed at worst idle 43.2% (mean 7.4%, longest 18 s).

Every after cell (fast/full × alone/loaded) passed all five runs with these same results:

| Check | Mean idle | Worst bot | Longest idle | Repeated samples |
| --- | ---: | ---: | ---: | ---: |
| Bank | 2.3% | 5.1% | 2 s | 20/20 identical |
| King | 5.4% | 27.3% | 11 s | 20/20 identical |

The **complete per-bot reports, status breakdowns and summaries** match across all twenty invocations per mode,
including between normal and optimized Debug. All forty checks passed with clean engine logs. The baseline spans
2.8–10.0% mean / 5.1–41.0% worst in Bank and 3.1–9.9% mean / 6.8–36.4% worst in King; two baseline Bank checks failed.
The separate initial King failure above is outside that forty-run baseline matrix.

A scratch regression probe restored `BankingBot`, `KingBot` and `BotNavigator` from immediately before
`c8aca2e`, with API-only telemetry/policy properties and King's old 30-second regroup maximum. It retained the
current world, replay controls and other navigation services. Both unchanged fixtures rejected it: Bank mean
13.7%, worst 33.3%, longest 7 s; King mean 36.4%, worst 65.9%, longest 27 s. This exercises the original failure
pattern against the replay settings rather than merely comparing the numerical limits to an old report.

The following two subsections record the **superseded near-rival scouting iteration** (`e987bab`), which routed
toward a point near the rival. Controlled strength verification subsequently rejected that design at four actors;
the final policy takes short local steps instead. These historical measurements remain to show that rejection.

### Lobby idle and retained banks

The requested 90-second clean lobby soaks use seeds 21–23, one primary Hard bot with `--melee-start`, and three
or seven lobby bots at the selected difficulty. Each cell below aggregates three matches; idle excludes the primary
bot, retained banks are per lobby bot, and longest is the maximum across those matches. All score audits passed.

| Actors/profile | Mean idle before → after | Longest before → after | Retained banks before → after |
| --- | ---: | ---: | ---: |
| 4/Easy | 7.6% → 5.0% | 5 → 3 s | 2.889 → 3.778 |
| 4/Normal | 6.7% → 7.2% | 4 → 4 s | 6.111 → 4.222 |
| 8/Easy | 7.7% → 7.3% | 5 → 4 s | 2.095 → 2.238 |
| 8/Normal | 5.4% → 9.0% | 4 → 56 s | 2.429 → 1.762 |

Easy's individual after means are **5.6%, 6.7%, 2.6%** at four actors and **8.2%, 5.8%, 8.0%** at eight. Every
requested Easy case meets mean <20% and longest <8 s. The reported 61% baseline was not reproduced at these seeds:
the new behavior is independently covered by an empty-objective unit assertion, including ordinary target priority,
unchanged Normal/points-policy goals, the local toggle and allocation-free scouting through waypoint refreshes.

The two extra four-actor probes used a Normal primary (`--bot-skill=0.6`) without `--melee-start`, matching an older
measurement style. Before, seeds 21/25 had mean 9.4%/10.9% and longest 10/8 s; after, 8.2%/13.9% and 3/15 s.
The latter misses the longest-wait target: its worst bot's idle windows span vantage, cover and scouting states.
The new fallback addresses an absent objective; it does not eliminate every route/cover stall. Normal's 56-second
outlier above also remains visible rather than being folded into an Easy-only success claim.

### Difficulty checks

Separate difficulty lobbies change the opponents as well as the brains. The optional same-round
`check_bot_points.py --easy` comparison uses five seeds (21–25), both spawn-group offsets and 120-second Bank
rounds, with actual Easy/Normal profiles and objective policies. These are mean retained banks per bot per round:

| Actors | Normal before → after | Easy before → after | Result |
| --- | ---: | ---: | --- |
| 4 | 5.150 → 5.000 | 3.700 → 3.850 | Normal remains ahead |
| 8 | 1.825 → 1.975 | 2.700 → 2.400 | Normal loses both baseline and candidate checks |

The ordinary Hard/Normal checker passed the baseline (Bank 2.275/1.375; King 3.125/1.325), but failed the near-rival
candidate suburb Bank check (1.500/2.900; King 2.850/1.075). Two earlier candidate iterations also failed suburb Bank ordering.
The near-rival candidate's documented arena policy probe (`--environment=void --policy-only`) passed: Bank 0.600/0.500 and King
9.625/1.850; King minted 88. These runs retain physical entropy and wall-clock hitstop. They do not certify a stable
suburb difficulty ordering. Easy's aim/reaction/query limits and Normal/Hard goal policies were not retuned to force
a result, and none of these outcome gates was weakened.

### Controlled short-step comparisons

`check_bot_points.py --replay-random` uses the same world-local physical-event seeding and zero hitstop as the idle
fixtures. The baseline source receives only that replay plumbing and reporting; its production brains are unchanged.
Five seeds and both spawn offsets give these retained-bank means per bot per round:

| Comparison | Baseline | Short-step candidate |
| --- | --- | --- |
| 4 actors, Normal / Easy | 4.300 / 3.600 | 4.400 / 3.950 |
| 8 actors, Normal / Easy | 2.050 / 1.600 | 2.225 / 1.825 |
| Bank, Hard / Normal | 2.300 / 1.525 | 2.300 / 1.525 |
| King, Hard / Normal | 3.100 / 1.200 | 3.100 / 1.200 |

Both Easy comparisons pass, as do Hard/Normal in both modes (King minted 116). All twenty baseline/candidate
Normal/Hard rounds have **identical points and complete per-bot idle reports**, including statuses. The superseded
near-rival policy failed the controlled four-actor check
(Normal 3.350, Easy 4.500); local steps avoid that strength increase. Ordinary physics still varies outcomes, as the
historical measurements above show; these results establish the ordering in the controlled sweep, not every match.

The first short-step implementation shared root-position observations across a think batch. Eight controlled
headless four-bot pairs still showed a 1.3% median cost increase (0.159877 → 0.161955 ms/tick). A simpler version
reads each upright opponent's position once within a melee search, without the shared cache. Three-pair pilot
medians fell at both four bots (0.160265 → 0.159661 ms) and eight (0.259889 → 0.256193 ms); the final grid uses that
local observation reuse. No navigation budget or skill dial is reduced to lower cost.

### Final source results

The final source is `df91caa`: short local scouting steps and one opponent-position read per melee-search pass,
without a shared position cache. Its replayed Hard/Normal and Easy/Normal sweeps reproduce every complete
points/skill and per-bot idle report from the controlled comparisons above. All four formation/world hashes and
sixty segment hashes match `964b408`.

The final ordinary 90-second lobby runs use the same commands, seeds and roster sizes as the baseline. Every
score audit passes. These are means over three matches per profile, and maximum longest waits:

| Actors/profile | Mean idle before → after | Longest before → after | Retained banks before → after |
| --- | ---: | ---: | ---: |
| 4/Easy | 7.6% → 9.0% | 5 → 5 s | 2.889 → 4.778 |
| 4/Normal | 6.7% → 11.0% | 4 → 32 s | 6.111 → 2.778 |
| 8/Easy | 7.7% → 6.8% | 5 → 4 s | 2.095 → 3.095 |
| 8/Normal | 5.4% → 5.9% | 4 → 6 s | 2.429 → 1.048 |

Easy's individual final means are **13.9%, 6.0%, 7.1%** at four actors and **6.4%, 9.8%, 4.3%** at eight. All six
requested cases meet mean <20% and longest <8 s. The extra Normal-primary/no-melee-start cases also meet both
limits: seeds 21/25 mean 3.4%/8.6%, longest 4/4 s. Seed 22's actor 2 scouts for 9% of its play, with all three idle
windows attributed to shooting/cover and none to scouting; the fallback produces real movement.

These separate lobbies change the opponents and the frequency of exchanging hits, so their retained banks mix
skill with opposition. The controlled shared-round comparisons keep Normal ahead of Easy at both actor counts.
The ordinary Normal outlier and historical uncontrolled Hard/Normal failures remain disclosed. The reported 61%
Easy baseline was not reproduced on current `dev` at the measured seeds.

Final rendered verification uses `--fixed-fps 60 --disable-vsync --resolution 1280x720`: Easy's four-actor suburb
soak at 10/40/75 seconds and round end (`build/wp-idle-flake-easy-local-final`), and King's sixteen-actor suburb
round at 5/20/40 seconds and round end (`build/wp-idle-flake-king-local-final`). All nine captures were reviewed:
movement, aiming, tank/vent effects, zone contention/minting and result overlays are intact. Both scenes pass;
Easy mean/worst/longest is 13.5%/16.9%/7 s, King 4.4%/9.1%/3 s.

The final source repeated both idle fixtures five times per engine/load cell: **40/40 passed**, with all twenty
complete Bank reports identical and all twenty King reports identical, including per-bot statuses. Their values
remain Bank 2.3%/5.1%/2 s and King 5.4%/27.3%/11 s (mean/worst/longest). Normal and optimized Debug agree, both
alone and with four CPU hogs plus another Lab. The original limits remain unchanged.

The final CPU grid alternated baseline/candidate in AB/BA/AB order per cell, three pairs each, one-times supply,
10 simulated measured seconds plus 2 warm-up seconds, seed 23, Debug, `--replay-random`, and actual skills 0.2/0.6.
All **72 runs** passed. Repeated workload counters (queries, live tanks, calls, physics ticks and frames) match
within each build/case, including rendered runs. Every median brain cost is lower, by 0.1–3.7%:

| Profile/view | Bots | Baseline ms/tick | Final ms/tick | Change |
| --- | ---: | ---: | ---: | ---: |
| Easy/headless | 4 | 0.159251 | 0.159135 | -0.1% |
| Easy/headless | 8 | 0.264282 | 0.260106 | -1.6% |
| Easy/headless | 16 | 0.431738 | 0.425730 | -1.4% |
| Easy/rendered | 4 | 0.143765 | 0.143224 | -0.4% |
| Easy/rendered | 8 | 0.250968 | 0.248986 | -0.8% |
| Easy/rendered | 16 | 0.449507 | 0.432929 | -3.7% |
| Normal/headless | 4 | 0.161382 | 0.160241 | -0.7% |
| Normal/headless | 8 | 0.244090 | 0.239569 | -1.9% |
| Normal/headless | 16 | 0.471255 | 0.463915 | -1.6% |
| Normal/rendered | 4 | 0.144896 | 0.143870 | -0.7% |
| Normal/rendered | 8 | 0.237670 | 0.231235 | -2.7% |
| Normal/rendered | 16 | 0.482242 | 0.467688 | -3.0% |

All six final `scale-final.png` benchmark overviews were reviewed after timing. They show the same suburb geometry
and active tank/vent/blast work at four/eight/sixteen bots. Capture readback happens outside the measured interval.

Raw evidence remains under `build/wp-idle-flake/`: `before/`, `after-local-final/`, `local-final-points/`,
`local-final-skills4/`, `local-final-skills8/`, `local-final-after-net-*`, `local-final-replay-bench-*`, merged
`local-final-replay-s*-{before,after}.json` and `local-final-bench-summary.csv`. Baseline runtime views keep the
original brains plus only the fixture/reporting adapters described above.

Finish verification with `make check`, `python3 -B tools/dev/lint_tests.py` and `make -j4 test-full` on the recorded
final head. Their logs are `build/wp-idle-flake/final-{check,lint-tests,test-full}.log`. Build and verification are
Linux-only; these measurements do not establish replay equality across architectures or engine versions. The
ordinary game, ordinary benchmark and default strength checks retain their original entropy/hitstop behavior.
