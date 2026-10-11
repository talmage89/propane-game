# World services

`WorldClock` measures simulation seconds. Its owner advances it once per physics tick with Godot's supplied
simulation delta; pause does not advance it, and hitstop slows it along with physics. Scheduled callbacks run once
on that clock and can be canceled individually. Disposing the clock cancels all pending work, including callbacks
already due in the same tick.

Tank drop shields and chain fuses use this clock, so fixed-FPS tests and hitstop cannot shorten their gameplay
durations through wall-clock drift.

Network timestamps, interpolation, transport fault delays and server-owned deadlines continue to use
`MonotonicClock` and the server clock. They must not be compared with simulation seconds.
