# Gather and ragdoll gate flakes

Investigation on `wp/test-flakes`, starting from `d067ef6` (October 14, 2026).
Neither assertion bound was widened. No tank, effect, bot or navigation implementation changed.

## Gather: the fixture did not have a clear departure

`FixedSeed=23` fixes the suburb, not blast spin. `Blast.Apply` constructs a separate
`RandomNumberGenerator`; Godot's [constructor](https://github.com/godotengine/godot/blob/4.7.2-stable/core/math/random_number_generator.h#L55)
automatically randomizes it from [wall-clock time](https://github.com/godotengine/godot/blob/4.7.2-stable/core/math/random_pcg.cpp#L39).
`--effects-seed` seeds the world generator, not these separately constructed generators. Fixed FPS therefore
does not make this fixture's landing positions deterministic.

The original fixture failed on repetition 91 of 100 four-process runs. All three members were loose and resting
by about eight seconds; they stayed connected but never entered gathering. Adjacent passes began gathering at
10.508 and 11.008 seconds. Every trace kept `TimeScale=1`. A subsequent effect-seed sweep failed at seeds 3, 37
and 45. In seeds 3 and 37, the departure sphere overlapped the parked van (`@RigidBody3D@1373`), with zero completed
or interrupted glides. At seed 3 the blocked tank rested at `(42.62649, 0.27303, 4.07768)` and the van at
`(41.38206, 0.11101, 1.37280)`. The group was correctly waiting for a clear path, not a late service result.

The fixture now clears nearby rigid props together with shell debris at six seconds, after the real rifle blast
has proven that it knocked an intact group loose. The checks at 18 and 22 seconds, 6 mm drift and 0.5-degree
rotation bounds remain unchanged. `gather_range` still covers obstacle avoidance and blocked departures.

`--gather-trace` buffers member states, positions, disturbance revisions, frame/physics clocks and nearby bodies
until the result, avoiding per-frame terminal I/O. It also reports lease events, glide counts and departure overlaps.

After the fix, 400 completed repetitions passed: effect seeds 1–100 and 300 unseeded runs, with four probes
running at a time beside the parallel deep gate and other worktree agents. Gathering began at 9.008–12.758
seconds and finished at 12.008–15.758, inside the unchanged deadline. Three additional launches coincided with
a clean C# rebuild, could not instantiate the scene, and were excluded and rerun successfully. Do not rebuild
the assembly while launching repetitions. The initial full-gate load command ended with SIGTERM before finishing;
its passing jobs are not a full-gate pass.

```sh
godot-mono --headless --path . --fixed-fps 60 res://scenes/dev/game_test.tscn -- \
  --scenario=gather --tune.FixedSeed=23 --gather-trace
# Optional: --effects-seed=3 (still leaves blast spin randomized).
make -j4 test-full-lab-king test-full-lab-soak-void test-full-generator-1 test-full-generator-2
```

(When this was written, the Lab sweeps were numbered `test-full-lab-1` to `-4`. `make -n test-full` lists the named jobs
that replaced them; the two above are the longest.)

## Motion: a discontinuity at the torso twist limits

The [failing CI run](https://github.com/talmage89/propane-game/actions/runs/38536993832) reported the same overall
maxima as passes. The root's 166.007-degree coordinate change dominated the statistics but was already exempt.
Local reproduction with the official editor failed four of 30 runs across two-, four- and twelve-CPU affinity
settings. The first snap was `Head`, about 77 degrees in an ordinary 0.01667-second frame near the end of get-up;
hitstop did not inflate this rate. Ragdoll flail also constructs its own automatically randomized generator.

One failing trace changed the signed rear aim angle from `3.1368` to `-2.9990` radians. Clamping it switched the
desired torso yaw from `0.7330` to `-1.7104` radians (140 degrees) in one frame. The random get-up heading decides
whether the camera aim crosses that wrap when the player becomes active.

The applied torso yaw now turns at the existing `RifleAimTurnSpeed`, including at that wrap. Logical aim, shots,
clips and IK remain immediate or authored as before. A deterministic rear-aim crossing in `rifle_reach_tests`
failed before the fix with a 79.536-degree head snap and passed afterward: maximum step 48.047 degrees and wrist
residual 0.000028 m. Its 60-degree final-bone bound matches the existing motion gate.

```sh
godot-mono --headless --path . --fixed-fps 60 res://scenes/dev/rifle_reach_tests.tscn
godot-mono --headless --path . --fixed-fps 60 res://scenes/dev/player_range.tscn -- \
  --scenario=ragdoll --assert-grip --assert-motion
```

After the fix, the official editor passed 60 ragdoll repetitions (20 each with affinities `0,1`, `0,1,2,3`,
`0-11`) and 30 more with `threading/worker_pool/max_threads=1,2,4` (10 each). The latter used scratch project
copies under `build/`; `/proc` confirmed each requested worker count. Jolt uses that
[worker pool](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/jolt_physics/spaces/jolt_job_system.cpp#L149).
The local Arch editor passed 20 repetitions and rendered player/side views. CI was read, not triggered.

## Final verification

| Command | Result |
| --- | --- |
| `make setup`, `make build` | Imported and built; final builds had zero warnings/errors. |
| `make check` | Passed, including every fast suite and separate-process hashes; warning-free build. |
| `python3 -B tools/dev/lint_tests.py` | Passed, including injected gate failures and parallel shard coverage. |
| `make -j4 test-full` | Failed only at the Lab `dials` session's 600-second timeout. All other offline jobs passed: four generator shards covering 1,044 seeds, 700/700 navigation goals, deep piles/ranges, menu lifetime and prototype sessions. The final bot jobs were skipped. |
| `godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- --scenario=dials` | Passed alone: all 530 sessions, clean engine log, about 390 seconds under the same 600-second timeout. |
| `make test-full-bots` | Passed separately: faulted 90-second rifle soak, two-match duel lane and loading-readiness stall; zero tank disagreements in each. |

The full parallel invocation remains a failure; the isolated passes do not replace that result. Its tuning sweep
needs separate performance/parallel-budget investigation. No timeout or gameplay value was changed for it.

Rendered probes used `godot-mono --fixed-fps 60 --resolution 960x540 --path .` with these scene arguments:

- `res://scenes/dev/player_range.tscn -- --scenario=ragdoll --assert-grip --assert-motion`, with and without
  `--view=side`; captures under `build/flakes-ragdoll` and `build/flakes-ragdoll-side`. Reviewed `rag_08`, `rag_10`
  and `rag_12`: physical tumble, rising with the rifle and the returning carry stance.
- `res://scenes/dev/game_test.tscn -- --scenario=gather --tune.FixedSeed=23 --effects-seed=3 --gather-trace`;
  captures under `build/flakes-gather-after-shots`. Reviewed `gather_04` and `gather_10`: cleared departure space
  and a compact, upright gathered pile. The earlier `build/flakes-gather/gather_04` shows the nearby van.
- `res://scenes/dev/rifle_reach_tests.tscn`: the final-pose regression also passed with the rendered backend.

Captures used `--capture-dir` with an absolute path under this checkout's `build/` and `TMPDIR` there too.
The 400 successful physics traces, plus three invalidated launches, are retained in
`build/flakes-gather-after/traces.tar.gz`; its `results.jsonl` and `summary.json` record the results and timing range.
Baseline failures are in `build/flakes-load/gather-parallel-091.log` and `build/flakes-seeds/gather-{003,037,045}.log`.
Official affinity and worker-count results are under `build/flakes-baseline/affinity`, `build/flakes-motion-after`
and `build/flakes-workers-logs`. Large download and scratch build copies were removed.

These changes affect the dev fixture and local rifle presentation. Match messages, suburb construction, tanks
and tuners are unchanged; `Protocol.Version` remains 3.
