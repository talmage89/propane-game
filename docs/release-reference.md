# Release-reference checks

These fixtures freeze v0.4.0 (`629bf19`) before the architecture refactors. They compare candidates with a saved
release, alongside the existing round trips and same-checkout determinism checks. Tests never update expected results.

`make test` discovers the reference unit suites in `src/Dev/Equivalence`:

- Wire: the existing message inventory in each supported direction, all Bank/King/lobby envelope kinds and backward
  compatible handshake suffixes. Encoders must reproduce saved bytes. Decoders read the saved bytes and must reproduce
  independently saved field values, including exact floating-point bits. The fixture inventory is checked too.
- Tuning: every public scalar tuner's default, CLR type, declaration/reflection order, attributes and arguments,
  Godot property-list/UI order and hints, and membership/order in the shared snapshot. Local brain and Lab dials are included.
- World: seed 23 with four actors and each forced formation. Generation must reproduce each saved plan; construction
  of the saved release plan must reproduce its geometry/collision hash. Sixty segment-plan hashes retain the existing
  runs 1–5, indices 11–0 inventory. The older two-process world checks remain unchanged.
- Publication: the actual private `Match.SendBodies` path, invoked synchronously, with fixed mixed tank/car states,
  manual network time, an injected loopback transport and a packet recorder. It includes batching, encounter/lane order,
  checkpoints, clock-offset sampling, sleeping gather motion, final rests, pending drop confirmation, ownership claims,
  epoch/sequence reset and disposable/required send failures. There are no sockets or physics steps in this test.
- Integrity: an explicit inventory and SHA256 manifest reject missing, modified or truncated fixtures, including Lab
  references. This is accidental-corruption detection; editing both a fixture and its manifest is a reviewable change.

Run just these assertions after `make build`:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ReferenceTests
```

`make test-equivalence` additionally runs local seeded Lab traces. Their scenarios and exact arguments are in
`tools/dev/check_equivalence.py`: the unchanged Bank and King idle workloads, the scripted Bank spill/chain rules,
Short Fuse's scripted death/round replacement, and controlled Dissolve Run retirement. The last fixture builds a
real Lab's initial segments, then records the half-floor, dissolved, retiring and freed boundaries for two segments,
without advancing physics or a worker. It invokes the existing arena retirement path and drains native retirement
with an unlimited test budget; it does not freeze ordinary wall-time slicing or streaming scheduling. Physical-event
generators use the existing world-local seeded factory. Hitstop is zero only in these fixture invocations. Inputs are observed from
`ActorRuntime.Intent` after physics; the observer never reads an input source or random generator.

Each physics tick (or explicit retirement boundary) records actor intent, resolved state, transform/velocity/aim,
ammo/life, scores, ordered mode commands,
and stable fixture-indexed tank lifecycle/transforms. Five-second windows retain SHA256 digests by named observation
field plus tick counts and a final summary. Each reference also records its effective seed and every local/shared
tuner value. No raw traces or captures are committed. Instrumentation is opt-in:
ordinary sessions have no observer node or per-tick trace work. The only production seams are null delegates on
`LabSession`, invoked when runners track a tank or apply a command. They allocate nothing when off.

For repeated checks, or to retain exact fields for diagnosis:

```sh
python3 -B tools/dev/check_equivalence.py --repeat=5 --output="$PWD/build/reference-alone"
python3 -B tools/dev/check_equivalence.py --only=bank --detail --output="$PWD/build/reference-detail"
```

A frozen comparison identifies the first differing window and named field. For the first exact tick/scalar value,
run two checkouts containing the **same observation code**, built independently:

```sh
python3 -B tools/dev/check_equivalence.py --only=bank --baseline=/path/to/instrumented-release \
  --output="$PWD/build/reference-pair"
python3 -B tools/dev/check_equivalence.py --compare-details \
  "$PWD/build/reference-pair/run-1/baseline/bank.detail.jsonl.gz" \
  "$PWD/build/reference-pair/run-1/bank.detail.jsonl.gz"
```

Diagnostic files go in the candidate's output directory; Godot also uses each checkout's ordinary ignored/user state. Build and import each
checkout first. The tool prints its evidence directory and adds a numeric suffix when the requested directory already exists,
preserving earlier failures. Diagnostics retain engine logs,
window data and optional compressed per-tick observations in `build/`. Do not use trace runs as performance samples.

## Deliberate refresh

Refresh only for an approved behavior change, after reviewing it. Check out the intended production revision,
apply the same observation code, `make setup`, and commit the instrumentation before freezing. The single command is:

```sh
python3 -B tools/dev/check_equivalence.py --refresh --production-revision="$(git rev-parse HEAD)" \
  --output="$PWD/build/reference-refresh"
```

This explicitly writes the references and manifest. Review their decoded contents and provenance before committing;
never run refresh to make a refactor pass. `provenance.json` records production and instrumentation revisions,
Godot/.NET versions, configuration, seeds, tuner sources/overrides and commands, with checkout paths removed.
Compressed JSON can be inspected with Python's `gzip` and `json` modules. Do not edit compressed files by hand.

[Recorded v0.4.0 verification](release-reference-verification.md) includes repeated-load results, timings, rejection
probes, reviewed captures and native smoke checks.

## Limits

World hashes include construction, meshes and collision, but do not prove feel. These exact Linux/Jolt traces do not
establish cross-platform or cross-CPU bit identity, so they are a local target, outside `make test` and CI.
The same holds for world generation and construction, which use the C library's trigonometry: on the CI runner's
glibc one prop yaw lands one ulp away. So the ordinary unit suite (and CI) checks only the platform-independent parts
of the world references (formation inventory, codec round trips of the frozen plans, segment seeds); the exact plans,
segment hashes and construction hashes run in `make test-equivalence` (its unit pass adds `--platform-references`).
The original 45-second streamed Dissolve bot workload was tested separately and **excluded** from exact traces:
repeated unmodified release runs produced different catch times, total ticks, transforms and scores. Incremental
streaming depends on worker completion and 2.5 ms build slices, so completion steps are sensitive to wall time. Its failed comparisons remain evidence, not passes.
A release pilot had 8,619 observed ticks; five separate repeats had 8,641, 8,645, 8,621, 8,477 and 8,579.
Their first differing window was 15–20 seconds (commands, tank lifecycle or transforms). All scene assertions still
passed. This supports excluding the streamed workload, rather than broadening a comparison tolerance. The controlled
retirement fixture covers lifecycle boundaries, not the full run's navigation or slice timing. Native
network delivery and worker completion are not assumed deterministic. Publication transcripts prove controlled
send behavior, not native convergence. Real-device input checks, rendered scene review and statistical soaks remain
necessary; idle percentages alone do not prove bot equivalence.
