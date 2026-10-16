# The Lab

The Lab ships alongside Single player and Multiplayer. Choose a starter, choose an environment when offered,
then **Enter Lab**. Void is always the default: the same endless gridded floor, pale sky and golden-hour light as
the rest of Propane. Huge stack and Range use void; the banking duel and Free tuning also offer suburb.

- **Huge stack** builds a square upright pyramid using the generator's `PileBuilder`. Choose its count before
  entering, or change **Lab stack count** in tuning and reset. First shots puncture; second shots and blasts use
  the ordinary chain rules. The stack starts intact, so the first blast punctures its intact neighbors.
  The shipped range is 24–512 tanks, starting at 40. Larger stacks are scale trials; persistent vents and chains
  cost more than an intact sleeping stack.
- **Range** alternates single tanks and small upright piles at marked distances from the orange firing line; each
  distance label grows with its distance, so the far lanes read from the line.
  Move along the line to line up with a lane; practice shots, punctures, melee and blast throws. Reset respawns
  the entire fixture and clears its debris.
- **Duel vs a banking bot** defaults to two owned actors in one offline world. **Lab bank bots** allows 1–15 bots; layout changes apply on reset. The bot has no camera and uses the
  existing banking brain. Easy, Normal and Hard are the lobby bots' difficulty profiles: they set the
  `LabBotSkill` dial to that profile's skill (0.2, 0.6 or 1), which free tuning can still set between them; the menu
  shows the nearest profile. Hard also enables the points policy in Tank Bank and King; tank/player physics stay the same. The default is three
  minutes; bank most to win. Bullets spill with hit grace, melee/blasts spill on the victim's ordinary ragdoll
  event, drops have the match's shield, ammo cans replenish, and delayed chains credit their initiating shooter.
  The void arena is marked by a ring of floor disks, half a step off both start points; the markings are reference
  points, not walls or a new boundary rule. A bot whose rifle ray stays blocked for `BotStuckAimTime` (4 s) without
  moving gives up that pile for `BotTargetRetryTime`: the vantage search can see past a loose fence panel that still
  stops the shot. With several opponents, the HUD shows the strongest bot and the result compares every bank.
  The round's result replaces the controls panel's title when the clock ends.
