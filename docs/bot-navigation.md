# Bot navigation

The smart brain in `src/Dev/Bot.cs` chooses targets, shooting positions and cover. `src/Navigation` chooses how to
reach those positions; it does not read `Match`, choose targets, fire, score or change player movement.

`BotNavigator.Navigate` takes an `INavigationWorld`, feet position/velocity/floor contact, actor capabilities, a
goal, local tuning and elapsed physics time. It returns a world direction, sprint/jump intent and arrived/failed
flags. The brain projects that direction into camera-relative input. A runtime bot can use the same component
with its own actor and world adapter.

## Routes and jumps

`NavigationWorld` borrows the actor's actual capsule and the world's direct physics queries. It tests supported
feet positions, sweeps walking segments, and sweeps the entire ballistic jump arc. There are no fence names or
height thresholds in navigation. Houses, cars, bins and fence panels are distinguished by their collision shapes.
The jump envelope is `height = jumpVelocity² / (2 × gravity)` and `range = runSpeed × 2 × jumpVelocity / gravity`.
With the baked 9.9 m/s jump and 19 m/s² gravity, that is 2.58 m high and 6.25 m long at the 6 m/s run gait.

Bounded A* searches an actor-relative horizontal grid with walking and jump links. Jump cost includes extra time,
so an equally fast gate wins. A jump must have a supported runway, aligned run velocity, a clear arc and supported,
clear touchdown beyond the obstacle. Sprint and aim are released for the runway and flight; movement holds the
run direction through touchdown, even if the tactical goal changes. This deliberately requires no air steering,
so the jump estimate does not depend on air control. The actor still uses its own acceleration and air control.

The adapter receives tank clearance circles from the brain: intact tanks get local pile clearance, vents get the
match's throw radius plus a margin. All jump samples and the landing must avoid them. Walking can escape a circle
that already contains the actor, while rejecting movement further into it. Dynamic collision/hazard queries are
refreshed at route validation and immediately before takeoff. A tank that moves into an already airborne actor's
path cannot be predicted by this planner.

Walking sweeps use the actual capsule without expanding its horizontal radius: an expansion would overlap a fence
or moving prop at contact and incorrectly reject every recovery direction. A small numerical lift keeps the
query off its supporting slab. Jump clearance conservatively lowers the sampled arc by `BotJumpMargin`.

## Recovery and difficulty

Routes and valid vantage/cover goals persist instead of being randomized every fraction of a second. Walk corners
are skipped when a full capsule sweep permits it; turns are smoothed, and distant walking goals permit sprint.
Progress is measured over a time window, not by a distance threshold on one physics tick. The old 2 cm threshold
misclassified normal 1.6 m/s creep at 120 Hz (1.33 cm/tick) as stuck and repeatedly interrupted combat movement.
On inadequate progress, the navigator replans with the failed direction excluded. Only after bounded retries does it report failure to the
brain, which temporarily excludes the target (including ammo) and chooses another. Standing to shoot, ragdolls and
inactive matches clear recovery state. An unavailable shooting position can still exclude a tank before travel.

`Tuning.Bot...` exposes route spacing/search budget, progress window/distance, retries, waypoint/goal radii,
validation interval, jump clearance/cost, turning, sprint distance, hazard clearance, target retry time, and aim
turning/error/reaction endpoints. They are local C# properties rather than exported scalar match tuners: adding
exported scalars would change the complete snapshot schema and require a minor release. Dev overrides use
`--tune.BotProgressWindow=...`, for example. The smart brain retains the local tuning object when a match installs
shared movement tuning. `Skill` continues to interpolate aim/reaction; the Easy, Normal and Hard profiles
(`BotEasySkill`, `BotNormalSkill`, `BotHardSkill`) pick it for lobby and Lab bots, as
[multiplayer.md](multiplayer.md#lobby-bots-a8-unreleased-protocol-2) describes. The simple brain stays unchanged as the
benchmark.

Runtime searches advance at most `BotSearchPerTick` nodes on each physics tick, with a total cap of
`BotSearchBudget`. Pending searches suspend progress detection and revalidate the route against the current world
before movement resumes. The synchronous `Plan` entry point is for tests/offline callers. The default grid has
eight walking directions and flat supported endpoints; jump links use the subset whose grid endpoint fits the
computed flight envelope. It is not a general stair/roof navigator. A later shared-world runtime can cache static
queries; single collision queries and direct-route checks still have a cost that depends on world complexity.

## Regression course

`scenes/dev/bot_navigation_course.tscn` runs seven courses per seed: closed fence, diagonal approach/bin, direct
gate, car/fence, tank pile behind a fence, house/fence, and starting in contact with a fence. It uses `Suburb.Build`
and the actual player controller. Seeds vary fence style, rotation and approach position; props/tanks are real
built bodies. `--baseline` substitutes the old direct movement and jump/sidestep recovery, without changing the
course. `--first-seed=39 --seeds=1 --nav-log` replays a seed and traces its route.

The default run fails unless at least 95% of goals are reached within 15 seconds. Additional assertions require a
planned jump on closed-fence courses, walking through the direct gate, and no airborne entry into pile clearance.
At takeoff, injected pile/vent circles must reject the same arc, and a bot inside danger must be able to walk out.
`make test` runs five seeds; larger sweeps and screenshots use the commands in the README. Screenshots capture
picket/privacy crossings and supported landings. Unit tests cover physics-derived reach, gates, bounded alternate
route recovery, nearby goal stability, committed flight, per-tick search work, valid creep at 60/120 Hz and the
unchanged match tuning schema.

## Measured checks

Linux, Godot 4.7.2 .NET/Jolt, baked movement tuning, physics at 120 Hz and courses at fixed 60 FPS:

| Course run | Goals reached | Mean successful time | Stuck events |
| --- | ---: | ---: | ---: |
| Old recovery, same 20 seeds | 120/140 (85.7%) | 3.21 s | 289 |
| Navigation, 20 seeds | 140/140 (100%) | 3.23 s | 0 |
| Navigation, 100 seeds | 700/700 (100%) | 3.20 s | 0 |

Every baseline house course timed out at 15 seconds; navigation reaches those goals in about 4.5 seconds.
The larger sweep includes 216 picket and 484 privacy courses and 600 planned jumps. Direct gates are walked.
The rendered five-seed run also completes all 35 courses, with fence crossings and supported landings captured.
The creep regression reproduces the old detector's false stuck result at 120 Hz and verifies the navigator accepts
normal aimed walking at both 60 and 120 Hz.

In separate four-bot, 120-second matches, the instrumented old smart brain recorded 73 stuck events and 123 target
exclusions, ending with banks 8/1/10/0. The final navigator recorded 0 stuck events and 7 exclusions, ending 2/5/2/4.
These are different randomly generated suburbs, so the bank difference is not a controlled strength comparison.
Three final mixed matches (smart/simple/smart/simple) ended 0/3/2/1, 0/4/22/2 and 9/1/11/1: smart bots banked 44
against 12 overall, winning two matches and losing one. Mixed smart counters totaled 6 stuck events and 146
exclusions; exclusions also include the brain's existing inability to find a shooting position. Every final
match's four clients passed `tools/dev/check_scores.py` against its server audit.

Sampled whole-client physics times fell from a 2761 ms maximum before budgeting to 236 ms after, with medians
13 and 10 ms. These are sparse samples under shared machine load, not isolated navigation benchmarks or a bound
on frame time. The asserted per-tick expansion limit is the stronger evidence for bounded search work.
Windows and macOS were not exercised here.
