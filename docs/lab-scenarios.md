# Lab scenario ownership

`LabTest` owns scene attachment, capture, trace observation, frame waits, collection checkpoints and shutdown.
Plain objects in `src/Dev/LabScenarios/` own each async case's state, input sources, subscriptions and completion.
The host awaits the selected owner at the former dispatch site; owners retain the existing awaits and frame counts.
Returning successfully from the complete async body satisfies completion. A fault or unfinished body cannot pass.
Disposal runs subscription cleanup once in reverse order; guarded callbacks ignore delivery after disposal.

`LabScenarioRegistry` registers selections. The source inventory in `tools/dev/lab_scenario_inventory.py`, session
inventory in `check_lab.py`, and fixture/shard inventory in `check_lab_shards.py` are deliberately independent.
Unknown names now fail explicitly instead of silently running lifetime; valid selections retain their behavior.

Run `python3 -B tools/dev/lab_scenario_inventory.py` to record assertion, log and capture expressions, options and
command references. Run `LabScenarioTests` through the unit-test scene for fake-context lifecycle proof.

The first migration moves lifetime and tracking together, including the two tracking checks at the end of lifetime's
first shard. The host still performs every GC/frame checkpoint. Subsequent families are recorded in the verification
report alongside their parent comparisons.

At parent `1019775`, the inventory contains 17 CLI cases, 52 options, 146 assertion calls, 46 log calls,
34 capture expressions and 45 command/reference sites. Reproduce the parent and compare the extraction with:

```sh
python3 -B tools/dev/lab_scenario_inventory.py --revision=1019775 --output=build/rfb7a-parent.json
python3 -B tools/dev/lab_scenario_inventory.py --compare=1019775 --output=build/rfb7a-current.json
```

The comparison checks executable expressions; caller context is retained for review because new documentation
naturally adds references. Numeric telemetry and native streaming completion are compared separately.