- **Free tuning** opens the tuning panel after a short settling delay on a range fixture or a generated suburb.
  Opening it manually cancels that introductory opening. It is also available in every
  other starter through F1/` or the Lab controls menu. Opening it pauses this world and its clock. Sliders and
  toggles work with the mouse, keyboard or controller. **Defaults** restores code defaults; **Save** explicitly
  writes gameplay dials to the saved template. Unsaved edits never touch that template.

Suburb setup pages (the banking duel, Free tuning, King and Short Fuse) offer **Tank supply** in every integer
step from 1× to 8×. It scales both the suburb's starting tanks and pile sizes through `TankCountMultiplier`.
Choose −/+ with the mouse, or focus either button and use Enter / A. The choice stays in the unsaved Lab draft
across scenario changes and returning from play; launching copies it into the session without editing saved
tuning. F1 still changes it offline, with a reset required to rebuild. The control appears only for a generated
suburb: void fixtures and Dissolve Run's segment supply use their own counts.

## Prototype modes

Below the starters, **Prototype modes · rules on trial** lists modes the owner is trying before setting their rules.
Each but Dissolve Run plays you against Lab bots (Lab prototype bots, 1–15, default 3; the duel's difficulty sets their skill) in an
offline round, with the match HUD on top. Their rules are in [modes.md](modes.md#lab-prototypes); every number is a
Lab dial in the tuning panel's Lab section, and layout dials apply on reset.
Short Fuse prefers rim starts; when a large roster exhausts starts separated by `MpStartSpacing`, it uses clear
interior road starts too. This changes only local actor placement, not the generated suburb or the mode's rules.

- **Ring Out** (void only). A slab of 5 × 5 lots of 16 m floats over the void, with four piles ringed around the
  middle. Every 15 s a band of the rim pulses orange for 4 s and dissolves, until the center lot remains. Fall off, or
  stand where the ground went, and you are out; the feed credits whoever threw you within 6 s. You then watch from a
  ledge off one side, free to look around. The last one standing wins; the controls panel shows every place.
- **King of the Cul-de-sac** (void or suburb). A beam marks a 12 m zone on a pile. Stand in it alone to mint a banked
  tank every 2 s; anyone else upright inside stops it (the beam pulses red), and the holder's bar fills toward each
  tank. Every 45 s the zone moves to another pile 15–25 m away after a 3 s warning (a pale beam marks the next spot)
  and a fresh pile materializes on the nearest clear, flat ground beside it. Banking, hits and throws spill as in Tank
  Bank. The void lays out a ring of eight pile spots around one in the middle; a suburb uses the plan's own piles.
  Most banked when the clock runs out wins. The King setup page also sets the pace and length a multiplayer lobby sets for King,
  on the lobby's steps: the mint pace (0.5 to 10 s) and the round length (3:00 in the Lab; the lobby keeps its existing 2:30 default).
  King is also playable online with humans and lobby bots; its HUD and zone visuals use the same state view.
- **Short Fuse** (suburb only). Last one standing, with health: 100 each, bullets take 7, a blast 90 at its center
  down to 30 at the edge of its throw and nothing beyond, and 5 HP/s comes back after 5 s unhit. You get 0.75 s of
  protection once you are back on your feet after a throw. A thrown player is helpless, so a blast sets up the kill
  and bullets finish it. Everyone starts around the rim of a 150 m suburb stocked for the players; the dead go limp,
  lie for 1.5 s and then watch the player who got them (or, once they are gone too, the next one still alive), with
  "SPECTATING <name>" under the crosshair. The last one alive wins the round; after 4 s a new suburb assembles for the
  next round, and the first to three round wins takes the match. A round reaching its 105 s limit goes to the
  healthiest survivor, or to nobody on a tie. The HUD shows round wins, how many stand, your kills, the round clock, a
  health bar, the round's kill feed and the result banner; the controls panel shows the match's result.

- **Dissolve Run** (suburb only, solo). One endless street, built a 60 m segment at a time from the generator's parts.
  The street materializes about 60 m ahead of the furthest point you have reached; behind the dissolve front, a glowing
  sheet across the street, everything is gone: the ground, the tanks and the props. The front starts 25 m behind you at
  1.5 m/s and speeds up toward 5 m/s (under sprint speed), but never trails you by more than 60 m. Every tank you
  detonate pushes it back 1.5 m, once per tank in a chain. If it catches you, the ground goes and you fall; the run is
  over. Score is meters run, plus 10 per tank, plus n² for each chain of n (two or more) once it has gone quiet for a
  second. Segments hold piles in the yards and two ammo cans on the sidewalks; the first holds 16 tanks and each later one
  half a tank fewer, down to 4, and every tenth adds a 30-tank pyramid in the middle of the street. Rifles carry limited
  ammo (R / X reloads; Backspace starts a new run). The menu chooses the seed: a new one each run, or today's, the same
  street for everyone that day. The HUD shows the score, meters and tanks, the run's clock, a bar for the front's gap
  (hot under 8 m), the open chain and the latest scoring chains; the controls panel shows the result and the best run
  (kept per seed choice in `user://dissolve_run.cfg`). Every number is a Dissolve dial in the tuning panel's Lab
  section, the sheet's glow and height included; segment and layout dials apply on reset.

Ring Out, King and Short Fuse have unlimited ammo and no ammo cans. Bots run on the Lab’s main thread with shared navigation budgets and cached sight lines
([bot navigation](bot-navigation.md)); the bot limit is 15 beside the player. Full matches still include tank,
character and rendering costs, which vary with hardware and activity.

R / X resets non-duel starters; R / X reloads in a duel. Backspace resets any starter, from the controls panel too,
so a finished duel is one key from the next. Esc / Start opens Lab controls, where Reset/Respawn, Free tuning and Lab
menu are controller-accessible buttons. B closes tuning or resumes a paused scenario. Leaving returns to the
scenario list; Main menu returns to the front end.

Reset preserves the session's unsaved tuning but retires every actor, local view, timer, effect and physics
resource before rebuilding. Re-entry uses the menu's settings and a fresh copy of the saved gameplay template.
King's shared zone and pile dials are exported for online matches and retain their Lab controls. Other Lab layout and difficulty properties are local `Tuning` dials (no `Export`), copied with each session but excluded
from match snapshots and saved `.tres` files. Layout changes take effect on reset; bot skill changes take effect
on resume. No socket or server is created by the Lab.

## Extension and ownership

