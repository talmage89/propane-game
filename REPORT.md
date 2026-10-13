# A11b part 2: Short Fuse and the optional health state

Work branch: `claude/short-fuse-health-3kr38y` (this session's harness assigned that name; the brief said
`claude/a11b-short-fuse`). Report branch: `claude/short-fuse-health-3kr38y-report` (this file and `review/`).
Based on `origin/lead/a11b1-verify` (dev + part 1), including its later commit `5bfb901`. `origin/dev` has not moved
since (`7357076`), so it is already in the branch and the gate ran on the merged head.

## 1. Summary by commit

- **385e9d2 Add the optional health state and Short Fuse's pure rules.**
  - `src/Modes/Health.cs`, `HealthState`: a reusable framework piece that is pure and has no nodes.
    - Typed damage: bullets deal a flat amount; blasts fall off linearly to the throw radius and deal nothing beyond.
    - Attribution: credit goes to the last other damager within a window. A chain is credited to its first detonator.
    - Also covers regeneration, the get-up grace, granted invulnerability, death, respawn (life ids only advance)
      and departures.
    - Damage arrives as reports:
      - `BulletHitReported`, from the shooter.
      - `BlastHitReported`, from the victim, naming a blast and waiting for `BlastAccepted`, as the causal gate would.
      - `BlastAccepted` / `BlastRefused`, from the ledger.
    - Validation covers known attacker and victim, lives at that time, the causal blast, the time window, duplicates
      and self hits. The damage amount is always the rules' own, never the reporter's.
    - Order independence: accepted damage is replayed in event-time order. Because anything older than the report
      window is refused, a death is declared once the window has passed it, and is then final. Arrival order therefore
      never changes health, deaths or credit.
  - `ShortFuseRules` composes the health state with rounds, the match and the limit rule.
  - `ShortFuseHud` presents the HUD; `ModeHud` gains an optional `HudBar`.
  - `HealthTests`: 12 unit tests.
- **403f7df Play Short Fuse as a Lab prototype against Lab bots.**
  - The Lab scenario sits under Prototype modes (suburb only). It is not in `ModeRegistry.All`.
  - `PrototypeRunner` feeds the reports along today's authority lines:
    - The shooter's `ShotImpacting` reports its bullet hit.
    - Inside each detonation, the explosion is accepted first (credited to `ChainCause` or the direct shooter). Then
      every actor within the ragdoll radius reports it with its own pre-push distance.
    - Samples carry the down state that opens the get-up grace.
  - `LabMatch` carries the rules, round and mode clock across the reset that builds each round's suburb (the match
    seed stepped by round).
  - `ShortFuseArena` handles rim spawns and death: the body goes limp, lies for a moment, then is parked hidden on a
    ledge 600 m below. The local camera then follows the rules' pure `SpectateTarget`.
  - `ShortFuseBot`, the bot goal policy.
  - Two `PlayerCharacter` hooks: `Collapse()` and `Spectating`.
  - `MatchHud` draws the bar.
  - The Lab overlay shows the match status and result.
  - The `shortfuse` match scene, and its `check_lab` session.
- **9100d45 Script a Short Fuse round through the real reports, and gate full matches.**
  - `--scenario=shortfuse-combo`: Bot 2 shoots the player dead, and the player spectates Bot 2. Bot 2 then sets off a
    pile beside Bot 1 and finishes it with bullets while it is down, and wins. The next suburb builds with new lives.
  - `shortfuse-full` runs in `make test-full`.
  - Bot detours.
- **34e9b8f** Formatting. **ecf43ff** merged `origin/lead/a11b1-verify` (`5bfb901`). **a344b98** pins the new Lab
  sessions in `lint_tests.py`.

### Rules as built (every number a Lab dial)

- 100 HP. A bullet deals 7. A blast deals 90 at its center, falling linearly to 30 at `RagdollRadius` (6.5 m, the
  shared tuner, read and not changed), and nothing beyond.
