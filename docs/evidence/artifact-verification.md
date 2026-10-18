**Status:** R1 complete for setup, packaged inventory, Linux/Proton smoke and local delivery tooling. Native Windows,
macOS runtime/signing, hosted release execution and remote Docker deployment remain unverified; retain the
[platform checklist](../runbooks/release-artifacts.md).

# R1 artifact verification — 2026-10-18

Branch `wp/rf-r1-artifact-proof`, immediate parent `620b817`. Final exports were built from committed code
`a55abb0`; subsequent verifier-only fixes do not change game artifacts, and the final evidence commit changes
documentation only. Linux x86_64/CachyOS, GTX 1660,
Godot 4.7.2 Mono (`ed1daf0bf`), .NET SDK 10.0.113. Export templates identify themselves as the official same-version
Mono engine. No release workflow, public push/tag or remote deployment was executed.

## Changes and boundaries

- `cfaeec7`: setup retains diagnostics, permits only the observed precompile ShutdownGuard bootstrap diagnostic,
  and requires a clean import after compiling.
- `47c8bce`: smoke observes received final standings before match replacement and proves returned lobby input.
- `599b831`: optimized Debug's isolated view retains `Propane.csproj`; real local Release and compiled optimized
  Debug checks protect supported source probes.
- `e5b6a1d`: the existing architecture reader inventories unpacked assemblies/PCKs; a shared local/release script
  runs Linux artifacts before publication. Unsigned macOS verification is explicit and marked against publication.
- `a55abb0`: deployment requires a clean matching local release tag in local main's history, inventories its export
  before SSH, and writes the selected tag/commit to `game/release-id.txt`.
- `f5cb24f`: artifact verification reaps children on interruption as well as timeout.
- `2b9d2de`: every archive requires its native entry; cancellation proof drives the actual CLI with disposable archives.

No production state moved, and no gameplay, tuners, wire bytes, RNG draws, traversal, callbacks or scene processing
order changed. No dependency/source-site baseline entries were added or removed. The only game C# edit is in
ReleaseSmoke. Its new position sample lives with that smoke node; stage deadlines use existing process delta.
The inventory pack owns/disposes its file stream, including failed construction; artifact child processes are
bounded and terminated/reaped on timeout, Ctrl+C and SIGTERM. Harness and delivery tooling landed separately.
Supporting changes to
`bench_tests.py`, `full_engine*` and `check_world_tests.py` preserve the existing assertions; no other package's
production files changed.

## Setup and negative proof

`make setup` passed with cold `.godot/` and again cached. For the cold proof only this worktree's cache was moved
aside, then its retired copies were removed after comparison. Logs: `build/r1-fresh-setup.log`,
`r1-fresh-import.log`, `r1-fresh-import-compiled.log`, `r1-cached-setup-final.log`.

The first import exits **0** while reporting exactly:

```text
ERROR: Failed to create an autoload, script 'res://src/Core/ShutdownGuard.cs' is not compiling.
```

There is no Debug assembly yet. Compilation succeeds with zero warnings; the following strict import is clean.
The exception is unavailable when that assembly exists, unavailable in strict mode, and never permits nonzero exit.

`make -C build/r1-broken-project setup` with a corrupt PNG failed (make exit 2) although the importer exited 0.
The error output names `res://broken.png`, `ERR_FILE_CORRUPT`, image loading and import context. The scratch copy
holds the resource; the source checkout is clean. `build/r1-injected-import-png.log` preserves the proof.
An earlier malformed SVG generated no Godot error and therefore was **not** accepted as negative proof; that
temporary source resource was removed before final exports.

`make check` passed completely after the final verifier changes (`build/r1-check-final-head.log`), including all
`make test` partitions and `make test-infra`, with zero compiler warnings. The earlier complete run is retained in
`build/r1-check-complete.log`. Its deliberate screenshot-error and architecture-negative fixtures
still print their expected errors. Infrastructure proof includes:

- `tools/dev/setup_import_tests.py`: exact bootstrap, existing assembly, unexpected/additional diagnostics and
  nonzero child exits (2 tests).
