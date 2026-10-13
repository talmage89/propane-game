# Modes

How a game mode plugs into Propane: pure rules, a definition that selects everything mode-specific, a runner that
supplies the clock and transport, and one fixed network envelope. Tank Bank is the first mode on this framework and
plays exactly as before; its rules are in [multiplayer.md](multiplayer.md#tank-bank). Sandbox is the second.

| Path | Contents |
| --- | --- |
| `src/Modes/ModeRules.cs` | `IModeRules`, phases, the mode clock, accepted events, commands, standings, `SandboxRules`. |
| `src/Modes/TankBankRules.cs` | Tank Bank's bank, spill tickets, gates, standings, statistics and audit; victim-owner helpers. |
| `src/Modes/ModePhases.cs` | The pure phase machine the server's runner drives. |
| `src/Modes/ModeHud.cs` | Plain HUD data and the Tank Bank and Sandbox presenters. |
| `src/Modes/ModeDefinition.cs` | `ModeDefinition`, supply policy and `ModeRegistry` (Sandbox and Tank Bank). |
| `src/Net/ModeEnvelope.cs` | The envelope record, schema registry, Tank Bank codecs, the causal gate and command order. |
| `src/Net/ModeRunner.cs` | One run's mode on the server, and Tank Bank's wire adapter. |
| `src/Bots/IBotWorld.cs` | Bot observations and the per-mode goal policy interface. |

## Rules are pure

`IModeRules` has four members: `Initialize(ModeSetup)`, `Handle(AcceptedEvent, ModeClock) -> commands`,
`Snapshot()` and `Permits(ModeGate, ModeClock)`. `ModeSetup` holds the roster (`ModeActor`: id, name, presence,
starting score), the world definition, an immutable copy of the tuner values the rules read (`ModeTuners`, by name)
and a deterministic seed. Rules hold no nodes, make no transport calls, read no clock and touch no UI.
Everything else is an explicit input: phase changes and the clock, joins and leaves, tuning changes
(`TunersChanged`) and physical facts the server has already accepted (`TankDetonated`). The same rules
instance type serves a server match (`ModeRunner`) and an offline Lab duel (`LocalBank`), and runs headless in the
unit suites and `mode_test`.

`ModeDefinition` selects the rules, the world source (the transmitted `SuburbPlan` or the void), the supply and
ammo policy (`ModeSupply`: suburb settings for a player count, limited ammo, ammo cans), the round timer, the HUD
presenter, the bot goal policy, the tuners the rules copy and the envelope schema version. `ModeRegistry` ships:

| Mode | Rules | World | Supply | Timer | HUD | Bots |
| --- | --- | --- | --- | --- | --- | --- |
| Sandbox (1) | no score, no spills, nothing gated | void | single-player suburb, unlimited ammo, no cans | none | tank arrows | none |
| Tank Bank (2) | `TankBankRules` | suburb plan | `SuburbPlans.Match`, limited ammo, cans | countdown | bank, clock, arrows | `BankingBot` |

Multiplayer lobbies play Tank Bank; `MatchStart` appends the mode id and schema version, and a client that does not
know the pair fails decoding by name. Lab scenarios name a mode: the banking duel plays Tank Bank and the other
starters play Sandbox. A Lab scenario's environment choice replaces the mode's world source, so a prototype can try
a mode in either the void or a suburb.

## Tank Bank's rules

`TankBankRules` owns banks, spill counts, phase gates, standings, statistics and the conservation audit. The ledger
(`TankLedger`) still owns existence, tombstones, ownership and atomic entity registration. Hit grace and drop
placement stay with the victim's owner, as distributed authority requires; their pure decisions are the static
`TryOpenGrace`, `SpillCount` and `Limit`, used by `Match` and `LocalBank` alike.

**Spills are tickets.** A ticket is the victim actor, its life and the owner's actor-local event id, so repeats
are idempotent. The owner chooses the drop transforms, predicts them at once and sends the placements as one Spill
event. The rules reserve available bank for the ticket (`SpillReserved`), the ledger registers the complete batch,
and only then does the debit commit (`ScoreChanged`, `SpillCommitted`), which everyone, the owner included,
receives. A refusal by the rules (play closed, more than the tuner or the unreserved bank), by the ledger, a
departure or the round's end cancels the ticket (`SpillCanceled`); the owner removes its predicted ids and
releases its local reservation. Reservations are what make concurrent spills safe: two open tickets can never
reserve more than the bank holds.

The adopted plan described the server issuing a ticket before the owner chose placements. That would add a
round trip before a spill appears on the victim's own screen, contradicting "predicts immediate feedback" and the
behavior the package had to keep, so the ticket's identity is the owner's and the placements travel with the
request. On the server the reserve, register and commit steps run in one handler call; the rules model them as
separate inputs so asynchronous runners (and the tests) can interleave them.

Gates: `World` (claims, punctures, explosions, gathers, melee, spills) is open while a round runs and its server
deadline has not passed; `Pickup` follows the round alone, exactly as the server always checked pickups.
`Snapshot()` returns standings ranked by score with ties in roster order and departed actors kept, plus report lines
the server logs unchanged (`bank audit ...`, `standings match ...`, the summary and the per-actor statistics).
At match end the server also compares the rules' detonation counts with the ledger's and logs any disagreement.

## Phases

`Lobby → Loading → Countdown → Round → RoundEnd → [next round | MatchEnd → Results] → Lobby`. Lobby membership and
the roster persist across matches; Loading is infrastructure. Each round has an id, a world revision, a timer kind
(countdown, count-up or none) and server-time parameters (`RoundInfo`); the first round of a run is round 1 in
world revision 1. `ModePhaseMachine` advances one timed step per server tick and reads countdown and results
lengths live from the match's tuning, as before. A countdown round ends on its deadline; count-up and untimed rounds
end when the rules say so. At RoundEnd the rules decide: `EndMatch`, or `NextRound` keeping the world (straight to
Countdown with the next round id) or rebuilding it (Loading again with the next world revision). Clients cannot
rebuild a world inside a run yet, so the server answers a rebuild request by ending the match and logging why; a
segment world source is where that lands. Life ids (A6) distinguish respawns; a new match advances every survivor's
life.

On the wire the lobby phase byte is unchanged: Round and RoundEnd read as Playing, MatchEnd and Results as Results.
`Countdown`, `MatchGo`, `Clock` and `MatchEnd` remain the runner's own messages.

## The envelope

Three fixed outer messages carry everything mode-specific: `ModeEvent` (client to server), `ModeCommand` and
`ModeSnapshot` (server to client). After the type byte, each has:

| Field | Type | Meaning |
| --- | --- | --- |
| RunId | Int32 | The match id, as on every match message. |
| RoundId | UInt32 | The round the message belongs to (nonzero). |
| WorldRevision | UInt32 | The world it was sent against (nonzero). |
| ModeId | UInt8 | Sandbox 1, Tank Bank 2. |
| SchemaVersion | UInt16 | The mode's payload schema. |
| ActorId, LifeId | Int32, UInt32 | An event's reporter, or a command's subject (both zero when it has none). |
| EventId | UInt32 | An event's actor action sequence, or a command's monotonic id (nonzero). |
| Kind | UInt16 | The registered payload kind, numbered per direction. |
| Length | UInt16 | Payload bytes that follow, bounded per kind. |

- **Sequencing.** A client event's EventId is its actor's action sequence, shared with that actor's other reliable
  actions, so the server's existing per-actor check deduplicates and orders it (one value per actor). Commands and
  snapshots carry the run's monotonic ids; a client applies only newer ids (`ModeCommandOrder`, one value per run),
  so duplicates and overtaken commands are dropped, and gaps from commands sent to another peer are normal.
- **Validation.** `ModeSchemas` holds each schema's kinds with their direction, reporter, byte bound and typed codec.
  Decoding checks all of them, consumes exactly Length bytes and rejects trailing data, before anything reaches a
  handler. Actor-owner kinds need the reporter's current life and a newer sequence. Unknown schemas, kinds and
  directions fail with an exception naming them.
- **Ordering.** A kind may declare a causal dependency, such as a victim report naming the blast that threw it.
  The server holds such an event in the run's `ModeEventGate` (64 events, two seconds) until the ledger accepts that
  explosion request, then runs it in arrival order; a refused blast, expiry or round end fails it through the mode's
  wire. Tank Bank's spill declares none, as before.
- **Current round.** The server refuses a spill sent for another round or world revision with its ids; clients
  ignore commands for another world revision or an older round.
- **Purpose.** A new mode registers a definition, rules, payload codecs and a wire adapter (`IModeWire`); the outer
  shapes, the runner and the session factory do not change.

Tank Bank's schema (version 1):

| Direction | Kind | Payload |
| --- | --- | --- |
| Event | 1 Spill | reason (0 hit, 1 thrown), count (1–64), that many body states |
| Command | 1 Score | bank, delta; subject = the actor |
| Command | 2 SpillAccepted | the committed spill, as the event; subject = the victim |
| Command | 3 SpillRefused | count (1–64), predicted ids; sent only to the owner |
| Snapshot | 1 Banks | count (0–64), then actor, bank, present |

The spill bound of 64 is a wire limit above the largest declared drop-count tuner (20). The former `Score` (40),
`DropRefused` (47) and `Drop` (71) messages are retired; their numbers stay unused so no other message renumbers.
Behavior is preserved message for message: a detonation's score follows its Authority, an accepted spill's debit
precedes its echo, and a round start sends one bank snapshot where the server used to restate every nonzero
starting score.

## HUD

A presenter turns mode state into plain `ModeHud` data: counters (the first renders large with its caption),
scoreboard columns, a timer, objectives (which generalize the tank arrows), a banner and an out-state. `MatchHud`
and `ResultsScreen` only render it; the "TANKS BANKED" caption and the results column now live in `TankBankHud`.
Local views still add the crosshair, ammo, hit markers, hit arrows, score tags, score popups and the countdown.

## Bots

`IBotGoalPolicy` picks goals from mode state and world facts (`IBotWorld`: play state, time left and scores from the
mode; tanks, players and pickups from the world); the shared navigation (`BotNavigator`) and aiming (`BotAim`) turn
them into intent. Tank Bank's policy is the existing banking brain, created through its definition by the Lab, the
network bot harness and each peer's match for the lobby bots it owns (`Match.Bots.cs`, A8). Its goal selection still lives inside `BankingBot`; separating it further is follow-up
work that would need its own bot-behavior proof.

## Room for later

These are shaped for, not built:

- **Health.** Typed damage, attribution, invulnerability, death, respawn or elimination are new accepted events and
  commands inside the envelope. Damage reports that name a blast use the causal gate; respawns advance life ids;
  an out-state shows on the HUD.
- **Teams.** Team ids plus predicates beside `ModeActor`, carried in a mode's snapshot payload. Appearance color has
  no team meaning.
- **Segments.** A segment world source builds and retires content roots (`WorldContext.RetireContent`) and lets
  `NextRound(RebuildWorld)` or a mid-round revision travel as a world revision in the envelope header.

## Verification

`make test` runs the pure rule suites (`ModeTests`), the envelope suites (`ModeEnvelopeTests`: round trips,
malformed, oversized, truncated, duplicate, out-of-order, stale-round and unknown-schema input, the causal gate and
a server-held victim report), the ported server, participant, lifecycle and authority suites, and
`scenes/dev/mode_test.tscn`, which feeds the scripts in `tools/dev/modes/` into the rules and checks their state.

The network adoption keeps `Protocol.Version` at 2 under the unreleased-cycle ruling; MatchStart and the mode
messages change shape, so the next release must be a minor release.
