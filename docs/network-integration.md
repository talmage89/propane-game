# Independent network integration review

Integration starts at `4a03c3d`, merging dev `d2aa9c9` into `wp/net-assert`.
The sole textual conflict was NetTest's loading-stall fixture beside A4's headless
actor assembly. Both remain. Match's five small integrations retain their behavior
on A4: nonpredictive gather leases, receipt-time authority targets, and per-body
checkpoint lane batching. No session, actor, view or logical-aim redesign was needed.

## Disconnect latency

The branch's unconditional 20/30 s ENet ACK timeouts regressed live-match departure.
Dev used a 3/8 s client timeout and the server's native default; the loading fix had
extended both endpoints permanently. A crashed player's actor and ownership would
therefore linger through a substantial part of a short round.

`IPacketTransport.SetLoading` now extends just the constructing connection before
MatchStart is sent and before the client's construction callback runs. Loaded
restores 3/8 s at both ends, even if it arrives after the loading deadline and Go.
Leaving construction restores it too. The fault decorator forwards the policy.
Only construction can retain the 20/30 s parameters; explicit disconnect remains
immediate. [Godot's timeout contract](https://docs.godotengine.org/en/stable/classes/class_enetpacketpeer.html#class-enetpacketpeer-method-set-timeout)
describes ACK deadlines checked on ENet's retry schedule, so the maximum parameter
is not an exact stopwatch guarantee.

The unit regression exercises the production client/server through decorators,
including a late Loaded after Go and leaving without Loaded. The completed suite
passed 226 tests with zero failures; make build had zero warnings/errors.

The native regression tool stops a separate process group during play (SIGSTOP),
sending neither Leave nor disconnect. It fails if detection exceeds 10 s. Logs
remain under build. Historical copies were built inside this worktree with
`git archive <revision>`, the same tuning.tres, and `make setup`.

| Revision | Silent endpoint | Detection |
| --- | --- | ---: |
| dev `d2aa9c9` | client | 6.311 s |
| integrated branch before fix `4a03c3d` | client | 25.208 s |
| loading-specific grace | client | 3.972 s |
| loading-specific grace | server | 8.350 s |

Commands:

```sh
TMPDIR="$PWD/build/vna-scratch" make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn
python3 -B tools/dev/check_liveness.py --project="$PWD/build/vna-dev-baseline" --output="$PWD/build/vna-liveness-dev-client" --maximum=35
python3 -B tools/dev/check_liveness.py --project="$PWD/build/vna-branch-baseline" --output="$PWD/build/vna-liveness-branch-client" --maximum=35
python3 -B tools/dev/check_liveness.py --output="$PWD/build/vna-liveness-fixed-client"
python3 -B tools/dev/check_liveness.py --output="$PWD/build/vna-liveness-fixed-server" --silent=server
tools/dev/net_bots.sh -n 2 -l 35 -s '--start-score=5' -o "$PWD/build/vna-load-stall" -- --scenario=melee --tank-check-load-stall=Bot2
```

The nine-second construction stall survived: 899 comparison grids, zero unobserved
pair grids, peak rest/moving drift 0.010/0.491 m, final 0/0 m, and both score audits
agreed (10 initial minus 5 spilled equals 5 banked). The bound and tuner defaults
were unchanged. The native probe's first attempt used melee assertions while
deliberately stopping its victim; that invalidated its unrelated gameplay fixture.
The final probe uses ordinary simple bots. Its server-silence condition observes
either client, since the first timeout can cause that client to quit before the
other times out.
