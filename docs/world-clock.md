# World clock dispatch

`WorldClock` advances simulation time; network authority keeps its own clock. Dispatch snapshots eligible timers
in insertion order, including canceled entries for cleanup. Each timer is removed and canceled immediately before
its callback, so callbacks see later live timers as pending and can cancel them. Zero-delay work scheduled during
a callback waits for another advance (which may be nested). Nested advances can consume timers in an outer
snapshot; their cleared callbacks prevent duplicate delivery. Disposal suppresses remaining callbacks. A thrown
callback propagates its original exception and leaves unvisited timers pending for a later advance.

`ClockDispatchTests` pins these behaviors with callback transcripts recorded against the immediate parent
(`1019775`). Run after `make build`:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ClockDispatchTests
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ClockDispatchTests --clock-bench
```

The optional benchmark warms 2,000 ticks, then measures 10,000 simulation ticks with 256 zero-delay timers each.
It reports managed bytes, mean and p95/p99 for dispatch plus pending count, and for the whole clock tick including
scheduling. Scheduling allocations are intentionally retained. These are controlled clock costs, not Jolt or
whole-scene frame times. Use identical instrumentation and alternate parent/candidate runs at least three times.
The release chain, Short Fuse and controlled Dissolve traces remain in `make test-equivalence`.
