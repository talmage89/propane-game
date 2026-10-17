# Release-reference verification — October 16, 2026

Production: v0.4.0, `629bf19`. Observation implementation: `f790480`. Linux x86_64, Godot
`4.7.2.stable.mono.arch_linux.ed1daf0bf`, .NET SDK `10.0.112`, normal Debug. Commands below used a worktree-local
`TMPDIR` under `build/`; all logs, captures and detailed traces stayed in `build/`.

## Reference inventory

The committed references contain 81 wire cases, 557 scalar tuners (404 shared), four saved forced-formation plans
and construction hashes, 60 segment-plan hashes, 48 publication packets across 12 manual-time steps, and five Lab
traces. The complete fixture directory is about 183 KB. No raw captures are committed.

The publication fixture crosses full/partial batch boundaries on interleaved traffic groups, exercises both tank
and car rates, preserves pending drops belonging to two owned actors, then covers sleeping gathers, final rests,
confirmation, wake, checkpoint rotation, claim suppression, epoch reset, and disposable/required send failures.
It calls the release's real `Match.SendBodies` through an injected recording transport.

Every original unit-test source is unchanged except three helpers in `WorldBuildTests` made internal for reuse.
Normalizing those three visibility keywords reproduces that file's release source exactly; no original assertion
was removed or edited. The final reference suite adds seven assertions in four discovered suites.

## Exact repeatability

The five selected Lab cases passed all five standalone runs and all five loaded runs: **50/50 exact comparisons**.
Every window digest and final summary matched the frozen release. Loaded runs held four busy Python processes plus
a separate eight-bot/player Lab soak throughout. One 600-second companion finished successfully; another was
started to maintain load and terminated after the matrix completed. All load processes were reaped. Loaded matrix
wall time was 411.06 seconds. These are checker wall times, not gameplay performance measurements.

Standalone command:

```sh
python3 -B tools/dev/check_equivalence.py --repeat=5 --output="$PWD/build/rfp0-alone-final"
```

The loaded driver in `build/rfp0/stress.py` ran the same five-repeat command with `--skip-unit`, four
`python3 -B -c 'while True: pass'` processes, and a restarting companion:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- \
  --scenario=soak --environment=suburb --bots=8 --seconds=600 --duel-seconds=600
```

| Check | Alone, five runs (seconds) | Loaded, five runs (seconds) |
| --- | --- | --- |
| bank | 16.57, 16.82, 24.22, 20.43, 24.79 | 27.17, 24.34, 24.35, 32.29, 24.24 |
| king | 24.23, 31.75, 29.51, 31.56, 28.03 | 45.24, 34.75, 35.42, 35.30, 35.66 |
| spill-chain | 4.72, 9.13, 4.97, 4.97, 5.07 | 6.78, 6.22, 7.19, 6.68, 6.32 |
| shortfuse | 5.87, 10.22, 6.02, 8.60, 6.27 | 7.87, 7.62, 11.52, 7.62, 8.28 |
| dissolve | 2.22, 4.19, 2.27, 2.27, 2.47 | 2.87, 3.07, 3.73, 2.97, 2.77 |

The new unit reference checks take about 6.8–7.8 seconds including engine startup. Python comparison/diagnosis
self-tests take under 0.01 seconds. The local target additionally runs one repetition of the five Lab cases.

The ordinary streamed Dissolve workload is excluded from this exact gate. A release pilot had 8,619 observed ticks;
five repeats had 8,641, 8,645, 8,621, 8,477 and 8,579. Every first mismatch appeared in the 15–20-second window,
in commands, tank lifecycle or transforms. All ordinary scene assertions passed. The retained diagnostics are in
`build/rfp0-alone/` and `build/rfp0/streaming-exclusion.json`. The replacement controlled fixture checks eight
floor/dissolve/retire/free boundaries, freeing two segments and forgetting 32 tanks; it does not certify natural
streaming timing or navigation equivalence.

## Rejection probes

`python3 -B build/rfp0/perturb.py` changed one source at a time, built it, required failure, and restored the source.
Every probe failed, and restored unit references passed. Raw output and exact temporary edits remain in
`build/rfp0-perturb/` and `build/rfp0/perturb.py`.

| Deliberate change | First diagnosis |
| --- | --- |
| Ping encoder adds one | `wire.Ping/client.byte[7]` |
| Ping decoder adds one | `wire.Ping/client.decode.Payload.Value` |
| BlastRadius upper hint 40 → 41 | `tuners.schema[120].attributes[1].arguments[1]` |
| Swap BlastRadius/BlastSpeed declarations | `tuners.schema[120].Name` |
| Export local BotEasyRoamRadius | `tuners.schema[16].shared` |
| Add 0.125 m to generated tank X | `world.Rows.plan.Tanks[0].Position[0]` (segment hashes also fail) |
| Missing wire fixture | `reference wire.json.gz: missing fixture` |
| Wire fixture truncated halfway | SHA256 failure naming `wire.json.gz` |
| One extra RNG draw per BankingBot | First window differs; detailed release/candidate comparison finds tick 3, `actors/5/pose.Yaw` |

The RNG probe used a separately built release-source copy with identical observation plumbing through
`--baseline`. The baseline matched the frozen digest; the candidate did not. The detailed first values were
`f32:429D3C08` versus `f32:429B37C0`. The final diagnostic command also passed its negative exit check:

```sh
python3 -B tools/dev/check_equivalence.py --compare-details \
  "$PWD/build/rfp0-perturb/trace/run-1/baseline/bank.detail.jsonl.gz" \
  "$PWD/build/rfp0-perturb/trace/run-1/bank.detail.jsonl.gz"
