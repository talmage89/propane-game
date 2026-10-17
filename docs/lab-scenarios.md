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

All selections now use the registry; the host has no case dispatch or mutable case state. `LabCases` is the C#
namespace to avoid colliding with the gameplay fixture registry `Propane.Lab.LabScenarios`.

| CLI selections | Owner |
| --- | --- |
| `lifetime` (default), `tracking` | `LifetimeScenario` |
| `dials` | `DialsScenario` |
| `frames` | `FramesScenario` |
| `duel`, `soak` | `BankScenario` |
| `rules`, `aimstall` | `BankRulesScenario`, `AimStallScenario` |
| `prototype` (Ring Out / King) | `PrototypeScenario` |
| `shortfuse`, `shortfuse-combo` | `ShortFuseScenario` |
| `effects`, `stackfire` | `EffectsScenario`, `StackFireScenario` |
| `measure`, `measure-play` | `MeasureScenario` |
| `dissolve`, `reference-retirement` | `DissolveScenario`, `RetirementScenario` |

Subscriptions can detach at fixture/round boundaries and are removed from the owner immediately, so cleanup does
not hold old fixtures across collection checkpoints. Case disposal drains remaining registrations in reverse order.
Every case must return successfully from its complete async body before the host prints PASS. Existing early-capture
options still return through their original paths. Events are guarded after disposal; frame waits are the original
host Tasks, without an additional scheduler or cancellation system. The existing measurement/stream probe nodes
remain ordinary diagnostics at their original attachment sites; none is a scenario.

To add a case, add its owner and registry entry, update the independent inventory and `LabScenarioTests` selection
list, and extend the independent session/shard coverage when it belongs in the gate. Keep its waits and completion
checks with the case, and use a scoped owned registration for subscriptions that outlive a local call.

At parent `1019775`, the inventory contains 17 CLI cases, 52 options, 146 assertion calls, 46 log calls,
34 capture expressions and 45 command/reference sites. Reproduce the parent and compare the extraction with:

```sh
python3 -B tools/dev/lab_scenario_inventory.py --revision=1019775 --output=build/rfb7a-parent.json
python3 -B tools/dev/lab_scenario_inventory.py --compare=1019775 --output=build/rfb7a-current.json
```

The comparison checks executable expressions; caller context is retained for review because new documentation
naturally adds references. Numeric telemetry and native streaming completion are compared separately.

[Verification record](evidence/lab-scenarios-verification.md) includes migration proofs, failed attempts and native capture comparisons.
