# Code quality

The C# formatting rules in `.editorconfig` describe the existing code: four spaces, Allman braces, file-scoped
namespaces, `var` locals and System usings first. Inline statements and switch-case blocks keep their existing
layout. Long expressions are not reflowed. Expression bodies and naming remain editor suggestions rather than
build warnings. Run `dotnet format Propane.sln` to apply the baseline.

`make lint` verifies formatting, then performs a full build with warnings as errors. A previous incremental build
cannot hide warnings. `make check` runs lint before the headless regression tests, including with `make -j`. It stops on a
lint failure. Both tasks appear in `make`'s help listing.

`Propane.csproj` pins analysis to .NET 10's default rules, with the recommended Reliability, Usage and Security
sets. Nullability is enabled. CA1001 additionally checks ownership of disposable fields; CA2007 requires explicit
async context capture. `UpdateCheck` keeps Godot's main-thread context when it updates menu nodes.

CA2000 (local disposal) and CA2213 (field disposal) are outside this baseline: they treat Godot's scene-tree and
reference-counted lifetimes as ordinary CLR disposal. Excluding Godot-derived classes also hides CLR leaks inside
those classes, so there are no such exclusions. These rules need an engine-aware ownership policy before they
can be useful here. Disposable resources in new code still need an explicit lifetime.
The CA2213 trial also flagged `CaptureDirector`'s Process and BlockingCollection; their disposal and shutdown lifetimes remain
a separate audit item rather than being changed as part of the gate.

Seven class-specific CA1001 suppressions preserve current lifetimes, with the reason beside each declaration:

- `Bot` and `SimpleBot`: the random generator is Godot RefCounted.
- `MeshKit`: SurfaceTool is Godot RefCounted.
- `CharacterAnimator`: the model root owns and frees the AnimationTree node.
- `NetTransport`: Close releases the connection while the RefCounted peer stays reusable for reconnects.
- `NetWriter` and `NetReader`: their streams contain managed byte arrays, without file or socket handles.

`python3 tools/dev/lint_tests.py` proves the gate rejects nullable dereferences, disposable-field ownership,
implicit async context, ValueTask misuse, and whitespace and style drift. It also checks corrected code passes.
The fixtures compile in an isolated temporary project; they never run or modify game sources.
The script also stubs the external tools to verify `make -j4 check` runs lint before every regression suite
(score checker, unit, movement, 100-seed generator, platform and separate-process world builds) and stops at either
lint failure. The probe supplies world hashes so this ordering check does not run the real game.
It checks that Godot setup reuses a valid cached editor and stops on a download failure.

`.github/workflows/ci.yml` runs asset import, `make lint` and `make test` on every push to `dev` and every pull
request. Superseded runs for the same ref are canceled. CI and release share the .NET 10/Godot 4.7.2 .NET setup;
CI caches only the editor, while release keeps its existing cache of the editor, templates and signing tools.
Both workflows stop and show the error if asset import fails.
