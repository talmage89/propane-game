# Bot navigation

The banking brain in `src/Bots/BankingBot.cs` chooses targets, shooting positions and cover. `src/Navigation` chooses how to
reach those positions; it does not read `Match`, choose targets, fire, score or change player movement.

`BotNavigator.Navigate` takes a shared `NavigationGrid`, this tick’s `NavigationHazards`, feet
position/velocity/floor contact, a goal, local tuning and elapsed physics time. It returns a world direction, sprint/jump intent and arrived/failed
flags. The brain projects that direction into camera-relative input. A runtime bot can use the same component
with its own actor and world adapter.

## Routes and jumps

`NavigationWorld` uses a capsule matching the actor and reusable `ShapeCast3D`/`RayCast3D` probes in the
world's own physics space. It tests supported
feet positions, sweeps walking segments, and sweeps the entire ballistic jump arc. There are no fence names or
height thresholds in navigation. Houses, cars, bins and fence panels are distinguished by their collision shapes.
The jump envelope is `height = jumpVelocity² / (2 × gravity)` and `range = runSpeed × 2 × jumpVelocity / gravity`.
With the baked 9.9 m/s jump and 19 m/s² gravity, that is 2.58 m high and 6.25 m long at the 6 m/s run gait.

Bounded A* searches a shared world-aligned horizontal grid with walking and jump links. Jump cost includes extra time,
so an equally fast gate wins. A jump must have a supported runway, aligned run velocity, a clear arc and supported,
clear touchdown beyond the obstacle. Sprint and aim are released for the runway and flight; movement holds the
run direction through touchdown, even if the tactical goal changes. This deliberately requires no air steering,
so the jump estimate does not depend on air control. The actor still uses its own acceleration and air control.

`BotServices` refreshes tanks, chain-radius neighbor counts and indexed clearance circles once for all brains
in a world. Intact tanks get local pile clearance; vents get the throw radius plus a margin. Jump samples and
landings must avoid them. Walking can escape a circle already containing the actor while rejecting movement
further into it. Route validation and takeoff use live capsule sweeps; moving tanks cannot be predicted in flight.
Ring Out supplies a separate navigation-area check so cached support never permits stepping onto dissolved lots.

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
Prototype motors retain a bounded direct walking attempt after `BotDirectStallTime` without progress greater than
`BotDirectProgressDistance`. It lasts `BotDirectTime`, respects live tank clearance and the mode's ground limit,
and never interrupts a committed jump or a running route search (restarting a search spread over ticks meant a far
one never finished). The navigator also keeps advancing its progress/retry state during direct recovery: resetting
it every 1.2 seconds prevented its 1.25-second stall detector from ever expiring against a persistent obstruction.
A planned jump cancels direct recovery; the player controller still resolves solid collisions.

A search that cannot reach its goal, because it is walled off, inside a clearance or beyond `BotSearchBudget`, still
yields a partial route to the explored point nearest the goal when that point is at least `BotPartialRouteGain` meters
nearer than the start; after `BotPartialSearchExpansions` expansions a search that has such a point settles for it.
Nearing the end of a partial route, the bot searches again from where it walks, and keeps walking meanwhile. When a goal
moves, the bot keeps following its previous route (never a jump) while the new search runs (`BotReplanWhileMoving`). A
search that fails after at most `BotIslandExpansions` expansions started on ground the lattice has no way off (a car
roof, debris): once per goal the bot steps straight toward it for `BotDirectTime`, within the hazards. A bot already
inside overlapping clearance circles may walk anywhere that lies no deeper in them in total, so it can leave a pile even
when every way out passes nearer one tank; it never enters a circle it started outside. If the direct island escape
would deepen a clearance, it tries `BotIslandDirections` short sidesteps, nearest the goal direction first, requiring
supported ground and a clear capsule sweep through the same shared query budget. Repeating only the direct escape
could pin a fleeing bot between overlapping tank clearances indefinitely. Brains choose fixed goals for
escapes (a venting tank), alternate escape directions after bounded route failure (`BotFleeDirections` and
`BotFleeDistance`), and vantage points outside every hazard, so a goal does not drift with the bot or lie where no
route can end.