- Regeneration is 5 HP/s after 5 s unhit, capped at full; 0 disables it.
- After a throw, 0.75 s of protection starts when the body is back under control (down → up in the samples).
- Kill credit goes to the last *other* actor to damage the victim within 8 s; for a blast, that is its chain's first
  detonator.
  - The feed: "A shot B", "A blew up B", "A got B" (credit for someone else's blow, for example B's own blast within
    8 s of A's damage), "B blew themself up", "B went down".
  - A dead detonator's chain keeps its credit. A bullet fired after its shooter died counts for nothing.
- Report window: 0.25 s. A death is declared 0.25 s after the lethal blow (the bar shows 0 at once).
- Rounds:
  - A round has a 105 s limit. Players start spread around the rim of a 150 m suburb, stocked for the player count
    as in a match.
  - Last alive wins the round. If the last two go down together, nobody wins it.
  - At the limit, the healthiest survivor wins; a tie wins nobody.
  - The round result shows for 4 s, then a new suburb builds.
  - First to 3 round wins takes the match.
- Unlimited ammo, no cans. The dead spectate; they watch their killer, then that killer's killer, and so on.

### Tuners added (all local Lab dials in `Tuning.Prototype.cs`, `Scenario = "shortfuse"`; none shared)

`ShortFuseRoundSeconds` 105, `ShortFuseMapSize` 150, `ShortFuseWinsToMatch` 3, `ShortFuseMaxHealth` 100,
`ShortFuseBulletDamage` 7, `ShortFuseBlastCenterDamage` 90, `ShortFuseBlastEdgeDamage` 30, `ShortFuseRegenPerSecond` 5,
`ShortFuseRegenDelay` 5, `ShortFuseGetUpGrace` 0.75, `ShortFuseCreditSeconds` 8, `ShortFuseReportWindow` 0.25,
`ShortFuseRoundEndSeconds` 4, `ShortFuseCorpseSeconds` 1.5. The rules also read the existing `RagdollRadius`.

## 2. Verification

All runs were on the final work head `a344b98` (dev + part 1 + this work), on the 4-vCPU VM.

- **`make setup`**: passed, 0 warnings.
- **`make lint`**: passed, 0 warnings, 0 errors. Run first, then `make -j4 test-full`, which runs the whole `make test`
  gate first (together, the same suites as `make check`).
- **`make -j4 test-full`**:
  - Every suite and deep shard passed:
    - `[unit] 328 tests, 0 failures`.
    - "Separate-process world builds agree for all four formations".
    - Generator shards 1–4 "ALL PASSED", and coverage of exactly 944 seeds.
    - Navigation 100 seeds.
    - Piles: stand, topple and gather.
    - Range.
    - Menu lifetime.
    - Lab: all fast sessions, then lab-1 (soak-void, **shortfuse-full**), lab-2, lab-3 (with dials, which now include
      every Short Fuse dial at both ends), lab-4.
  - The faulted `rifle-soak-gate` (4 bots, `--melee-start`, 120 ms lag, 30 ms jitter, 3% loss) **passed on the first
    run**: 0 tank disagreements, final moving 0.131 m, "All 4 bots agree with 1 server audits". No rerun was needed.
  - Then the session's 30-minute background limit stopped the job during the last two bot runs (my own harness limit,
    not a failure). I reran those two commands alone, exactly as the Makefile runs them:
    - `duel-lane-gate`: passed (2 matches, all audits agree).
    - `loading-readiness-gate`: passed.
- **Clean Tank Bank soak**: `tools/dev/net_bots.sh -n 4 -l 90 -t clean -o $PWD/build/soak-clean -- --melee-start
  --digest-every=0.5`. Passed: 0 disagreements, final moving 0.153 m, "All 4 bots agree with 1 server audits".
- **Engine errors**: 0 `ERROR`/`WARNING` lines across the faulted, clean, duel-lane and loading soak logs.
- **`python3 -B tools/dev/lint_tests.py`**: all PASS, including the new Lab shard pins.
- **Short Fuse matches, headless** (`lab_test --scenario=shortfuse`):
  - `--quick --seeds=1`: 4 rounds, legal, 113 s wall.
  - `--seeds=2 --first-seed=11` (shipped dials): 13 rounds, 26 deaths, 1 finished while down, 0 refused reports in
    the Lab, PASS.
- **Scripted round, headless** (`--scenario=shortfuse-combo`): PASS. The audit reads `accepted=23 refused=1 Dead=1`
  (one shot into a corpse).
- **Captures**: `--scenario=shortfuse-combo --windowed --resolution 1280x720 --capture-dir=…`, rendered on the CPU.
  - I looked at each one. They are in `review/` on the report branch:
    - Chip damage with the health bar.
    - Spectating ("SPECTATING BOT 2", the feed reading "Bot 2 shot Player").
    - The blast throwing Bot 1.
    - The blast-then-bullet kill, with both feed lines.
    - The round-win banner ("BOT 2 WINS THE ROUND").
  - The rendered run hit my 25-minute cap just after the banner frame, so it has no "round two" still. The same step
    passes headless. I made no Movie Maker clip: CPU rendering took about 10 minutes per scripted step.
