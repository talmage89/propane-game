# Loading grace: why a loading player is dropped on this VM

Work branch: `claude/loading-grace-iqlm37`. The task named `claude/loading-grace`, but this session's harness
assigns the `-iqlm37` suffix. Report branch: `claude/loading-grace-iqlm37-report`. Base: `dev` at `0dbae24`.

## Cause (with evidence)

**It is not slow first frames.** The cause is ENet's retransmission backoff, which survives the end of the grace.

1. **Instrumentation.** I added timestamped wall-clock traces to the server, Bot1 and Bot2 (`loading-trace.patch`
   on this branch is the diagnostic tree). They log each `SetLoading` and timeout change, `MatchStart` handling,
   `AddChild` and the stall, `Loaded` sent and received, ENet poll gaps over 250 ms, the first 40 frame deltas, and
   `PeerDisconnected` on each side.
2. **The grace does not end early.** A typical failure (2026-10-13 13:58:55–13:59:24):
   - Bot2 constructs for 2.1 s, then sleeps 9 s, its first poll gap being 11.3 s.
   - Bot2 sends `Loaded` at 06.751, and the server receives it at 06.754 and restores the 3/8 s timeout.
   - **The server drops Bot2 at 10.830, 4 s after `Loaded`.** Bot2's own ENet declares the server lost only at
     24.994, after the server has stopped answering. So the server's ENet declared the timeout.

   In all 8 baseline failures, the server drop came 0.02–4.5 s after it received Bot2's `Loaded`.
3. **Frame times are fine.** After the stall, Bot2's frames take about 17 ms (bots are headless). One windowed
   llvmpipe bot (`-w 1`) shows the heavy first render (6.6 s) *before* `Loaded`, because `Loaded` already waits two
   frames (`eb9bbe7`). Afterward it runs at a steady 1 fps that never settles, and the server still dropped it 2 s
   after `Loaded`, by the same mechanism.
4. **Packet loss during the stall.** The server streams bodies to Bot2 throughout its 9 s sleep, and Bot2's 208 KB
   UDP receive buffer (`net.core.rmem_default`) overflows. Measured per run as the `/proc/net/snmp` `RcvbufErrors`
   delta:
   - Baseline alone: 158–326 drops in every failing run. In passing runs, 18–89 in 11 of 12, and 201 in one.
   - Beside the generators: 179–403 in failing runs, 24–227 in passing runs.
5. **ENet mechanism.** In Godot 4.7.2's bundled ENet 1.3.18 (`thirdparty/enet/protocol.c`,
   `enet_protocol_check_timeouts`; the file is identical to 4.5-stable's):
   - Each unacknowledged send doubles `roundTripTimeout`, with no cap.
   - A peer is declared lost when an expired command's time since its last send is ≥ `timeoutMaximum`, or
     ≥ `timeoutMinimum` once it has had ≥ 6 attempts (`1 << (sendAttempts-1) >= timeoutLimit` = 32).
   - Any acknowledgment resets `earliestTimeout`.

   So commands lost in the overflow keep backing off through the stall, to intervals of seconds. After Bot2
   resumes, the next retry of such a command comes due after `Loaded`. By then the short timeout applies, and an
   interval ≥ 3 s after ≥ 6 attempts disconnects the peer instead of resending. A run passed when no command with
   that long an interval was left to retry after `Loaded`. That explains the intermittency and why more load makes
   it worse.

Why it never failed on the lead's machine is not measured here. A larger receive buffer or less overflow is
plausible, since faster construction leaves less queued.

## Fix

The fix is in `NetTransport.cs` only (the ENet adapter), plus a unit test and docs. No message, protocol, tuner or
`LobbyServer`/`Match` changes.

- `LoadingGrace` records when loading began on each connection. On `SetLoading(false)` (`Loaded` or leaving), it
  keeps the 20/30 s timeouts for a hold equal to the time spent loading plus `RetryMargin` (1 s). `Poll` then
  restores 3/8 s.
- **Why this bound:** a command first sent at s₀ ≥ load start, last retried before `Loaded` (time L), has a doubled
  interval of at most (L − s₀) + RTO₀. Its next retry therefore comes by L + (L − start) + RTO₀. The 1 s margin
  covers RTO₀, which is the round trip plus four deviations.
- A new `MatchStart` during a hold cancels the pending release. Retirement forgets the connection.
- **Why not a frame-time gate:** the evidence rules it out. Bot2's frames were already fast when it was dropped, and
  the windowed bot's heavy first frame already falls before `Loaded`. Its later 1 fps never settles, so a settle gate
  would never release the grace, or would need its own timeout.
- **Cost:** a player who vanishes within the first (load time + 1 s) of play is detected within that hold plus the
  20/30 s grace instead of 3–8 s. After the hold, detection is unchanged.

## 1. Summary by commit

- `e616984` **Hold the loading grace after Loaded for as long as the load took.** Adds `LoadingGrace` and its use
  in `NetTransport.SetLoading`, `Poll` and `Retire`. Adds the unit test `transport/native loading grace outlasts
  readiness by the time spent loading`, which covers the hold length, single release, repeated `Loaded`,
  cancellation by a new load and forgetting. Updates `docs/multiplayer.md` (Transport contract).