`LabScenarios.All` is the compiled registry: id, name, description, allowed environments and a setup callback.
The callback runs after the source builds content and before actors/views enter. Later prototypes register here;
each scenario names a registered mode ([modes.md](modes.md)): the duel plays Tank Bank, the prototypes play their own
modes, and the other starters play Sandbox, taking supply and ammo policy from it. A prototype scenario names a mode
from `ModeRegistry.Prototypes`; its setup builds an arena presenter (`RingOutArena`, `KingArena`, `ShortFuseArena`) and
the session adds the bots and a `PrototypeRunner`. Dissolve Run's world is a `SegmentWorldSource` (below), and its
arena (`DissolveArena`) owns the segment policy. A Lab dial with a `Scenario` lays out only that scenario. A
multi-round prototype (Short Fuse) keeps a `LabMatch` on the session: each round is a reset that keeps the match's rules,
round and mode clock and builds the next suburb from the match's seed stepped by round, while Backspace or Reset starts a
new match. There are still no teams.

`VoidWorldSource`, `SuburbWorldSource` and `SegmentWorldSource` implement the existing `ISessionWorldSource` seam. The
factory needs no source-specific branches. The segment source is the streaming one: each segment is its own suburb (its
own content root, effects and physics bodies) under a run root, built from a `SegmentPlan` that an `ISegmentFeed`
supplies by index, and retired whole through `WorldContext.RetireContent`. Its owner decides when: Dissolve Run's arena
builds the next segment once the horizon comes within `DissolveBuildLead` (20 m) of the last one's end and retires one
once the front is `DissolveRetireLag` (10 m) past its end. A build runs a few pieces per physics step inside 2.5 ms
(`Suburb.BuildStaged`: the suburb enters the tree hidden and fills in place, with the same pieces in the same order as
`Suburb.Build`), and retirement frees 16 nodes at a time inside 1.5 ms after the segment has left sight and physics at
once. Plans come from a worker thread (`LocalSegmentFeed`, generating from a `PlanCatalogSnapshot` measured on the main
thread), and every model and collision shape a suburb can use is loaded once when the session starts (`Suburb.Warm`).

`LocalBank` is an offline Tank Bank runner: it feeds `TankBankRules` attributed detonations, spill tickets and its
clock, and spawns the spilled tanks between reservation and commit. It adapts actor/tank events locally. `ShotImpacting` attributes a direct tank contact before lifecycle
callbacks run; `BlastRequest`/`ChainCause` carry local causal actor keys through the existing chain path. The brain
reads an actor-specific `IBotWorld`, with scores and hit attribution keyed by actor id. The Lab uses a
simulation clock; its Protocol 2 dev adapter uses the existing monotonic clock. Both local actors share one owner
peer and retain separate banks. The factory and session retain ordered teardown on reset, exit and partial failure.

Both runners own a `TankTracking` collection, with one session unsubscribe drain. Each registration detaches its
puncture/detonation handlers and releases captured state on detonation or content retirement. Remaining registrations
drain in reverse registration order after world invalidation and before runner services stop. Attribution stays in the
runners. Dissolve Run explicitly forgets tracking before starting native segment retirement; visual dissolution alone
keeps the registration until retirement. Shooter/drop references retire with their tank. This bounds subscription roots,
not every streaming data structure: forgotten-index history, timing lists, navigation chunks and the global reveal window
remain separate work.

## Verification

