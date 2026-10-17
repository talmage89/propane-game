# Actor scale

Actor CPU and rendering work on `wp/scale-actors`, measured against dev `617a4f5` after the tank package landed.
The full grid compares candidate `8413382` with that baseline plus identical measurement-only actor timers,
rendering counters, camera modes and feature omissions. The overlay is retained as
`build/scale-actors-baseline-instrumentation.patch`; it contains no actor optimization. The later gate-only dev
merge and lobby tank-supply UI/codec merge (`804c068`, default 1×) do not alter this benchmark production path.
Final integration is branch `d26761f`. These runs still use the original brains.

Measured October 14, 2026. Intel Core i5-10400F (12 logical CPUs), GTX 1660, Godot 4.7.2 .NET, .NET 10.0.12,
Linux. Other work was active. Use the [measurement definitions](../runbooks/measuring-scale.md) and [original
baseline](scale-baseline.md) alongside this report: inclusive script timings, callback intervals and GPU timings
cannot be summed.

The actor changes reduce per-system work, but **sixteen rendered production bots are still unplayable on this
snapshot**. The measured dev has the original brains; integration after brain scaling remains required.

## Attribution and decisions

The actor dictionary separates movement, `MoveAndSlide`, physics-tick target rays, ragdoll/get-up decisions,
locomotion parameter writes, logical aim/camera queries, rifle pose/IK, recovery blending and hand follow.
Automatic native animation and physical-bone work remain in the engine residual. The explicit `AnimationTree`
scope measures only manually advanced distant samples, so its increase does not mean total animation became more
expensive. The native tail remains an upper bound, not a Jolt solver profile.

GPU attribution uses one production baseline with a single rendering feature omitted at a time. Each row is the
median of three Release runs, 10 simulated seconds after 2 seconds of warm-up, 40 tanks. Odd repetitions reverse
omission order. CPU work and fights continue when meshes/effects are hidden. These are overlapping feature
costs, not independent pass timers; actor omission also removes actor shadows, and hidden particles can still
simulate.

| Omitted feature | GPU ms, 0 bots | 8 bots | 16 bots | Render CPU ms, 16 bots | Visible primitives, 16 bots |
| --- | ---: | ---: | ---: | ---: | ---: |
| none | 4.43 | 33.56 | 44.64 | 3.61 | 1,235,429 |
| actors | 4.40 | 32.03 | 39.22 | 2.96 | 999,007 |
| shadows | 3.05 | 28.90 | 38.69 | 2.91 | 1,233,368 |
| effects | 4.40 | 33.48 | 40.23 | 2.68 | 1,239,189 |
| debris | 4.45 | 31.91 | 43.73 | 3.27 | 1,218,230 |

At 16 bots the individual GPU means span 43.25–55.84 ms for the complete view, 35.02–44.81 with actors hidden,
38.05–46.03 without shadows, 39.98–47.65 without effects, and 43.70–43.84 without debris. Removing actors,
shadows or effects reduces submitted work, but the ranges overlap and the GPU often downclocks while waiting for
brains. At 8 bots the complete-view range is 19.98–34.16 ms. Do not treat the median differences as precise
causal costs or add them. At zero bots the reported shadow counters are zero even though the sun casts visible
shadows; these counters are not complete GPU pass attribution.

A focused three-pair 16-bot mesh probe reduced visible primitives approximately 10.5%; GPU pairs were
38.52/36.68, 45.06/40.17 and 43.94/42.37 ms for imported bias/0.1 bias. This supports using the already imported
mesh LODs at distance. No global shadow or effect setting was changed: the near look is preserved and the tank
package already bounds dense effects. Exact GPU pass durations remain unavailable.

## Changes

Retain actor ray/shape query parameters, rifle clip names/lengths and immutable finger-rest quaternions. Query
geometry, exclusions, frequency and hit decisions stay the same. Native query results still allocate. Rifle pose
work has no routine managed allocations; combat/recovery can allocate within its inclusive scope.

The eight **Actor Detail** tuners control the enable switch, generated mesh LOD multiplier, near/far distances,
hysteresis, and middle/far/hidden pose rates. Defaults keep original idle animation and skeleton callbacks
within 30 m, then sample at 30 Hz; beyond 80 m they sample at 15 Hz, outside the frustum at 10 Hz. A 3 m outward
hysteresis prevents chatter. Owning views, missing/mismatched cameras, hit reactions including fade, and
ragdoll/capture/get-up always keep full detail. The mesh multiplier is 0.1 for eligible distant actors and the
original imported value otherwise. Live band/rate changes resample immediately; restoring full detail returns
the imported mesh bias.

