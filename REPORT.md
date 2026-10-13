# A7: the mode runner (Tank Bank extracted into pure rules)

Branch `claude/a7-modes-5o8zjw`, based on `origin/dev` at `66fd1e8`.

## 1. Summary by commit

- **`14e2eb0` Pure mode rules; the Lab duel runs on them.**
  - `src/Modes`:
    - `IModeRules` (`Initialize`, `Handle(AcceptedEvent, ModeClock)`, `Snapshot`, `Permits`), the phase and timer enums, `RoundInfo`, accepted events, commands and standings.
    - `TankBankRules`, which owns the bank, spill tickets, gates, standings, statistics and the audit.
    - `SandboxRules`, the pure `ModePhaseMachine`, plain `ModeHud` data with Tank Bank and Sandbox presenters, and `ModeDefinition`/`ModeRegistry` (Sandbox id 1, Tank Bank id 2).
  - `BankingBot` now implements `IBotGoalPolicy`, and `IBankingBotWorld` is renamed `IBotWorld`.
  - `LocalBank` now runs `TankBankRules`.
  - Lab scenarios name a mode. The duel plays Tank Bank and the other starters play Sandbox, taking their supply and ammo policy from it.
- **`a6bde8c` Tank Bank on the runner and the fixed mode envelope.**
  - Adds `ModeEvent` (15), `ModeCommand` (51) and `ModeSnapshot` (52). All three share one header: RunId, RoundId, WorldRevision, ModeId, SchemaVersion, ActorId/LifeId, EventId, Kind, Length, then the payload.
  - Typed per-kind codecs check direction and bounds. Unknown schemas and kinds fail by name.
  - Deduplication:
    - Client events reuse the actor's action sequence, so the server checks them exactly as before.
    - Commands carry monotonic ids, and the client applies only newer ones.
  - A bounded causal gate holds dependent events.
  - `Score`, `Drop` and `DropRefused` are retired. Their numbers (40, 71, 47) stay reserved, so no other message renumbers.
  - `LobbyServer` now runs on `ModeRunner`: the phase machine, rules-owned gates, audit, standings and statistics, and `TankBankWire` for ticket → ledger → commit.
  - `MatchStart` appends the mode id and schema version.
  - `MatchHud` and `ResultsScreen` render the presenter's data. The bank caption moved into `TankBankHud`.
  - Existing tests are ported, and new `ModeTests` and `ModeEnvelopeTests` suites are added.
- **`e1b82ae` The `mode_test` harness.** `scenes/dev/mode_test.tscn` runs the scripts in `tools/dev/modes/*.mode` against the pure rules. It covers:
  - scoring and chain credit;
  - concurrent, idempotent, refused, expired and departed tickets;
  - retuning and repeated matches;
  - the sandbox.

  It is wired into `make test` (test-core).
- **`3baa50e` Docs.**
  - New `docs/modes.md`: the architecture, the envelope layout and Tank Bank's schema, the ticket flow, phases, HUD, bots, where health, teams and segments slot in, and verification.
  - Updated `docs/multiplayer.md`, `docs/lab.md`, `docs/code-quality.md` and the README.

### Where I departed from the plan, and why

- **Ticket timing.** The plan has the server issue the ticket before the victim's owner picks placements. That adds a round trip before a spill appears on the victim's own screen, which breaks "predicts immediate feedback" and identical behavior. So the ticket identity belongs to the owner (actor, life, action id) and the placements travel with the request.
  - The rules model reserve, register and commit as separate inputs, and the tests interleave them (concurrent tickets, expiry, departure).
  - On the server those three steps run in one handler call, as before.
- **Hit grace stays with the victim's owner.** Moving it to the server would change timing. Its pure decision lives in `TankBankRules.TryOpenGrace`, which `Match` and `LocalBank` share.
- **Repeated rounds are server-only.** The server runner supports `NextRound` that keeps the world. Clients cannot rebuild a world mid-run yet, so a `NextRound(RebuildWorld)` request ends the match and logs why. That path belongs to a future segment world source.
- **Bots.** The banking brain is wired in as Tank Bank's goal policy. Splitting goal selection out of `BankingBot` itself is left as follow-up, because it would need its own bot-behavior proof.
- **Smaller details:**
  - Round start sends one bank snapshot instead of restating each nonzero starting score; the client effect is identical.
  - The spill wire bound is 64 bodies, above the 20 maximum of the drop-count tuners.
  - The envelope header puts RunId first and then follows the plan's field order.

## 2. Verification

| Command | Result |
| --- | --- |
| `make setup` | pass |
| `make -j4 check` (final tree) | **exit 0**: lint clean with zero build warnings, 298 unit tests with 0 failures, `[mode] 4 scripts, 103 steps, 0 failures`. Also passes the `game_test` clear and gather scenarios, `check_lab.py` (Lab lifecycle, bank rules, duel), every `menu_test` scenario, the generator checks and the world hashes. See the log notes below the table. |
| `make test-world` (inside check) | unchanged world hashes for all formations |
| unit suites alone, before the final check | 298/0 |
| `mode_test -- --script=sandbox` with one expectation deliberately broken | fails with exit 1, naming the file and line |
| `check_world_tests.py` after the Makefile edit | OK |
| Soaks in a separate `dev` worktree and on the branch, same seeds (`net_bots.sh`, `--melee-start`) | every run exits 0 with zero logged engine or protocol errors; score audits agree on every bot (the table below) |
| Loading-readiness (`-n 2 -l 20 -- --scenario=body-stream --tank-check-ready-stall=Bot2`) on the branch | passed both times |