```

The comparison helper directly rejects positive/negative zero differences and ignores compiler-generated
anonymous type names. It preserves declared field order and exact numeric bits. Missing/truncated JSON, field
inventory, evidence-directory retention and detailed actor/tick diagnosis have independent Python assertions.

The explicit final provenance refresh recorded production `629bf19` and instrumentation `f790480`. All ten
compressed reference files remained byte-for-byte unchanged; only provenance and its manifest hash changed.

## Rendered and native checks

The following rendered runs passed with `--fixed-fps 60 --resolution 1280x720`:

- `lab_test --scenario=shortfuse-combo --replay-random --tune.HitstopDuration=0`, captures in
  `build/rfp0-shortfuse/`: all seven images reviewed, showing chip damage, spectating, the blast, a round win and
  the rebuilt second round.
- `menu_test --scenario=input --classic-lobby --port=24681`, captures in `build/rfp0-input/`: 165 assertions,
  zero failures. Representative Lab jump/pause, match turn and return-to-single-player captures were reviewed.
  Device delivery uses the existing `Input.ParseInputEvent` path.

Native clean network checks used `tools/dev/net_bots.sh -n 2 -w 0 -t clean -s '--match-seed=23'`, plus:

- `-l 15 -o "$PWD/build/rfp0-net-bank" -- --melee-start`: both clients agree with the server audit; 499 grids,
  13,156 body pairs, zero disagreements.
- `-l 20 -o "$PWD/build/rfp0-net-king" -- --mode=king --mint-seconds=0.5 --melee-start`: both clients agree with
  the audit, including 12 minted tanks; 599 grids, 15,804 pairs, zero disagreements.

These native checks retain the existing convergence tolerances. They are additional statistical evidence,
not an exact network-delivery oracle. The exact publication test remains controlled and socket-free.

An initial full gate emitted one native Jolt job-limit warning in the existing gather grant-race scene. Separate
release-source and candidate runs of that scene both passed without warnings; the original log was retained.
The intentionally malformed-packet unit probe also retains its existing warning.

Ubuntu CI, Windows/macOS execution and cross-CPU trace identity were not exercised locally. The Lab target remains
local; the wire/schema/world/publication unit checks are discovered by the unchanged CI core partition. No
production message, tuner, generation rule or gameplay rule changed; `Protocol.Version` remains 3.

## Final gates

`make setup` and `make build` passed. `make check` passed after restoring every mutation: 481.63 seconds,
zero compiler warnings, 415 unit assertions with zero failures, all original scripted suites, bounded generator
sweeps and both same-checkout process comparisons. The final run did not repeat the native Jolt warning; only the
existing intentional malformed-packet warning remained. The earlier full gate also passed (555.96 seconds).

`python3 -B tools/dev/lint_tests.py` passed its analyzer-negative, formatting, gate-wiring, CI-equivalence, thread,
coverage and failure-propagation probes. The explicit six Python reference-checker tests passed. Final logs are
`build/rfp0/check-final.log`, `build/rfp0/lint-tests-complete.log` and `build/rfp0/final-gates.json`.

`make test-equivalence` then passed in 61.61 seconds: six Python checker tests, seven unit reference assertions
and one exact repetition of all five Lab cases. Per-case times were 16.72 / 24.13 / 4.72 / 5.62 / 2.22 seconds
for Bank / King / spill-chain / Short Fuse / controlled retirement; the unit reference process took 6.87 seconds.
Its engine logs were clean. The disposable 128 MB release-source/import/build copy was removed after diagnosis;
compressed detailed baseline/candidate observations and all failed-probe logs were retained.