`Tuning.Bot...` exposes route spacing/search budget, progress window/distance, retries, waypoint/goal radii,
validation interval, jump clearance/cost, turning, sprint distance, hazard clearance, target retry time, and aim
turning/error/reaction endpoints. The existing navigation, aiming and points dials are local C# properties rather
than exported scalar match tuners: adding exported scalars would change the complete snapshot schema and require a minor release. Dev overrides use
`--tune.BotProgressWindow=...`, for example. The smart brain retains the local tuning object when a match installs
shared movement tuning. `Skill` continues to interpolate aim/reaction; the Easy, Normal and Hard profiles
(`BotEasySkill`, `BotNormalSkill`, `BotHardSkill`) pick it for lobby and Lab bots, as
[multiplayer.md](multiplayer.md#lobby-bots-a8-protocol-2) describes. The simple brain stays unchanged as the
benchmark.

Runtime searches advance at most `BotSearchPerTick` nodes per bot and `BotSearchBudget` per route. All bots in
a process share `BotQueriesPerTick` (128 native navigation queries) and `BotExpansionsPerTick` (160 expansions).
An entire takeoff arc reserves its query allowance before starting; a query budget below that minimum is raised
to permit one arc. Pending searches resume after the budget refills and suspend progress detection. Owners rotate
the first brain each tick so every bot gets a turn at an unspent budget.

The grid remembers support, capsule fit and eight walking/jump links at each lattice point. Queries happen only
on demand, so construction stays quick. Ground samples use a finer grid, and feet within `BotLayerTolerance` of
a cached ground plane share its answers. An explosion invalidates nearby answers,
and answers obtained while props settle expire after `BotDisturbSettle`; inadequate progress invalidates local
answers too. Straight route shortcuts and takeoffs stay live. This handles loose fences and cars without sweeping
the physics world for every node on every search. Search buffers and known-map answer storage are reserved before
the first physics tick, including `BotLayerReserve` ground-layer objects and dictionaries so standing on debris
does not immediately allocate another layer. Heights still come from grounded actors, rather than guessing the
road height at setup. Additional heights, exhausted chunk storage or growing dynamic populations can still grow buffers.
Changing body capabilities or grid accuracy tuning replaces the shared grid; bots discard its old searches and
routes, finishing an already launched jump before planning again.

Brains steer and aim every physics tick, but cache sight lines for `BotSightInterval`, watchers for
`BotWatchInterval` and cover for `BotCoverInterval`. Target selection retains its periodic cadence and seeded
random stream. King uses its separate `BotKingSightInterval` (zero by default): live visibility preserves combat
through brief sight gaps around the zone while prize searches remain periodic. Observation staggering does not
consume King's seeded goal-choice stream. When all tanks are banked, `BotHuntWhenEmpty` lets a trailing banker pursue a richer opponent
beyond sight/rifle range, then react and fire after closing in; clearing the map cannot leave it idle indefinitely.
Hot paths use indexed lists, reusable query nodes and deferred status formatting instead of LINQ,
physics-result dictionaries and interpolated status strings. `Status` allocates only when a log or overlay reads it.
Sight probes are created during setup. Visibility checks only read whether the ray collided; geometric answers
and crosshair checks use value RIDs instead of binding managed collider wrappers. Actor capsule and bone RIDs are
indexed during roster setup, preserving standing and ragdoll ownership without walking native node parents each tick.
King brains read a small value view of the rules or received zone at the owner's clock; they do not construct the
HUD/wire snapshot or its minted roster for each thought.
With at least `BotKingRegroupPlayers` actors, a King bot knocked down in the crowd regroups outside the zone for
a seeded interval between `BotKingRegroupMin` and `BotKingRegroupMax` (3–12 s), then returns. Immediate synchronized
reentry kept sixteen bots shoving indefinitely without a lone holder; minting and combat rules stay unchanged. While
it regroups it keeps playing from the edge: it shoots the nearest rival within 22 m that has a bank to spill, else banks
a tank in sight, and walks the zone's edge `BotKingRegroupStep` meters at a time instead of standing at one point.

`Plan` is synchronous for tests/offline callers. The grid supports horizontal ground and ballistic jumps, rather
than general stairs or roofs. Navigation caching and these local bot dials change no player physics, match
messages, shared tuners or world construction.

## King crowd tactics

At four or more actors (`BotKingCrowdPlayers`), a bot reaching an occupied zone waits at its edge before a raid.
A bot already inside gives a sustained contest an independently seeded
`BotKingContestMin`–`BotKingContestMax` seconds (1–3). If it cannot win, it retreats and fights from the perimeter,
using the same circling, safe banking and banked-rival pressure as knockdown recovery. After
`BotKingRaidMin`–`BotKingRaidMax` seconds (6–14), it raids again. The wait is multiplied by
`1 + extraActors * BotKingCrowdScale` (0.15 per actor above the threshold), spreading larger rosters over longer
independent intervals. Small rosters and `BotKingRaidMax=0` skip crowd observation and its random draws.
The nearest upright challenger can pounce early on an empty zone; distance ties use actor id. A lone holder stays.
Hard still takes a better points opportunity first. Set `BotKingRaidMax=0` to disable proactive retreats for A/B
measurement; the existing knockdown recovery remains.

Crowd observation reads the shared actor list once per `BotKingCrowdInterval` (0.4 s), with no physics queries or
per-tick allocations. Steering, aiming and close melee retain their existing cadence. Siege statuses distinguish
these tactical retreats from knockdown regrouping in activity logs. These seven new dials are exported in **Bots**
and travel in the creator's tuning snapshot; they shipped in 0.4.0 (Protocol 3), a minor release. Existing local dials,
King's rules, shared state and messages are unchanged. Prize searches cache native positions and ranking distances
within each search; crosshair hit tests skip only the existing trigger cooldown. Hard's blocked-shot observation
still runs every tick. [Crowd verification](evidence/king-crowd.md) records the paired outcomes and performance limits.

## Hard points policy

Easy and Normal retain objective-first target scores. Hard enables `IBotGoalPolicy.PointsDriven` in Tank Bank and
King. `BotPoints` estimates score swing per second: nearby chain neighbors bank points; bullets/melee or a blast
beside a banked rival spill points; leaders' losses matter more than trailing rivals'. Spills get only fractional
recovery credit because another player may bank them. Estimates include travel time, one versus two tank shots, drop
shields, the available bank and return-fire exposure. Hard bankers compare those rates and protect a lead; a
trailing Hard banker keeps seeking points even late. Ammo, escape, safe firing positions and immediate melee remain
tactical constraints.

King compares visible banking/spilling opportunities with reaching or holding the zone. Its mint estimate discounts
contests, travel, risking its bank in a crowd and a relocation before it can mint, and credits stopping a rival
holder's income. It can bank safe tanks outside the zone rather than joining an unprofitable crowd, or shoot a
banked leader while holding its own zone. A banked bot circles outside an unprofitable crowd until a better
opportunity opens. If nothing in sight pays better, it resumes the zone policy. The existing regroup behavior
remains.

`BotPointsHysteresis` boosts the current choice at each periodic decision; `BotPointsInterval` controls King's
comparisons (bankers retain their target cadence). Every estimate weight/range/time is a local `Tuning.BotPoints...`
property; `BotHardChasesPoints=false` provides an objective-policy A/B without changing difficulty's aim. These are
estimates, not a promise of kills or bank recovery, and use no extra navigation searches. The shared tuner schema,
world generation, scoring and messages are unchanged. King validates prizes against the full rifle collision mask,
including debris, tanks and players, before choosing them. A prize whose rifle shot later stays blocked for
`BotStuckAimTime` is excluded for `BotTargetRetryTime`, so a good observation ray cannot pin a bot indefinitely.

`tools/dev/check_bot_points.py` compares four Hard and four Normal actors in the same full Lab matches, alternates
which four spawn slots are Hard, and requires a higher aggregate mean final bank for Hard in each scoring mode.
`--policy-only` gives Hard Normal's aim to isolate target policy. Logs include earned and retained banks; retained
banks decide the comparison. King conservation is checked every round, and the sweep requires some zone minting in
aggregate (a single crowded round may mint nothing). This deeper strength sweep is separate from `make test`;
headless unit assertions cover leader preference, spill caps, return-fire/travel costs, zone opportunities and
runtime difficulty wiring.

## Regression course

`scenes/dev/bot_navigation_course.tscn` runs seven courses per seed: closed fence, diagonal approach/bin, direct
gate, car/fence, tank pile behind a fence, house/fence, and starting in contact with a fence. It uses `Suburb.Build`
and the actual player controller. Seeds vary fence style, rotation and approach position; props/tanks are real
built bodies. `--baseline` substitutes the old direct movement and jump/sidestep recovery, without changing the
course. `--first-seed=39 --seeds=1 --nav-log` replays a seed and traces its route.

The default run fails unless at least 95% of goals are reached within 15 seconds. Additional assertions require a
planned jump on closed-fence courses, walking through the direct gate, and no airborne entry into pile clearance.
At takeoff, injected pile/vent circles must reject the same arc, and a bot inside danger must be able to walk out.
`make test` runs five seeds; larger sweeps and screenshots use the commands in the [developer guide](development.md#dev-harnesses). Screenshots capture
picket/privacy crossings and supported landings. Unit tests cover physics-derived reach, gates, bounded alternate
route recovery, nearby goal stability, committed flight, per-tick search work, valid creep at 60/120 Hz and the
unchanged match tuning schema.
The original navigator's course sweeps and paired match outcomes are in the
[navigation measurements](evidence/bot-navigation-measurements.md).

## Idle bots

Easy bankers that have no useful banking target wander roughly toward the nearest upright rival, even one with an empty bank.
Ammo, tanks and profitable fights take priority. A short step of `BotEasyRoamRadius`, within `BotEasyRoamSpreadDegrees`
of the hint's direction, stays fixed for `BotEasyRoamSeconds` or until reached. It avoids routing straight across the map to a rival.
An unreachable scouting point is replaced without blacklisting the rival. Ordinary melee and profitable rifle fights
keep Easy's existing aim and reaction, and navigation retains its shared query/expansion budgets. These dials and
`BotEasySeekRivals` are local; Normal and Hard retain their goal policies.

Melee searches read each upright opponent's root position once per pass and reuse it for distance and reach tests.
Sight, aim and ragdoll-bone observations keep their existing paths.

`BotActivity` measures whether a bot plays. Play is cut into one-second windows; a window is idle when the bot stayed
upright, ended it less than a meter from where it began, and neither fired, meleed nor stood in King's zone. It counts
idle windows (and the blocked ones, in which the bot asked to move most of the time), the longest idle run and the time
in each status (by its format, `IBotGoalPolicy.StatusKind`). Owners sample their bots after thinking, outside the
measured brain cost. `net_test` logs `bot activity` per owned bot and a `bot idle:` summary at the end of a match, and
`bot navigation budget:` (how many ticks waited on the shared query or expansion budget); the Lab prints `[lab idle]`
and gates it ([lab.md](lab.md)). Measurements before and after the fixes above are in
[bot-scale.md](evidence/bot-scale.md#idle-bots).

