# Lab idle replay and Easy's empty-map fallback

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

Easy's Tank Bank fallback uses an upright rival as a hint and wanders to a fixed point near its last observed
position. `BotEasyRoamRadius` (12 m) and `BotEasyRoamSeconds` (4 s) are local dials, alongside `BotEasySeekRivals`.
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
ten measured seconds, two warm-up seconds, three repetitions, and `BENCH_ARGS=--bot-skill=0.2` or `--bot-skill=0.6`.
The baseline benchmark sources receive only the same `ScaleBench` argument/telemetry additions; its production
brains remain at the baseline commit. Keep all JSON and raw samples.

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
