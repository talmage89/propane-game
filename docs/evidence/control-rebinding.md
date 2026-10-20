# Control rebinding (KB)

2026-10-20. Base `910ada8`, branch `wp/rebinding`. Linux, Godot 4.7.2 .NET, Debug unless stated otherwise.
Production: `4545d6b`; regression proof: `cdb450a`.
Current contracts: [architecture](../architecture.md) and [personal preferences](../development.md#personal-preferences).

## Design and ownership

Eleven Controls rows expose twenty editable slots: thirteen physical keyboard/mouse slots and seven gamepad
slots. Movement sticks, mouse/right-stick look, pause, menu navigation and fullscreen remain fixed. Developer
bindings are excluded. Reload/new suburb share one contextual row, preserving their existing alias. Melee keeps
its existing three keyboard/mouse alternatives; other actions have one slot per editable device. Additional
secondary slots, wheel bindings and modifier chords are not implemented. Short thumb labels retain full tooltips.

A conflict leaves only the matching slot unbound and names that slot in a visible warning. Per-action reset uses
the same rule; category reset restores every default. Capture consumes actual device events before GUI with navigation suspended,
ignores repeats/sticks, and accepts keyboard positions, left/right/middle/thumb mouse buttons, gamepad buttons and
positive triggers. Esc (physical or logical), B, Start and Cancel binding cancel; ten unscaled render seconds expire
without changing the slot. Reserved inputs cannot enter the saved map. Menu navigation has its own fixed delivery.

`ControlBindings` owns immutable Foundation defaults/validation. `PlayerSettings` retains the application-owned
ConfigFile, adding a store-lived dictionary cleared on disposal. Only differences go into `bindings`, including
explicit unbound victims. Missing, unknown, wrong-type, wrong-device, reserved, sentinel and malformed entries use
defaults. Duplicate corrupt claims fall back as a group, including collisions exposed by restoring a default.
Existing identity/server/preferences/leaderboard data stays in the same file. Saves precede synchronous apply
notifications; failed saves retain live edits and the existing visible save-error behavior.

Main registers saved bindings before attaching input consumers and detaches its apply hook on exit. SessionFactory
also registers the borrowed application selection, so entering/replacing a world cannot reset the map. InputSetup
owns the derived application map revision and managed label cache. SettingsMenu owns capture/focus controls until
its calling menu retires; suspended navigation clears held repeat state. ControlsReference, MatchHud, LobbyCard
and LabOverlay follow the applied map/device. HUD/Lab strings refresh on revision/device changes, avoiding repeated
binding formatting in their steady updates. No DeviceInput polling, intent representation or simulation code changes.

No preexisting mutable state moved, no owner/dependency retired, and no dependency exception was added. The
inventory has 107 statics (41 fields, 5 settable properties, 61 readonly collections/tables), 31 launch reads in 11
groups, and 103 source sites. The new source clock sites are test-only monotonic readiness deadlines. The only
new product timer consumes unscaled render delta, independent of WorldClock/network time. No gameplay tuner,
world/RNG traversal, physics callback ordering, combat/scoring rule or message changes. Protocol.Version remains 3;
this is a client-only, patch-compatible 0.5.x change. No frozen fixture was regenerated.

## Verification

Startup: existing wp/rebinding selected, make setup passed, repeated make build warning-free. New scripts have UIDs.
Focused ControlBinding suite: six groups, zero failures (round trip, sparse saves, reset, old/invalid files, conflicts,
corrupt duplicates, physical map/alias/sticks, held input). Main menu real-input failures caught SessionFactory's
unconditional default registration before it was fixed. An early fixture reused ConfigFile.Load without Clear
(the API merges), and another expected keyboard labels immediately after a gamepad event; both fixture errors
were corrected. A held-input test now creates distinct press/release objects, removing its initial native warning.
Pre-stage lint required tracking the new source/UID pair before its documentation link could resolve.

Rendered runs: 1600x900 and 1280x720; final reviewed set build/kb-controls-reviewed contains controls_normal.png,
controls_waiting.png, controls_conflict.png, controls_bottom.png, match_controls_reference.png. The final normal
shot distinguishes Thumb 1/2; waiting shows the action, countdown and cancel controls; conflict identifies melee
keyboard/mouse slot 1 as unbound. The bottom shot shows lobby/fixed shortcuts; the pause reference shows K for
Jump and Middle click for Fire after live edits. Initial alignment shots clipped thumb labels; final labels fix it.
Rendered bindings: 39 checks. Separate process bindings-read: 20 checks, including actual new/old device behavior
inside a network match. Existing input: 170 assertions. Settings: 112 headless checks.


Commands (all scratch/logs below are ignored `build/` output):

```sh
make setup
make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ControlBinding
godot-mono --fixed-fps 60 --resolution 1280x720 --disable-vsync --path . res://scenes/dev/menu_test.tscn -- --scenario=bindings --classic-lobby --match-seed=23 --port=24681 --windowed --capture-dir="$PWD/build/kb-controls-reviewed"
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=bindings-read --classic-lobby --match-seed=23 --port=24681
make lint
make check
make test-equivalence
tools/dev/net_bots.sh -n 4 -l 90 -p 24681 -s '--match-seed=23' -o "$PWD/build/kb-net-fault4-90" -- --net-lag=120 --net-jitter=30 --net-loss=3 --net-seed=11
```

Earlier rendered iterations used the same bindings command at 1600x900 into `build/kb-controls` and
`build/kb-controls-final`, and at 1280x720 into `build/kb-controls-720`. Headless bindings iterations used the same
scenario/options without capture/window arguments. Direct runs and the gate both exercised the separate reader
sequentially after the writer. No player settings file was used: the two scenarios share `menu_bindings_test.cfg`.

The initial complete `make check` passed (`build/kb-scratch/check.log`): 764 unit tests, zero failures; input 170
assertions; settings 112 checks; bindings 39; bindings-read 20. Generator bounds/forced formations passed, all four
separate-process world formations matched, and five twelve-segment plan runs agreed. Gate self-tests passed 55
compiled negatives and 17 source negatives. The animation gate retains its inherited 34/46 checks and 12 pending
redo; this work does not claim to complete animation acceptance.

The second complete `make check` also exited 0 (`build/kb-scratch/check-final.log`), on restored production with
the final Special/Unknown key-sentinel validation and cascading duplicate regression. It retained all counts above,
warning-free Debug/ExportRelease builds, zero architecture failures and all gate self-tests. Final link validation
passed all 88 tracked Markdown files; `git diff --check` and the source backup comparison passed.

`make test-equivalence` passed its six checker tests, frozen wire/schema/world/publication fixtures and all five
Lab traces: Bank, King, spill-chain, Short Fuse and Dissolve. Evidence: `build/rfp0-equivalence-22` and
`build/kb-scratch/equivalence.log`. Frozen files are unchanged.

The faulted four-bot run exited 0. All four clients agreed with the server's 27 banked tanks: 36 initial map tanks,
48 drops, 57 spills. Body comparison passed 1,999 grids / 90,688 body pairs, zero unobserved pair grids, zero
disagreements. Rest/moving peak error 0.215/3.583 m; final 0.001/0.139 m; 966 above-tolerance episodes, longest
1.15 s, within the existing fault profile. An anchored header scan (`ERROR:`, `SCRIPT ERROR:`, plus unhandled
exceptions) found zero engine errors in each of `server.log`, `bot1.log`, `bot2.log`, `bot3.log`, `bot4.log`.
Logs: `build/kb-net-fault4-90/`; audit summary: `build/kb-scratch/soak.log`. No own scene/build ran concurrently.

## Deliberate assertion proof

After the unmodified native runs, `python3 -B build/kb-scratch/prove_faults.py` backed up InputSetup under build,
changed only its event conversion, rebuilt for each mutation and ran the real-event `bindings-read` scenario:

| Temporary production mutation | Required failure (exit 1) |
| --- | --- |
| Convert stored J to physical Space | Check 6: old keyboard input does not jump in match |
| Convert stored middle mouse to left mouse | Check 10: old mouse input does not fire in match |
| Convert stored gamepad Y to A | Check 8: old gamepad input does not jump in match |

The script required each expected message on the actual FAIL line, not a PASS elsewhere in the log. All three
failed as required. It restored InputSetup byte for byte in a finally block and rebuilt; `cmp` against its backup
also passed. Logs: `build/kb-scratch/fault-proof.log`, `fault-keyboard.log`, `fault-mouse.log`, `fault-gamepad.log`.
The focused six-group unit suite also passed on the final sentinel/cascade code before the mutations. The final
full gate runs on restored production. No diagnostic flag or fault injection remains in runtime or test source.

## Limits and follow-ups

No native Windows/macOS or physical keyboard/mouse/gamepad hardware acceptance was performed. Device proof uses
Input.ParseInputEvent through the real application endpoint, including rendered Linux runs; it does not establish
OS/driver delivery on those platforms. No packaged exports, deep test-full sweep or native performance comparison
was run. There is no performance claim or inherited performance obligation retirement in this menu feature.
No blocking design question remains. Extra secondary slots, wheel bindings, modifier chords and per-local-user
maps can be considered separately. AppearancePicker and design/ were not edited.
