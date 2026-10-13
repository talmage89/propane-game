# A11b part 1: Lab prototypes of Ring Out and King of the Cul-de-sac

Work branch: `claude/a11b-ringout-king-yx7llw` (the session's assigned name for `claude/a11b-ringout-king`), started
from `origin/lead/a8-verify`. `origin/dev` (39fc801) is already an ancestor of the branch: dev has not moved since
A8's verify branch merged it, so the gate ran on the branch head, which is the merge result. Review assets are in
`review/` on this report branch.

## 1. Summary by commit

1. **a05dc8c Add Ring Out and King of the Cul-de-sac as pure prototype rules, presenters and bot policies.**
   - `src/Modes/BodyEvents.cs`: new accepted inputs `ActorsSampled` (positions the runner already has) and
     `ActorThrown` (victim and who knocked them down); new commands `ActorEliminated` and `RoundOver`; `ModeSpot`; a
     pure SplitMix `ModeRandom`.
   - `RingOutRules` + `RingOutPlan`: the slab plan, the band schedule as a pure function of round time, eliminations,
     the kill-credit window, shared placements, departures and a round limit.
   - `KingRules`: composes `TankBankRules` for bank, tickets, gates and audit; adds the zone, occupancy, minting
     and a seeded relocation schedule. `TankBankRules` gains one input, `TankMinted`, that Tank Bank never sends
     (its audit text is unchanged; `Expected` adds `Minted`, always 0 there).
   - `ModeWorld` gains an optional trailing `Spots`; `ModeHud` an optional `Feed`; `ModeHudInput` an optional
     `Rules` and `Clock`; `IBotWorld` default members `Rules`/`Clock`.
   - `ModeRegistry.Prototypes` (ids 3 and 4), deliberately outside `ModeRegistry.All`.
   - Presenters `RingOutHud`, `KingHud`; policies `RingOutBot`, `KingBot` on a shared `BotMotor` (BotNavigator +
     BotAim glue).
   - Local Lab dials in `Tuning.Prototype.cs`; `LabDial` gains `Scenario`.
   - `PrototypeModeTests` (8 tests).
2. **2ef81c0 Play Ring Out and King of the Cul-de-sac as Lab prototypes against Lab bots.** `PrototypeRunner`
   (offline runner), `RingOutArena`, `KingArena`, the two Lab scenarios under a "Prototype modes · rules on trial"
   menu group, the match HUD in prototype sessions (with the feed), the Lab overlay's status and results,
   `VoidEnvironment.FloorHeight`, `LocalBank.SpawnDrops` shared (moved verbatim, same RNG order),
   `Suburb.GroundMaterial` made internal, the `prototype` scenario in `lab_test`, `check_lab.py` sessions and a fourth
   `test-full` Lab shard, docs (`modes.md`, `lab.md`, README).
3. **c189eca Tune the prototype bots and presentation after watching rounds.** Fixes found by watching traces and
   captures (listed under "What felt off"), the HUD counter overlap, beam blending, `--tune.`, `--overview`,
   `--until` for captures.
4. **4865292 Lower Ring Out's void floor to 150 m** so the fog sells the drop; `--menus-only` Lab frames.
5. **c43a4f7 Keep King bots moving**: clear stand points, a stall fallback in `BotMotor`, shooting a venting tank
   out of the zone; the "someone minted" check applies across full multi-seed runs only.
6. **ec36b33 Solve a wrist target rounded just past full reach as a straight arm** (`RifleRig.SolveTwoBone`). A
   King round logged `[rifle] unreachable wrist: distance=0.547081, lengths=0.274440/0.272640` and a contact failure
   at handoff weight 0.003: a straight arm 1 µm past its length at the start of a get-up. Within 0.1 mm it is now
   solved as a straight arm. Pre-existing; prototype rounds ragdoll far more often than Tank Bank.
7. **2ebd5bd Keep KingBeamAlpha's slider end exactly representable as a float** (0.8 → 0.75; found by the dial
   sweep).

## 2. Verification

