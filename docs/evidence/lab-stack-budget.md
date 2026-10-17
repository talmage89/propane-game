# Lab stack budget measurements, October 2026

**Status:** Dated record moved out of [lab.md](../lab.md), the contract. It was written when the shipped range was
24–40 tanks; the range is now 24–512 tanks (`LabStackMaximum`), so its "retains the shipped range" and "no larger
product ceiling is justified" conclusions describe that earlier decision, not the present range. GTX 1660, seed 23,
baked tuning; the underlying runs are in [crowded effect profiling](vent-performance.md).

The crowded-effect optimization retains the shipped adjustable range at **24–40 tanks**. A repeated comparison
on the GTX 1660 at 1920×1080, Forward+, seed 23 and baked tuning measured these pooled results (two back-to-back
before/after repeats, each with 180 warm-up frames and 600 samples):

| Tanks | Ordinary blast GPU p95 before → after (ms) | Blast wall p95 before → after (ms) | Primed chain wall p95 before → after (ms) |
| ---: | ---: | ---: | ---: |
| 32 | 26.4 → 22.2 | 29.7 → 25.7 | 86.8 → 78.7 |
| 40 | 37.5 → 37.5 | 43.0 → 38.5 | 107.4 → 99.1 |
| 64 | 62.6 → 58.6 | 67.0 → 61.5 | 129.4 → 40.0 |
| 128 | 193.1 → 138.5 | 199.2 → 219.0 | 292.6 → 484.5 |

Shared one-minute CPU load ranged from 5.89 to 38.00, with 0–3 other rendered Godot processes; wall results,
especially chain spikes, cannot establish an isolated hardware limit. The 128-tank 16.7 ms p95 / 50 ms worst
requirements remain unmet. Forty is still near a 30 FPS blast budget, not a promise of uninterrupted 60 FPS,
and no larger product ceiling is justified. The original idle sweep reached 1,024 tanks at GPU p95 9.9 ms
(wall p95 18.4 ms); that size was not repeated in this comparison. Resting bodies remain cheap; persistent lit smoke
and chain CPU/physics bursts remain expensive. Gameplay values and particle counts are unchanged. See
[crowded effect profiling](vent-performance.md) for every phase's GPU/wall p50, p95 and worst, profiler breakdown,
load records, rendered suburb matches, visual comparisons and reproducible baseline commands.