- `7e72a12` **Record the loading-readiness cause and before/after pass counts.** Updates `docs/test-speed.md`
  ("Intermittent loading-readiness failure"): corrects the old "during the stall" wording, adds the evidence, and
  adds a before/after table.

## 2. Verification

All runs were on this 4-CPU VM. The "before" build is `dev` plus print-only traces; the "after" build adds the fix.
Command: `tools/dev/net_bots.sh -n 2 -l 20 -- --scenario=body-stream --tank-check-ready-stall=Bot2`.

| Loading-readiness match | Before | After |
| --- | ---: | ---: |
| Alone | **12/20** (8 failures, all the server dropping Bot2 0.02–4.5 s after `Loaded`) | **20/20** |
| Beside `make -o build -j3 test-full-generator-1 test-full-generator-2 test-full-generator-3`, looped | **4/10** | **10/10** |

`-o build` stops make rebuilding the assembly while the "before" runs use the pre-fix build. The shards ran
throughout both sets. After the fix, runs with 244–287 buffer drops passed; before it, such runs failed. The traces
show the hold working. In one probe:
- Bot1 loaded in 2.2 s, and its short timeout returned 3.2 s after `Loaded`.
- Bot2 loaded in 11.0 s, and its short timeout returned 12.0 s after `Loaded`.

The fixture is unchanged and still meaningful: it fails 8/20 and 6/10 without the fix.

| Command | Result |
| --- | --- |
| `make build` (after the fix) | 0 warnings, 0 errors |
| `make check` (final code, traces removed) | Exit 0 in 9 m 35 s; build 0 warnings; `[unit] 272 tests, 0 failures`; all suites pass. The one runtime `WARNING: [net] bad message` line comes from the unchanged LifecycleTests malformed-message probe. |
| `python3 -B tools/dev/check_liveness.py --output=$PWD/build/liveness-client` | `PASS native silent client: detected in 4.697s (bound 10s)`: a client SIGSTOPped mid-match is dropped within the established timeout |
| `python3 -B tools/dev/check_liveness.py --output=$PWD/build/liveness-server --silent=server` | `PASS native silent server: detected in 3.132s (bound 10s)` |
| `net_bots.sh -n 4 -w 0 -l 90 -s '--match-seed=25' -- --melee-start --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3` (faulted) | Exit 0; `PASS tanks (faults)`, 0 disagreements, longest episode 0.70 s; all 4 bots agree; 0 `ERROR` lines in all 5 logs |
| `net_bots.sh -n 4 -w 0 -l 90 -t clean -s '--match-seed=25' -- --melee-start` (clean) | Exit 0; `PASS tanks (clean)`, 0 disagreements, longest episode 0.30 s; all 4 bots agree; 0 `ERROR` lines |
| `make -j4 test-full` (final head `7e72a12`) | Exit 0 in 18 m 36 s, including the rifle soak, the duel lane and the loading-readiness match (all `PASS tanks`, all bots agree) |

## 3. Risks, open questions for the owner, follow-ups

- **Delayed detection right after loading.** For (load time + 1 s) after `Loaded`, a vanished player is detected
  on the 20/30 s grace instead of 3/8 s. That is about 3–5 s for a normal load and up to about 20 s for a very slow
  one (the server's own 20 s `LoadTimeout` does not bound this hold). Explicit disconnects stay immediate. Mid-match
  liveness is unchanged (the liveness probe passes). A shorter alternative would need ENet internals that Godot does
  not expose, such as outstanding reliable data or the last acknowledgment time.
- **Limit of the grace itself (pre-existing).** A loader stalled ≥ 20 s can still be dropped during the grace: the
  ≥ 6-attempt rule at the 20 s minimum. The windowed run with two llvmpipe bots showed a 42 s first frame, beyond
  even the 30 s maximum. That is CPU starvation on this VM, not a real-hardware case, but it bounds what any grace can
  absorb.
- **Steady slow clients.** One windowed llvmpipe bot here runs at about 1 fps throughout, with 0.8–1.9 s poll gaps.
  Whether such a client stays connected under 3/8 s after the hold was not measured beyond these runs. It is not a
  loading problem.
- **`RetryMargin` is a 1 s constant,** not a `Tuning` tuner, matching the other transport timeouts (the task asked
  to keep them constants). On very high-latency links (RTO₀ > 1 s) the bound could be short by the difference.
- **Possible follow-up:** a larger socket receive buffer would reduce the loss itself, but Godot's ENet peer does not
  expose it.
- **Merge with A7:** the change does not touch `LobbyServer.cs` or `Match.cs`, so it should not conflict with the
  mode runner's rewrite.

## 4. Compatibility

- **No message changes.** `Protocol.Version` stays 2.
- **No shared tuners or gameplay values change.**
- **The change is local:** it alters only when each endpoint's own ENet timeout returns to 3/8 s. Mixed versions
  interoperate, since an old peer keeps the old behavior.
- **A patch release is enough.** It needs no minor release.