- `tools/architecture/tests.py`: existing 51 compiled/16 source negatives plus artifact Dev type, missing smoke
  type, missing method, Dev source/scene resources, missing main resource/remap target, wrong entry setting and
  truncated pack. Shipping inventory succeeds without a PDB; ordinary dependency checks still require symbols.
- `tools/release/artifact_tests.py`: runtime error/nonzero exit/missing success/timeout propagation, archive layout,
  unsafe tar paths, missing/empty native entries, real CLI child retirement on SIGINT/SIGTERM, and release-only ordering (5 tests).
- `tools/server/deploy_tests.py`: dirty/unreleased/off-main tag refusal, successful identity/inventory ordering,
  and failed inventory stopping before SSH (5 tests, private Git repositories and stub SSH only).
- `tools/dev/release_tests.py`: import/export failures and retained output boundaries, including the default
  signer requirement and explicit unsigned marker (17 tests).

The old deployment error-tail fixture initially stopped at the new identity preflight because its fake project
omitted project settings and its Git stub printed text for `status`. Its fixture now supplies a valid release
identity; its original earlier-error/backtrace/no-SSH assertions remain and pass.

## Source configurations and equivalence

All commands below passed:

```sh
make build
make bench CONFIG=Release BOTS=0 TANKS=1 RENDERED=0 SECONDS=.5 WARMUP=.2 REPEATS=1 BENCH_ARGS=--output=build/r1-bench-release.json
python3 -B tools/dev/source_run.py --config Release res://scenes/dev/platform_tests.tscn --headless
python3 -B tools/dev/full_engine.py --prepare --godot godot-mono
python3 -B tools/dev/full_engine.py --headless --fixed-fps 60 --path . res://scenes/dev/core_tests.tscn
make test-equivalence
python3 -B tools/dev/check_equivalence.py --baseline "$PWD/build/r1-parent-view" --output build/r1-parent-equivalence
dotnet format tools/architecture/Architecture.csproj --no-restore --verify-no-changes
dotnet build tools/architecture/Architecture.csproj -nologo -v quiet -warnaserror
```

Local Release reports Release/optimized and restores Debug; the source CLI platform probe passes 9 checks.
The first optimized Debug core run exposed the missing compile metadata: 513/514 assertions passed and
`TuningPartsTests` could not read `Propane.csproj`. After the read-only link fix, **514 assertions in 49 unit suites**
and all **3 core suites** pass. Logs preserve both runs (`r1-optimized-debug-core.log`, `...-core-2.log`).

Frozen wire/schema/world/publication references and all five Lab traces pass. Parent/candidate Bank, King,
spill-chain, Short Fuse and Dissolve agree exactly. The parent runtime view used the untouched Debug assembly saved
by the initial setup at `620b817`; its checksum and detailed traces remain under `build/`. The cache/view copies
were removed after proof. No references were regenerated. These are correctness checks, not native performance
acceptance; no hot path changed. `make test-full` was not rerun.

## Produced artifacts

```sh
tools/release/build.sh godot-mono --unsigned-macos
python3 -B tools/release/check_artifacts.py --output build/r1-complete-artifacts-headless
python3 -B tools/release/check_artifacts.py --rendered --output build/r1-complete-artifacts-rendered
```

All three packaged exports pass inventory: **1,013 metadata types, 493 PCK resources, zero failures each**.
No Dev types/resources ship; the reviewed smoke closure, entry methods, native executables, remapped main scene and autoload settings
are present. All **193 Linux export files** compare byte-identically with the unpacked Linux archive.
Both artifact scripts pass ordinary startup, classic flag startup, server binding and main-menu/single-player smoke
(40 tanks) outside the source project. Logs/captures remain in their named output directories.

Archive SHA-256 (local verification builds, not published releases):

| Archive suffix | SHA-256 |
| --- | --- |
| `linux-x86_64.tar.gz` | `a51bdf8f4ea316af46a7b391ca42906dd2faf0b3d4d7343b7ef062eeec4d27a5` |
| `macos-arm64.zip` | `6a125c682b1713c4fcdedca5eee95b32e9e2592aca6c0ec93ee8e4b9292e4727` |
| `windows-x86_64.zip` | `35e26eb53a7d51ee8842b4d0c8780bee46ea14ac6bf1af0496a88a8228cddf2a` |

