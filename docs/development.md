# Developer guide

How to build, run, test, tune, measure and release Propane from a checkout. Players need only the
[README](../README.md); [design/VISION.md](../design/VISION.md) is what the game is and must stay, and
[README.md](README.md) in this folder says which documents are current contracts and which are dated evidence. The
rules for agents working in this repository are in [AGENTS.md](../AGENTS.md).

Propane is a Godot 4.7.2 .NET (C#, Jolt) game. Windows, macOS (Apple Silicon) and Linux are all targets; development
and verification happen on Linux.

| Section | What it covers |
| --- | --- |
| [Running from source](#running-from-source) | Setup, the editor, `make`, the quality gate. |
| [Measuring scale](#measuring-scale) | `make bench` and the scale records. |
| [Lab](#lab) | The Lab menu, its starters and prototype modes. |
| [Multiplayer](#multiplayer) | Lobbies, bots and hosting from a player's side, and the version rule. |
| [Controls and input](#controls-and-input) | The full binding table, device rules and how the player controller reads input. |
| [Tuning](#tuning) | Tuners, saving them, piles, chains, supply and match settings. |
| [Layout](#layout) | What lives where in the repository. |
| [Dev harnesses](#dev-harnesses) | The scripted scenes in `scenes/dev` and their commands. |
| [Versions and releases](#versions-and-releases) | Which versions play together, cutting a release, packaging. |
| [Assets](#assets) | The Blender scripts that build `assets/`. |
| [README media](#readme-media) | How the README's still and GIF were made, and how to make them again. |

## Running from source

`make setup` keeps import diagnostics in `build/setup-import.log`, compiles, then requires a clean second import
(`build/setup-import-compiled.log`). Before the Debug assembly exists, only the exact ShutdownGuard autoload
"not compiling" diagnostic is tolerated: Godot imports resources before C# is built. Any other import error or
nonzero exit fails setup, including errors Godot reports with exit zero. Run setup after changing checkouts.

Requires Godot 4.7.2 (.NET edition) and the .NET 10 SDK.

- First: `make setup` imports the assets and compiles. Run it again after switching branches or pulling. It also
  gives the checkout its own Godot user data folder, `Propane-dev/<folder name>`, in a gitignored `override.cfg`.
- Editor: open `project.godot` and press Play (F5).
- Command line: `make run`. `make play` runs optimized Release C# locally (`CONFIG=Debug` selects Debug); see
  [Measuring scale](runbooks/measuring-scale.md#playing-optimized-c-locally). `make` lists the other tasks (tests, a local
  server, bots, releases); they find Godot on `PATH` (`godot-mono` or `godot`) or in `/Applications`, or set `GODOT` to its binary.
- Quality gate: `make lint` verifies C# formatting and rebuilds Debug and the shipping ExportRelease configuration
  with warnings as errors (no export needed), then enforces the [architecture](architecture.md) dependency and source-site ratchets. `make check` runs lint,
  then `make test`, then `make test-infra` (the gate's own self-tests, `tools/dev/lint_tests.py`: it needs no Godot,
  and CI runs it as the `infra` job). Run it before submitting a change. `tools/dev/check_world.sh` prints log tails and keeps its
  scratch directory on failure. GitHub Actions runs the same checks on pushes to `dev`
  and every pull request. `make -j4 test-full` adds the deep sweeps as parallel jobs, each line prefixed with its job's
  name; plain `make test-full` runs the same jobs one at a time. Deep probes use an isolated optimized Debug
  assembly (all `DEBUG` checks and tuners retained); fast tests and the final real-time bot matches use normal Debug.
  Its isolated view includes the project compile metadata so core tuning-order assertions work there too.
  Both generator and Lab shard coverage are checked. See [gate measurements](evidence/test-speed.md).

[code-quality.md](code-quality.md) describes the formatting and analyzer baseline and its regression
checks. The gate follows the existing code's style and checks correctness without API design or naming churn.

The game opens maximized on the main menu: Single player, Multiplayer, Lab or Quit. In play it captures the mouse; click
the window to recapture it after switching away.

## Measuring scale

`make bench` measures a fixed suburb across 0/2/4/8/16 in-process bots and 1×/2×/4×/8× tank supply,
headless and rendered, with three repetitions and JSON comparisons. `make bench CONFIG=Debug,Release` alternates
the two C# builds per case. [Workload, columns and comparison procedure](runbooks/measuring-scale.md).
[Bot scale results](evidence/bot-scale.md) compare the shared navigation implementation with the previous brains,
including rendered sixteen-bot runs, match outcomes and remaining allocation/network limits.
[King crowd verification](evidence/king-crowd.md) compares holds, mints and thinking cost at 4/8/16 actors.

## Lab

Choose **Lab** for an upright huge stack, a marked shooting range, a local duel against the banking bot, or free
session tuning. Void is the default; duel and free tuning also offer a suburb. Under **Prototype modes**, Ring Out,
King of the Cul-de-sac and Short Fuse (last one standing, with health) are new modes on trial against Lab bots, and
Dissolve Run is a solo run down an endless street that builds ahead and dissolves behind, so their rules can be judged
before anything ships. Each starter can reset and has a
controller-accessible controls menu. Unsaved tuning changes stay in that Lab session. [Lab controls and verification](lab.md).
Banking, Ring Out, King of the Cul-de-sac and Short Fuse Lab sessions support up to fifteen NPCs.

## Multiplayer

Choose Multiplayer, type your name and the server's address (`host` or `host:port`, UDP port 24680 by default), then
create a lobby or join one. A lobby is a shared void world where everyone roams, shoots and throws tanks while
waiting; Tab (View on a controller) opens its panel. Everyone picks a name and any color there (duplicates are allowed),
and whoever created it sets capacity, mode, length, tank supply and King mint pace and starts: a short stage shows the
lineup and counts down, and after the match everyone is back in the lobby world. Tank supply scales the suburb's tanks and pile sizes and locks at match start; everyone sees the choice. **Host on this computer** runs a server inside your game instead; the
lobby then shows the addresses friends can use. Versions with the same major and minor number play together
(0.2.0 with 0.2.5); the server turns away others and says which version it needs.

To play against bots, press **Add bot** in the lobby: give it a name, a color and Easy, Normal or Hard. Anyone in the
lobby can add bots; they count toward its capacity, run inside their creator's game and leave with their creator. From
source, `make bots SERVER=host[:port]` still starts separate bot processes for testing (`make bots-stop` stops them).
Starting the game with `-- --connect=host[:port]` skips straight to that server's lobbies.
The message transport is injectable for deterministic tests; see the [transport contract](multiplayer.md#transport-contract)
for fault simulation, disconnect reasons and diagnostics. On Windows or macOS, the
first time you host, allow incoming connections if the system asks.

The dedicated server is the same game run headless: `./Propane.x86_64 --headless -- --server [--port=24680]`.
`tools/server/deploy.sh <ssh host> <dir>` exports a clean release-tag checkout contained in local `main`, inventories
it and runs it in Docker on any machine you can reach over ssh;
see [multiplayer.md](multiplayer.md) for hosting and reaching it. Keep a server on the latest release: it
then plays with every patch of that minor version.

## Controls and input

| Keyboard / mouse | Gamepad (Xbox / PlayStation) | Action |
| --- | --- | --- |
| W A S D | Left stick | Move at the default gait (6 m/s) |
| Shift (hold) | Left stick click (hold) | Sprint forward (9.5 m/s) |
| Space | A / Cross | Jump, including while aiming |
| Mouse | Right stick | Look |
| Left click | Right trigger | Fire (hold for full auto; toggle it in the tuning panel) |
| Right click (hold) | Left trigger (hold) | Aim and creep: upright shouldered walk (1.6 m/s), tight spread; camera stays put |
| F or either mouse thumb button | Right stick click (R3) | Rifle-butt strike: grounded lunge and knockdown; shoves tanks intact |
| R | X / Square | New suburb (single player); reload (multiplayer) |
| Esc | Start / Options | Pause and show the controls (a match keeps running) |
| F1 or ` | — | Tuning panel (live sliders; in a match, the creator's debug build only, and it changes everyone's values) |
| F11 or Alt+Enter | — | Fullscreen |

Aim cancels sprint. Releasing aim with sprint still held restores sprint's target speed on that physics tick,
including after firing. Jumping drops effective aim in the air; held aim returns on landing. Reload owns the weapon
and suppresses aim/fire while leaving the selected movement gait free. Melee cancels sprint/reload, suppresses
jump/aim/fire, lunges forward through windup/contact, and stops during recovery before its cooldown. Right-stick
click puts melee under the looking thumb while leaving movement and the aim/fire triggers available.

The lobby keeps eight quick color presets. **Custom color** opens a live character preview, twelve presets, and hue,
saturation and brightness strips; click or drag a strip, or focus it and use left/right or the left stick. Taps
make fine adjustments; holding accelerates larger moves. Up/down moves between rows and left/right selects a
preset in its row; A / Cross picks it. Hex entry is available for exact colors. The choice is saved between runs,
and old saved palette choices migrate automatically; **Use color** saves and **Cancel** keeps
the previous choice.

Menus work with D-pad or left stick, A / Cross to confirm, and B / Circle to go back. Start / Options opens and
closes pause or the match menu. Left / right changes the focused match length; disabled choices are skipped.
Name and server fields accept their current text with A / Cross, so defaults can be accepted or skipped without
typing. On-screen text entry is supplied by Steam, outside the game. The pause reference and reload hint follow the
last active input device.

Gameplay buttons and stick axes use named actions ([Godot's controller input model](https://docs.godotengine.org/en/stable/tutorials/inputs/controllers_gamepads_joysticks.html)).
Mouse look is displacement; stick look is a rate with separate sensitivity, radial curve and deadzone tuners.
Pause and focus changes clear pending presses and mouse motion, and resample held actions before input resumes.

`src/Player/PlayerState.cs` resolves lifecycle, support, gait and weapon action once per physics tick, from intent and
pre-move floor contact. The accepted jump overrides contact; landing is the first tick that samples contact again.
Held aim is intent, effective aim owns creep/spread, and recent fire only owns ready presentation. Animation plays
polished public clips as authored ([sources](animation-sources.md)): walk, jog and sprint blend over actual
speed on one stride clock, and while aiming or after firing the walk and creep step in any direction relative to
the aim (backpedal, sidesteps and diagonals). Running, the legs turn toward travel or run backward under the
twisted torso. Remote clips follow displayed snapshot displacement. Jump poses follow latched takeoff, vertical
velocity and impact-scaled landing. See [locomotion](locomotion.md). The node facade owns collisions, ammo and
lifecycle transitions; the resolver has no scene or engine dependencies.
A light rifle stance layers authored rifle poses (carry, hip ready, shouldered, sprint port, reload and the butt
strike), the aim twist and pitch, and two-bone hand IK over the clips ([rifle stance](motion.md)). Shots, their
obstruction and hits come only from the logical aim, never from the animated pose. Shots climb the logical aim; the
player pulls against the retained recoil. Short rifle and camera springs supply the visible kick. See
[recoil](recoil.md) for the climb and the logical muzzle, and the [recoil report](evidence/recoil.md) for
R19's measurements.
A throw immediately clears weapon poses and cancels reload, so get-up cannot restore a pose from before the throw.

## Tuning

Every gameplay and effect dial is a property of `Tuning`, declared by domain in the parts `src/Core/Tuning.NN.Name.cs`.
The number orders the parts, and with them the panel and inspector groups and the shared snapshot's bytes, on every
machine: a new part takes a free number and a line in `Propane.csproj`, and `Tuning.cs` keeps the machinery and no dials.
Press F1 in game for live sliders. Run from
the editor, **Save** writes them to `res://tuning.tres`, which the game loads on start and which the Godot inspector
can edit. In a downloaded build, Save writes the player's own `tuning.tres` in the game's data folder
(`%APPDATA%\Godot\app_userdata\Propane` on Windows,
`~/Library/Application Support/Godot/app_userdata/Propane` on macOS,
`~/.local/share/godot/app_userdata/Propane` on Linux), which then takes precedence. **Defaults** restores the values
in code. `FixedSeed` (World group) replays one suburb; 0 picks a random one.

Tank piles use weighted formations in both modes: side-laid rows, upright square and triangular tiers, and upright
tight clusters. The **Tank Piles** tuners control weights and spacing. Each upper tier is placed only after its supports;
cramped sites use smaller whole formations. Pyramids stand on clear ground rather than moving pickup beds.
Upright tiers span two collars below, using `PileUprightLayerHeight`; rows retain `PileLayerHeight`.
[Upright pile trials](upright-piles.md) describes the contacts and stability proof.
Rifle hits puncture, then detonate. A blast within `ChainRadius` acts as a shot: an intact neighbor punctures and
keeps venting and moving; an already-venting neighbor detonates after its `ChainFuseMin`/`ChainFuseMax` fuse.

Dense chains use **Explosion full effect budget** (16 overlapping full blasts); additional blasts keep their core
flash and fireball. **Debris pieces per tank** selects 6 of the authored 25 fragments, and **Debris spawn per frame**
limits body creation to 8 per frame. Spawned debris stays until the suburb retires. These dials affect presentation
and fragment physics; punctures, chain fuses, blast pushes and banking happen immediately.

Physics remains at 120 Hz with the usual twelve-step catch-up budget. An offline chain with at least 64 pending
fuses temporarily limits catch-up to four steps, retaining the limit for 0.5 real seconds after pressure drops.
The **Dense chain** tuners control the threshold, steps (0 disables it) and tail. Network worlds veto this limit;
their owned bodies retain ordinary catch-up. Dense offline bursts can briefly slow simulation, but a
low frame rate alone no longer activates a lower budget.
[Tank scale trials](evidence/tank-scaling.md) records the trade-offs, timing and compatibility.

Loose intact tanks gather with nearby resting neighbors after 20 undisturbed seconds. The **Tank Gather** tuners
control radius (3 m), minimum group size (2), delay, a three-stage lift/glide/settle and optional scan shimmer (set
`GatherShimmer` to 0 to disable it). Connected neighbors gather at a clear center; isolated singles, venting tanks,
shielded drops and tanks still in their original or gathered pile stay put. Shots, blast pushes, movement, new members
and player bumps reset the wait or interrupt a gliding tank. Cramped groups wait for a clear site and path.
Rebuilt piles use `PileBuilder`'s upright cluster: every count fits, and removing one tank leaves no suspended tier.

**Tank count multiplier** (World, 1–8) scales suburb tank supply and pile sizes; rebuild the suburb to apply it.
It defaults to 1. A match uses the lobby's **Tank supply** instead of anyone's saved multiplier; the lobby previews
the current roster's count at default tuning (custom debug tuning can change that count). Lab suburb setups
offer the same 1×–8× choice and remember it in their unsaved draft. Void fixtures and Dissolve Run keep their own counts.
The Lab's Huge stack offers 24–512 tanks, starting at 40.

Match settings use the actual player count. Before the multiplier, tank supply is `MpTankCount + MpTanksPerPlayer × players`, ammo cans are
`PickupCount + PickupsPerPlayer × players`, and map width is `MpMapSizeBase + MpMapSizePerPlayer × players`. Pile
limits gain `floor(MpPileSizePerPlayer × players)`. `MpStartSpacing` is a hard minimum between clear road starts;
invalid settings fail generation instead of placing players on top of one another. Lobby capacity is a separate
creator setting, bounded by the tested technical ceiling of 16. King's new messages and shared tuners require the next
release to be minor (`Protocol.Version` is 3).

[World services](world-services.md) documents scoped ownership, cleanup, and simulation/network clock boundaries.

## Layout

| Path | Contents |
| --- | --- |
| `src/Main.cs` | The root: menus over their backdrop, then single player, Lab, a match, or the server. |
| `src/Game.cs` | Single player: the void, player, HUD, menus, and the suburb swap with its dissolve and materialize. |
| `src/Net` | Multiplayer: the server, the client connection, the match world, body sync, lobby contracts, the mode runner and envelope. |
| `src/Modes` | Pure mode rules (Sandbox, Tank Bank, King and Lab prototypes), mode definitions, phases and HUD presenters ([modes](modes.md)). |
| `src/Lab`, `src/Bots` | Shipped Lab scenarios, local bank adapter and camera-free banking brain. |
| `src/Session` | Ordered session assembly, actors, local views and retirement. |
| `src/Core` | Tuning, launch arguments, input map, physics and render layers, shared identity/appearance/body values, assets, events, spawn roots. |
| `src/World` | The void environment, ammo pickups, the model catalog, and the suburb builder. `Plan/` holds the pure-data generator. |
| `src/Tank` | Propane tanks, the vent jet, blasts, debris and breakaway fence panels. |
| `src/Player` | Character controller, camera, animation, the rifle stance, ragdoll and get-up. |
| `src/Diagnostics` | Shared opt-in effect/bot/navigation profiling; process-lifetime counters drained by diagnostic callers. |
| `src/Fx` | Explosions, tracers, impacts, hitstop, particle helpers, and the shader warm-up. |
| `src/UI` | HUD, menus (main, multiplayer, lobby, pause, match, results), match HUD, tuning panel. |
| `src/Navigation` | Reusable bot routes, capsule sweeps, jump links and bounded recovery. |
| `src/Dev`, `scenes/dev` | Test harnesses (below), and the `--release-test` smoke the exported game carries ([code quality](code-quality.md#release-smoke)). |
| `shaders` | World, tank, ground, void and effect shaders, plus the shared reveal transition. |
| `tools/blender` | Scripts that build the assets in `assets/`. |
| `tools/release`, `.github/workflows` | Export templates, the release build, quality checks, and release publishing. |
| `tools/server` | The multiplayer server's Docker setup and deploy script. |
| `tools/dev` | Multiplayer test runs with bot players, and a comparison of what each bot saw. |
| `design` | The owner's standing decisions: the vision the game holds to. |
| `docs` | The contracts, runbooks and evidence ([index](README.md)), this guide, and the README images. |

`ShutdownGuard` must stay first in `project.godot`'s autoload list. Godot frees root children in reverse insertion order;
quit through `ShutdownGuard.Quit(tree, code)`. It destroys the scene, drains managed resource finalizers and waits
for the next physics tick to flush their queued RID frees before quitting. Its final predelete drain runs before
the engine tears down its C# bindings and native servers. Static caches remain alive for Godot's shutdown tracker.
[shutdown.md](shutdown.md) explains the lifecycle, failure mechanism and regression checks.

## Dev harnesses

[Release-reference checks](release-reference.md) freeze wire/schema, world construction, body publication and
seeded Lab behavior before refactors. Unit references run in `make test`; `make test-equivalence` adds local exact traces.

The [animation review harness](runbooks/animation-review.md) compares controller-driven motion and external CC0 clip candidates from both review views, with calibrated metrics and any-two-build media.

Each scene in `scenes/dev` is a scripted test. Arguments after `--` are parsed once by `src/Core/LaunchArgs.cs`: the game
reads its own [launch options](code-quality.md#runtime-launch-options-and-the-dev-boundary) there, and the scenes
read theirs through `src/Dev/DevArgs.cs`. `--capture-dir=<dir>` saves screenshots, `--record=<file.mp4>` films the window in real time (needs ffmpeg on `PATH`,
including `ffmpeg.exe` on Windows; a game in a match stays in step, unlike Godot's `--write-movie`), and
`--tune.<Name>=<value>` overrides any tuning value. A capture that cannot be delivered (a screenshot that cannot be saved,
an ffmpeg that is missing, stalls, dies, exits nonzero or writes no file) fails the run with exit status 1, and no recorder
wait outlasts `--record-timeout=<seconds>` (default 30). `game_test` and `player_range` also accept
`--effects-seed=<integer>` to seed their world random stream before constructing content for paired captures. For example,
`tools/dev/net_bots.sh -n 4 -l 90 -w 1 -- --record=/tmp/bots.mp4` films a match from the first bot.

```sh
GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot

# Shutdown: finalizers for resources discarded during scene destruction must finish before engine teardown.
# Also runs in make test; --exit-code=7 checks that shutdown preserves a requested failure status.
$GODOT --headless res://scenes/dev/shutdown_tests.tscn

# Session actors: two assigned views on one World3D, independent shots/melee, view closure and a camera-free bot.
$GODOT --headless --fixed-fps 60 res://scenes/dev/session_actors_tests.tscn
$GODOT --fixed-fps 60 res://scenes/dev/session_actors_tests.tscn -- --capture-dir=/tmp/session-actors

# Participant capacity: production lobby/results layouts and real keyboard/mouse/gamepad delivery.
# Repeat with 2/8 entries and 1920x1080; make test covers all counts headlessly.
$GODOT --resolution 1280x720 --fixed-fps 60 res://scenes/dev/capacity_ui_tests.tscn -- --players=16 --capture-dir="$PWD/build/capacity-ui"
# Deterministic menu portraits: host/guest, classic/docked, 2/8/16 players, color/bot dialogs and focus.
# Also checks draft/caret/focus preservation across decoded lobby packets; runs headlessly in make test.
$GODOT --resolution 1600x900 --fixed-fps 60 res://scenes/dev/menu_composition_tests.tscn -- --capture-dir="$PWD/build/menu-composition"
# Repeated sixteen-client acceptance and resource measurements: docs/runbooks/network-soak.md.
make test-capacity

# Movement resolver: gait/target speed on each transition tick, lifecycle, jump buffer and coyote time;
# injected gamepad actions and stick look.
$GODOT --headless res://scenes/dev/movement_tests.tscn

# Generator: exact determinism and encoding, authored hull overlaps and tier supports, placement and reachability,
# all four formations, and supply/start safety at 2, 8, 16, 24 and 32 players. Exits 1 on failure.
# make test uses bounded sweeps; make test-full retains the deep seed coverage in four seed-range shards.
$GODOT --headless res://scenes/dev/generator_tests.tscn -- --seeds=200
# --first-seed, --first-large-seed and --first-formation-seed start each sweep's range; --list-seeds lists its seeds.
$GODOT --headless res://scenes/dev/generator_tests.tscn -- --first-seed=51 --seeds=50 --large-seeds=0 --formation-seeds=0

# Bot navigation: real picket/privacy fences, gates, bins, cars, piles, houses and touching-fence recovery.
# Asserts >=95% success, walking through a direct gate, planned fence jumps and tank clearance.
$GODOT --headless --fixed-fps 60 res://scenes/dev/bot_navigation_course.tscn -- --seeds=100
$GODOT --fixed-fps 60 res://scenes/dev/bot_navigation_course.tscn -- --seeds=5 --capture-dir=/tmp/bot-fences
# --baseline compares direct movement plus the old stuck recovery; --first-seed=39 --seeds=1 replays one seed.
# --nav-log traces waypoints/replans. make test runs five seeds; docs/bot-navigation.md describes the component.

# Plain C# assertion suites; make test runs these in a shared runner and checks saved plans in two fresh processes.
$GODOT --headless res://scenes/dev/unit_tests.tscn
tools/dev/check_world.sh

# Scoped worlds: overlapping physics/tuning, events/spawns/callbacks, pause/reveal/hitstop and leak-free teardown.
$GODOT --headless --fixed-fps 60 res://scenes/dev/world_services_tests.tscn
# Native vent correction: fresh observers add no speculative jet impulse; stale/predicted/owned vents keep thrust.
$GODOT --headless --fixed-fps 60 res://scenes/dev/body_correction_tests.tscn
# Native server entry: explicit debug --tune overrides reach lobby admission.
$GODOT --headless res://scenes/dev/server_tuning_tests.tscn -- --server --port=24680 --tune.MaxPlayers=6
# Native debug seed: the transmitted plan must use the requested candidate seed.
$GODOT --headless res://scenes/dev/server_seed_tests.tscn -- --server --port=24680 --match-seed=23
# Native client delivery between physics steps without process frames.
$GODOT --headless res://scenes/dev/transport_physics_tests.tscn -- --server --port=24680
# Clear/replace proof: shooting sequence, finish remaining fixture tanks, assert automatic replacement.
$GODOT --fixed-fps 60 res://scenes/dev/game_test.tscn -- --scenario=clear --quit=32 --capture-dir=/tmp/clear
# Real menu lifetime: ten single-player and ten solo-match retirements with exact node/object/orphan counts.
$GODOT --headless --fixed-fps 60 res://scenes/dev/menu_test.tscn -- --scenario=lifetime --classic-lobby
$GODOT --fixed-fps 60 res://scenes/dev/menu_test.tscn -- --scenario=lifetime --classic-lobby --capture-dir=/tmp/menu-lifetime

# Blast-query completeness: 1,100 rigid bodies and duplicate collision shapes; asserts every body is pushed.
$GODOT --headless --fixed-fps 60 res://scenes/dev/blast_query_tests.tscn

# Platform download URLs and recorder arguments, including Windows paths and non-English locales. Exits 1 on failure.
$GODOT --headless res://scenes/dev/platform_tests.tscn

# Release smoke test: main menu and single player; fails on a missing button, empty suburb or timeout.
$GODOT --resolution 1280x720 res://scenes/dev/release_test.tscn -- --windowed --capture-dir=/tmp/release
# --scenario=network --connect=host:port creates a lobby, waits for a second player, then plays a 30-second match.
# Official export templates disallow scene overrides. The same test runs in an untouched archive with
# ./Propane.x86_64 -- --release-test --capture-dir=/tmp/release (Propane.exe on Windows).
# Add --scenario=network --connect=host:port --name=Tester to test against another client on a source server.

# Custom color with mouse/keyboard: presets, HSV click/drag, exact hex, saved settings, preview and match.
$GODOT res://scenes/dev/menu_test.tscn -- --scenario=mouse --classic-lobby --capture-dir=/tmp/menu-color

# The real game with a scripted player. Scenarios: loop (default), clear (destroys every tank), ragdoll, shoot,
# tour (camera in tight spots), spots (visits each tank), settle (fails on tank drift >6 mm or rotation >0.5 degrees),
# dissolve, menus,
# readme and readme-scout (the README's chain reaction filmed from a free camera, and the search for where to put
# the camera; see README media below).
$GODOT --fixed-fps 60 --resolution 1280x720 res://scenes/dev/game_test.tscn -- --capture-dir=/tmp/g --scenario=ragdoll

# Soak: punctures every tank, then detonates them all at once, and reports frame times. Run without --fixed-fps.
$GODOT res://scenes/dev/soak.tscn

# A dense pile with equal simulation-time measurement windows, including its primed chain.
# Omit --measure-seconds to retain the 600-frame measurement; --measure-warmup adds simulated warm-up time.
DISABLE_GAMESCOPE_WSI=1 $GODOT --disable-vsync --resolution 1920x1080 res://scenes/dev/lab_test.tscn -- --scenario=measure --count=160 --phase=chain --measure-warmup=3 --measure-seconds=4 --metrics="$PWD/build/chain.csv"

# Tank close-ups (scenarios: full, vent, blast, look), the character and rifle rig, the world kit, map overviews.
$GODOT --fixed-fps 60 res://scenes/dev/tank_range.tscn -- --capture-dir=/tmp/t --scenario=blast
# Gathering: separate close groups, isolated single, frozen vent, shielded drop, house/player clearance,
# shot/bump/blast interruption and settled-pile stability. These and the following probes run in make test.
$GODOT --fixed-fps 60 res://scenes/dev/gather_range.tscn -- --capture-dir=/tmp/gather
$GODOT --headless --fixed-fps 60 res://scenes/dev/gather_range.tscn -- --scenario=grant-races
# A real single-player rifle blast knocks intact tanks loose, then they gather; seed 23 fixes the road fixture.
# Once the knock is proven, the fixture clears nearby loose props and blast fragments so departure paths stay clear.
# --gather-trace buffers per-physics-tick member states/positions and nearby bodies, then prints them at the result.
$GODOT --fixed-fps 60 res://scenes/dev/game_test.tscn -- --scenario=gather --tune.FixedSeed=23 --capture-dir=/tmp/gather-game
# Upright pyramids: every physics tick for 120 simulated seconds, player passes, gather neighbors and toppling.
$GODOT --headless --fixed-fps 60 res://scenes/dev/upright_pile_tests.tscn
# --deep --awake adds 64/128-tank contact trials (make test-full).
# Formation close-up and stability: Rows, Square, Triangular, Cluster; --count defaults to 14.
# --hit=shot|blast|shove captures a disturbance and falling from player height.
# --awake tests contact settling; --chain=fresh requires intact neighbors in range to puncture and keep venting.
# --chain=primed first punctures each tank, then requires venting neighbors in range to detonate after the fuse.
$GODOT --fixed-fps 60 res://scenes/dev/tank_range.tscn -- --capture-dir=/tmp/pile --formation=Square --chain=fresh
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --capture-dir=/tmp/p
# Melee: wall occlusion, one hit per swing, and an intact tank/loose prop shoved by the real collision path.
$GODOT --headless --fixed-fps 60 res://scenes/dev/melee_range.tscn
# Four strikes from carry/aim/sprint/reload, with final grip and rotation assertions; side or player view.
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=melee --view=side --assert-grip --assert-motion --capture-dir=/tmp/wpmelee-side
# Deeper cancellation sweep of both reloads (also in make test-full).
$GODOT --headless --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=melee-reloads --assert-grip --assert-motion
# Motion: all gaits, hip/aimed bursts, walking reload and get-up into run, captured at 60 Hz.
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=motion --view=side --assert-motion --assert-grip --capture-dir="$PWD/build/motion"
# Camera-free actor detail: native near/far/hidden pose rates, mesh detail, rifle contacts, owning view and far recovery.
$GODOT --fixed-fps 60 res://scenes/dev/actor_detail_tests.tscn -- --capture-dir="$PWD/build/actor-detail"
# A held distant pose cannot change physical-bone starts during walking, hits, aim, reload or melee.
$GODOT --headless --fixed-fps 60 res://scenes/dev/ragdoll_pose_tests.tscn
# Remote grips stay on the rifle through snapshot relocation/turns, delayed shot echoes, reload, melee and get-up.
$GODOT --headless --fixed-fps 60 res://scenes/dev/rifle_reach_tests.tscn
# Full-auto magazine, persistent aim climb and the raised barrel's direction; aim, hip or creep.
$GODOT --fixed-fps 60 --resolution 1280x720 res://scenes/dev/player_range.tscn -- --scenario=recoil --stance=aim --view=player --assert-grip --assert-motion --capture-dir="$PWD/build/rcl-player"
# Ten magazines against a player capsule with the production bot aim controller.
$GODOT --headless --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=bot-recoil --skill=1 --distance=20
# Real device delivery: held mouse burst, retained climb and mouse pull-down in single and network views.
$GODOT --headless --fixed-fps 60 res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby
# Player scenarios: stances (default), reload, ragdoll, creep (two measured strides), movement (labeled gait/jump transitions),
# sprint-repro (discovery sequence, exact-tick assertions and transition log), grip (all rifle contacts).
# feet (all eight body-relative directions in every gait) and feet-events (local starts/stops/turns/jumps/terrain)
# write final-pose feet.csv and report near-floor drift and ankle separation.
# --assert-feet enforces finite poses, no snaps, jump phases and stepping turns; --feet-corrections exercises snapshot snaps.
# --view=rifleleft or rifleright follows the rifle up close.
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --capture-dir=/tmp/p --scenario=reload --view=rifleleft
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --capture-dir=/tmp/movement --scenario=movement --view=side
$GODOT --headless res://scenes/dev/player_range.tscn -- --scenario=sprint-repro
# Rifle contacts: all holds, both reloads, every gait, full pitch and recoil. Also runs in make test.
$GODOT --headless --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=grip --grip-sweep --assert-grip
# Repeat rendered with --view=rifleleft, rifleright, side and player. Writes per-frame grip.csv.
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=grip --grip-sweep --grip-metrics --view=rifleleft --capture-dir=/tmp/grip
# Compare aimed walk speeds from the side and the player camera (--view=player).
# Captures 12 frames per stride and writes final world-space foot/toe samples to stride.csv.
$GODOT --fixed-fps 60 res://scenes/dev/player_range.tscn -- --scenario=creep --view=side --tune.CreepSpeed=1.6 --capture-dir=/tmp/creep
$GODOT --fixed-fps 60 res://scenes/dev/world_lab.tscn -- --capture-dir=/tmp/w
$GODOT --fixed-fps 60 res://scenes/dev/suburb_preview.tscn -- --capture-dir=/tmp/s
# --players=16 previews a scaled match instead of single player; --formation=Square forces a formation.

# Every menu screen, a solo match, and single player and back (keyboard/mouse).
$GODOT res://scenes/dev/menu_test.tscn -- --capture-dir=/tmp/m
# Return from single player or leave a match, then press the real main-menu Quit button.
# Fails with exit 1 if the Quit button leaves the menu running.
$GODOT --fixed-fps 30 res://scenes/dev/menu_test.tscn -- --quit-after=single --capture-dir=/tmp/quit-single
$GODOT --fixed-fps 30 res://scenes/dev/menu_test.tscn -- --quit-after=match --capture-dir=/tmp/quit-match
# Joypad events only: focus, text defaults, hosting/joining, color/length updates, pause/resume, results and repeat.
# Fails with exit 1 on a missing focus target, incorrect outcome or timeout; also runs headlessly in make test.
# --port=<port> overrides local hosting for parallel runs (make test uses this checkout's port).
$GODOT --fixed-fps 60 res://scenes/dev/menu_test.tscn -- --scenario=gamepad --classic-lobby --capture-dir=/tmp/pad
# Switch devices while paused and verify the reference and reload hints in both directions.
$GODOT res://scenes/dev/menu_test.tscn -- --scenario=devices --classic-lobby --capture-dir=/tmp/devices
# Real keyboard/mouse events through the root viewport in single player and a solo match: turn, jump, melee,
# single-click fire, R/new suburb or reload, and remapped Escape. Assertions also run headlessly in make test.
$GODOT --fixed-fps 60 --resolution 1280x720 res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby --port=24681 --windowed --capture-dir="$PWD/build/input"
# Lobby bots: add, edit, cancel and remove by keyboard, then by controller; a second client sees them and a full lobby
# refuses one. Also runs headlessly in make test.
$GODOT --fixed-fps 60 --resolution 1280x720 res://scenes/dev/menu_test.tscn -- --scenario=bots --classic-lobby --port=24681 --capture-dir="$PWD/build/bots"

# Multiplayer: a local server and bot players under simulated lag and loss; asserts tank agreement and scores.
# Logs go to build/net (-o picks another directory). A directory that still holds *.log files from an earlier run is
# refused rather than emptied: pick a new -o, or pass -f to replace them. Ctrl-C or a failure stops the server, every bot
# and anything they started.
GODOT=$GODOT tools/dev/net_bots.sh -n 3 -l 60 -- --net-lag=120 --net-jitter=30 --net-loss=3
# Add --tank-supply=4 to the bot arguments for 4× lobby supply in either mode.
# Logged engine/protocol errors also fail the run. make test-full includes a 90-second faulted rifle soak.
# -t clean uses tighter tank limits; -k skips post-run checks.
# Rerun a comparison: tools/dev/compare_tanks.py [--profile clean] build/net/bot*.log
```

`net_test.tscn` scenarios: bots playing (default), `melee` (five-tank and zero-bank knockdowns and both get-ups),
`duel` (hits, drops and a thrown player, filmed from both sides),
`pickup` (an ammo can taken and returning), and `movement` (remote creep, sprint, jumping and held-aim landing,
with assertions and labeled frames), `drop-refusal` (rolling back a refused predicted drop), and `puncture-race`
(canonical vent adoption and trajectory assertions after two simultaneous bullet contacts), and `vent-contact`
(eight owner-started, rapidly spinning vents on clear floor, with contact and trajectory logs).
`tools/dev/compare_vents.py` compares `puncture-race` logs; `vent-contact` uses the native assertions and
`net_bots.sh`'s tank and score checks.
[multiplayer.md](multiplayer.md) lists the commands. [Network scenario ownership](net-scenarios.md) describes the harness lifecycle,
completion obligations and full scenario/option inventory.

The creep probe reports median and 75th-percentile horizontal foot/toe drift for adjacent samples within 3 cm of
each bone's minimum height, after two seconds of settling. These are near-floor proxies for planted contacts, not
a contact proof. The headless movement suite also checks actual facade velocity and animation playback at 0.98,
1.6, 2.2 and 2.8 m/s.
[evidence/movement.md](evidence/movement.md) records the speed comparison and the default's trade-off.

The rifle probe measures final wrist position/rotation residuals, sampled skinned hand-surface penetration into
solid rifle proxies, and right-eye distance from `Socket_Eye` while fully aimed. `--assert-grip` exits 1 on
non-finite values, missing scenario coverage, a wrist more than 2 cm off its grip, a hand 1 cm into the rifle, or an
aimed eye more than 15 cm from the sight. These are sanity bounds; rendered views judge the fingers, shoulder pocket
and cheek weld. [evidence/rifle-contact.md](evidence/rifle-contact.md) records the wrist and finger calibration.

## Versions and releases

All tooling downloads are pinned in [tools/downloads/pins.txt](../tools/downloads/pins.txt), grouped by version.
The shared verifier checks fresh downloads and cached archives before extraction or packaging. The CI caches retain
archives; each install recreates extracted tools/templates from verified bytes. A checksum mismatch stops the run:
delete the rejected cache archive and retry; never update a pin just to accept an unexpected download.

When upgrading Godot, update the SDK/project and workflow versions together with **all four** Godot pins (editor,
.NET templates, LICENSE.txt, COPYRIGHT.txt). Obtain the archive hashes from the exact stable release's publisher
`SHA512-SUMS.txt`, download the artifacts and confirm the hashes locally. For the notices, verify that release's
source tarball against the same checksum list, then compare its root LICENSE.txt/COPYRIGHT.txt byte-for-byte with
the raw tagged files packaging downloads and record their SHA512 hashes. Update the provenance URLs and source
archive hash in the pin comments. A version without a matching pin fails closed. Run `make check`, both installers
with fresh caches, and a local packaging run before submitting the upgrade.

Godot's .NET exporter selects `ExportRelease` for `--export-release` and `ExportDebug` for `--export-debug`.
`ExportRelease` excludes all `src/Dev` C# sources; the presets also omit their script resources and `scenes/dev`.
The small exported smoke and its capture, metrics and bot helpers live in `src/ReleaseSmoke`; `--release-test`
keeps working inside an untouched archive. Editor/`make build` Debug, debug exports and local optimized Release
(`make play` and `make bench CONFIG=Release`) retain the harnesses. See the
[Dev boundary](code-quality.md#release-smoke).

After packaging, the release job inventories all three archives and runs the unpacked Linux artifact headlessly
before publishing. Run the same check locally with
`python3 -B tools/release/check_artifacts.py --output build/artifact-proof` (a new directory). The
[artifact runbook](runbooks/release-artifacts.md) describes rendered/network proof, CI limits and the manual
Windows/macOS runtime checklists. Source builds alone do not prove the packaged paths.

Work happens on `dev`; `main` is what has been released. The version is `config/version` in `project.godot`,
MAJOR.MINOR.PATCH, and it decides who plays together:

| Release | For | Plays with |
| --- | --- | --- |
| Patch (0.2.0 → 0.2.1) | Changes local to each player: fixes, menus, HUD, visuals, performance, single player. | Every 0.2.x, so nobody has to update. |
| Minor (0.2.x → 0.3.0) | Anything players in one match share: a message, the suburb built from the server's plan, tanks, the tuners. | Only 0.3.x. Older games are told to update. |
| Major | Kept for big milestones. | Only its own minor version. |

`Protocol.Version` in `src/Net/Protocol.cs` must also match, and is bumped whenever a message changes shape. A
release that changes it has to be at least a minor one. Hello, Welcome and Rejected never change shape (fields may only
be appended), so any build can be told why it was turned away and which version it needs. In a match everyone plays
on the creator's tuning, so changing a baked tuning value is fine in a patch; adding or removing a tuner is not.

To release, on a clean, pushed `dev`:

```sh
make release-patch   # or release-minor, release-major; tools/release/cut.sh X.Y.Z picks the number
```

`tools/release/cut.sh` takes the next version after the latest release tag and checks it with
`tools/release/check_version.sh`: newer than every release, and a patch only when the protocol is unchanged. For a
patch, it lists any changed files that players in a match share (`src/Net`, `src/World`, `src/Tank`, the tuners and
`src/Modes`) and asks before going on; the list is a review prompt, not a proof of compatibility. Then it shows the
changes and runs the whole gate, `make check` (lint, every test partition and the gate self-tests) followed by a
100-seed generator sweep, writing the output to `build/release-gate.log`. Only after that passes does it change anything:
it commits the new version on `dev`, and pushes `dev` and fast-forwards `main` to it in one push. A refusal, a declined
confirmation or a failing gate leaves the version, the branches and the remote as they were.
`python3 -B tools/dev/release_tests.py` proves this against a scratch repository with a local remote and fake tools.

That push runs `.github/workflows/release.yml` on a Linux runner: the same version check, the headless tests, all three
exports, and a new GitHub release with the changes since the previous one. Releases are never replaced.

A downloaded game checks GitHub for a newer release at launch and, if there is one, offers this platform's archive
on the main menu; updating is up to the player. A newer server that turns a game away offers the download it needs.

The same build runs locally:

```sh
tools/release/install_templates.sh 4.7.2   # .NET templates for all three platforms; macOS is Apple Silicon only
RCODESIGN=/path/to/rcodesign tools/release/build.sh   # writes build/dist/*.zip and *.tar.gz
```

Packaging owns only `build/package` (exports, notices, logs, the folders that become the archives) and `build/dist`,
and replaces both on every run; other output under `build/` is left alone.

Godot's built-in ad-hoc signing writes entitlement data that current macOS rejects (the app is killed at launch),
so the macOS preset exports unsigned and [rcodesign](https://github.com/indygreg/apple-platform-rs) signs the app ad hoc.
It runs on Linux, which keeps CI off the more expensive macOS runners. Windows exports on the same runner, with
Godot applying `icon.svg` and the current project version to the executable; it needs no signing or resource tools.
The release and development shell tools require a Unix environment (Linux or macOS); downloaded games do not.

For a signer upgrade, change `APPLE_CODESIGN_VERSION` in the release workflow and its matching archive pin together.
Read the exact apple-codesign release's publisher `.tar.gz.sha256` file (or a stronger published checksum if offered),
verify the downloaded archive locally, and record the checksum source URL beside the pin. The release installer
uses only the checked-in expected value, including for cache hits; it never fetches an expected hash at run time.
Validate the workflow and run `make test-infra` after changing it.


## Assets

The tank, its debris and the fireball flipbook come from the propane-tank Blender project, and the rifle from the
rifle Blender project; the other models are CC0 kits (see [CREDITS.md](../CREDITS.md)). The scripts in `tools/blender`
rebuild them with Blender 5.1 (`import_rifle.py` needs only Python):

| Script | Builds |
| --- | --- |
| `export_tank.py` | `assets/tank/tank.glb` and `debris.glb` (with convex hulls) from the tank project's Unreal export |
| `build_character.py` | `assets/character/mannequin.glb`: the Quaternius mannequin with animations from both libraries |
| `import_rifle.py` | `assets/rifle`: the rifle (one skinned mesh, its clips and sockets), a lone magazine for dropped magazines, and the textures both share, from the rifle project's export |
| `build_props.py` | `assets/props/*.glb`: fences, grill, bins, mailbox, lamp, hydrant, patio heater and more |

Each script's header gives its exact command line.

## README media

`docs/readme_still.jpg` (the hero, 1920×1080) and `docs/readme_chain.gif` (640×360, 25 fps, about 5 s) are renders of
the current game, made with the `readme` and `readme-scout` scenarios of `scenes/dev/game_test.tscn`
(`src/Dev/GameTest.Readme.cs`) and `tools/dev/readme_gif.py`. Remake them when the look of the game changes. They need
a rendered Godot (see [Dev harnesses](#dev-harnesses)), ffmpeg, ImageMagick (`magick`) and Python with Pillow.

The scene is seed 11 at 3× tank supply, which gives a 15-tank pile beside a house and a car at the edge of the slab. The
player stands 5.5 m from the pile, shoots one tank (it vents), shoots a second, and a blast chains through the pile. A free
camera across the street films it with the HUD hidden: `--cam=back,side,height,ahead,fov[,look]` places it relative to
the player (meters behind, to the right and up; how far ahead it looks; vertical field of view; height of the look
point), and `--angle` and `--distance` place the player around the pile. `--effects-seed` fixes the debris and effect
random stream. A rerun differs in small ways (the ragdoll's pose, by about a frame), so pick the still by eye.

`readme-scout` finds camera poses for a pile: it tries random stand angles and camera poses, keeps the ones that see both
the pile and the player, saves a still of each (`c000_…png`) and prints the `--angle` and `--cam` values that make it.
The poses used here were adjusted by hand from its candidates.

```sh
GODOT=godot-mono   # or the path of your Godot .NET binary, as in Dev harnesses
SCENE=(--tune.FixedSeed=11 --tune.TankCountMultiplier=3 --tune.HitstopDuration=0 --effects-seed=1 --pile=0
       --distance=5.5 --angle=316 --cam=2.75,7,1.5,2.0,40,1.1 --hud=0)

# Candidate camera poses (optional): 40 stills in build/readme/scout.
$GODOT --fixed-fps 60 --resolution 1280x720 res://scenes/dev/game_test.tscn -- --scenario=readme-scout \
  --tune.FixedSeed=11 --tune.TankCountMultiplier=3 --pile=0 --distance=7 --fov=55 --count=40 --capture-dir="$PWD/build/readme/scout"

# The still: 30 frames at 60 Hz from 4.5 s, in 2560x1440.
$GODOT --fixed-fps 60 --resolution 2560x1440 res://scenes/dev/game_test.tscn -- --scenario=readme "${SCENE[@]}" \
  --shot-start=4.5 --shot-fps=60 --frames=30 --quit=5.2 --capture-dir="$PWD/build/readme/hero"
# Frame 15 has the player in the air and the pile just burst. The crop drops most of the road and a thin effect line
# along the top edge.
magick build/readme/hero/frame_0015.png -crop 2400x1350+80+30 +repage -resize 1920x1080 \
  -sampling-factor 4:2:0 -interlace Plane -quality 90 docs/readme_still.jpg

# The GIF: 135 frames at 25 per second from 2.5 s (the first shot) to 7.9 s (the smoke). A 50 Hz step keeps them two physics ticks apart.
$GODOT --fixed-fps 50 --resolution 1920x1080 res://scenes/dev/game_test.tscn -- --scenario=readme "${SCENE[@]}" \
  --shot-start=2.5 --shot-fps=25 --frames=135 --quit=8.2 --capture-dir="$PWD/build/readme/gif"
python3 tools/dev/readme_gif.py build/readme/gif docs/readme_chain.gif \
  --first 4 --last 134 --crop 24,27,1872,1053 --gate 8 --colors 128 --width 640
```

`--tune.HitstopDuration=0` matters. A blast near the player starts a hitstop (time scale 0.05), and under `--fixed-fps` the
capture director divides each fixed step by that scale, so its clock jumps and a run of queued screenshots fires in one
frame and saves the same picture many times. With hitstop off every frame is distinct.

`readme_gif.py` crops and scales the frames, keeps the previous pixel wherever a frame changed by less than `--gate` levels
(this is what lets a still camera cost almost nothing), fades the last frames to white to soften the cut back to the start,
and has ffmpeg build one palette for the clip. The first frame is never faded: GitHub shows it until the GIF has loaded,
and keeps showing it to viewers with animation autoplay off. The tool exits nonzero over 4 MB: GitHub loads large README
images slowly or not at all (a 5.5 MB GIF once failed to appear), while 3 MB loads reliably.
Look at the frames before committing: no HUD or dev text, the player and the pile in frame, no repeated or skipped
pictures. The still is 175 KB and the GIF 3.1 MB.
