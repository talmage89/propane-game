# Architecture gate

This plain .NET 10 console tool inspects built assemblies with System.Reflection.Metadata; it never loads Godot or
executes the inspected code. It is excluded from game compilation (`tools/**`); `.gdignore` keeps its files out of Godot imports and exports. Its build output, fixtures and
scratch are under `build/architecture/` or temporary directories under `build/`.

`make lint` rebuilds Debug and ExportRelease, checks dependencies and process-state inventories in both assemblies, then
checks source sites. The exact same steps run in CI's lint job. `make test-infra` runs `tests.py` through `tools/dev/lint_tests.py`, without Godot or imports;
it compiles an independent fixture assembly with stub Godot types, requires each prohibited edge independently to
fail, and verifies source negatives, stale approvals and the exact smoke entry. Existing make-order probes require
all three gate commands to run, stop on each failure and precede game tests.

## Policy

`inventory assembly.dll game.pck tools/architecture` uses the same metadata reader and module map without requiring
shipping symbols. `artifact.json` pins the complete authored smoke closure (nested/compiler-generated types inherit
reviewed owners), entry methods and resources. Dev types/resources and unreviewed smoke owners fail. The standalone,
unencrypted PCK v4 reader follows Godot 4.7.2's `PackedSourcePCK::try_open_pack`, bounds-checks its directory and
resource spans, and requires the main scene's remapped target and packed entry settings. Unsupported formats/flags
fail closed. The [artifact script](../release/check_artifacts.py) invokes this mode on the three unpacked release
archives; runtime smoke complements inventory because metadata presence cannot prove execution or rendering.

- `modules.json` maps namespace prefixes (longest first), files and exact top-level types (highest precedence) to
  logical modules; it also lists allowed direct directions. Generated/nested types inherit their top-level module.
  Keep mixed namespaces explicit. Unknown Propane types/namespaces fail closed. The map describes logical ownership,
  so native `World.AmmoPickup` is Simulation and the separate plain `Player.PlayerNetState` is Foundation. Front-end screens are Composition, HUD/device views
  Presentation. Small value contracts can live in a namespace whose default is another module.
- `baseline.tsv` is ordinally sorted, one line per `rule TAB source-type TAB target-type-or-member TAB removing-package
  TAB reason`. It approves individual type edges, never whole module pairs. New forbidden edges and stale entries
  both fail. Keys normalize compiler-generated/nested owners to the outer type; diagnostics retain the actual type.
  Duplicate keys, unsorted lines, missing reasons and placeholder `REVIEW` owners fail.
- `source-sites.tsv` is sorted `rule TAB file TAB token TAB count TAB owner/domain TAB reason`. It permits today's
  exact counts per source/file/token, including harness/diagnostic domains; an additional use, removal, or new file
  fails. Global-write owners are ProcessPhysicsPolicies, DissolveArena and ShutdownGuard, with named isolated Dev
  probes. New simulation timers must use the world clock or a reviewed explicit domain. A diagnostic approval is
  never approval to use wall time for gameplay. Runtime-to-Dev source references have no allowlist.

- `process-state.tsv` is sorted `rule TAB declaring-type TAB member TAB count TAB owner TAB reason/lifetime`.
  It inventories production (including exported Smoke) `LaunchArgs.Current` getter references by top-level owner and
  exact compiled count: 11 groups, 31 reads. Compiler duplication of a constructor initializer counts twice; async/
  lambda bodies belong to the same authored owner. New owners, added/removed reads and stale groups fail.
  It also inventories 107 inventoried static declarations: 41 non-readonly fields (including event storage), 5 static
  properties with any setter visibility, and 61 readonly collection/table fields (including get-only auto-property
  storage). Constants are excluded; fields behind settable auto-properties count once under the property name.
  Arrays of any rank (including System.Array), array segments/memory, BCL/Godot collections, read-only collection
  interfaces/wrappers, ConditionalWeakTable and authored collection subclasses require approval. Compiler-generated closure/delegate cache types and Dev declarations are
  excluded. Each approval identifies an owner and reason/lifetime; this inventory records existing ownership, not
  permission for arbitrary mutations. Counts, stale entries, duplicates and placeholder owners fail independently of
  the dependency baseline. No runtime reads or static storage changed to create the inventory.

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
dotnet run --project tools/architecture/Architecture.csproj --no-launch-profile -- state-candidate \
  .godot/mono/temp/bin/Debug/Propane.dll tools/architecture > build/architecture-state-candidate.tsv
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
runtime-to-Dev lexical check covers that particular erased-source boundary. The Dev-only MessageCodec facade is
absent from ExportRelease; runtime callers fail both that source rule and compiled runtime-to-Dev checks.

Process-state discovery covers the inspected assembly, including types outside the Propane namespace; Dev and
compiler caches are excluded as above. It uses compiled getter references and metadata declarations, not source
spelling. It cannot
see reflection/dynamic reads, inactive preprocessor branches or a new ambient facade's indirect callers. Per-owner
counts cannot distinguish replacement/reordering of approved reads, and do not prove the same option or sampling time.
The mutable-static check is structural, not deep mutability analysis: arbitrary readonly object graphs, collection
contents inside immutable/frozen containers, and unrecognized external collection subclasses are outside its
scope. It follows authored subclass bases/interfaces in the inspected assembly; generic arguments alone do not make
a container mutable. The BCL/Godot collection namespace classifier is conservative (including interfaces, wrappers
and other types in those namespaces), even when the current backing is immutable. Only compiler-generated cache types with unspeakable C# names and CompilerGeneratedAttribute (and their
nested types) are excluded; an attribute on an authored type does not exempt its state. New instance state reachable
through an approved static requires review; the inventory cannot prove retirement or main-thread-only use. Worker closure tracing does not resolve
virtual/interface/delegate dispatch, generic member-reference call bodies or reflection, and can overapproximate
multiple delegates created by a worker-starting method. Source token counts do not prove an approved clock still has
the same semantic use. These limits require review; they are not permission to bypass a boundary.

Shipping inventory and execution of produced archives remain the separate R1 artifact obligation. Checking the
ExportRelease source assembly proves neither package contents nor Windows/macOS runtime behavior. Frozen equivalence
references, scene/device tests and native network/performance evidence remain independent oracles.
