# A11b part 3 — Dissolve Run (Lab prototype, solo)

Branch: `claude/a11b-dissolve-run-xn5qn0` (the session's assigned name for `claude/a11b-dissolve-run`), started from
`origin/lead/a11b2-verify`, with `origin/dev` merged in (`5e01122`) before the final gate. Report and review media:
`claude/a11b-dissolve-run-xn5qn0-report` (`REPORT.md`, `review/`).

## 1. Summary by commit

- **`4b6c7d3` Add strip layouts to the generator and Dissolve Run's segment plans, rules and HUD.**
  - `SuburbSettings.StripLength` / `StripSeamGap` (`SuburbGenerator.cs`, +30 lines). A strip is one straight through
    road along X with lots on both sides, keeping 1.5 m of lawn clear at each end so strips join street to street. At 0,
    which every shipped suburb uses, no branch runs and no random draw changes, so generator output is unchanged (proved
    in §2).
  - `src/World/SegmentPlans.cs`: `SegmentSettings`, `SegmentPlan` and `SegmentPlans.Generate(settings, catalog, runSeed,
    index)`. It is pure: the segment's seed is SplitMix64 of the run seed and index. It holds the tank schedule, the
    jackpot pyramid on every 10th segment and the ammo cans, and retries with derived seeds if a strip can't fit them
    (0 retries in 2,400 sweep segments). It also has the daily seed and `PlanCatalogSnapshot`, a thread-safe catalog
    for the worker.
  - `DissolveRunRules`, `DissolveRunHud`, `DissolveRunBot`, `Tuning.Dissolve.cs` and the `ModeRegistry.DissolveRun`
    definition (id 6). It sits in `Prototypes`, not `All`.
  - The generator suite sweeps segment runs.
- **`a911485` Stream Dissolve Run's street: segment world source, arena, staged builds and the window shader.**
  - `SegmentWorldSource`: a run root, with each segment as its own `Suburb` content root, retired through
    `RetireContent`. `ISegmentFeed` / `LocalSegmentFeed` generate plans on a worker thread.
  - `Suburb.Build` is refactored into ordered pieces (`Steps`), so `Suburb.BuildStaged` can build in-tree and hidden
    under a time budget. `Build` runs the same pieces in the same order. `Suburb.Warm` preloads models and shapes.
  - `DissolveArena` runs the window, streaming, floor cut, dissolving tanks and bodies, ammo cans, the front sheet, the
    catch fall and the best run.
  - `reveal.gdshaderinc` gains a planar mode (`reveal_side == 2`) read from one global uniform `reveal_window`, declared
    in `project.godot [shader_globals]`. `shaders/dissolve_front.gdshader` draws the sheet.
  - Lab wiring: the scenario, a seed toggle in the menu, R reloads (`LabScenario.Reloads`), the results panel, and runner
    chain IDs.
- **`ddd5018` Test the rules; script a headless run.** `DissolveRunTests` (7 unit tests) and `lab_test
  --scenario=dissolve`, which runs the bot or a pacer, then a stop, the catch, counts, frame/build/retire/GC
  reporting, cameras for captures, and `--until`.
- **`1672072` Wire into the gates and docs.**
  - `check_lab.py` adds `dissolve` (gate, 45 s) and, under `--full`, `dissolve-full` (bot, 2 seeds × 300 s) and
    `dissolve-pace` (pacer, 300 s).
  - The generator shards sweep 100 segment runs. `check_world.sh` compares segment plans from two fresh processes.
  - `lint_tests.py` is updated. Every Dissolve dial is scoped to the scenario.
  - README, `docs/lab.md` and `docs/modes.md` are updated (rules, a Segments section, gaps, verification).
- **`fe46bdb` Front glow and height become Lab dials.**
- **`5e01122` Merge `origin/dev`** (clip-based animation; no conflicts).
- **`e5f159e` Test fixes.**
  - The pacer steers around piles on the crown. Before this it got stuck on the jackpot pyramid, which ended the
    5-minute run at 110 s.
  - The scripted run no longer asserts on Godot's object count, which follows .NET GC timing.

## 2. Verification (VM: 4 CPUs, no GPU; figures indicative)

| Command | Result |
| --- | --- |
| `make check` on the merge `5e01122` | exit 0. `[unit] 335 tests, 0 failures`; the generator gate also ran 10 segment runs × 12 (0 rerolled); `check_world.sh` printed "Separate-process segment plans agree for five runs of twelve segments"; all Lab sessions including `dissolve` passed. Zero compiler warnings. Two engine `WARNING:` lines come from existing suites, not this work: a deliberate malformed-payload probe in core (`bad message: ... Score payload`) and a Jolt "job system exceeded the maximum number of jobs" in `rifle_reach_tests` under load. |
| `make -j4 test-full` on `fe46bdb` + merge | exit 0. All four generator shards passed, including 100 segment runs × 12 (1,200 segments, 0 rerolled). Coverage: "shards cover exactly the 1044 seeds". `dissolve-full` passed (2 × 300 s, bot). The bot matches passed, including `rifle-soak-gate`; neither known flake occurred. |
| `check_lab.py --full --only=dissolve-pace` on `e5f159e` (after the pacer fix) | Passed: 316 s run, 1,799 m, caught 2.6 s after stopping (it had stopped 60 m ahead of the front). |
| `make check` on the final head `e5f159e` | exit 0. `[unit] 335 tests, 0 failures`; `Lab dissolve passed`; both separate-process checks agree. Zero compiler warnings; the only engine `WARNING:` line is the existing core malformed-payload probe. |
| Base vs. branch identity: `--write-world-plans` and `--hash-world-plans` in a worktree of `origin/lead/a11b2-verify` and on this branch | All 4 formation plans byte-identical; all 4 built-world SHA-256 hashes identical (the base's plans hashed by both builds). |
| Base vs. branch generator output: a throwaway patch printing SHA-256 of `PlanCodec` bytes plus spawns and cans, in separate worktrees, `--seeds=60 --large-seeds=20` | 200 plans identical: single-player ×60, 2-player ×60, and 8/16/24/32-player ×20 each. |
| `generator_tests --segment-seeds=100 --segments=24` (during development) | 2,400 segments passed, 0 rerolled, 5–42 tanks per segment. |
| `python3 -B tools/dev/lint_tests.py`, `check_world_tests.py` | PASS |
| Movie Maker clips and windowed captures (960×540, `--fixed-fps 30`, Xvfb) | 4 clips and 5 stills, all reviewed by me (below). |

**What the tests prove.**
- Segments, over many seeds:
  - Each segment is identical when generated again, out of order, and from the worker's snapshot catalog.
  - Each is full length, with one through street, placed end to end with its neighbors.
  - Each has exactly its scheduled tanks (plus 30 on every 10th), one jackpot pyramid on the crown, its cans, lots clear
    of the seams, and a plan that survives `PlanCodec`.
  - Each run's 12 segments, stitched into one plan, have every tank reachable on foot from the start, with the street
    unbroken: no dead ends and no tank starvation.
  - The schedule never rises and reaches its minimum.
- Determinism across fresh processes: `check_world.sh` (5 runs × 12 segments, two processes).
- The rules:
  - The front is the exact integral, the same at 120 Hz or in one step, and respects the max gap.
  - A runner who stops is caught at the moment the closed form says, falls for 2.5 s, then the round ends.
  - Each tank pushes the front once; chains score n² once closed, honoring the minimum and window; a catch closes open
    chains and nothing scores after it.
  - The HUD content and the audit line are checked.
- Scripted runs check:
  - The front catches a stopped player within gap / start speed + fall + 2 s, and the player ends below the slab.
  - The score adds up, every tank is in exactly one chain and one push, and segments are built and retired.
  - At most 6 live segments are ever alive.
  - Segment nodes, segment bodies and tree nodes do not grow: the last third of the run is within the first third ×1.5
    plus slack.
  - The run pauses on the results panel.

**Frame time and memory over long runs (VM).**

| Run | Frames | Frame wall p50 / p95 / p99 / max | Segment build step avg / max | Retire step avg / max | Live segments | Segment nodes / bodies | Tree nodes | Static memory | Managed alloc, GC pauses |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Pacer, 300 s, seed 11 (stream only, no bot) | 18,802 | 2.1 / 3.1 / 7.8 / 106 ms | 3.06 / 64.7 ms | 1.6 / 4.3 ms | 2–3 | ~2.3–2.6k / ~470–540 | ~4.3–4.6k | 401–463 MB, no trend | 307 MB in 316 s; 4 GCs, 198 ms total |
| Bot, 300 s, seed 11 | 18,781 | 3.4 / 13.5 / 21.5 / 642 ms | 2.88 / 9.4 ms | 1.9 / 6.3 ms | 2–4 | 2.7–6.1k / 0.6–1.6k | 3.9–8.2k | 413–589 MB | 1,115 MB; 12 GCs, 3.97 s total |
| Bot, 300 s, seed 12 | 18,840 | 3.5 / 13.7 / 21.8 / 708 ms | 2.82 / 6.1 ms | 1.9 / 9.5 ms | 3–4 | 2.6–5.8k / 0.5–1.6k | 4.8–8.2k | 413–618 MB | 1,249 MB; 15 GCs |

**Where the slow frames come from.** On the pacer run there were 3 frames over 50 ms in 5 minutes, each lining up with a
.NET collection. Every collection in these runs is gen 2, and each pauses about 80–110 ms on this VM; I measured a
single 83 ms pause directly.

The stream's own work stays inside its budgets:
- Builds run in 2.5 ms slices (a step averages 2.8–3.1 ms).
- Retirement runs in 1.5 ms slices (average 1.6–1.9 ms).
- The window is one global shader value per frame.
- Before staging, a segment build cost 50–120 ms in one frame and a retirement 7–17 ms in one frame.

The rare 43–65 ms build step is a GC landing inside the slice.

With the bot, p95 rises to ~13.5 ms and the worst frames come from:
- bot navigation;
- the bot's allocation rate (~3.7 MB/s against the pacer's ~1 MB/s), which means more GCs;
- the 30-tank jackpot chains (one physics step of 521 ms on seed 11).

The docs already record bot navigation spikes and large-chain costs for other modes.

**What I looked at** (`review/` on the report branch):
- `hud-chain.png`: the run's HUD with "CHAIN ×2", score, meters, tanks and the front bar.
- `results.png`: the results panel with "RUN OVER · 80", the breakdown, best run and seed.
- `front-behind.png`, `front-side.png`: the cut and glow where the street ends behind the front.
- `street-ahead.png`: the street ending about 60 m ahead.
- `1-street-builds-ahead.mp4`: sprinting, the far edge materializes as the horizon moves.
- `2-front-dissolves-behind.mp4`: a house and trees cut away with the reveal glow as the front advances.
- `3-chain-pushes-front.mp4`: side view of the glowing sheet, a bot chain, and the gap widening from 9 to 14 m. This
  clip used a 14 m max gap to keep both in frame, so most of the push was absorbed.
- `4-caught.mp4`: the front reaches a stopped runner; "CAUGHT BY THE FRONT", the fall, "RUN OVER · 3".

## 3. Risks, open questions for the owner, follow-ups

**Rules as built.** Every number is a dial; defaults are given.
- **Window.** The street materializes 60 m past the furthest point reached (`DissolveAhead`). Segments are 60 m strips
  of street with yards; the run goes along +X.
- **Front.**
  - It starts 25 m behind and moves at 1.5 m/s.
  - Its speed ramps as `5 + (1.5 − 5)·e^(−t/180 s)`: about 3.4 m/s at 2 minutes and 4.5 m/s at 5 minutes. Sprint is
    9.5 m/s and run is 6 m/s.
  - Each tank pushes it back 1.5 m, once per tank in a chain.
  - **New rule (my addition):** the front never trails your furthest point by more than 60 m (`DissolveFrontMaxGap`).
- **Catch.** A sample behind the front is caught. When caught, the ground goes, you fall for 2.5 s, and the run ends.
- **Score.** Meters + 10 per tank + n² per chain of n ≥ 2. A chain closes after 1 s with no new tank in it, or at the
  catch.
- **Segments.**
  - Tanks: 16 in the first segment, falling 0.5 per segment to 4 at segment 24 (~1.4 km).
  - Piles of 2–8 in the yards, from the generator's usual spots.
  - 2 ammo cans per segment, taken once each.
  - Every 10th segment adds a 30-tank square pyramid on the crown of the street.
- **Ammo.** Limited, using the match's magazine and reserve dials.
- **Seed.** A fresh seed per run, or today's UTC date (`20261014`), toggled in the Lab menu. The best run is kept per
  choice in `user://`.

**Open questions for the owner.**
1. **The max gap (60 m) is a rule I added.** It keeps memory bounded and stops a sprinter from leaving the front
   minutes behind. Without it, the front starts slow, a sprinter outruns it by 140 m+, and 7+ segments stay alive.
   - A cost: when you are already 60 m ahead, pushes are absorbed.
   - Options:
     - (a) keep it;
     - (b) make the front catch up faster instead of clamping, so pushes always count;
     - (c) remove it and accept more live segments.
2. **Do chains of one score?** As built, n² applies only for n ≥ 2 (`DissolveChainMin`), so a lone tank gives 10 points
   plus a 1.5 m push.
3. **Ramp shape.** The speed uses an exponential approach with a 180 s time constant. The alternative is a linear ramp
   to 5 m/s over a fixed time.
4. **The jackpot pyramid on the crown of the street** blocks the street until shot. Is that wanted, or should it sit in
   a yard?
5. **Look.**
   - The front sheet is faint seen from behind the runner but strong from the side (`DissolveFrontGlow` 1,
     `DissolveFrontHeight` 14 m).
   - The materializing edge reads at a distance only.
   - Do you want a stronger sheet, or a visible scan line at the horizon?
6. **Best run** is per device only. A shared daily leaderboard would need a server.

**Risks and known limits.**
- **Content beyond the horizon.** Segments are built up to `DissolveBuildLead` (20 m) past the horizon. That content is
  invisible but physical, so a very long shot could hit an unseen fence.
- **Things behind the front.** Loose bodies and tanks are removed from physics and sight up to 0.1 s after the front
  passes them. Static colliders (houses, trees, curbs) stay until the segment retires; after a catch, the curbs of the
  segments behind are disabled too. Explosion particles and lights are not cut by the shader.
- **Floor.** Each segment's floor is one box that shrinks from behind. Seams between segments are coplanar boxes, and I
  saw no snagging in the runs.
- **Global shader value.** `reveal_window` is global, so two simultaneous Dissolve worlds in one process would share
  it. The Lab only ever has one.
- **GC.** Full collections pause about 100 ms on this VM, and they are engine-wide, not specific to this mode. The lead
  should re-measure on the 12-thread machine.
- **Untested sizes.** No timing figures exist for a real GPU, and Movie Maker at 960×540 is the only rendered evidence.
- **Lab dials sweep.** The `dials` session sweeps the new dials to both ends under test-full; it passed.

**Follow-ups.**
- **Networking.** A `Segments` `ModeWorldSource`, a networked `ISegmentFeed` sending `PlanCodec` bytes per segment, a
  world revision per segment in the envelope header, and front/score snapshot payloads.
- **Bot.** `DissolveRunBot` is a simple forward-runner. It does not seek chains.
- **GC.** Reduce per-frame allocation in `PrototypeRunner`, which copies `ModeTuners` by reflection every step for every
  prototype.

## 4. Compatibility

- **Shared tuners.** None. All 29 new tuners are local Lab dials (`[LabDial]`, no `Export`), so they never enter a match
  snapshot or saved `.tres`:
  - Front: `DissolveFrontStartGap`, `DissolveFrontSpeed`, `DissolveFrontMaxSpeed`, `DissolveFrontRampSeconds`,
    `DissolveFrontPush`, `DissolveFrontMaxGap`, `DissolveFallSeconds`.
  - Score: `DissolveTankPoints`, `DissolveChainMin`, `DissolveChainWindow`.
  - Look: `DissolveFrontGlow`, `DissolveFrontHeight`.
  - Window and stream: `DissolveAhead`, `DissolveBuildLead`, `DissolveRetireLag`, `DissolveFloorStrip`,
    `DissolveStartOffset`, `DissolveVoidDepth`.
  - Segments: `DissolveSegmentLength`, `DissolveStreetWidth`, `DissolveTanksStart`, `DissolveTanksMin`,
    `DissolveTanksDrop`, `DissolvePileMin`, `DissolvePileMax`, `DissolveJackpotEvery`, `DissolveJackpotTanks`,
    `DissolveAmmoCans`.
  - Seed: `DissolveDaily`.
- **Messages and protocol.** No message changed and `Protocol.Version` is untouched. Dissolve Run is in
  `ModeRegistry.Prototypes`, not `All`, so no lobby, `MatchStart` or envelope schema names it.
- **Shared generation and building.**
  - Suburb generation is unchanged for every shipped setting (`StripLength` defaults to 0). 200 plans are byte-identical
    to the base across single-player and 2–32-player matches.
  - Built worlds are hash-identical for all four formations.
  - `Suburb.Build`'s refactor keeps its pieces and their order.
- **Shaders.** `reveal.gdshaderinc` gains a branch used only by `reveal_side == 2`, which nothing else sets, and a global
  uniform declared in `project.godot`.
- **Release.** A patch release would suffice; nothing players share in a match changed. This lands only Lab content.
