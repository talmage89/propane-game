# Modes

How a game mode plugs into Propane: pure rules, a definition that selects everything mode-specific, a runner that
supplies the clock and transport, and one fixed network envelope. Tank Bank is the first mode on this framework and
plays exactly as before; its rules are in [multiplayer.md](multiplayer.md#tank-bank). Sandbox is the second. Ring Out,
King of the Cul-de-sac, Short Fuse and Dissolve Run began as Lab prototypes, the framework's first test with new rules
([below](#lab-prototypes)); King also plays online. Short Fuse is the first to use the optional [health state](#health),
and Dissolve Run the first [segment world source](#segments).

| Path | Contents |
| --- | --- |
| `src/Modes/ModeRules.cs` | `IModeRules`, phases, the mode clock, accepted events, commands, standings, `SandboxRules`. |
| `src/Modes/TankBankRules.cs` | Tank Bank's bank, spill tickets, gates, standings, statistics and audit; victim-owner helpers. |
| `src/Modes/ModePhases.cs` | The pure phase machine the server's runner drives. |
| `src/Modes/ModeHud.cs` | Plain HUD data and the Tank Bank and Sandbox presenters. |
| `src/Modes/ModeDefinition.cs` | `ModeDefinition` and supply policy, with no networking dependency. |
| `src/Modes/ModeRegistry.cs`, `ModeCatalog.cs` | Composition-owned compiled bundles, validated network catalog and scoped test overlays; lobby/prototype exposure stays explicit. |
| `src/Modes/BodyEvents.cs` | Sampled positions, attributed throws, eliminations, `RoundOver` and a pure seeded generator. |
| `src/Modes/Health.cs` | The optional health state: damage reports, validation, attribution, invulnerability, death, respawn. |
| `src/Modes/RingOutRules.cs`, `KingRules.cs`, `ShortFuseRules.cs`, `DissolveRunRules.cs` | The prototypes' rules: Ring Out's slab plan, King's zone schedule, Short Fuse's rounds, Dissolve Run's front and score. |
| `src/Modes/PrototypeHud.cs`, `ShortFuseHud.cs`, `DissolveRunHud.cs` | The prototypes' HUD presenters. |
| `src/World/Plan/SegmentPlans.cs`, `src/Session/SegmentWorldSource.cs` | Segment plans from a run seed and index; the segment world source and its feed. |
| `src/Lab/PrototypeRunner.cs`, `LabMatch.cs` | The offline runner that drives a prototype's rules in the Lab, and a multi-round match it carries. |
| `src/Net/ModeEnvelope.cs` | The envelope record, explicit schema constructors/codecs, the causal gate and command order. |
| `src/Net/ServerModes.cs` | Server-owned presentation-free mode inputs, ordered eligibility and plain live wire tuner observations. |
| `src/Net/ModeRunner.cs` | One run's mode on the server, with Tank Bank and King wire adapters. |
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
presenter, the bot goal policy, the tuners the rules copy and the envelope schema version. Composition pairs each
networked definition with its existing schema and server/client factories in a validated `ModeBundle`. Sandbox
selects explicit silent wire and client adapters; a missing integration fails before a runner or client starts. `ModeRegistry.CreateServer` selects server-owned mode inputs and supplies only metadata, rules/schema/wire
factories, tuner names, supply settings and rule tuner values. Server owners never resolve ModeRegistry or carry
ModeDefinition/ModeSupply/HUD factories. ModeSettings and MatchSetup remain plain values; composition resolves a
client definition with `ModeRegistry.Resolve(setup)`. Lobby setting bounds live in Foundation's LobbyModeLimits.
`ModeRegistry.Shipped` is immutable; tests use `RegisterScoped` overlays, disposed in reverse order to
restore the exact prior catalog on success or exception. The codec layer has no mutable registration table.
Network client policy is selected through each validated bundle's client factory. `BankClientAdapter` owns scores
and spills, `KingClientAdapter` composes it with zone state and pile handling, `LobbyClientAdapter` owns roaming
roster/staging/stock/refills, and Sandbox explicitly uses `SilentClientAdapter`. They live for one physical session,
borrow synchronous replica operations, and release those bindings on session stop. Match retains generic envelope
validation/order and callback/lifetime coordination; concrete HUD and world visuals remain presentation bindings.
Prototype runner/bot observation conversion is separate B3b2 work; this boundary does not enable a prototype online.

`ModeRegistry` ships:

| Mode | Rules | World | Supply | Timer | HUD | Bots |
| --- | --- | --- | --- | --- | --- | --- |
| Sandbox (1) | no score, no spills, nothing gated | void | single-player suburb, unlimited ammo, no cans | none | tank arrows | none |
| Tank Bank (2) | `TankBankRules` | suburb plan | `SuburbPlans.Match`, limited ammo, cans | countdown | bank, clock, arrows | `BankingBot` |
| Ring Out (3), Lab only | `RingOutRules` | the Lab's slab | unlimited ammo, no cans | count-up | standing, knock-offs, next dissolve, feed | `RingOutBot` |
| King of the Cul-de-sac (4) | `KingRules` | suburb plan (Lab: void or suburb) | unlimited ammo, no cans | countdown | bank, minted, clock, zone arrows | `KingBot` |
| Short Fuse (5), Lab only | `ShortFuseRules` | a suburb per round | `SuburbPlans.Match` on a Lab-sized slab, unlimited ammo, no cans | countdown | wins, standing, kills, health bar, feed | `ShortFuseBot` |
| Dissolve Run (6), Lab only | `DissolveRunRules` | a segment stream | strip segments of the single-player piles, limited ammo, cans | count-up | score, meters, tanks, front bar, chain banner, feed | `DissolveRunBot` |
| Lobby (7), the lobby world | `LobbyRules`: no score, no spills, emptied piles refill | a hand-built void ring of piles | unlimited ammo (`LobbyInfiniteAmmo`), no cans | none | none (lobby card) | `BankingBot`, with a bottomless magazine |

Multiplayer lobbies default to Tank Bank; the creator can choose King and its mint pace (0.5–10 s in 0.5 s steps,
default 2 s). The lobby's length is the round length. `MatchStart` appends the mode id and schema version, and a client
that does not know the pair fails decoding by name. Lab scenarios name a mode: the banking duel plays Tank Bank and the other
starters play Sandbox. A waiting lobby's [world](multiplayer.md#lobby-world-a12-protocol-3) plays Lobby,
which no one chooses. A Lab scenario's environment choice replaces the mode's world source, so a prototype can try
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
| ModeId | UInt8 | Sandbox 1, Tank Bank 2, King 4, Lobby 7. |
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
- **Purpose.** A networked mode adds one complete compiled bundle containing its definition, existing schema and
  explicit server wire factory (`IModeWire`). Registration does not implement unsupported client or world behavior.

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

King's schema (version 1) shares Tank Bank's bank kinds under the same ids, plus:

| Direction | Kind | Payload |
| --- | --- | --- |
| Event | 2 PilePlaced | zone (UInt16), count (0–64), transforms; only the current world authority may report |
| Command | 4 PileWanted | zone, spot, count, reach, clearance; only to the world authority |
| Command | 5 PileSpawned | zone, owner peer, count (1–64), registered body states; to everyone |
| Snapshot | 2 Zone | zone, spot, next spot, radius, holder, occupants, next mint, mint interval, move time, warning length, per-actor mints, elapsed |

The server samples its latest accepted player poses at `PlayerSendRate`. Upright actors occupy by feet position;
ragdolled/getting-up actors do not occupy. Actor lives and pose sequences use the ordinary validation; departure
removes all that peer's samples and releases its hold. Only the server runs `KingRules`. Zone snapshots are reliable
and restated when holder, occupants, schedule, dials or mints change. Times are seconds since Go; peers advance only
presentation clocks (`KingState.At`), never occupancy, minting or moves. The snapshot retains minted counts for results.
The Lab preserves its original single-player suburb supply; hosted King uses match supply scaled by actor count.
A late builder replays buffered zone snapshots and pile creation commands in receive order, ending at current state.
Lobby admission remains waiting-only; reconnecting does not resume a running match.

On a move, the server asks the current world authority to find a clear pile site through `PileSite`, since the
server has no physics. The authority reports transforms, or an empty answer if no ground is clear. The server checks
authority, round, world, zone, count and bounds, assigns unused map-range ids after the plan's tanks, registers the
whole pile resting in the ledger, then broadcasts PileSpawned before any body publication can be accepted. These
are ordinary world tanks: punctures, claims, chains and detonations use the existing paths, and the audit counts them
as map detonations. A pending request passes to a successor authority; duplicate, superseded and closed-round
answers cannot create a second pile. No world revision or suburb rebuild is needed.

## HUD

A presenter turns mode state into plain `ModeHud` data: counters (the first renders large with its caption),
scoreboard columns, a timer, objectives (which generalize the tank arrows), a banner, an out-state, a feed and an
optional bar (a mode's health, drawn at the bottom center; null for every other mode). `MatchHud`
and `ResultsScreen` only render it; the "TANKS BANKED" caption and the results column now live in `TankBankHud`.
Local views still add the crosshair, ammo, hit markers, hit arrows, score tags, score popups and the countdown.

A match also shows the live leaderboard (`MatchHud.SetStandings`, drawn by `LeaderboardPanel`) for any mode whose HUD
has a score column, from the scores and roster every peer already has, so it needs no messages. `Leaderboard` is its
pure model: score order with ties in roster order (as `Snapshot()` ranks the results), ranks shared on ties, the top
`LeaderboardRows` plus the local row below a divider when outside them, and the slide, flash, "+N" and drop tint
timed by `LeaderboardSlideTime`, `LeaderboardFlashTime` and `ScorePopupTime`. It hides while loading and on the results
screen, with fewer than two players, at `LeaderboardRows` 0, and when the player turns it off in the match menu
(`PlayerSettings.ShowLeaderboard`). The Lab's prototypes do not show it: the Lab overlay holds that corner.

## Bots

`IBotGoalPolicy` picks goals from mode state and world facts (`IBotWorld`: play state, time left and scores from the
mode; tanks, players and pickups from the world); the shared navigation (`BotNavigator`) and aiming (`BotAim`) turn
them into intent. Tank Bank's policy is the existing banking brain, created through its definition by the Lab, the
network bot harness and each peer's `OwnedBotSet` for the lobby bots it owns. `BankingBot` retains ordered decision
assembly over its target, combat and demolition owners. Hard uses the local `BotPoints` estimates in both Tank Bank and
King: expected bank gains and leader spills per second compete with King's mint/denial rate. Easy and Normal keep
their objective policy. Travel, contest, shield delay, return-fire risk and choice hysteresis are local tuners;
[bot navigation](bot-navigation.md#hard-points-policy) describes the choices and strength comparison.

## Lab prototypes

Ring Out, Short Fuse and Dissolve Run remain Lab-only definitions in `ModeRegistry.Prototypes`. King is in both
`Prototypes` and `All`, with a lobby choice and envelope schema; its online promotion joined Protocol 3 and shipped in
0.4.0, a minor release.
The Lab plays them against its own bots ([lab.md](lab.md#prototype-modes)) through `PrototypeRunner`, which feeds the
rules what a server would: the round's phases on its clock, and every step the positions of all actors
(`ActorsSampled`; a server would sample what its clients already send, so a mode needs no traffic of its own), who
knocked whom down (`ActorThrown`: a melee hit's attacker, or the initiator of the chain whose blast threw them), and
for King the attributed detonations, hits and throws exactly as the Lab duel feeds Tank Bank. King's shared zone and pile dials (`Tuning.95.Prototype.cs`) are exported and transmitted with match tuning;
the other prototypes and Lab-only layout dials remain local. Rules copy their dials into `ModeTuners`.

**Ring Out** (`RingOutRules`). A square slab of lots (`RingOutLots` per side, odd, `RingOutLotSize` meters) centered
on the origin; its top is at height zero and the void's floor lies `RingOutVoidDepth` (150 m) below, far enough for
the fog to sell the drop. `RingOutPlan` cuts each ring of lots, outermost first, into angular bands of about
`RingOutBandLots`, from a seeded start angle. Band `b`
pulses for `RingOutWarningSeconds` and dissolves at `(b + 1) * RingOutBandSeconds` of round time, until the center
lot alone remains (5 × 5 lots: six bands, one lot left at 90 s). Whether ground is solid is a pure function of round
time and the plan, so the boundary is never sent. A sample eliminates an actor below `-RingOutFallDepth`, or below
`RingOutStandHeight` over ground that is gone (or off the slab). A faller is credited to whoever last threw them
within `RingOutCreditSeconds`; a later throw with nobody to blame (their own blast) clears it. Places follow
elimination order and actors out in the same step share one; a departure is an elimination without credit. The round
ends when one actor stands (or none, the last ones sharing first), or at `RingOutRoundLimit`, when survivors share
first. Standings score a place as `starters - place`. There is no bank: spills are refused.

**King of the Cul-de-sac** (`KingRules`). Tank Bank's rules, composed: the bank, tickets, gates and audit are
`TankBankRules`, which gains one input, `TankMinted`, that Tank Bank never sends (its audit is unchanged; `Expected`
adds the minted count, always zero there). The zone is a circle of `KingZoneRadius` (and `KingZoneHeight` above or
below the spot) on one of the world's spots (`ModeWorld.Spots`: the void's pile ring, or a suburb plan's piles,
`KingPlacement.SpotsFrom`).
Zone `k` is in force from `k * KingMoveSeconds` of round time; each next spot is a seeded pick among spots
`KingMoveMin`–`KingMoveMax` meters from the last (or the nearest miss when none is in range), so the schedule is a
pure function of the clock, the spots and the seed. A move warns for `KingWarningSeconds`; timing dials change from the
next round. Only upright, present actors occupy. A lone occupant mints one banked tank per `KingMintSeconds` held
without a break; a contest, leaving, being thrown or a move restarts the count. `ZoneMoved` asks the runner to
materialize a fresh pile there: `PileSite` finds the nearest clear, flat ground for the whole pile within
`KingPileReach` of the spot with the world's physics, keeping `KingPileClearance` open around it and twice that from
players. `State(clock)` is the zone as every peer sees it (`KingState`): the zone and next spot, radius, holder and
occupants, the next mint, the move and its warning in round time, and each actor's mints. The HUD, `KingBot` and the
shared `KingZone` presenter read only that, so they behave the same from the Lab's rules or a match's snapshot. The
audit line reads `king audit initial= map= drops= minted= spills= banked= expected= ok=`.

`KingBot` holds, shoves and shoots intruders, and banks safe tanks in sight. With at least `BotKingCrowdPlayers`
actors (4), it waits at the edge of an occupied zone before raiding and breaks off a sustained contest after a seeded 1–3 seconds and fights from the perimeter before
raiding again after 6–14 seconds, scaled by roster size. These independent clocks spread out reentry; the nearest
challenger pounces early when the zone empties. It keeps circling and shooting while outside, retaining the
knockdown regroup policy and Hard's points comparisons. The new crowd dials are in the shared **Bots** tuner group;
existing bot dials and all mode rules stay unchanged. [Bot navigation](bot-navigation.md) describes the policy.

**Short Fuse** (`ShortFuseRules`). Last one standing in a suburb of tanks, with [health](#health). Each round starts
everyone present on a new life at full health (`BeginRound`); a declared death puts the victim out to spectate, and
when one starter is left alive (or none: two going down together) the rules send `RoundWon` and `RoundOver`. A round
that reaches `ShortFuseRoundSeconds` goes to the survivor with the most health; a tie wins nobody the round. At RoundEnd
the rules answer `EndMatch` once someone has `ShortFuseWinsToMatch` round wins (or fewer than two remain), else
`NextRound(RebuildWorld)`: the Lab builds a new suburb from the match's seed stepped by round, with the same rules
(`LabMatch`), so wins, kills and life ids carry over. Standings score round wins. `SpectateTarget` is pure: a dead
actor watches whoever got them while they live, then follows that chain of killers, then the lowest-numbered actor still
alive. The kill feed reads `A shot B`, `A blew up B` (the chain's first detonator), `A got B` (the last other damager
credited for someone else's blow, such as B's own blast), `B blew themself up` or `B went down`. There is no bank:
spills are refused. The audit adds `short fuse round= alive= over= match_over= rounds=` to the health audit.

**Dissolve Run** (`DissolveRunRules`). Solo, along world +X from the run's start (the world's first spot). The front
starts `DissolveFrontStartGap` (25 m) behind the start; its speed ramps from `DissolveFrontSpeed` (1.5 m/s) toward
`DissolveFrontMaxSpeed` (5 m/s, under `SprintSpeed`) as `max + (start - max)·e^(-t/τ)` with τ =
`DissolveFrontRampSeconds` (180 s), integrated exactly between samples so the front does not depend on the step; and it
never trails the furthest point reached by more than `DissolveFrontMaxGap` (60 m). Each tank detonated
(`RunTankDetonated`, carrying its chain) pushes it back `DissolveFrontPush` (1.5 m), once per tank. A sample behind the
front is caught (`RunCaught`): the runner falls for `DissolveFallSeconds` (2.5 s), then `RoundOver`. Score = meters (the
furthest sample past the start) + `DissolveTankPoints` (10) per tank + n² per chain of n ≥ `DissolveChainMin` (2); a
chain closes (`ChainClosed`) once none of its tanks has gone up for `DissolveChainWindow` (1 s), or at the catch. The
runner supplies chains: a directly shot tank starts one, and its blasts carry its id (`BlastRequest`, then `ChainCause`)
to every tank they set off; a tank the front has dissolved cannot go up. The audit line reads `dissolve run meters=
tanks= chains= bonus= best_chain= pushes= score= caught= front=`. Its world is a [segment stream](#segments).

**Where the framework did not fit.** Recorded rather than bent:

- `IModeHudPresenter.Present` and `IBotGoalPolicy` see only scores and the clock; the prototypes need the boundary and
  the zone. `ModeHudInput` and `IBotWorld` gained an optional `Rules` and `Clock`, supplied only where a peer runs
  the rules itself (the Lab), and an optional `View` (`IModeView`): the mode's own state, from the rules or decoded
  from its snapshot payload. King reads only its `View`.
- Untimed and count-up rounds had no way to say they were decided: `RoundOver` is that command. No server runner
  acts on it yet.
- Tank Bank's goal selection still lives inside `BankingBot`; the prototype policies share `BotMotor`, a small layer
  over `BotNavigator` and `BotAim` that `BankingBot` could adopt later.
- King now has a wire and snapshot view, shared by its HUD, zone presenter and bots; the Lab still supplies that
  view from its rules. Ring Out, Short Fuse and Dissolve Run need their own schemas and wires to play online.
- `ModePhaseMachine` can rebuild a world between rounds (`NextRound(RebuildWorld)`), but a server match cannot yet: it
  ends the match instead. Short Fuse's new suburb each round works only in the Lab, which rebuilds its whole session.
- `RoundInfo.StartsAt` and the clock are the runner's; the Lab keeps a match's mode clock running across its round
  rebuilds by offset (`LabMatch.ClockOffset`) so that the carried rules never see time go back.
- `ModeHud` had no bar; it gained an optional `HudBar` for health. `ModeHudInput` carries no spectate choice, so the
  presenter and the arena both read the rules' pure `SpectateTarget` to agree.
- Dissolve Run is the first mode whose world is not one plan. `ModeDefinition.World` names only `SuburbPlan` or `Void`,
  so its definition says `SuburbPlan` and the Lab chooses the segment source by mode; a networked version would add a
  `Segments` world source. Chains had no identity: the Lab's `BlastRequest` carried the scorer, so the runner now gives a
  Dissolve Run chain its own id instead (solo, every tank is the runner's). `ModeSupply.Suburb` builds one suburb for a
  player count; segment settings come from their own dials (`SuburbPlans.Segments`).
- A runner that moves on keeps tracking new tanks (`PrototypeRunner.Track`) as segments build, and the Lab forgets the
  ones the front dissolves, so the tank lists stay bounded by the live segments.
- Death needed two small `PlayerCharacter` hooks: `Collapse()` (go limp where you stand) and `Spectating` (orbit
  another body's camera focus). Neither changes a match.

## Health

`HealthState` (`src/Modes/Health.cs`) is a reusable, pure piece a mode composes, as King composes Tank Bank's bank;
Tank Bank does not use it. A mode builds its numbers (`HealthRules`) from its own tuners: max health, bullet damage,
blast damage at the center and at the edge (linear between, none beyond `BlastRadius`), regeneration per second after
a delay unhit (zero disables it), the get-up grace, the credit window and the report window.

**Reports follow today's authority lines.** Hit detection stays with the shooter and blast ragdolls with the victim, so
damage arrives as their reports and the rules validate them:

| Input | From | Carries |
| --- | --- | --- |
| `BulletHitReported` | the shooter | shooter, its life, its shot id, victim, the victim's life as seen, the shot's time |
| `BlastAccepted` / `BlastRefused` | the server's ledger | blast id, the chain's first detonator, the blast's time |
| `BlastHitReported` | the victim | victim, its life, the blast it names, how far it stood |
| `ActorsSampled` | the runner | each body's down state: getting up opens the grace |
| `InvulnerabilityGranted` | the mode | a life protected until a time (spawn protection) |
| `ActorLeft` | the runner | the life ends without a death |

A report is refused (`DamageRefused`, with a reason) when it repeats a shot or a blast for that victim, is older than
the report window or from the future, falls outside a round, names an unknown actor or a life that is not the actor's
at that time, hits a dead or protected victim, is the shooter's own, names a blast refused or never accepted within the
window, or reports a distance beyond the throw. A victim's report waits for the blast it names, as the envelope's
causal gate would hold it; the damage takes the blast's own time. The damage amount is always the rules', never the
reporter's: a client reports what happened, not how much it hurt.

**Order does not matter.** Accepted damage is kept per life and replayed in event-time order (ties broken by content,
never arrival). Because a report older than the window is refused, once the window has passed a moment everything
before it is known: a death there is declared (`ActorDied`, with the last blow's kind and attacker and the credit) and
final. Until then health reads what has arrived, so a HUD may show zero a window's length before the death is
declared. In the replay a bullet fired after its shooter's death counts for nothing, a chain keeps its detonator's
credit even after they die, and nothing reaches a victim after its death. Credit goes to the last other actor to damage
the victim within the credit window (for a blast, its chain's first detonator); a blow by the victim itself or by
nobody credits that earlier damager, if any. A round closing (`Close`) declares nothing more, so how it ends never
depends on which late reports happened to arrive. `BeginRound` respawns everyone (`ActorRespawned`, life ids only
advance) and starts report and blast ids afresh, so a runner may number each round from one.

Reports carry server times: a networked runner would stamp a report with its arrival time, or the reporter's time
mapped to the server clock and bounded by the window. The window is the price of order independence: a death is
declared that long after its blow. Short Fuse's is 0.25 s.

**On the network.** Nothing here is wired yet; the shapes are meant for the envelope unchanged: `BulletHitReported`
and `BlastHitReported` as two actor-owner event kinds (their EventId the reporter's action sequence, deduplicated as
every event is), the blast report declaring its causal dependency so the server's `ModeEventGate` holds it until the
ledger accepts that explosion request, `BlastAccepted` fed by the server from the ledger, and `DamageApplied`,
`ActorDied` and `ActorRespawned` as commands with health in the snapshot payload. Victims report their own blast
damage, so a modified client could under-report it; that is the trade victim-side ragdolls already make.

## Room for later

These are shaped for, not built (Ring Out added the first out-state, in the Lab only):
- **Teams.** Team ids plus predicates beside `ModeActor`, carried in a mode's snapshot payload. Appearance color has
  no team meaning.
- **Segments.** Built for one player in the Lab ([below](#segments)); a server would send plans per segment and a
  revision in the envelope header.

## Segments

A segment world source (`SegmentWorldSource`) builds and retires content roots under one run root: each segment is its
own suburb, so retiring one through `WorldContext.RetireContent` frees its nodes, physics bodies and every effect it
spawned. Segments are strips from the ordinary generator (`SuburbSettings.StripLength`: one straight through road along X
with lots on both sides and `StripSeamGap` of lawn kept clear at each end, so strips laid end to end join street to
street with no dead end; zero, as in every shipped suburb, leaves the generator's output unchanged). `SegmentPlans`
generates segment `i` of a run from the run seed and `i` alone (SplitMix64 of both), placed at `(i - 0.5) * length`
along X, with its tank count from the schedule (`DissolveTanksStart` 16, falling `DissolveTanksDrop` 0.5 per segment to
`DissolveTanksMin` 4), piles of `DissolvePileMin`–`DissolvePileMax` (2–8), `DissolveAmmoCans` (2) cans, and on every
`DissolveJackpotEvery`th (10th) segment a square pyramid of `DissolveJackpotTanks` (30) on the crown of the street; a
layout that cannot hold them retries with derived seeds. The daily seed is the UTC date (`20261013`).

Plans are `SuburbPlan` data, so the shape is ready for a server: an `ISegmentFeed` supplies plans by index (the Lab's
`LocalSegmentFeed` generates them on a worker thread); a networked feed would receive each as `PlanCodec` bytes, the way
a match's suburb travels, with a world revision per segment in the envelope header so commands for retired segments are
dropped as for an old world. The policy (which segments to build and retire, and when) is the mode's: Dissolve Run's
arena builds ahead of a window that materializes `DissolveAhead` (60 m) past the furthest point and retires behind the
front. The window is drawn by the reveal shader's planar mode: geometry tagged `reveal_side = 2` exists only between two
planes set by one global shader value (`reveal_window`: the run's direction, its back edge and its leading edge), so the
window moves for free each frame. None of this is networked yet.

## Verification

`make test` runs the catalog completeness, immutability and scoped-restoration suite (`ModeCatalogTests`), the
registration/supply inventory (`ModeInventoryTests`), the pure rule suites (`ModeTests`), the envelope suites (`ModeEnvelopeTests`: round trips,
malformed, oversized, truncated, duplicate, out-of-order, stale-round and unknown-schema input, the causal gate and
a server-held victim report), the ported server, participant, lifecycle and authority suites, and
`scenes/dev/mode_test.tscn`, which feeds the scripts in `tools/dev/modes/` into the rules and checks their state.
`PrototypeModeTests` cover Ring Out's band schedule, eliminations, credit window and placements, and King's
occupancy, contests, minting, relocation, departures mid-hold and conservation. `HealthTests` cover damage typing and
falloff, attribution windows, chain credit, regeneration, the get-up grace, invulnerability, a death during a ragdoll,
departures alive and dead, every refusal, when a death is declared, and a scripted four-player fight delivered in
order and then on forty fault schedules (reports delayed within the window, dropped and resent, duplicated, reordered)
with the same deaths, credit and health every time, and past the window refused as if lost; and Short Fuse's rounds,
match, limit, departures, spectate targets, feed and HUD. `DissolveRunTests` cover the front's ramp (the same however
it is stepped) and max gap, the catch and fall, pushes, chain bonuses and their minimum, a catch closing open chains, and
the HUD. The generator suite sweeps segment runs (`--segment-seeds`, 10 in `make test`, 100 in `make test-full`): each
segment the same when generated again, out of order and from the worker's catalog snapshot; full length, one through
street, end to end with its neighbors, its scheduled tanks and jackpot, its cans, lots clear of the seams and a plan that
survives `PlanCodec`; and a run's segments stitched into one plan with every tank reachable on foot from the start. `make
test-world` (`tools/dev/check_world.sh`) also generates five runs of twelve segments in two fresh processes and requires
the same plans, seeds and cans.
`check_lab.py` plays whole prototype
rounds with bots (see [lab.md](lab.md#verification)).

The network adoption stayed within Protocol 2 (released in 0.3.0); MatchStart and the mode
messages changed shape, which needed a minor release.