`rcodesign` is unavailable locally; macOS was exported with the explicit unsigned option and `UNSIGNED.txt`.
The release workflow retains its default signer requirement. Signing and macOS execution are not proven here.

## Native paths and the roaming defect

The supplied D1 and pre-D1 roaming logs show the new lobby world and a stage-3 timeout without the smoke's final-score
message. `Main.EnterLobbyWorld` legitimately retires the previous match; its local results timer need not win the
race against the server's new world. The smoke incorrectly treated that timer's `Finished` callback as a necessary
completion fact. The corrected smoke requires received final standings, Waiting state and the real returned world.

Final Linux `--release-test --scenario=network --length=10` runs pass against an exported headless server with a
second source `net_test` peer, both default roaming and `--classic-lobby`. In roaming, parsed Tab/Escape events
open/close the panel and W moves the returned player **2.72 m**. Logs/captures:
`build/r1-final-network-roaming/` and `build/r1-final-network-classic/`. An initial added assertion tried Tab to close
the open panel; it failed because Tab navigates UI focus. It was corrected to the existing documented Escape path.
No production navigation, timer or gameplay change was made.

The exported server also passes:

```sh
tools/dev/net_bots.sh -n 2 -l 20 -S 127.0.0.1:24682 -k -t clean -o "$PWD/build/r1-final-export-server-bots/clients" -- --match-seed=23
python3 -B tools/dev/check_scores.py build/r1-final-export-server-bots/server.log build/r1-final-export-server-bots/clients/bot1.log build/r1-final-export-server-bots/clients/bot2.log
python3 -B tools/dev/compare_tanks.py --profile clean build/r1-final-export-server-bots/clients/bot1.log build/r1-final-export-server-bots/clients/bot2.log
```

Both bots agree with the server's 18-tank conservation audit. **600 grids, 10,012 body pairs, zero disagreements**;
final rest/moving residual **0.000/0.071 m**, longest above-tolerance episode 0.10 s. No engine/protocol errors were
found in final network logs. Scratch launch mistakes (wrong peer arguments/environment, launching during a cold
source import, and reusing a bot log directory) are retained separately and are not passing proof.

Rendered `menu_test --scenario=input --classic-lobby --port=24682` passes **165 real device-event assertions**;
headless also passes in the full gate. Reviewed `build/r1-menu-input/` before/after-turn, jump and Lab captures show
changed camera heading, airborne movement, populated worlds and readable UI. Reviewed final artifact Linux
`01_main_menu.png`/`02_single_player.png`, roaming `03_cross_platform_match.png`/`05_returned_to_lobby.png` and
classic `05_returned_to_lobby.png`: menu, populated suburb/match and correct return surface are visible.

The final Windows `Propane.exe -- --release-test` passes under the existing Steam Proton Experimental installation,
inside the invisible wrapper, with prefix/cache/config under `build/`. Reviewed `build/r1-windows-final/` captures
show the menu and a 40-tank suburb. The menu heading uses a different system-font fallback from Linux; native
Windows fonts/layout still need checklist review. Expected compositor/VSync/SDR warnings are not compiler warnings
or a performance measurement. Native Windows input/firewall/first-launch behavior is not established by Proton.

## Delivery limits and compatibility

The workflow parses with PyYAML BaseLoader; its main trigger and inventory-before-publication order were checked.
`bash -n` passes all six workflow shell blocks and both changed shell scripts. The actual artifact script ran locally;
hosted execution, GitHub expression validation and publication were not exercised. CI deliberately uses headless
display/audio; it proves loading/construction/physics/menu flow, not pixels, shader rendering, hardware input,
audio or network exchange. The runbook records the runner/Xvfb/software-rendering rationale and platform checklist.

An actual `tools/server/deploy.sh example.invalid unused godot-mono` invocation on this unreleased branch refuses
with exit 1 before export or SSH. Private fixtures cover accepted identity and failure ordering. Remote SSH/Docker
was not run. Deployment trusts local release tags/main history and does not assert GitHub's latest release identity.
The PCK reader intentionally pins 4.7.2/v4; an engine upgrade must update and reprove it.

This is patch-safe tooling/harness work: messages, suburb construction, tanks and tuners are unchanged;
`Protocol.Version` remains **3**. Publication policy and platform targets are unchanged.
