# Unit time regression proof, October 2026

**Status:** Dated record moved out of [code-quality.md](../code-quality.md), the contract, which keeps the rule that
network unit tests use manual clocks and advance time explicitly (its Deterministic unit time section). Linux, October
2026. The counts below describe that regression, not a bound the gate enforces.

The October 2026 regression repeated
`godot-mono --headless --path . res://scenes/dev/unit_tests.tscn` with eight concurrent processes on 12 available
CPUs. Additional Python processes ran `while True: pass` throughout each batch. Every child's exit status, named
assertion failures, final test count and suite BEGIN markers were checked; a successful parent alone is insufficient.

| Code | Runs | CPU hogs | Assertion failures | Native crashes |
| --- | --- | --- | --- | --- |
| Before manual time | 300 | 4 | 8 | 1 |
| After manual time | 300 | 4 | 0 | 0 |
| After manual time, added load | 300 | 6 | 0 | 0 |

All eight assertion failures were `authority/new canonical vent reconciles an intact observer (moving True)`.
Each fixed run completed all 13 suites and 190 tests (189 before the additional gather deadline assertion).
The baseline's separate signal-11 crash interrupted ProtocolTests, with a Godot `propagate_notification()` caller-thread
error; this change does not diagnose or fix that engine crash. The baseline consequently has 299 complete suite runs.

A full `/tmp` truncated 16 baseline logs whose recorded child exits were zero. Those incomplete records were
retained and replaced with 16 additional baseline invocations; all assertion failures and the native crash remain
in the reported 300 records. No failed assertion was retried or discarded. Proof logs were then stored under ignored
`build/wpflaky-proof/`, along with the repetition driver and validated summaries.

The added-load batch used an isolated copy matching all 305 committed source, scene and project-setting files,
excluding preexisting unrelated shutdown edits. `make check` passed both there and in the working checkout.
The rendered three-client puncture race also passed with 120 ms lag, 30 ms jitter and 3% loss: canonical vent
comparison covered 1,658 trajectory pairs (median/p95/max separation 0.104/0.246/0.390 m), and all score audits agreed.
Four captured frames were reviewed, showing the tank venting, tipping and tumbling.

