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

Run these assertions after `make build`:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ReferenceTests
```

The references were captured from v0.4.0 on Linux with Godot 4.7.2 and .NET SDK 10.0.112, normal Debug. World
settings use the default tuners, seed 23 and four actors; segment settings use defaults, runs 1–5 and indices 11–0.
Wire inputs retain the release version rather than the next patch's build label.

World hashes prove constructed geometry/collision, not feel. Publication transcripts prove controlled publication,
not native convergence; real-time network soaks remain statistical evidence.

For a deliberate reviewed behavior change only, regenerate the unit references explicitly:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- \
  --suite=ReferenceTests --write-reference="$PWD/src/Dev/Equivalence/Fixtures"
```

Inspect compressed JSON using Python's `gzip` and `json` modules before committing new references. Tests never refresh them.