Every actor still advances locomotion and logical aim clocks each frame and movement/collision each physics
tick. Skeleton work consumes actual accumulated time, staggered across actors. The top-level rifle follows its
cached final pose relative to the moving body on held frames. Before a hit or physical throw, pending animation
time is consumed before queuing the new action. This matters for physics: native physical bones read the
authored pose immediately. See [motion](../motion.md#actor-detail) for the implementation contract and checks.

## Paired measurements

The grid has 120 separate processes: 0/2/4/8/16 bots, headless/rendered, Debug/Release, three repetitions per
branch. For each count/view/repetition, run baseline/candidate, candidate/baseline, baseline/candidate; reverse
Debug/Release order on odd repetitions. Both use seed 23, the same map hash and 40 tanks, 120 Hz physics,
1280×720 overview, disabled vsync and idle pacing, 10 measured simulated seconds plus 2 warm-up seconds. The
fixture has an idle player in addition to bots; 16 bots therefore means seventeen actors, beyond a sixteen-actor
network lobby. Release is optimized C# on the same editor engine, not an exported engine.

```sh
make bench BOTS=<count> TANKS=1 RENDERED=<0|1> CONFIG=<Debug,Release|Release,Debug> \
  SECONDS=10 WARMUP=2 REPEATS=1 BENCH_ARGS="--output=build/bench/<unique>.json"
python3 -B tools/dev/bench_compare.py --merge build/bench/<matching-runs>.json --output=build/bench/<merged>.json
python3 -B tools/dev/bench_compare.py build/bench/<base>.json build/bench/<actors>.json
```

A/B is baseline/candidate; medians of the three per-run statistics.

### Debug

| View | Bots | FPS | Wall p50 ms | p95 | p99 | GPU ms | Allocated MiB | Gen-0 | GC pause ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Headless | 0 | 2220.7/7811.3 | 0.39/0.11 | 0.82/0.18 | 2.16/0.59 | 0.00/0.00 | 18.6/8.6 | 3/1 | 115.0/17.7 |
| Headless | 2 | 321.7/1714.0 | 1.02/0.18 | 10.71/0.35 | 54.22/8.67 | 0.00/0.00 | 251.3/249.1 | 43/42 | 998.0/905.4 |
| Headless | 4 | 52.3/268.8 | 2.00/0.29 | 155.48/1.51 | 249.01/143.69 | 0.00/0.00 | 422.1/417.2 | 74/73 | 1912.0/1905.5 |
| Headless | 8 | 4.3/3.8 | 239.51/265.62 | 363.63/449.77 | 394.40/465.46 | 0.00/0.00 | 945.2/939.9 | 170/168 | 3301.9/3871.8 |
| Headless | 16 | 3.6/3.0 | 264.38/323.39 | 481.08/523.57 | 599.14/656.81 | 0.00/0.00 | 1281.8/1331.6 | 229/236 | 3945.5/5044.2 |
| Rendered | 0 | 212.7/169.5 | 4.71/5.08 | 5.73/8.73 | 6.63/9.18 | 4.50/5.65 | 4.2/1.6 | 2/2 | 18.5/14.2 |
| Rendered | 2 | 76.4/110.5 | 5.35/5.16 | 52.99/27.59 | 115.19/89.57 | 4.50/4.42 | 248.4/245.8 | 42/43 | 965.0/803.3 |
| Rendered | 4 | 45.7/44.1 | 5.45/5.34 | 135.76/140.92 | 196.72/207.30 | 5.79/6.18 | 418.5/414.4 | 75/71 | 1310.4/1378.2 |
| Rendered | 8 | 5.3/4.9 | 182.33/187.82 | 311.33/351.85 | 347.77/418.96 | 30.85/31.71 | 943.1/942.9 | 167/168 | 2549.4/2880.8 |
| Rendered | 16 | 2.7/2.5 | 359.71/382.81 | 547.89/685.76 | 658.14/760.63 | 43.76/43.38 | 1294.7/1280.0 | 231/228 | 5430.0/5932.3 |

#### Actor physics, headless, ms/tick

| Bots | Physics | Movement | MoveAndSlide | AimRay | Ragdoll | GetUp |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.108/0.088 | 0.048/0.054 | 0.028/0.022 | 0.046/0.023 | 0.000/0.000 | 0.000/0.000 |
| 8 | 0.601/0.520 | 0.311/0.328 | 0.240/0.237 | 0.229/0.121 | 0.000/0.000 | 0.000/0.000 |
| 16 | 1.045/0.960 | 0.531/0.577 | 0.423/0.428 | 0.377/0.214 | 0.000/0.004 | 0.000/0.000 |

#### Presentation, headless, ms/frame

| Bots | Presentation | Locomotion | RiflePose | PoseBlend | HandFollow | Aim | AimQuery | AnimationTree |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.046/0.025 | 0.010/0.002 | 0.177/0.000 | 0.001/0.000 | 0.001/0.000 | 0.029/0.011 | 0.023/0.006 | 0.000/0.000 |
| 8 | 0.404/0.834 | 0.090/0.088 | 1.666/1.254 | 0.009/0.009 | 0.007/0.007 | 0.244/0.127 | 0.196/0.076 | 0.000/0.470 |
| 16 | 0.632/1.410 | 0.139/0.147 | 2.483/2.207 | 0.013/0.017 | 0.011/0.016 | 0.370/0.229 | 0.299/0.139 | 0.000/0.783 |

#### Actor physics, rendered, ms/tick

| Bots | Physics | Movement | MoveAndSlide | AimRay | Ragdoll | GetUp |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.114/0.086 | 0.046/0.051 | 0.026/0.025 | 0.056/0.023 | 0.000/0.000 | 0.000/0.000 |
| 8 | 0.491/0.446 | 0.261/0.282 | 0.188/0.197 | 0.181/0.110 | 0.000/0.000 | 0.000/0.000 |
| 16 | 1.332/1.244 | 0.615/0.731 | 0.488/0.576 | 0.493/0.292 | 0.008/0.001 | 0.000/0.000 |

#### Presentation, rendered, ms/frame

| Bots | Presentation | Locomotion | RiflePose | PoseBlend | HandFollow | Aim | AimQuery | AnimationTree |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.073/0.062 | 0.017/0.005 | 0.216/0.015 | 0.002/0.000 | 0.001/0.000 | 0.040/0.020 | 0.032/0.013 | 0.000/0.006 |
| 8 | 0.326/0.690 | 0.068/0.068 | 1.190/1.012 | 0.007/0.008 | 0.005/0.006 | 0.202/0.105 | 0.165/0.062 | 0.000/0.392 |
| 16 | 0.993/1.851 | 0.180/0.182 | 2.913/2.786 | 0.019/0.027 | 0.017/0.018 | 0.578/0.305 | 0.462/0.198 | 0.000/1.040 |
### Release

| View | Bots | FPS | Wall p50 ms | p95 | p99 | GPU ms | Allocated MiB | Gen-0 | GC pause ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Headless | 0 | 2743.4/9047.5 | 0.35/0.10 | 0.49/0.16 | 1.00/0.49 | 0.00/0.00 | 19.9/9.7 | 5/2 | 118.0/6.1 |
| Headless | 2 | 379.7/1943.1 | 0.98/0.17 | 7.71/0.28 | 36.89/7.05 | 0.00/0.00 | 248.0/245.9 | 43/42 | 1045.1/953.7 |
| Headless | 4 | 65.3/301.6 | 1.87/0.25 | 123.96/1.58 | 217.48/141.00 | 0.00/0.00 | 413.7/407.6 | 71/71 | 1975.1/2001.7 |
| Headless | 8 | 4.7/4.1 | 220.57/240.58 | 344.77/380.26 | 396.73/416.06 | 0.00/0.00 | 942.3/919.6 | 168/163 | 3346.7/3830.0 |
| Headless | 16 | 4.0/3.8 | 244.41/229.91 | 446.50/473.34 | 489.71/568.58 | 0.00/0.00 | 1245.7/1011.2 | 220/179 | 3994.9/4043.6 |
| Rendered | 0 | 215.3/153.3 | 4.63/5.93 | 5.57/9.64 | 6.51/10.22 | 4.46/6.25 | 4.2/2.7 | 1/0 | 11.9/0.0 |
| Rendered | 2 | 106.5/106.8 | 5.28/5.14 | 28.45/35.70 | 90.35/89.58 | 4.51/4.44 | 244.7/241.8 | 42/42 | 835.8/891.8 |
| Rendered | 4 | 51.9/56.2 | 5.40/5.25 | 113.58/106.14 | 165.26/204.12 | 5.56/5.24 | 411.9/406.0 | 71/72 | 1273.9/1355.1 |
| Rendered | 8 | 6.1/5.3 | 162.64/188.10 | 284.23/325.34 | 318.19/368.11 | 25.36/32.16 | 933.5/926.4 | 167/165 | 2567.2/2873.4 |
| Rendered | 16 | 2.6/3.0 | 376.15/315.38 | 720.59/563.14 | 843.30/625.11 | 44.66/40.90 | 1346.8/1247.5 | 242/221 | 5784.2/4945.0 |

#### Actor physics, headless, ms/tick

| Bots | Physics | Movement | MoveAndSlide | AimRay | Ragdoll | GetUp |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.083/0.078 | 0.040/0.051 | 0.022/0.021 | 0.033/0.021 | 0.000/0.000 | 0.000/0.000 |
| 8 | 0.558/0.482 | 0.332/0.326 | 0.235/0.248 | 0.193/0.121 | 0.000/0.000 | 0.000/0.000 |
| 16 | 0.915/0.862 | 0.499/0.541 | 0.419/0.443 | 0.321/0.222 | 0.000/0.001 | 0.000/0.000 |

#### Presentation, headless, ms/frame

| Bots | Presentation | Locomotion | RiflePose | PoseBlend | HandFollow | Aim | AimQuery | AnimationTree |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.034/0.020 | 0.007/0.001 | 0.155/0.000 | 0.001/0.000 | 0.001/0.000 | 0.023/0.010 | 0.018/0.006 | 0.000/0.000 |
| 8 | 0.448/0.811 | 0.081/0.075 | 1.654/1.228 | 0.009/0.009 | 0.006/0.007 | 0.275/0.117 | 0.223/0.072 | 0.000/0.487 |
| 16 | 0.597/1.373 | 0.121/0.127 | 2.306/2.040 | 0.013/0.016 | 0.009/0.012 | 0.363/0.216 | 0.293/0.134 | 0.000/0.801 |

#### Actor physics, rendered, ms/tick

| Bots | Physics | Movement | MoveAndSlide | AimRay | Ragdoll | GetUp |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.097/0.081 | 0.044/0.051 | 0.026/0.024 | 0.045/0.023 | 0.000/0.000 | 0.000/0.000 |
| 8 | 0.478/0.402 | 0.261/0.277 | 0.189/0.199 | 0.194/0.095 | 0.000/0.000 | 0.000/0.000 |
| 16 | 1.407/0.985 | 0.726/0.593 | 0.605/0.482 | 0.529/0.242 | 0.004/0.003 | 0.000/0.000 |

#### Presentation, rendered, ms/frame

| Bots | Presentation | Locomotion | RiflePose | PoseBlend | HandFollow | Aim | AimQuery | AnimationTree |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0.063/0.056 | 0.014/0.003 | 0.196/0.015 | 0.002/0.000 | 0.001/0.000 | 0.039/0.019 | 0.031/0.013 | 0.000/0.006 |
| 8 | 0.299/0.653 | 0.060/0.059 | 1.139/0.932 | 0.007/0.007 | 0.004/0.005 | 0.188/0.099 | 0.151/0.061 | 0.000/0.387 |
| 16 | 0.952/1.470 | 0.192/0.138 | 3.464/2.322 | 0.022/0.019 | 0.018/0.015 | 0.588/0.228 | 0.483/0.141 | 0.000/0.854 |

### Debug: load, clocks and allocations

| View | Bots | Load A/B | Graphics MHz A/B | Memory MHz A/B | GPU utilization % A/B | Render CPU ms A/B | Visible primitives A/B | Shadow primitives A/B |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Rendered | 0 | 4.61/4.45 | 1890/1890 | 4001/4001 | 100/100 | 1.16/0.98 | 995552/986839 | 0/0 |
| Rendered | 2 | 10.62/7.86 | 1905/1905 | 4001/4001 | 40/59 | 1.27/1.07 | 1011215/993731 | 0/0 |
| Rendered | 4 | 6.44/6.28 | 1718/1312 | 4001/4001 | 26/26 | 1.13/1.23 | 1042359/1007706 | 10797/11205 |
| Rendered | 8 | 3.33/4.03 | 382/375 | 405/405 | 20/20 | 1.41/1.55 | 1097739/1037213 | 22607/17427 |
| Rendered | 16 | 20.33/19.05 | 375/375 | 405/405 | 18/14 | 2.74/3.16 | 1246096/1114365 | 56140/37872 |

| View | Bots | Physics bytes/call A/B | AimRay bytes/call A/B | AimQuery bytes/call A/B | RiflePose bytes/call A/B | Gen 0/1/2 A then B |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Headless | 0 | 1040/616 | 632/208 | 186/32 | 384/0 | 3/2/1; 1/1/0 |
| Headless | 8 | 856/440 | 629/206 | 136/32 | 384/0 | 170/169/12; 168/167/11 |
| Headless | 16 | 900/432 | 631/206 | 136/32 | 444/57 | 229/229/15; 236/236/11 |
| Rendered | 0 | 1040/616 | 632/208 | 341/32 | 384/0 | 2/1/1; 2/1/1 |
| Rendered | 8 | 863/437 | 632/205 | 136/32 | 384/0 | 167/166/12; 168/167/10 |
| Rendered | 16 | 926/479 | 631/207 | 136/32 | 445/58 | 231/230/15; 228/228/13 |
### Release: load, clocks and allocations

| View | Bots | Load A/B | Graphics MHz A/B | Memory MHz A/B | GPU utilization % A/B | Render CPU ms A/B | Visible primitives A/B | Shadow primitives A/B |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Rendered | 0 | 4.40/4.89 | 1890/1890 | 4001/4001 | 100/100 | 1.08/0.97 | 995563/986858 | 0/0 |
| Rendered | 2 | 7.43/5.34 | 1905/1905 | 4001/4001 | 51/56 | 1.13/1.06 | 1011165/993852 | 0/0 |
| Rendered | 4 | 6.89/7.38 | 1530/1770 | 4001/4001 | 31/26 | 1.12/1.06 | 1042553/1008029 | 11970/12229 |
| Rendered | 8 | 3.93/4.56 | 390/375 | 405/405 | 20/20 | 1.34/1.49 | 1097996/1037630 | 27865/18640 |
| Rendered | 16 | 18.01/16.22 | 375/375 | 405/405 | 18/17 | 3.60/2.58 | 1238569/1114054 | 58230/43673 |

| View | Bots | Physics bytes/call A/B | AimRay bytes/call A/B | AimQuery bytes/call A/B | RiflePose bytes/call A/B | Gen 0/1/2 A then B |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Headless | 0 | 1040/616 | 632/208 | 136/32 | 384/0 | 5/3/1; 2/0/0 |
| Headless | 8 | 856/439 | 632/205 | 136/32 | 384/0 | 168/167/11; 163/163/9 |
| Headless | 16 | 944/455 | 630/208 | 136/32 | 441/57 | 220/220/12; 179/179/10 |
| Rendered | 0 | 1040/1618 | 632/1210 | 338/32 | 384/0 | 1/0/0; 0/0/0 |
| Rendered | 8 | 933/438 | 630/205 | 136/32 | 384/0 | 167/166/11; 165/165/10 |
| Rendered | 16 | 905/479 | 631/206 | 136/32 | 445/58 | 242/242/15; 221/221/11 |

Run-start one-minute load: 2.36–36.53, median 10.70. Complete individual configuration reports: 120.

Actor script physics improves in the heavy cases: Release rendered 8/16 bots is 0.478/1.407 ms per tick before
and 0.402/0.985 after; target-ray time is 0.194/0.529 before and 0.095/0.242 after. Rifle pose/IK is 1.139/3.464
ms per frame before and 0.932/2.322 after. The low-bot headless workload benefits strongly from holding distant
native poses. At slow 8/16-bot frame rates, a current pose is needed each frame, so the LOD scheduler cannot
skip much work. Ragdoll/get-up cells often round to zero in this short fixture; their correctness is exercised
separately, and this grid does not establish their worst-case native cost.

Frame-rate results are mixed. Release rendered 16 bots improves from 2.55 to 3.04 FPS, p95 from 720.59 to 563.14
ms and GPU from 44.66 to 40.90 ms; Debug changes from 2.66 to 2.49 FPS. Release rendered 8 bots regresses
6.15–5.33 FPS and GPU 25.36–32.16 ms. Zero-bot Release regresses 215.33–153.29 FPS; the three candidate GPU
means are 6.31, 6.25 and 4.42 ms, versus baseline 5.51, 4.46 and 4.45. Candidate render CPU and submitted
geometry are lower even in that case, so these runs do not isolate the cause of the GPU regression. Keep the
regression and remeasure it quietly rather than calling every change a gain.

At 16 bots Release visible primitives drop 1,238,569–1,114,054 (10.1%) and rendering CPU 3.60–2.58 ms.
Graphics/memory clocks are about 375/405 MHz at 8/16 bots, versus 1890/4001 MHz at zero bots. The GPU spends
long intervals waiting for CPU simulation. Do not extrapolate a full-clock frame rate from these values.
One-minute load ranges 2.36–36.53, median 10.70. Changing tank survival and navigation query counts, native
scheduling, GC and descheduling all contribute to variation; even identical seeds are not identical fights.

Routine query allocation drops about 632–208 bytes per target ray and 136–32 per camera query; rifle posing
removes its ordinary 384 bytes per call. Inclusive combat/native callbacks can add allocation, including the
zero-bot Release target-ray outlier shown above. Total allocations and GC remain dominated by the unchanged
brains. Release rendered 16-bot brain time is still 24.25 ms per physics tick (28.10 baseline): that change is
outside the actor scopes and cannot be credited to this package. **The sixteen-bot playability target is not met
on this snapshot.** The brain package has not landed in this measured dev revision.

Full reports, individual run summaries, stage splits, raw frame CSVs, logs and first-repetition screenshots are
in `build/bench/actors-paired-final/`. Repeated feature attribution is in
`build/bench/actors-features-repeated/`. NVIDIA clocks/utilization/power sampled at 1 Hz are in
`build/scale-actors-gpu-clocks.csv`; associate them with the measured CSV's monotonic timestamp interval. No
graphics or power configuration was changed. `uptime` before/after is embedded in every run. Keep this evidence
when remeasuring on a quiet machine.

A focused zero-bot control, after all local verification processes finished, repeats baseline/candidate,
candidate/baseline, baseline/candidate on the same frozen revisions (Release rendered, 10+2 seconds, 40 tanks).
Run-start one-minute load is 4.13–5.68. GPU means are 4.411/4.466, 4.497/4.437 and 4.498/4.427 ms; median
baseline/candidate FPS is 214.77/217.72, p50 4.671/4.609 ms, p95 5.356/5.331, p99 6.147/6.120, and GPU
4.497/4.437 ms. Rendering CPU is 0.897/0.863 ms. The large zero-bot regression does not reproduce under this
lower load. This does not identify its cause or replace the original grid. Reports are in
`build/bench/actors-zero-control/`, with separate 1 Hz clock samples in
`build/scale-actors-zero-gpu-clocks.csv`. The scratch runner initially failed aggregation because
single-configuration filenames have no config suffix; the six measured processes passed, and their existing
reports were merged after correcting the glob.

## Verification and visual review

Latest integration (`d26761f`, including dev `804c068`) passes `make setup`, `make check` with zero warnings and
364 unit tests, `make -j4 test-full`, and `python3 -B tools/dev/lint_tests.py`. Logs and explicit supervisor
exit codes are `build/scale-actors-lobby-{setup,check,test-full,lint-tests,net}.*`; setup/lint use ordinary
logs. The full gate covers all four generator shards, every forced formation and separate-process world/segment
hash, all 700 navigation courses (mean 3.20 seconds, zero stuck), all 25 Lab jobs and their coverage proof, 510
tuner sessions split across eight shards, awake piles, motion/grip/recoil, menu lifetime and network tail. King
full passes in 445.5 seconds. The earlier complete integration full gate before the lobby merge also passes; an
interrupted check attempt exited 143 without an assertion failure and is not counted as a pass. Detached
supervisors retain a separate exit file for each long command. No checker tolerance was changed.

The real device-input scenario in `make check` injects `Input.ParseInputEvent` events and verifies movement,
mouse look, trigger tap/hold/release, recoil control, jump, keyboard/gamepad reload and melee through menus and
the match viewport. Scripted intent fixtures are used for physical poses, not as a substitute for this gate.
`player_range` grip/feet/recoil/ragdoll/melee probes and animation-review metrics pass. The motion/grip scenario
measures 1,555 frames, 60 aimed-creep frames, 0.065997 m muzzle range and 7.980773 degrees of aim climb.
Simulated ragdoll limbs are excluded from the existing authored-pose snap bound as before.

Explicit latest-tip network commands, each 90 seconds, are the following loops (faulted adds exactly
`--net-lag=120 --net-jitter=30 --net-loss=3`; clean adds no transport faults):

```sh
for count in 4 8; do
  for profile in faults clean; do
    fault_args=()
    if [[ "$profile" == faults ]]; then fault_args=(--net-lag=120 --net-jitter=30 --net-loss=3); fi
    tools/dev/net_bots.sh -o "$PWD/build/scale-actors-net-integration/$count-$profile" \
      -n "$count" -w 0 -l 90 -t "$profile" -- \
      --melee-start --bot-log --digest-every=0.5 "${fault_args[@]}"
  done
done
for profile in faults clean; do
  fault_args=()
  if [[ "$profile" == faults ]]; then fault_args=(--net-lag=120 --net-jitter=30 --net-loss=3); fi
  tools/dev/net_bots.sh -o "$PWD/build/scale-actors-net-integration/lobby-$profile" \
    -n 4 -w 0 -l 90 -t "$profile" -- \
    --lobby-bots=3 --melee-start --bot-log --digest-every=0.5 "${fault_args[@]}"
done
```

| Clients / actors | Profile | Final rest/moving m | Longest episode s | Disagreements | Audits |
| --- | --- | ---: | ---: | ---: | --- |
| 4 / 4 | faults | 0.001/0.000 | 0.50 | 0 | 4 clients / 1 server |
| 4 / 4 | clean | 0.001/0.082 | 0.50 | 0 | 4 clients / 1 server |
| 8 / 8 | faults | 0.001/0.205 | 1.45 | 0 | 8 clients / 1 server |
| 8 / 8 | clean | 0.006/0.167 | 1.05 | 0 | 8 clients / 1 server |
| 4 / 16 | faults | 0.001/0.320 | 0.85 | 0 | 4 clients / 1 server |
| 4 / 16 | clean | 0.006/0.097 | 1.30 | 0 | 4 clients / 1 server |

All six cases have zero engine errors. The 1.45-second eight-client faulted episode is close to the existing
limit and remains visible here. Full-gate network probes also pass: four faulted melee clients (seed 25, net
seed 2), two blocked-duel clients over two matches, and the loading-readiness stall. All audits agree. The
previously failing clean eight-client seed 183549434 passed after the hit-transition fix, before the tank merge;
it is supplementary evidence, not a substitute for the latest-tip matrix.

The complete performance grid and feature reports are separately audited: all 120/45 runs have the expected
three repetitions per case, one shared world hash, exact actor/brain/physics tick counts, complete raw evidence
and clean engine logs (`build/scale-actors-{grid,feature}-audit.log`).

Additional direct commands used for native/rendered proof (build before scenes):

```sh
make build
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/ragdoll_pose_tests.tscn
godot-mono --fixed-fps 60 --resolution 1280x720 --path . \
  res://scenes/dev/actor_detail_tests.tscn -- --capture-dir="$PWD/build/scale-actors-integrated-detail"
# Baseline and candidate, near and far, sixteen bots (visuals, never timings):
godot-mono --path <worktree> --fixed-fps 60 --disable-vsync --resolution 1280x720 \
  --write-movie <absolute-capture-directory>/movie.avi res://scenes/dev/scale_bench.tscn -- \
  --config=Debug --bots=16 --tanks=1 --seconds=4 --warmup=8 --camera=<near|far> \
  --frame-metrics=<absolute-capture-directory>/movie-metrics --capture-dir=<absolute-capture-directory>/movie-shots
```

The earlier far pair uses `--warmup=1`; the four stills use three measured seconds after one warm-up second. The
six final clips are trimmed/encoded with ffmpeg, validated with ffprobe assertions, and reviewed below. The
probe's numeric scratch constants are fixtures, not new gameplay values.

The native detail fixture observes actual final skeleton modifier callbacks, not the scheduler alone: near,
middle, far, hidden, restored near and owning view produce approximately 120/61/31/21/120/120 samples in 120
frames. The initial near frame can add one startup callback. It checks held skinned wrist contacts, unchanged
creep/reload movement and support/aim, live mesh bias changes, full-rate far recovery and hit/fade, then
restoration to distant detail. The current rendered fixture measures 6.6 mm maximum wrist error with zero proxy
penetration. Pure cadence checks also verify hysteresis, live changes, hitch handling, elapsed time, staggering
and zero scheduler allocations.

The physical transition fixture holds distant trees at 1 Hz and compares every physical bone with a full-rate
actor, for walking, a hit reaction, aiming, reload and melee before a throw. It asserts the distant trees really
were manual, both reactions were active, and each weapon action was in progress. Walking was initially wrong by
29.4 cm; consuming pending animation fixed it to about 1–2 micrometers/0.040 degrees. The additional hit case
failed by 65.0 cm/116.1 degrees before the catch-up/full-rate-hit fix, then measured 0 micrometers/0.056
degrees. Aiming measures 0 micrometers/0.040 degrees, reload 23 micrometers/0.056 degrees and melee 16
micrometers/0.040 degrees. Bounds remain 1 mm/0.1 degrees. No collider, hit, motion or network tolerance was
weakened.

The current common-dev visual refresh uses baseline `617a4f5` plus measurement overlays and candidate `a0d50c6`
(production actor code identical to measured `8413382`). Four 1280×720 near/far final screenshots are in
`build/scale-actors-integrated-16-{base,actors}-{near,far}/scale-final.png`. All four were inspected. Near body
detail and rifle contacts remain intact; distant silhouettes show no obvious geometry loss.

Four native Movie Maker captures use 60 fixed frames/s, 8 warm-up simulated seconds and 4 measured seconds. Trim
the final four seconds with ffmpeg; `movie.mp4`, `movie-info.json` and `movie-review-{0,1,2}.png` remain beside
each screenshot. Native Movie Maker produces 1920×1080 despite the 1280×720 window override. ffprobe assertions
verify every final clip is four seconds/240 video frames/60 FPS. Three frames per clip at 0.5/2/3.5 seconds were
inspected. The near clips retain body detail and hand/rifle contact. The late candidate far camera is occluded
by foreground trees, so it cannot establish detailed character fidelity. An additional earlier far pair
(1-second warm-up, 4-second measurement) in `build/scale-actors-integrated-16-{base,actors}-early-far/` shows
the actor in the open; its six sampled frames were inspected too, with no obvious silhouette loss. All six clips
validate the same frame count/duration. Fight/camera trajectories diverge, so these are not pixel-identical
comparisons or frame-time evidence. Native callback assertions establish cadence; three sampled movie frames do
not prove continuous motion.

The current rendered `actor_detail_tests` captures are in `build/scale-actors-integrated-detail/`; all six were
inspected. Near and restored-near detail match, 50/110 m actors are tiny silhouettes, the hidden phase looks
away, and the owning-view phase keeps full native detail even under a distant observer camera. Earlier
animation-review sprint/reload/melee captures, range motion/grip runs, and pre-tank near/far clips remain
supplementary evidence. All large intermediate AVIs are removed after successful encoding.

Failure evidence is retained. Early verification caught and fixed teardown of an uninitialized skeleton, the
held-pose physical transitions above, and clean eight-client/lobby moving-vent disagreements before the hit fix.
The exact failing clean eight-client seed subsequently passed, as did the full final matrix. Two earlier
full-gate attempts were superseded; the legal final Ring Out tie failure came from a harness assumption that
winners survived. The existing pure-tested legal tie audit from dev is retained; game rules did not change. The
first partial performance grid was stopped after finding the hit regression and is excluded. A network matrix
accidentally started before its build completed is invalid startup evidence and is excluded.

## Compatibility and limits

The eight new shared tuners require a minor release. Actor changes do not change messages or `Protocol.Version`
(3), suburb construction, tank rules, scoring, drops, movement, collision or logical hit decisions. Bot,
navigation, tank and physics settings belong to their other packages. This branch includes the landed tank
package through a dev merge but adds no tank/effect optimization of its own. The later lobby-supply dev merge
adds supply fields to SetMode/LobbyState and deliberately retains protocol 3 for the unreleased 0.4.0 minor;
that inherited codec change is separate from the actor patch.

Land the brain package, merge its dev revision and repeat the paired grid on that common baseline before
claiming sixteen production bots are playable. These actor changes do not remove the dominant navigation work.
The focused zero-bot control does not reproduce that regression; remeasure the eight-bot rendered case on a
quiet machine. The lower geometry/callback cost is established, but this shared-machine grid does not establish
a uniform frame-rate gain.

Near actors deliberately retain full animation, IK and imported mesh detail. A close crowd can still cost more
than a dispersed crowd. Distant limbs visibly hold between 30/15/10 Hz samples; distances/rates and the mesh
multiplier are reversible live tuners. Recovery/hit transitions rely on Godot's native one-shot
request/active/internal-active and manual skeleton APIs. Keep the native cadence, contact, all-weapon
physical-transition and real device-input gates when upgrading the engine. Exact native solver/automatic
animation and independent GPU pass times, quiet-machine capacity, and exported-engine performance remain
unverified.
