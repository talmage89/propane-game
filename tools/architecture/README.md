# Architecture gate

This plain .NET 10 console tool inspects built assemblies with System.Reflection.Metadata; it never loads Godot or
executes the inspected code. It is excluded from game compilation (`tools/**`); `.gdignore` keeps its files out of Godot imports and exports. Its build output, fixtures and
scratch are under `build/architecture/` or temporary directories under `build/`.

`make lint` rebuilds Debug and ExportRelease, checks both assemblies, then checks source sites. The exact same steps
run in CI's lint job. `make test-infra` runs `tests.py` through `tools/dev/lint_tests.py`, without Godot or imports;
it compiles an independent fixture assembly with stub Godot types, requires each prohibited edge independently to
fail, and verifies source negatives, stale approvals and the exact smoke entry. Existing make-order probes require
all three gate commands to run, stop on each failure and precede game tests.

## Policy

- `modules.json` maps namespace prefixes (longest first), files and exact top-level types (highest precedence) to
  logical modules; it also lists allowed direct directions. Generated/nested types inherit their top-level module.
  Keep mixed namespaces explicit. Unknown Propane types/namespaces fail closed. The map describes logical ownership,
  so `Net.AmmoPickup` is Simulation even before its file moves. Front-end screens are Composition, HUD/device views
  Presentation. Small value contracts can live in a namespace whose default is another module.
- `baseline.tsv` is ordinally sorted, one line per `rule TAB source-type TAB target-type-or-member TAB removing-package
  TAB reason`. It approves individual type edges, never whole module pairs. New forbidden edges and stale entries
  both fail. Keys normalize compiler-generated/nested owners to the outer type; diagnostics retain the actual type.
  Duplicate keys, unsorted lines, missing reasons and placeholder `REVIEW` owners fail.
- `source-sites.tsv` is sorted `rule TAB file TAB token TAB count TAB owner/domain TAB reason`. It permits today's
  exact counts per source/file/token, including harness/diagnostic domains; an additional use, removal, or new file
  fails. Global owners are WorldTimeScale, TankSimulation, DissolveArena and ShutdownGuard, with named isolated Dev
  probes. New simulation timers must use the world clock or a reviewed explicit domain. A diagnostic approval is
  never approval to use wall time for gameplay. Runtime-to-Dev source references have no allowlist.

Dependency rules enforce the declared module directions plus explicit Rules/Plans native purity, implicit rule/plan-clock
member use (including engine/platform access and elapsed Stopwatch reads), simulation-to-client/codec/HUD boundaries, bot-to-Match/NetClient/live-rule boundaries, transport helper
independence from NetTransport, server-to-physical actor/view/HUD boundaries, runtime/Smoke-to-Dev exclusion and the
single exact Main-to-ReleaseTest smoke entry. Named compiled global-owner checks also cover setters (including object initializers); source-site counts prevent additional writes even inside an approved owner. Task.Delay, blocking OS delays and explicit Stopwatch/BCL timer construction require a named timer owner too, including target-typed `new()` that has no type name at its source call site. Native Godot inheritance is read from the adjacent GodotSharp assembly,
which is required for a game assembly; Godot value types stay valid. Member checks distinguish clocks from harmless
`Environment.CurrentManagedThreadId` generated for iterators.

Worker checking follows local method-definition calls from delegates created by Task.Run/Task.Factory.StartNew/Thread/ThreadPool starting
methods (including local type initializers and PDB-linked async/iterator bodies) and rejects native object/resource references in that closure. The single named TestRunner._Ready probe
intentionally tests rejected off-main calls; its worker entry count is separately pinned. This is conservative static
reachability, not thread execution analysis. Source checks flag new worker boundaries for review.

Source checks mask comments/literals, retain interpolated expressions and resolve simple type aliases. They recognize
engine scale/catch-up assignments, pause writes, shader-global mutations, direct quit, Task/SceneTree/thread timers,
wall clocks and worker starts. Existing D1 source checks and ExportRelease compilation remain in place: unused `using`
imports and excluded Dev constants cannot disappear silently through compilation.

## Review a shrinking baseline

Build first. Generate **candidate files under build/**, never into the policy paths:

```sh
mkdir -p build
dotnet run --project tools/architecture/Architecture.csproj --no-launch-profile -- candidate \
  .godot/mono/temp/bin/Debug/Propane.dll tools/architecture > build/architecture-candidate.tsv
python3 -B tools/architecture/source_check.py candidate > build/architecture-source-candidate.tsv
```

Compare the candidate keys with the checked baseline; retain the reasons/owners for surviving entries and remove
resolved ones in the same commit as the extraction. Every candidate entry says `REVIEW`, which cannot pass the gate.
Inspect differences and explain any proposed new exception to the lead; adding one is an explicit architecture
review, never an automatic gate repair. After a source move, update module/source-site mappings and release-impact
paths, then rerun Debug, ExportRelease and checker fixtures. The checker never writes policy files.

Run independently after building:

```sh
dotnet run --project tools/architecture/Architecture.csproj --no-launch-profile -- check \
  .godot/mono/temp/bin/Debug/Propane.dll tools/architecture
python3 -B tools/architecture/source_check.py
python3 -B tools/architecture/tests.py
```

## Scope and limits

The reader visits bases, interfaces, generic constraints/arguments, fields/properties/events, method/parameter/return
signatures, local signatures, exception types, attributes' constructor references, method specifications and every IL
type/member/method token (including qualified/aliased calls and nested/generated methods). Portable PDBs supply source
locations. For declarations without sequence points, PDB documents locate the declaration text for diagnostics/file
mapping only; source text never establishes a dependency edge. Missing assembly/PDB/required Godot metadata fails.

The graph is a type-edge policy, not a call graph proving behavior. Within one approved type edge, a new call still
requires ownership review. Reflection, dynamic dispatch, type names serialized inside attribute blobs, erased const
references, inactive preprocessor branches and strings/resource paths are not general metadata edges. The strict
runtime-to-Dev lexical check covers that particular erased-source boundary. Worker closure tracing does not resolve
virtual/interface/delegate dispatch, generic member-reference call bodies or reflection, and can overapproximate
multiple delegates created by a worker-starting method. Source token counts do not prove an approved clock still has
the same semantic use. These limits require review; they are not permission to bypass a boundary.

Shipping inventory and execution of produced archives remain the separate R1 artifact obligation. Checking the
ExportRelease source assembly proves neither package contents nor Windows/macOS runtime behavior. Frozen equivalence
references, scene/device tests and native network/performance evidence remain independent oracles.