| Command | Result |
| --- | --- |
| `make check` (on ec36b33; 2ebd5bd only changes one dial bound) | exit 0, `0 Warning(s)`, 316 unit tests 0 failures, Lab sessions clean incl. `ringout`, `king` |
| `make -j4 test-full` (first run, ec36b33) | every job passed except `test-full-lab-3`: the dial sweep hit the KingBeamAlpha float bound (fixed in 2ebd5bd); bot matches were not reached |
| `make -j4 test-full` (final head 2ebd5bd, 24 min) | every job passed (all four Lab shards incl. 372 dial sessions, generator shards and coverage, navigation 700/700, piles, range, menu lifetime) except the first bot gate: the faulted 4-bot `rifle-soak-gate` reported 1 tank disagreement, a venting tank still moving at match end 0.534 m apart between peers 2 and 4 (`final match 1 peers 2/4 body 2020000b position: moving`); its bank audits agreed. `make` then skipped the other two bot gates. |
| the same `rifle-soak-gate` command, rerun once | exit 0, 0 disagreements, all 4 agree with the server audit, 0 engine errors |
| `duel-lane-gate` and `loading-readiness-gate` commands from `test-full-bots`, run after | both exit 0; audits agree; loading readiness 0 disagreements (it passed first time, so no known-flake rerun was needed) |
| `python3 -B tools/dev/check_lab.py --full --only=ringout-full,king-full,king-suburb` | passed (part of both test-full runs; numbers below) |
| `tools/dev/net_bots.sh -n 4 -w 0 -l 90 -s '--match-seed=25' -- --melee-start` (clean) | exit 0; `0 initial + 35 map + 35 drops - 43 spills = 27 banked`; all 4 bots agree with the server audit; 0 tank disagreements; 0 engine errors/warnings in 5 logs |
| same with `--net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3` (faulted) | exit 0; `0 + 24 map + 36 drops - 44 spills = 16 banked`; all 4 agree; 0 disagreements; 0 engine errors/warnings |
| Rendered captures (`--fixed-fps 30 --resolution 1280x720`, `--overview`, compressed band/move timings) | reviewed; in `review/` |

All scripted prototype rounds ran with **3 brains per process** (the player's actor plus 2 Lab bots, all driven by
the mode's policy). Larger bot counts are for real hardware.

Scripted full rounds at shipped dials (`ringout-full`, `king-full`, `king-suburb`, seeds 11–13), from the last
completed run:

| Round | Result | Notes |
| --- | --- | --- |
| Ring Out seed 11 | over at 50.4 s; places 1, 2, 3 | knock-offs: player-bot 1, Bot 1 1 |
| Ring Out seed 12 | round limit at 180 s; two share first | one knock-off |
| Ring Out seed 13 | over at 98.5 s; places 1, 3, 2 | player-bot 2 knock-offs |
| King void seed 11 | `map=40 drops=55 minted=28 spills=83 banked=40 expected=40 ok=True` | mints 6/9/13 |
| King void seed 12 | `map=46 drops=41 minted=20 spills=89 banked=18 expected=18 ok=True` | mints 2/5/13 |
| King void seed 13 | `map=33 drops=28 minted=16 spills=69 banked=8 expected=8 ok=True` | mints 4/9/3 |
| King suburb seed 11 | `map=0 drops=0 minted=4 spills=4 banked=0 expected=0 ok=True` | see "felt off" |

Each Ring Out round ended with a winner, every starter placed, places non-increasing in elimination order, every
eliminated actor spectating, credits matching the feed, and the slab down to one lot when the round outlasted the
collapse. Each King round ended on its clock with the bank conserved, every mint counted, scores equal to the bank,
and one materialized pile per zone move.

Brain cost on this VM, uncontended: about 0.4–2.4 ms per physics tick for three brains (`think_ms_per_tick` above);
under contention from parallel jobs the same measurement read up to ~7 ms (and 3.3 ms in the final -j4 run), so treat
it as indicative.

## 3. The rules as built

**Ring Out** (Lab, void only). 5 × 5 lots of 16 m (80 m), top at y = 0, void floor 150 m below. Four piles of six
ringed 13 m from the center. Each ring, outermost first, is cut into angular bands of about four lots from a seeded
start angle: six bands, so with 15 s bands one lot remains at 90 s. A band pulses for 4 s, then its lots lose their
collider at once and dissolve visually over 0.8 s (reveal shader). An actor is out when below −2 m, or below 0.6 m
over ground that is gone or off the slab (so a body in the air over a hole is not out until it drops). Kill credit:
the last `ActorThrown` within 6 s (a melee hit names its attacker; a blast names its chain's initiator; a throw with
nobody to blame, e.g. their own blast, clears earlier credit). Places by elimination order; same-step eliminations
share the best open place; the last one standing (or the last ones out together) wins. A departure is an
elimination without credit. The eliminated are teleported to a gallery ledge off one side, input-locked but free to
look. Unlimited ammo, no cans; a hit marks the shooter but spills nothing (there is no bank). Not in the spec, added so a round always ends: a
**180 s round limit**, where survivors share first.

**King of the Cul-de-sac** (Lab, void or suburb). Tank Bank's bank and spills (detonations bank to the chain's
initiator; hits spill 1 with hit grace; throws spill 5). Zone: radius 6 m (12 m across), within 3 m of the spot's
height, on a pile spot. Only upright, present actors occupy. A lone occupant mints 1 banked tank per 2 s held without
a break; a contest, leaving, being thrown or a move restarts the count. The zone moves every 45 s, warned 3 s ahead,
to a seeded pick among spots 15–25 m away (nearest miss if none), and a fresh pile materializes beside the new spot.
Void layout: six pile spots on a 20 m ring plus one in the middle (all 20 m from their neighbors). Suburb: the
suburb's own tank groups. 180 s round; most banked wins. Unlimited ammo, no cans. FFA only.

