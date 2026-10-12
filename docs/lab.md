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
  existing banking brain. Easy, Normal and Hard change its skill, not tank/player physics. The default is three
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
this package introduces no health, teams, mode runner, boundary policy or streamed segments.

`VoidWorldSource` and `SuburbWorldSource` implement the existing `ISessionWorldSource` seam. The factory needs no
source-specific branches. A later streaming implementation can build and retire content roots with
`WorldContext.RetireContent`; it owns segment policy rather than teaching the factory about segments.

`LocalBank` adapts actor/tank events locally. `ShotImpacting` attributes a direct tank contact before lifecycle
callbacks run; `BlastRequest`/`ChainCause` carry local causal actor keys through the existing chain path. The brain
reads an actor-specific `IBankingBotWorld`, with a simulation clock for Lab and the existing monotonic clock for
its Protocol 2 dev adapter. The factory and session retain ordered teardown on reset, exit and partial failure.

## Verification

`python3 -B tools/dev/check_lab.py` is the fast gate: one lifecycle cycle per allowed pair, local rules, the bot's
aim-stall probe and a 15-second scripted duel. It requires a success marker, exit 0 and no engine warnings/errors. `--full` performs
10 entry/reset/exit cycles for all six pairs and 180-second bot-versus-bot soaks in both environments. Both are
wired into `make test` / `make test-full`. `--full` also sweeps every layout dial to both slider ends, and all at once,
through every scenario and environment (`--scenario=dials`). `menu_test --scenario=input` also injects real keyboard,
mouse and controller events through Lab menus, movement/combat, reset, local tuning dials and Defaults without
template edits. It clicks through the menus and the controls and tuning panels with the mouse, walks the tuning list
with the D-pad and checks the focused row stays in view, and then starts single player and a hosted match.
`--scenario=aimstall` puts a shot-stopping prop in front of the bot and asserts it leaves the pile.
The local rules probe also asserts firing
foregrip contacts after a stale reload pose and after animation and body frames advance between shot fitting and rendering.
Fixtures, captures, soaks and measurements load the game's baked tuning rather than C# defaults.

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

On a GTX 1660 at 1920×1080 (Forward+), each row samples 600 real frames after 180 warm-up frames, with seed 23
and baked gameplay/effect tuning. These are throughput runs, without `--fixed-fps`. Wall timings include the
gamescope backend's 60 Hz fallback and shared-machine scheduling; GPU timings help distinguish rendering cost.

| Tanks | Intact GPU p95 (ms) | Ordinary blast median / p95 (ms) | Blast GPU p95 (ms) |
| ---: | ---: | ---: | ---: |
| 32 | 6.7 | 16.8 / 26.3 | 26.0 |
| 40 | 6.6 | 24.8 / 37.1 | 36.4 |
| 48 | 6.6 | 38.2 / 49.9 | 49.3 |
| 64 | 6.7 | 48.9 / 61.2 | 58.1 |
| 128 | 7.1 | 132.9 / 160.7 | 158.3 |
| 256 | 7.4 | — | — |
| 512 | 8.4 | — | — |
| 1,024 | 9.9 | — | — |

Forty is a conservative usable ceiling near a 30 FPS blast budget, not a promise of uninterrupted 60 FPS.
Fully primed 32/40/48-tank chains measured p95 18.6/23.0/44.6 ms with worst frames 289/293/681 ms; the burst
can hitch even below the cap. Idle bodies alone are cheap, but their persistent overlapping vents make a
substantially larger shipped stack impractical on this GPU. No effect dial or tank rule was reduced to hide
that limit. Larger stacks need a rendering optimization before raising the cap; review this limit on other GPUs.

The measurement registry callback deliberately permits 24–1,024 tanks while retaining the actual Lab fixture
builder. It can profile beyond the product ceiling without relaxing the shipped menus or tuning validation.
