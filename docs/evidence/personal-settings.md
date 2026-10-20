# Personal settings (ST)

2026-10-20. Base: `4a468ce` (v0.5.0 development). Linux, Godot 4.7.2 .NET, Debug unless stated otherwise.

Status: implemented on `wp/settings`; current contracts are [architecture](../architecture.md) and
[personal preferences](../development.md#personal-preferences). This is dated verification, not native
Windows/macOS acceptance or a rendered performance claim.

Commits: `ad5ed5e` model/store, `156fcc3` menu/device/camera integration, `41b7e16` behavioral proof,
and `c4bcf2d` exact scenario matching in the gate self-tests.

## Implementation and ownership

`PreferenceCatalog` declares twelve preferences once, as immutable public static readonly fields. Its read-only registry
is discovered once in metadata order; adding a field plus its target's apply hook adds persistence, validation,
menu editing and category reset. No second application store/file exists. `PlayerSettings` retains Main's application
lifetime and existing identity/server keys, migrates the leaderboard's cached bool into its declaration/cache, and
keeps `hud/leaderboard` intact. New values use `preferences`. Unknown keys are not interpreted or overwritten.
Missing/wrong-type/nonfinite values default; finite numbers clamp and snap. Each edit saves before synchronous
notifications, including category reset. Save failure remains live but is reported by the editor. Immediate writes
match the existing store and require no debounce timer or pending write to survive a menu/session transition.

`SettingsMenu` lives with its caller and borrows the store. It retains only category, focus/control visibility and
save/display status. Native controls, slider resources and event handlers retire with it; its viewport-size hook
detaches on exit. Main owns `DisplayPreferences` until application exit; camera and match shortcut subscriptions
retire with their view/menu. No prohibited module edge was added, and the dependency exception baseline stays empty.
The process-state inventory adds the immutable declaration table; source-site inventory names the new Dev-only
monotonic readiness timeout and optional diagnostic Stopwatch window. Engine `--disable-vsync` is read only by the
display binding to retain the existing developer override at startup.

| Preference | Application |
| --- | --- |
| Mouse sensitivity / invert Y | DeviceInput transforms physical screen-pixel displacement once. |
| Relative aiming sensitivity | Multiplies mouse and stick gain while Aim is held. |
| Gamepad sensitivity / invert Y | DeviceInput inverts Y and applies gain after the existing radial curve/deadzone; non-unit gain produces degrees/second integrated once by logical aim with physics delta, before its single pitch clamp. |
| FOV | Local CameraRig: base FOV × preference/72, clamped to 30–110°. The synchronous hook works while paused. |
| Shake strength | Scales rendered eye rotation/translation only. Logical targeting, recoil and shared shake remain unchanged. |
| Window / VSync / frame cap / render scale | DisplayServer mode, DisplayServer VSync, Engine.MaxFps and the presentation viewport's Scaling3DScale. Deferred swapchain fallbacks are explained in the menu. |
| Leaderboard | Existing HUD read and match shortcut share the same declaration/store. The shortcut text updates after settings edits. |

No tuners were removed or reclassified. MouseSensitivity, StickLookDegreesPerSecond, FieldOfView and shake remain
shared base tuning; personal settings never mutate their snapshots. Neutral/scripted input retains its former units
and default calculation path. Replicated aim stays in its existing units. No world generation, traversal, RNG draw,
combat/scoring rule, message shape or Protocol.Version change occurred. This is a client-only patch-compatible change
within 0.5.x. New input velocity is an in-process intent field, not a wire field.

## Commands and results

All scratch/captures/logs are under the worktree's ignored `build/`; TMPDIR is already `build/tmp` in the environment.
Startup used `git switch wp/settings`, then `make setup`. Repeated `make build` and final setup were warning-free;
final setup's strict post-compile import was clean. New scripts include their generated `.uid` files.

- `godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=PersonalSettings`: all four functional
  groups passed (the fifth registered group is the opt-in benchmark). Every declaration round-trips and resets;
  tests cover missing/old files, wrong types, NaN/infinity, bounds, unknown keys, category isolation, failed writes,
  synchronous apply ordering, disposal, device-only scaling and unchanged shared tuner snapshots.
- `godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby --port=24681`:
  170 assertions, zero failures in the direct run; also retained in the complete gate.
- `godot-mono --fixed-fps 60 --resolution 1600x900 --disable-vsync --path . res://scenes/dev/menu_test.tscn -- --scenario=settings --classic-lobby --port=24681 --capture-dir="$PWD/build/st-final-1600"`:
  119 checks passed. Repeated at `1280x720`, capture directory `build/st-final-1280`: 119 passed.
- After the writer exited, `godot-mono --headless --path . res://scenes/dev/menu_test.tscn -- --scenario=settings-read`:
  a separate process confirmed mouse=1.5, ADS=0.5, both Y inversions on, gamepad=1.5 and FOV=90. The isolated
  `menu_settings_test.cfg` avoids other menu scenarios' fixture files. Both processes run sequentially in `make test`.
- `dotnet format Propane.sln --no-restore`, `git diff --check`, `python3 -B tools/dev/check_links.py` and
  `python3 -B tools/architecture/source_check.py`: passed. Full gate result and soak follow below. Markdown links and whitespace were checked again after staging
  the final evidence links.

The settings scenario uses Input.ParseInputEvent, not action/intent injection. It exercises every field by D-pad,
left-stick navigation/editing, keyboard Tab/arrows, actual mouse clicks, every display choice/cap, category reset,
main-menu reopen, single-player and Lab pause, hosted match, shortcut synchronization, F11 persistence and device
input suppression. Returning closes only the editor; its caller remains paused/open. Multiplayer keeps running.
Live mouse motion `(10,5)` yields yaw −1.8908°, pitch +0.9454° with 1.5× gain/inversion; aiming halves it. Full stick
with 1.5× gain/inversion yields +27° over 0.100 simulation seconds. A nonzero logical shake fixture proves zero
rendered shake and restoration; targeting is unchanged.

## Final complete gate and faulted soak

`make check` exited **0** on final production and harness code (`build/st-check-complete.log`): warning-free
Debug and ExportRelease builds, formatting/UID checks, 758 unit tests with zero failures, all behavior suites,
bounded generator sweeps, identical separate-process builds for Rows/Square/Triangular/Cluster, five matching
runs of twelve segment plans, and the gate self-tests (including each new scene's independent failure propagation).
Architecture has zero reviewed dependency exceptions, 104 inventoried statics and zero failures; 102 source-site
entries pass. The existing animation gate reports 34/46 visual checks and 12 pending redo, an inherited acceptance
limit outside this work. `make test-full` and packaged exports were not run.

Headless menu scenarios in the complete gate all passed: gamepad 134 assertions, devices 22, input 170, mouse 32,
bots 112, King 114, lobby 56, settings 112, and the separate-process settings reader. Rendered settings runs have
119 checks because native display assertions run too. The earlier standalone nine-scenario regression is retained
in `build/st-menu-regression.log` and `build/st-regression-*.log` (before the final combined-input correction);
the complete gate reruns all nine on the final code.

```sh
tools/dev/net_bots.sh -n 4 -l 90 -p 24681 -s '--match-seed=23' -o "$PWD/build/st-net-final-fault4-90" -- --net-lag=120 --net-jitter=30 --net-loss=3 --net-seed=11
```

Exit **0**. Four clients completed the faulted 90-second match and agreed with the server's audit (30 banked).
Tank comparison: 1,999 grids, 102,869 body pairs, zero unobserved pair grids and zero disagreements. Rest/moving
peak error 0.136/6.740 m; final 0.000/0.000 m; 966 above-tolerance episodes, longest 0.60 s, all within the unchanged
fault profile. A header scan for `ERROR:`, `SCRIPT ERROR:` and `Unhandled exception` found **zero engine errors in
each of all five logs**. Summary: `build/st-net-final-summary.log`; process logs: `build/st-net-final-fault4-90/`.

The earlier pre-correction soak also passed (140,628 body pairs, zero disagreements and engine errors):
`build/st-net-fault4-90-summary.log`, `build/st-net-fault4-90/`. It remains an earlier sample rather than final-code proof.

## Assertion and failed-run history

Temporary production mutations were restored before final verification. Removing the paused-camera apply hook
made the settings scenario exit 1 at its live-FOV assertion (69 checks reached). Omitting mouse Y inversion made
both the unit suite (one failure) and real-device scenario exit 1; the latter reached 83 checks and measured the
wrong −0.9454° pitch sign. Logs: `build/st-fault-fov.log`, `build/st-fault-invert.log`,
`build/st-fault-invert-unit.log`.

Exploratory failures remain failures: an initial test compile used the wrong RecursiveEqual overload; a unit run
started against the older assembly found no new suite. The first controller fixture incorrectly tried to increase
a slider already at its maximum; it now chooses a direction with room and restores the value. An early reader
was incorrectly launched concurrently with the writer's fixture reset; final reads are strictly sequential. Initial
link validation required tracking the new catalog file. A strict VSync equality check exposed Gamescope forcing
VSync on; an initial notice check then exposed the deferred swapchain update. Production now observes that deferred
fallback and the test requires the visible explanation. The final rendered runs pass rather than claiming VSync-off
support on this display. Gamescope/PipeWire/xkb startup messages and expected Vulkan VSync fallback warnings are
not game engine errors; the final soak's engine-error count is recorded separately.

The first complete `make check` invocation failed in the legacy gamepad browser test: its two unscoped Single
queries also found the new hidden settings scroll container. Both now require the single **visible** browser list;
scroll position and focus-outline enclosure assertions remain unchanged. Its next standalone run then reached an
old held-stick sequence expecting Quit immediately after Lab. The sequence now asserts the added Settings row,
then Quit, then that neutral stops repeating, retaining and extending the original coverage. The failed full invocation is retained in
`build/st-check.log`; the complete corrected gate is recorded in the final-gate section.

The second complete gate (`build/st-check-final.log`) was deliberately stopped during the unit phase after source
review found that personalized stick velocity and mouse displacement could clamp pitch separately at the limit.
LogicalAim now combines them before its existing single Rotate/clamp; a unit regression proves opposing inputs at
45°. A deliberate split-clamp mutation then made the new unit assertion exit 1 (`build/st-fault-clamp.log`).
The real-device scenario also now combines opposing mouse/stick events at the limit in single player and the match.
Defaults and scripted units remain unchanged. Final proof in this record was repeated after this production correction.

Before the final gate reached its self-tests, inspection found their substring scene matcher would count
`settings-read` as a second `settings` invocation. It now requires an argument boundary; the same self-test still
requires exactly one invocation of each scene and checks both injected failure paths.

## Controlled callback cost

`godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=PersonalSettings --settings-bench --match-seed=2053938876`
passed all five registered groups. Three alternating neutral/preferences-enabled pairs, one seed (2053938876),
2 actors, 1 owned actor, 56 registered bodies, 1 camera, 20,000 warm-up and 60,000 measured callbacks per case.
Each callback includes Match physics, owned-character physics and CameraRig presentation. Manual 120 Hz deltas;
Stopwatch samples wall time. Other own rendered/gate/soak runs were stopped during this window.

| Pair | Neutral mean / p95 / p99 µs | Enabled mean / p95 / p99 µs | Neutral / enabled allocated bytes |
| --- | --- | --- | --- |
| 0 (neutral first) | 40.972 / 49.900 / 60.600 | 53.056 / 59.900 / 69.600 | 126845760 / 126845760 |
| 1 (enabled first) | 59.872 / 67.200 / 77.300 | 42.978 / 52.100 / 62.800 | 126845760 / 126845760 |
| 2 (neutral first) | 67.800 / 67.800 / 87.600 | 81.688 / 71.100 / 238.900 | 126845760 / 126846048 |

Allocation difference is 0–288 bytes over 60,000 callbacks; the final small discrepancy is not attributed to a cause.
Timing varies materially in both directions; the last enabled p99 is worse (238.9 vs 87.6 µs). No timing neutrality or improvement is established. This compares enabled/neutral paths in the same candidate binary,
not parent/candidate binaries. Native physics/render frames do not advance between these explicit callbacks;
these data do not establish populated match frame time, GPU cost or a performance improvement. Final log: `build/st-cost-final.log`. The earlier pre-correction probe remains in `build/st-cost.log`; it is not final-code proof.

## Visual review

Reviewed all twelve main/single/Lab/match category shots at 1600×900 and all six main/match category shots at
1280×720, plus the explicit VSync fallback. Text/values fit, focus rings have room, knobs have visible contrast,
and controls do not overlap. The one-row Interface category is intentionally sparse, with the same panel/footer
positions as the other categories. No unresolved clipping or layout defect was seen. Native hand feel is still
for the owner's review; synthetic device events do not prove physical controller hardware or platform-specific
presentation on Windows/macOS.

Each directory contains `main_camera.png`, `main_display.png`, `main_interface.png`, `single_camera.png`,
`single_display.png`, `single_interface.png`, `lab_camera.png`, `lab_display.png`, `lab_interface.png`,
`match_camera.png`, `match_display.png`, `match_interface.png`, plus changed-camera/live-camera and VSync-fallback shots.
Final directories: `build/st-final-1600`, `build/st-final-1280`.

## Limits and follow-ups

Gamescope forced VSync on; disabling it on a supporting native display and true exclusive presentation remain
unverified here. Windows and Apple Silicon macOS execution is unverified. Key rebinding is deliberately deferred:
roughly 3–5 engineering days for bindings, conflicts, glyphs, persistence and real-device regression, plus native
platform validation. Audio preferences await audio work. The existing base tuners remain available in F1; FOV's
relative-to-base choice and reduced rendered shake retaining logical targeting are explicit review decisions.
No owner decision is required to run the current implementation.