## 4. Tuners added (all local Lab dials: never in a match snapshot or a saved .tres)

| Tuner | Default | Range | Use |
| --- | --- | --- | --- |
| LabPrototypeBots | 3 | 1–5 | Lab bots beside the player |
| LabWarningPulseHz | 2 | 0.5–6 | warning pulse rate (rim lots, beam) |
| RingOutLots | 5 | 3–7 (odd) | lots per side |
| RingOutLotSize | 16 m | 10–24 | lot size |
| RingOutBandLots | 4 | 1–8 | lots per band |
| RingOutBandSeconds | 15 s | 3–60 | time per band |
| RingOutWarningSeconds | 4 s | 0.5–10 | pulse before a band goes |
| RingOutCreditSeconds | 6 s | 1–15 | kill-credit window |
| RingOutFallDepth | 2 m | 0.5–10 | out below this depth |
| RingOutStandHeight | 0.6 m | 0.2–2 | out over gone ground below this height |
| RingOutRoundLimit | 180 s | 30–600 | survivors share first |
| RingOutPiles / RingOutPileCount / RingOutPileDistance | 4 / 6 / 13 m | 1–8 / 1–12 / 4–40 | the pile ring |
| RingOutDissolveSeconds | 0.8 s | 0.2–3 | visual dissolve |
| RingOutVoidDepth | 150 m | 10–300 | void floor depth |
| KingZoneRadius / KingZoneHeight | 6 m / 3 m | 3–12 / 1–8 | zone size |
| KingMintSeconds | 2 s | 0.5–10 | mint interval |
| KingMoveSeconds / KingWarningSeconds | 45 s / 3 s | 10–120 / 0.5–10 | move period and warning |
| KingMoveMin / KingMoveMax | 15 / 25 m | 5–40 / 5–60 | move range |
| KingRoundSeconds | 180 s | 30–600 | round length |
| KingSpots / KingSpotRing / KingPileCount | 6 / 20 m / 6 | 3–12 / 10–40 / 1–12 | void layout |
| KingBeamHeight / KingBeamAlpha | 40 m / 0.3 | 5–80 / 0.05–0.75 | beam look |
| KingMaterializeSeconds | 1.2 s | 0.2–4 | fresh pile's build-in |

## 5. What felt off in bot play

- **Ring Out sudden death stalls.** Once one 16 m lot remains, two bots often trade shoves until the 180 s limit
  (1 of 3 shipped-dial seeds in the final run, 2 of 3 in an earlier one). One melee launch (5 m/s, 1.6 lift) carries a body only
  1–2 m, so it takes several shoves at the edge, and both swing at once. The center lot has no piles by then (they
  sit on the first ring), so there is no blast to finish it.
