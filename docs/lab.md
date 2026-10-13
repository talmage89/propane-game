# The Lab

The Lab ships alongside Single player and Multiplayer. Choose a starter, choose an environment when offered,
then **Enter Lab**. Void is always the default: the same endless gridded floor, pale sky and golden-hour light as
the rest of Propane. Huge stack and Range use void; the banking duel and Free tuning also offer suburb.

- **Huge stack** builds a square upright pyramid using the generator's `PileBuilder`. Choose its count before
  entering, or change **Lab stack count** in tuning and reset. First shots puncture; second shots and blasts use
  the ordinary chain rules. The stack starts intact, so the first blast punctures its intact neighbors.
  The shipped range is 24–40 tanks, capped by crowded vent rendering on the measured hardware, not body capacity.
- **Range** alternates single tanks and small upright piles at marked distances from the orange firing line; each
  distance label grows with its distance, so the far lanes read from the line.
  Move along the line to line up with a lane; practice shots, punctures, melee and blast throws. Reset respawns
  the entire fixture and clears its debris.
- **Duel vs a banking bot** runs two owned actors in one offline world. The bot has no camera and uses the
  existing banking brain. Easy, Normal and Hard are the lobby bots' difficulty profiles: they set the
  `LabBotSkill` dial to that profile's skill (0.2, 0.6 or 1), which free tuning can still set between them; the menu
  shows the nearest profile. They change its skill, not tank/player physics. The default is three
  minutes; bank most to win. Bullets spill with hit grace, melee/blasts spill on the victim's ordinary ragdoll
  event, drops have the match's shield, ammo cans replenish, and delayed chains credit their initiating shooter.
  The void arena is marked by a ring of floor disks, half a step off both start points; the markings are reference
  points, not walls or a new boundary rule. A bot whose rifle ray stays blocked for `BotStuckAimTime` (4 s) without
  moving gives up that pile for `BotTargetRetryTime`: the vantage search can see past a loose fence panel that still
  stops the shot. The round's result replaces the controls panel's title when the clock ends.
- **Free tuning** opens the tuning panel after a short settling delay on a range fixture or a generated suburb.
  Opening it manually cancels that introductory opening. It is also available in every
  other starter through F1/` or the Lab controls menu. Opening it pauses this world and its clock. Sliders and
  toggles work with the mouse, keyboard or controller. **Defaults** restores code defaults; **Save** explicitly
  writes gameplay dials to the saved template. Unsaved edits never touch that template.

R / X resets non-duel starters; R / X reloads in a duel. Backspace resets any starter, from the controls panel too,
so a finished duel is one key from the next. Esc / Start opens Lab controls, where Reset/Respawn, Free tuning and Lab
menu are controller-accessible buttons. B closes tuning or resumes a paused scenario. Leaving returns to the
scenario list; Main menu returns to the front end.

Reset preserves the session's unsaved tuning but retires every actor, local view, timer, effect and physics
resource before rebuilding. Re-entry uses the menu's settings and a fresh copy of the saved gameplay template.
Lab layout and difficulty properties are local `Tuning` dials (no `Export`), copied with each session but excluded
from match snapshots and saved `.tres` files. Layout changes take effect on reset; bot skill changes take effect
on resume. No socket or server is created by the Lab.

## Extension and ownership

`LabScenarios.All` is the compiled registry: id, name, description, allowed environments and a setup callback.
The callback runs after the source builds content and before actors/views enter. Later prototypes register here;
each scenario names a registered mode ([modes.md](modes.md)): the duel plays Tank Bank and the other starters play
Sandbox, taking supply and ammo policy from it. There is still no health, teams, boundary policy or streamed
segments.

`VoidWorldSource` and `SuburbWorldSource` implement the existing `ISessionWorldSource` seam. The factory needs no
source-specific branches. A later streaming implementation can build and retire content roots with
`WorldContext.RetireContent`; it owns segment policy rather than teaching the factory about segments.

`LocalBank` is an offline Tank Bank runner: it feeds `TankBankRules` attributed detonations, spill tickets and its
clock, and spawns the spilled tanks between reservation and commit. It adapts actor/tank events locally. `ShotImpacting` attributes a direct tank contact before lifecycle
callbacks run; `BlastRequest`/`ChainCause` carry local causal actor keys through the existing chain path. The brain
reads an actor-specific `IBotWorld`, with scores and hit attribution keyed by actor id. The Lab uses a
simulation clock; its Protocol 2 dev adapter uses the existing monotonic clock. Both local actors share one owner
peer and retain separate banks. The factory and session retain ordered teardown on reset, exit and partial failure.

## Verification

`python3 -B tools/dev/check_lab.py` is the fast gate: one lifecycle cycle per allowed pair, local rules, the bot's
aim-stall probe and a 15-second scripted duel. It requires a success marker, exit 0 and no engine warnings/errors.
`--full` performs 10 entry/reset/exit cycles for all six pairs, sweeps every layout dial to both slider ends and all
at once through every scenario and environment (`--scenario=dials`), and runs 180-second bot-versus-bot soaks in both
environments, each of which must bank at least ten tanks. Both are wired into `make test` / `make test-full`.
`--only=soak-void,dials` runs only the named sessions (`lifetime`, `rules`, `aimstall`, `duel`, and with `--full`
`dials`, `soak-void`, `soak-suburb`); `make test-full` splits `--full` into three such jobs.
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