Two notes on the `make -j4 check` log:

- One `[net] bad message` line is the intentionally truncated score in the lifecycle test.
- One Jolt "job system exceeded" warning came from a physics fixture that has nothing to do with modes, under `-j4` load.

Soak comparison. "f" runs add 120 ms RTT, 30 ms jitter and 3% loss; "c" runs are clean.

| Run | dev audit (initial / map / drops / spills = banked) | branch audit |
| --- | --- | --- |
| f2: 2 bots, seed 24, net-seed 1 | 0 / 12 / 14 / 15 = 11 ok | 0 / 12 / 10 / 12 = 10 ok |
| f4: 4 bots, seed 25, net-seed 2 | 0 / 31 / 37 / 52 = 16 ok | 0 / 26 / 28 / 47 = 7 ok |
| c3: 3 bots, seed 26 | 0 / 28 / 5 / 18 = 15 ok | 0 / 28 / 10 / 22 = 16 ok |
| c4: 4 bots, seed 27 | 0 / 36 / 34 / 36 = 34 ok | 0 / 17 / 14 / 21 = 10 ok |
| duel, 2 matches, blocked lane, faulted | 16 / 0 / 1 / 7 = 10 per match, standings 1=9 2=1 | **identical** |
| drop-refusal, faulted | 16 / 0 / 0 / 1 = 15, standings 8/7 | **identical** |
| gather, faulted | 16 / 0 / 0 / 2 = 14, standings 8/6 | **identical** |
| melee duel, faulted | 10 / 0 / 0 / 5 = 5, standings 0/5 | **identical** |
| two rounds on one connection (seed 28), three runs | 9-0 then 10-0; 9-0 then 10-0; 0-0 then 18-0 | 10-0 then 0-0; 10-0 then 0-0; 8-0 then 10-8 |

- The free-play soaks are real-time and multi-process, so their totals vary from run to run on both trees. What must agree is the audit and every bot's final scores, and they do.
- The scripted scenarios reproduce `dev` exactly.
- I investigated the 0–0 second match on the branch:
  - The server logged no refusals.
  - The bots roamed without engaging a tank.
  - `dev` itself produced a 0–0 match on rerun, and the branch's next rerun scored 10 and 8 in match 2.

  This is bot variance, not a regression.

HUD before and after, reviewed by eye and pixel-compared:

- **In-match HUD plus results** (`menu_test --scenario=gamepad`, rendered at 1280×720, shot `11_custom_character`):
  - The top HUD region (clock "0:00", "0", "TANKS BANKED") is pixel-identical.
  - The results panel ("YOU WIN", "Tanks banked", rank, name, score) and the ammo counter look the same. Their pixel differences sit only where the suburb, which has a different random seed, shows through the translucent panels.
- **Results with eight standings**, including a departed player (`capacity_ui_tests --players=8`, rendered): `results_8.png` is **byte-identical** to `dev`.
- **Not possible on this VM:**
  - Rendered network bot captures (the duel with `-w 1`, and periodic shots) fail on `dev` too. The rendered client hits an ALSA audio error, and the duel's timing fails on CPU rendering; the periodic shot was blank and timed out.
  - So there is no rendered in-match frame with a nonzero bank. The singular caption ("TANK BANKED") and the counter values are asserted in `ModeTests` instead.

## 3. Risks, open questions for the owner, follow-ups

- **Risk: hotspots touched.** `LobbyServer`'s phase loop was rewritten onto the phase machine, and spills, scores and snapshots moved to the envelope.
  - The ordering is preserved: a detonation's score follows its Authority, and an accepted spill's debit precedes its echo.
  - Countdown and results lengths are still read live from the match tuning.
  - The ported server, participant, lifecycle and authority suites all pass, and so do the scripted network scenarios.
- **Open question for the owner:** whether the plan's server-issued tickets are wanted despite the extra round trip. I kept the existing latency; see "Where I departed from the plan".
- **Follow-ups:**
  - Client support for multi-round runs and world rebuilds (segment world source).
  - Splitting goal selection out of `BankingBot`.
  - A11 modes register a definition, rules, codecs and an `IModeWire`.
  - Health, teams and segments have documented slots in `docs/modes.md`.
- **Soaks for the lead to rerun at 8 and 16 players on real hardware**, each on both `dev` and this branch:
  - `tools/dev/net_bots.sh -n 8 -w 0 -l 90 -s '--tune.MaxPlayers=8 --match-seed=24' -- --melee-start --net-seed=1 --net-lag=120 --net-jitter=30 --net-loss=3`
  - The same with `-n 16`, `--tune.MaxPlayers=16` and `--match-seed=25 --net-seed=2`.
  - Clean versions of both with `-t clean` and no network faults (seeds 26 and 27).
  - `tools/dev/capacity_acceptance.sh -n 16 -r 3 -w 1`.
  - `-n 8 ... -- --matches=2`, for repeated matches with a full roster.

## 4. Compatibility

- **Shared tuners:** none added or changed, and no tuner values changed. `Tuning.Bot*` and Lab dials are untouched.
- **Messages:**
  - New: `ModeEvent`, `ModeCommand`, `ModeSnapshot`.
  - Retired: `Score`, `Drop`, `DropRefused` (their numbers are reserved).
  - `MatchStart` gains the mode id and schema version.
  - Hello, Welcome and Rejected are unchanged.
- **World:** suburb construction, formations and world hashes are unchanged (`make test-world` passes).
- **`Protocol.Version` stays 2**, under the unreleased-cycle ruling.
- **This needs a minor release:** message shapes changed for everyone in a match.
