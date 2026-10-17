# Bot navigation measurements

**Status:** Dated record of the original navigator, moved out of [bot-navigation.md](../bot-navigation.md), the
contract, which keeps how the component works and the regression course that gates it. The sweeps and matches below
are not acceptance bounds. Linux, recorded 2026-10-10, before the shared-cache implementation.

The following measurements record the original navigator. The shared-cache implementation's
[scale verification](bot-scale.md) includes its final course sweeps, paired match outcomes and frame/brain costs.

Linux, Godot 4.7.2 .NET/Jolt, baked movement tuning, physics at 120 Hz and courses at fixed 60 FPS:

| Course run | Goals reached | Mean successful time | Stuck events |
| --- | ---: | ---: | ---: |
| Old recovery, same 20 seeds | 120/140 (85.7%) | 3.21 s | 289 |
| Navigation, 20 seeds | 140/140 (100%) | 3.23 s | 0 |
| Navigation, 100 seeds | 700/700 (100%) | 3.20 s | 0 |

Every baseline house course timed out at 15 seconds; navigation reaches those goals in about 4.5 seconds.
The larger sweep includes 216 picket and 484 privacy courses and 600 planned jumps. Direct gates are walked.
The rendered five-seed run also completes all 35 courses, with fence crossings and supported landings captured.
The creep regression reproduces the old detector's false stuck result at 120 Hz and verifies the navigator accepts
normal aimed walking at both 60 and 120 Hz.

In separate four-bot, 120-second matches, the instrumented old smart brain recorded 73 stuck events and 123 target
exclusions, ending with banks 8/1/10/0. The final navigator recorded 0 stuck events and 7 exclusions, ending 2/5/2/4.
These are different randomly generated suburbs, so the bank difference is not a controlled strength comparison.
Three final mixed matches (smart/simple/smart/simple) ended 0/3/2/1, 0/4/22/2 and 9/1/11/1: smart bots banked 44
against 12 overall, winning two matches and losing one. Mixed smart counters totaled 6 stuck events and 146
exclusions; exclusions also include the brain's existing inability to find a shooting position. Every final
match's four clients passed `tools/dev/check_scores.py` against its server audit.

Sampled whole-client physics times fell from a 2761 ms maximum before budgeting to 236 ms after, with medians
13 and 10 ms. These are sparse samples under shared machine load, not isolated navigation benchmarks or a bound
on frame time. The asserted per-tick expansion limit is the stronger evidence for bounded search work.
Windows and macOS were not exercised here.

