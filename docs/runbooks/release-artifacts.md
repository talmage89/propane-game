# Verify release artifacts

Run from a prepared checkout. These commands build and test; they never publish:

```sh
tools/release/build.sh godot-mono
python3 -B tools/release/check_artifacts.py --output build/artifact-proof
# On the development machine's invisible godot-mono wrapper, also review the rendered screenshots:
python3 -B tools/release/check_artifacts.py --rendered --output build/artifact-rendered
```

Use a new output directory each time. `--dist` selects another directory containing exactly one archive per target;
`--inventory-only` checks all three archives without executing Linux. Extraction and logs stay in the output directory.
The script builds the architecture tool, inventories the actual unpacked assemblies/PCKs, then runs the unpacked Linux
executable from outside the source project: ordinary menu startup, `--classic-lobby`, headless `--server` and
`--release-test`. It rejects engine errors, nonzero exits, timeouts and a missing smoke success marker. Its inventory
requires the reviewed smoke types, normal/server/smoke entry methods, packed main scene and autoload resources and
their entry settings; Dev types and `src/Dev`/`scenes/dev` resources fail.
Child processes are terminated and reaped on timeout, Ctrl+C or SIGTERM; earlier evidence is retained.

The release workflow runs this script after packaging and before publication, only in the release job. Its default is
headless: Godot's dummy display/audio drivers need neither a GPU nor X11, and the smoke's screenshots are optional.
This executes menus, C#/.NET loading, native scene/resource construction, single-player suburb generation and physics,
plus orderly shutdown; the startup probes cover server binding and the classic flag. It does **not** validate pixels,
shaders, real hardware input, audio, multiplayer exchange, macOS execution or native Windows execution.

The [Ubuntu runner image](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md)
includes Xvfb. Xvfb supplies a display, not a Vulkan device. Software OpenGL changes this Forward+ project's renderer;
a software Vulkan smoke would need an explicitly provisioned/tested ICD and its own time budget. Neither is assumed
by this gate. The [Godot command-line contract](https://docs.godotengine.org/en/latest/tutorials/editor/command_line_tutorial.html)
supports headless exported games. Rendered Forward+ verification remains a local GPU obligation.

If rcodesign is unavailable, `tools/release/build.sh godot-mono --unsigned-macos` makes a verification-only macOS ZIP,
with `UNSIGNED.txt` and an explicit log message. Never publish that ZIP. The release workflow does not use this option;
its default still requires ad-hoc signing. Inventory is not signature/notarization proof.

## Network smoke

Start the unpacked Linux artifact with `--headless -- --server --port=PORT`. A second client can use
`godot-mono --headless --path . res://scenes/dev/net_test.tscn -- --role=join --server=127.0.0.1 --port=PORT`.
Run the exported client with `-- --release-test --scenario=network --connect=127.0.0.1:PORT --length=10` and a
worktree-local absolute `--capture-dir`. Locally use the invisible wrapper with `PROPANE_REAL_GODOT` set to the
exported executable; unset that variable for the source peer. Repeat with `--classic-lobby` on server and smoke client.
Always stop and reap the server and peer. For independent bot audits use `tools/dev/net_bots.sh -S HOST:PORT -k`
with a new `-o`, then run the score/tank checkers separately with the exported server's log.

The roaming smoke observes received final standings, not the local results timer's `Finished` callback: the server's
new lobby world may replace the match before that timer fires. Success additionally requires a live returned world,
Waiting lobby, hidden menus, Tab opening and Escape closing the actual panel, and W moving the returned player through
`Input.ParseInputEvent`. It never changes production timers or gameplay to make this race disappear.

## Windows runtime checklist

- Record the tag/commit, ZIP digest, OS/Proton version and logs. Keep every extracted file together.
- Run `Propane.exe -- --release-test --capture-dir=ABSOLUTE_PATH`; review menu and populated-suburb screenshots.
- On Linux, use Steam's existing Proton Experimental installation with a prefix/cache/config under `build/` and
  the invisible `godot-mono` wrapper. Record this as **Proton**, not native Windows acceptance.
- Check ordinary startup, single player, Lab, mouse look, jump, reload, melee, gamepad and focus/menu transitions.
- Play a roaming network match with a Linux peer; inspect results and return controls. Repeat classic lobby and
  headless server mode. Check protocol errors, standings, tank audits and clean process exit.
- Before a release claims native Windows coverage, repeat on Windows with Vulkan drivers, including firewall and
  first-launch behavior. Proton does not prove those operating-system paths.

## macOS (Apple Silicon) runtime checklist

- Record the tag/commit, ZIP digest, macOS version and hardware. Inventory the packaged assembly/PCK first.
- Use the normal ad-hoc signed archive; verify `codesign --verify --deep --strict Propane.app`. An unsigned local
  verification ZIP cannot establish launch/signing behavior. Check first-launch quarantine/Privacy & Security flow.
- Run `Propane.app/Contents/MacOS/Propane -- --release-test --capture-dir=ABSOLUTE_PATH`; inspect both screenshots.
- Check ordinary startup, single player, Lab, device delivery, gamepad, focus/fullscreen and audio.
- Play roaming and classic matches with Linux/Windows peers, through results and return controls; verify standings,
  tank audits, disconnects and repeated matches. Run the app binary headless with `-- --server` too.
- Check orderly quit and re-entry without orphaned processes. Attach logs and captures to the release review.

These platform checklists remain manual until automated execution covers them. Linux exports and inventory alone
do not complete either checklist; macOS cannot execute on the Linux development host.