`python3 -B tools/dev/check_lab.py` is the fast gate: one lifecycle cycle per allowed pair, local rules, the bot's
aim-stall probe and a 15-second scripted duel. It requires a success marker, exit 0 and no engine warnings/errors.
The lifecycle run's first shard also runs `--scenario=tracking`: detonate/retire/reset/repeated-exit permutations,
captured shooter/drop cleanup, the unchanged visual-dissolve boundary and weak-reference collection while a session
remains alive. Discovered `TankTrackingTests` cover all six detonate/retire/shutdown orders, duplicate registration,
reverse drain through removal/re-registration, already-freed native objects, cleanup failure and captured callback state.
`LabCreditTests` exercise both runners' direct/chain/drop attribution and emit a parent-comparable command transcript.
It also plays a Ring Out round and a King round to their ends with shortened dials (`--scenario=prototype --quick`),
a bot on every actor, one quick Short Fuse match (`--scenario=shortfuse --quick`: 60 s rounds, first to two), and a
scripted Short Fuse round (`--scenario=shortfuse-combo`) through the real reports: Bot 2 shoots the player dead (the
player then watches Bot 2), sets off a pile beside Bot 1 and finishes it with bullets while it is down, wins the round,
and the next suburb builds with new lives; with `--capture-dir` it screenshots each step. `--full` performs 10 entry/reset/exit cycles for every scenario and environment, sweeps every
layout dial to both slider ends and all at once through every scenario and environment it lays out
(`--scenario=dials`), and runs 180-second bot-versus-bot soaks in both
environments, each of which must bank at least ten tanks, and full prototype rounds at the shipped dials (Ring Out on
three seeds, King on three in the void and one in a suburb). Each prototype round must end legally: Ring Out with a
winner, every starter placed in elimination order and the out spectating; King on its clock with the bank conserved,
every mint counted and a materialized pile per zone move (in a suburb, one may find no clear ground within reach). `--full` also plays two Short Fuse matches at the shipped
dials (`shortfuse-full`), reporting how often a bot finished a thrown player with a bullet. Each Short Fuse
round must end legally: every death a starter's, once, credited to someone else or to nobody; the dead spectating;
alive plus dead equal to the starters, and one alive at most unless the limit ended it; the winner alive. Each match
must have one winner with the wins, round wins and kills that add up, lives that advanced with every round, and no
report the rules refused as late, early, duplicated or naming an unknown actor or blast (the Lab's reports arrive at
once). All are wired into `make test` / `make test-full`.
It also runs Dissolve Run for 45 s with the bot driving the player (`--scenario=dissolve --seconds=45`), then stops it:
the front must catch it within the time it needs to cover the gap at its start speed, plus the fall, and the run must
end with the player fallen below the slab, the score adding up (meters, tank points and chain bonuses, every tank in one
chain and one push), segments built and retired, and the result paused on the controls panel. `--full` adds five-minute
runs (`dissolve-full`: the bot on two seeds; `dissolve-pace`: a pacer that runs down the street without bot navigation,
so its frame times measure the stream rather than the bot), which also require the live segments, their nodes and bodies
and the whole tree's nodes to stay bounded (the last third of the run holding no more than the first third, give or take).
They also enable `--tracking-proof --assert-tracking`: every census requires one registration per unexploded tank in
active segments (including visually dissolved tanks), and the end checkpoint collects retired wrappers before exit.
The dial sweep locks actor input while gravity settles before and after each reset. Its floor/fixture assertions
check layout and retirement; active bot combat (including Hard's immediate banking) is covered by the duel,
prototype rounds and points sweep. A valid early blast or hitstop must not masquerade as a failed reset.

Two idle checks keep crowds of bots playing: `idle-bank` (a 40-second suburb soak with eight NPCs and the driven
player) and `idle-king` (a 45-second suburb King round with sixteen driven actors). Their fixtures seed the per-event
blast, ragdoll and debris generators (`--replay-random`) and disable wall-clock hitstop. Every other Lab run retains
ordinary entropy and hitstop. [Idle check verification](idle-checks.md) records the cause and repeated load trials.
Every Lab bot run prints `[lab idle]` lines: each bot's idle share (see [bot navigation](bot-navigation.md#idle-bots)),
longest idle run and commonest statuses. `--max-idle=F` and `--max-idle-worst=F` assert that run's mean and worst shares;
the checks retain their original limits of 12%/30% and 15%/40%.
`--only=soak-void,dials` runs only the named sessions (`lifetime`, `rules`, `aimstall`, `duel`, `ringout`, `king`,
`shortfuse`, `shortfuse-combo`, `dissolve`, `idle-bank`, `idle-king`, and with `--full` `dials`, `soak-void`, `soak-suburb`, `ringout-full`, `king-full`,
`king-suburb`, `shortfuse-full`, `dissolve-full`, `dissolve-pace`); `make test-full` splits
`--full` into 25 jobs: separate sessions, individual Ring Out/Short Fuse/Dissolve seeds, four lifetime shards and eight dial shards.
King keeps its three rounds together because its mint assertion spans the rounds. `--dial-shard=1 --dial-shards=8`
selects one eighth of the actual dial/scenario/environment cases; `--list-dials` lists those cases without building
worlds. Lifetime has the same listing and shard arguments (`--list-lifetime`, `--lifetime-shard`, `--lifetime-shards`); each selected pair keeps its two warm-ups and all ten checked cycles. `make test-full-lab-coverage` proves that the shards cover every full session, setting and dial fixture
exactly once. The fast checker retains its 600-second session timeout. Full sessions have 1,800 seconds, while dial
sweeps have `max(600, 2400 / shards)` seconds; a timeout kills the engine and any timing wrapper together.

King outcome runs print `[king crowd]`: audited mints, mints per minute, the sampled fraction of simulation time
with a lone holder, and the number of actors that minted. `net_test` logs `king crowd` with the client-observed
lone-holder share and minter count beside its final per-actor mints; compare those mints with the server audit.
`python3 -B tools/dev/check_king_crowd.py` runs 90-second rounds in void and suburb at 4/8/16 actors and all three
skills, checks audits and writes `build/king-crowd/summary.csv`. `--network --faults` runs 90-second King soaks with
four clients clean and faulted, and two clients plus six or fourteen lobby bots clean, at 1×/4× supply. All cases
use fixed world/brain seeds; network faults default to seed 2. Use distinct `--output` directories for comparisons.
`--minimum-mints=0 --minimum-minters=0` permits dry baseline measurements; the Lab's standalone outcome check
still requires a mint unless explicitly given `--min-king-mints=0`. Tank, score and conservation checks remain active.

A prototype round alone: `lab_test.tscn -- --scenario=prototype --mode=ringout|king
[--environment=suburb] [--seeds=3] [--first-seed=1] [--bots=2] [--quick]`; `--shots=4,12.5` adds `--capture-dir`
screenshots at those round times. Short Fuse matches alone: `lab_test.tscn -- --scenario=shortfuse [--seeds=2]
[--first-seed=1] [--bots=2] [--quick] [--trace]`; `--capture-events` adds `--capture-dir` screenshots of the first
blast-then-bullet kill, the kill feed, the player spectating, the first round-win banner and the match's end.
A Dissolve Run alone: `lab_test.tscn -- --scenario=dissolve [--seconds=240] [--stop-at=240] [--seeds=1] [--first-seed=23]
[--driver=pace --pace=walk|run|sprint] [--report-every=30] [--census-every=1] [--metrics=$PWD/build/run-{seed}.csv]
[--trace] [--trace-frames]`; `--shots=4,12.5` adds `--capture-dir` screenshots at those run times and `--overview` watches
from a high camera behind the runner. It prints frame times, segment build and retire step times, segment, node, body and
tree counts, memory, managed allocation and GC pauses. `--tracking-metrics` also brackets all managed physics
callbacks with first/last tick probes and reports tick count, callback mean/p95/p99 and allocation, plus whole-session
allocation after assembly. Whole-tick timers close at the next physics/process signal, including the native tail
(engine bookkeeping and query flush too, as in `FrameMetrics`); their mean/p95/p99 exclude rendering and are not
Jolt-internal solver timings. Frame-wall timings include rendering; callback timings exclude the native tail.
`--tracking-proof` observes tanks through weak references, prints the tracking count alongside active content and
historical supply, and collects freed wrappers while the session is still alive at the end checkpoint.
`--assert-tracking` additionally requires matching counts and no retained freed wrappers (use with `--tracking-proof`).
These diagnostics are opt-in; keep equivalence observers out of measurements and collection proofs because they retain
tanks deliberately. The immediate parent can run the same instrumentation: its absent tracking count prints as -1.
`--scenario=aimstall` puts a shot-stopping prop in front of the bot and asserts it leaves the pile.

`menu_test --scenario=input` injects real keyboard, mouse and controller events through Lab menus, movement/combat,
reset, local tuning dials and Defaults without template edits. It clicks through the menus and the controls and
tuning panels with the mouse, walks the tuning list with the D-pad and checks the focused row stays in view, and then
starts single player and a hosted match. The local rules probe also asserts firing foregrip contacts after a stale
reload pose and after animation and body frames advance between shot fitting and rendering. Fixtures, captures,
soaks and measurements load the game's baked tuning rather than C# defaults.

```sh
make build
godot-mono --headless --fixed-fps 60 res://scenes/dev/lab_test.tscn
python3 -B tools/dev/check_lab.py --full
godot-mono --fixed-fps 60 --resolution 1920x1080 res://scenes/dev/lab_test.tscn -- \
  --scenario=frames --windowed --capture-dir="$PWD/build/lab-1920x1080"
godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/lab_test.tscn -- \
  --scenario=frames --windowed --capture-dir="$PWD/build/lab-1280x720"
godot-mono --fixed-fps 60 --disable-vsync --resolution 1920x1080 res://scenes/dev/lab_test.tscn -- \
  --scenario=stackfire --windowed --capture-dir="$PWD/build/lab-stackfire"
godot-mono --fixed-fps 60 --resolution 1920x1080 res://scenes/dev/lab_test.tscn -- \
  --scenario=duel --duel-seconds=30 --seconds=40 --windowed --capture-dir="$PWD/build/lab-duel-end"
godot-mono --disable-vsync --resolution 1920x1080 res://scenes/dev/lab_test.tscn -- \
  --scenario=measure --count=40 --phase=idle --windowed
```

The measurement samples 600 real frames after 180 warm-up frames. `--phase=blast` fires twice into an intact
stack; `--phase=chain` first punctures every tank for a deliberately harsh primed-chain case. It reports wall
frame time, GPU render time and process/physics time. The headless gamescope backend can fall back to 60 Hz
VSync despite `--disable-vsync`; GPU timings remain useful when wall frames are capped. Run measurements without
other rendered scenes or heavy CPU tests; fixed-FPS captures are independent of throughput.

## Stack budget

The crowded-effect optimization retains the shipped adjustable range at **24–40 tanks**. A repeated comparison
on the GTX 1660 at 1920×1080, Forward+, seed 23 and baked tuning measured these pooled results (two back-to-back
before/after repeats, each with 180 warm-up frames and 600 samples):

| Tanks | Ordinary blast GPU p95 before → after (ms) | Blast wall p95 before → after (ms) | Primed chain wall p95 before → after (ms) |
| ---: | ---: | ---: | ---: |
| 32 | 26.4 → 22.2 | 29.7 → 25.7 | 86.8 → 78.7 |
| 40 | 37.5 → 37.5 | 43.0 → 38.5 | 107.4 → 99.1 |
| 64 | 62.6 → 58.6 | 67.0 → 61.5 | 129.4 → 40.0 |
| 128 | 193.1 → 138.5 | 199.2 → 219.0 | 292.6 → 484.5 |

Shared one-minute CPU load ranged from 5.89 to 38.00, with 0–3 other rendered Godot processes; wall results,
especially chain spikes, cannot establish an isolated hardware limit. The 128-tank 16.7 ms p95 / 50 ms worst
requirements remain unmet. Forty is still near a 30 FPS blast budget, not a promise of uninterrupted 60 FPS,
and no larger product ceiling is justified. The original idle sweep reached 1,024 tanks at GPU p95 9.9 ms
(wall p95 18.4 ms); that size was not repeated in this comparison. Resting bodies remain cheap; persistent lit smoke
and chain CPU/physics bursts remain expensive. Gameplay values and particle counts are unchanged. See
[crowded effect profiling](vent-performance.md) for every phase's GPU/wall p50, p95 and worst, profiler breakdown,
load records, rendered suburb matches, visual comparisons and reproducible baseline commands.

The measurement registry callback deliberately permits 24–1,024 tanks while retaining the actual Lab fixture
builder. It can profile beyond the product ceiling without relaxing the shipped menus or tuning validation.

For a mixed Hard/Normal strength trial, `python3 -B tools/dev/check_bot_points.py` runs both scoring modes across
five seeds and both assignments of four Hard spawn slots. `--policy-only` equalizes aim; `--environment=void`
selects the arena. Its local `LabHardBots`/`LabHardOffset` overrides choose a consecutive range of attached brains
(the player attaches last in bot-driven runs); remaining actors keep `LabBotSkill`. `--compare-points` on the Lab
harness prints both groups' final and earned banks. Duel/soak `--trace` includes positions and navigation state.

Prototype outcome probes bound the round's simulation clock, since explosion hitstop slows that clock while
process frames keep arriving. Their summary includes process-frame count; `check_lab.py` also enforces a wall
timeout so a stopped simulation still fails. Scoring, conservation and legal-outcome assertions remain active.

`check_bot_points.py --easy` compares Normal and Easy together in Tank Bank at four/eight actors, swapping their
spawn groups across seeds. Both use objective policies and their actual skill dials; it gates retained banks rather
than inferring difficulty from separate all-Easy and all-Normal matches.
`--replay-random` seeds physical events from each world's seeded stream and disables wall-clock hitstop for either
comparison. Without it, the checker retains ordinary gameplay entropy and hitstop.