- **Ring Out bots shove more than they shoot.** Pile blasts (the most Propane-like way to ring someone out) are
  opportunistic; most eliminations early on are the rim dissolving under bots or blasts at the piles.
- **King is a scrum.** Three bots all go straight for the zone, so it is contested most of the time; mints come in
  short bursts (12–29 per 180 s round at shipped dials). Banks swing hard because throws spill five; final banks are
  mostly from detonations and re-detonated spills, not minting.
- **King in a suburb barely engages**: 4 mints and no detonations in 180 s. Fences and houses break the bots'
  sight lines to tanks and the zone's piles sit in yards; the policy was tuned in the void.
- **Venting tanks deny the zone.** A punctured tank in the zone keeps navigation out until someone sets it off;
  the bots now do, from outside its throw.
- Earlier bot faults, fixed in the branch: King bots tried to stand in the zone's pile and in other players'
  capsules (route search ran to its budget every tick, ~18 ms per tick); a fresh pile could land on a bot's stand
  point; the headless "player bot" was silently replaced by device input; Ring Out bots orbited each other at the
  edge of melee reach.

## 6. Risks, open questions for the owner, follow-ups

Questions (current behavior is the default listed; nothing here changes Tank Bank):

1. Ring Out round limit: keep the 180 s limit with shared first, or end the last lot another way (shrink it, a final
   band, a pile on the last lot)?
2. Ring Out: should piles sit on the last lot so sudden death has blasts? Should melee launch harder in Ring Out?
3. Ring Out ammo: unlimited and no cans; shots at players do nothing. Should shots push?
4. King: does detonating tanks still bank (built: yes, "bank and spills work as in Tank Bank")? Limited ammo and
   cans as in Tank Bank (built: unlimited, no cans)?
5. King occupancy: downed (ragdolled) bodies neither hold nor contest (built). And a contest restarts the holder's
   2 s count rather than pausing it.
6. King zone spot choice in a suburb: tank groups move as piles are blown up; the spots are fixed at round start.
7. HUD: Ring Out's clock counts to the next dissolve, and turns red in its last 10 s (most of each 15 s band).

Framework gaps (recorded in `docs/modes.md`, not bent):

- Presenters and goal policies only see scores; prototypes read the rules directly through an optional
  `Rules`/`Clock` on `ModeHudInput` and `IBotWorld`, supplied only where a peer runs the rules (the Lab). A networked
  mode would put that state in its snapshot payload.
- `RoundOver` lets untimed rounds end; no server runner acts on it yet.
- **No hosted multiplayer Lab match**: the Lab has no networked session. Taking these modes online needs envelope
  schemas, `IModeWire` adapters and snapshot payloads for eliminations and the zone.

Risks and follow-ups:

- The `RifleRig` straight-arm tolerance touches shared player code (0.1 mm). Grip/motion suites pass.
- `MatchHud`'s second-counter spacing and feed label only show for modes that use them; Tank Bank never sets them.
- The dial sweep now covers 30 more dials (scoped to their scenario where they lay one out); `test-full-lab-3`
  takes longer.
- `BotMotor` could replace `BankingBot`'s private navigation glue later (separate proof needed).
- Captures are CPU-rendered at 30 fps with compressed timings (4 s bands, 10 s moves) so they finish here; frame
  times are not representative.

## 7. Compatibility

- **Shared tuners: none.** Every new dial is a local Lab dial (no `[Export]`): `Tuning.Snapshot()` and saved
  `.tres` files are unchanged (the Lab lifetime test asserts Lab dials stay out of the snapshot).
- **Messages: none.** No change to `Protocol.cs`, `MessageCodec.cs`, `Match.cs`, `LobbyServer.cs` or
  `SuburbGenerator.cs`. The prototypes are not in `ModeRegistry.All`, so no lobby, `MatchStart` or envelope schema
  can name them. `Protocol.Version` stays 2.
- `TankBankRules` gained an input Tank Bank never sends; its audit line and standings are unchanged. World hashes
  and formations: `make test`'s world comparison passes; `VoidEnvironment` places its floor exactly as before unless
  `FloorHeight` is set.
- **Minor release: not needed for this package.** It plays with every patch of 0.2 (the cycle's earlier mode work
  already needs a minor release, per `docs/modes.md`).