- **Unit tests (`HealthTests`)**: damage typing and falloff, attribution windows, chain credit (also after the
  detonator dies), regeneration, the get-up grace and invulnerability, death during a ragdoll, departure while dead and
  alive, every refusal reason, the moment a death is declared, the closed round, and the match/limit/departure/HUD
  rules. One scripted four-player fight (shooter and victim reports) is delivered in order and then under 40 fault
  schedules (delays within the window, drops resent, duplicates, reordering): every schedule gives the same deaths,
  credit and health. Reports past the window are refused as if lost, on 10 more schedules.

## 3. Risks, open questions for the owner, follow-ups

### What felt off in bot play

- **Bots rarely set up the combo.**
  - Across 2 full-dial matches (13 rounds, 26 deaths), only one kill finished a thrown player.
  - Almost every kill was chip damage: bots trade bullets at 20–37 m, and 15 hits kill.
  - The combo is rare because bots keep their navigation clearance from tanks, so they seldom stand within a throw
    of a pile. A player who loiters by piles will feel it much more.
  - The mechanic itself is proven by the scripted round (blast then bullets, the kill credited to the shooter).
- **Rounds are short.** Many end in 8–30 s, because rim spawns on a 150 m slab can be in sight of each other across
  open roads. The 200 m match-sized slab had the opposite problem (long walks, stalls), hence the dial.
- **Hunting stalls.** Bots hiding behind the same house used to deadlock at 11–14 m; a detour after 3 s without
  closing fixed the case I saw. Bot hunting in a suburb is still the weakest part.
- **Spectate camera framing.** After a death the camera orbits the killer at the dead player's last yaw, so the
  action can be off-frame (in the review capture, Bot 1's thrown body is out of shot).

### Open questions for the owner

1. **The round limit.** The starting rules give no tiebreak. As built, the healthiest survivor wins and a tie wins
   nobody. Alternatives: sudden death, or a draw for everyone alive.
2. **Draws.** A drawn round wins nobody, so a match could in principle run on. Should there be a round cap?
3. **The get-up grace.** It starts when the player regains control (the get-up animation has finished). The
   alternative is to start it at the ragdoll's end (the start of the get-up), which would make a downed player
   protected sooner.
4. **The death delay.** The report window (0.25 s) is the price of order-independent deaths: a death lands that long
   after the blow. Is it acceptable on a server?
5. **Melee.** Melee does no damage, and a shove that throws someone is not "damage" for credit. Should melee damage,
   or count as an assist?
6. **Ammo and slab.** Unlimited ammo and a 150 m slab are my choices, not given. Limited ammo with cans would slow the
   chip duels.
7. **Kill-feed wording.** A blast-then-bullet kill reads "A shot B". Would "A finished B" sell the combo better?

### Follow-ups and risks

- **Networking is not built.** The reports are shaped as envelope event kinds (the blast report declares its causal
  dependency on the explosion request), but schemas, wires and the server's rebuild-per-round (`NextRound(RebuildWorld)`
  currently ends a server match) are still to do. See `docs/modes.md#health`.
- **Victim-side blast reports can be under-reported** by a modified client. This is the same trade victim-side
  ragdolls already make.
- **`PlayerCharacter` changes.** `Collapse()` fires `Ragdolled` like any throw; `Spectating` only changes the camera
  focus of a local actor. Neither runs outside Short Fuse.
- **Lab menu change.** `LabMenu` now starts a scenario in its first allowed environment, rather than always the void.
  This matters only for suburb-only scenarios.
- **`.uid` files.** Part 1's and my new scripts have untracked `.uid` files. I followed part 1 and did not commit them.

## 4. Compatibility

- **Untouched:** `Protocol.cs`, `MessageCodec.cs`, `Match.cs`, `LobbyServer.cs`, `ModeEnvelope.cs`, `ModeRunner.cs`,
  `TankBankRules.cs` and everything in `src/Net` and `src/World`. `Protocol.Version` is unchanged.
- **No shared tuner added or changed.** Every new dial is a Lab dial (no `Export`), excluded from snapshots and `.tres`.
- **Short Fuse is unreachable from a match:** it is in `ModeRegistry.Prototypes` only, not in `ModeRegistry.All`, the
  lobby or `MatchStart`.
- **`ModeHud` gained a trailing optional field** (`Bar`), which is local HUD data that is never sent.
- **Tank Bank:** world hashes and formations (`test-world`), the generator, the mode suites and the soaks below are
  unchanged.
- **Release:** no minor release is needed for this package; a patch is enough. It changes nothing players in a match
  share.
