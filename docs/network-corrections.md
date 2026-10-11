# Tank correction investigation

Follow-up to [the soak assertion](network-soak.md), on `wp/net-assert`, after merging dev `a50b9e5`.
Gameplay rules and tuner defaults remain unchanged.

## Resting bodies

The loading relay filter dropped body publications before countdown. A tank could sleep during construction,
publish its only reliable rest while another client was still building, and never publish again. Initial map
bodies also registered asleep without requesting any publication. The server now accepts owner/epoch/sequence
validated motion and rests during loading; the existing match inbox buffers them until bodies exist. An initially
sleeping owner publishes once on registration. No message shape changed.

Observers discarded the rest target once they reached it. Later local collision/solver drift therefore persisted
without any new owner packet: the owner was still asleep. Body sync now retains the target, restoring displaced
copies without modifying an already sleeping copy at the correct pose. A newer owner sequence, epoch or predicted
local claim still replaces/clears it. Gather uses the same ordered stream; its completion and body rest remain
reliable. Blast impulses continue to execute locally, with confirmed owner publications determining lasting pose.

In `/tmp/nas-rest-trace-baseline`, Bot1 owned map tank `10000023`, epoch 1. It published moving sequences 1–7,
then rest sequence 8 at monotonic 85580.923798 during loading. The other peers received none of these states.
Their final intact/resting positions differed by up to 0.481 m. For drop `2000000d`, Bot1 owned epoch 1 and
published rest sequence 122 at 85658.835081. Every receiver accepted and applied it, then drifted after discarding
that target; final resting disagreement reached 0.133 m. Neither case required an ownership handoff or gather.

Three unit regressions failed before the fix and passed afterward: initial sleeping publication, loading relay
(including rejection of nonowners/stale sequences), and an observer drifting after applying its rest. Commands:
`make build`, then `godot-mono --headless --path . res://scenes/dev/unit_tests.tscn`. The merged baseline recorded
198 tests/3 failures in `/tmp/nas-rest-before-all.log`; the fix recorded 198 tests/0 failures in
`/tmp/nas-rest-after.log`. Both builds had zero warnings/errors.

`--tank-trace` is optional dev logging. It reports owner, epoch, sequence, pending claim, accepted state/rest,
correction target, sleep, position/velocity and gathering on body-sync transitions. Publish traces precede the
caller's sequence increment, so a trace showing sequence N publishes N+1. Ordinary matches have no listener.
