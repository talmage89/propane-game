# A8: lobby bots — report

I worked on branch `claude/a8-bots-wj333u` (this session's assigned name for `claude/a8-bots`), starting from `origin/dev` at `e2dbe29`. It has 14 commits and is pushed. `make -j4 check` passes with 0 warnings.

**Main risk:** with 7 bots on one machine, this VM can't keep the 120 Hz physics rate. The owner needs to choose how to cap bot cost (details under Risks).

## 1. Summary by commit

- **`ed1d3b4` Difficulty profiles.**
  - `src/Bots/BotDifficulty.cs` adds `BotDifficulty` (Easy, Normal, Hard) and `BotProfile`.
  - Three new local dials in `Tuning.Bot.cs` set each profile's skill: `BotEasySkill` 0.2, `BotNormalSkill` 0.6, `BotHardSkill` 1.0. Like the other `Bot...` dials they are not exported, so they never enter the match snapshot.
  - Skill drives aim error, reaction time and aim turning through the existing Easy/Hard endpoint dials.
  - The Lab's difficulty button now uses these profiles. I kept `LabBotSkill` as the fine-tuning slider; the menu sets it to the profile's skill and shows the nearest profile.
  - Lab behavior is unchanged: its Easy, Normal and Hard were already 0.2, 0.6 and 1.0.
- **`b67a85f` Server validation.**
  - `AddActor`, `UpdateActor` and `RemoveActor` gain a trailing UInt32: the roster revision the request was made on. The server refuses a stale revision without changing anything.
  - Clients can add only bots, and can change only actors they own.
  - Bot names must be 1–16 printable characters with no leading or trailing spaces, and unique in the lobby ignoring case.
  - Capacity is checked as before, and the roster stays frozen during a match.
  - `NetClient` sends its current revision. Existing tests were updated.
- **`7bee18e` Runtime.**
  - New `src/Net/Match.Bots.cs`: each bot actor the peer owns gets `ModeDefinition.CreateBot`, seeded from the match and actor ids, at the skill of its frozen difficulty.
  - It runs every physics tick through an allocation-free `IBotWorld` adapter, and keeps an up-to-date list of the other actors when a peer leaves.
  - Bots have no view, camera, audio listener or HUD.
  - `BotCost` counts think calls, time and bytes allocated.
  - Unit suite `BotTests` (9 tests).
- **`8746036` Lobby UI.**
  - An "Add bot" row in the lobby, an "Edit" button on each bot you own, and "(Easy|Normal|Hard bot)" labels.
  - New `src/UI/BotEditor.cs` dialog: a name (defaults to the first free "Bot N"), a color (the 8 presets or "Custom color" through `AppearancePicker`), Easy/Normal/Hard, and Save, Remove and Cancel buttons.
  - Works with mouse, keyboard (Esc cancels) and gamepad (B cancels). A refused request stays open showing the server's reason.
  - `UiStyle.Swatch` is now shared by the lobby and the dialog.
  - While an overlay is open, lobby rebuilds no longer steal focus from it.
  - New `menu_test --scenario=bots`, added to `make test` and `lint_tests.py`.
- **`80a1415` net_test tooling.**
  - `--lobby-bots=N` (or a per-client list such as `2,1`) and `--lobby-bot-difficulty`.
  - `--leave-who` limits `--leave-at` to one client.
  - `--melee-start` now stages every owned actor; it used to crash when a pair's anchor was one of the client's own bots.
  - `net_bots.sh` leaves a deliberate leaver's log out of the completion checks, with a launcher test.
- **`b3dbfc8`, `c49f85a` Docs.**
  - README, `multiplayer.md` (new "Lobby bots (A8)" section, and the stale "grayed out" color wording fixed), `bot-navigation.md`, `lab.md` and `modes.md`.
  - `network-soak.md` gains the soak and cost evidence and the reruns for real hardware.
- **`04ccbc4`, `a4646f0` Cheaper navigation, same results.**
  - Navigation query objects are reused for each brain, and the brain caches its capsule.
  - A jump link checks its pure range test before its walk test.
  - Each per-tick `NavigationWorld` remembers its standable and supported points.
  - Physics queries on the five-seed course fell from 210,188 to 131,896 (37%). The 20-seed course output is identical to `dev`'s navigation (140/140, same per-course times and distances).
- **`d25a72b`, `43756e2`, `f322dd8` Measurement aids.**
  - `--human=idle` keeps the client's own player still, so costs measure the bots alone.
  - `--frame-metrics` now also works headless.
  - `--look-at-bots` keeps the observer still, facing the nearest remote bot, for captures.
- **`e9545e1`, `36b9cce` Fixes from my own review and the gate.**
  - The Edit button is compact, so a 16-actor lobby fits at 720p; the bots test checks this.
  - A new lobby screen still opens with focus on the Name field, which the existing gamepad test expects.

## 2. Verification

**Gate and unit tests**
- `make setup`: passed.
- `make -j4 check`, final run: exit 0. Lint and build report 0 warnings and 0 errors. Unit tests: 307 tests, 0 failures. All menu, Lab and other scenes passed.
  - The first gate run failed in `menu_test --scenario=gamepad` ("expected focus Name, got add_bot"). Commit `36b9cce` fixes it.
  - The one runtime WARNING is the intentional malformed-score probe, which `dev` also logs.
- `godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/unit_tests.tscn`: 307 tests, 0 failures.
- `python3 -B tools/dev/net_bots_tests.py`: OK.
  - It failed twice early on, while a rendered Godot run was loading the CPU at the same time. It then passed 18 of 18 runs with my change and 12 of 12 without it, plus the gate run.

**Menu scenario**
- `menu_test --scenario=bots`, headless: PASS, 112 assertions.
- Same scenario rendered at 1280×720 under xvfb: PASS, 112 assertions.
  - An earlier rendered run timed out after 15 s waiting for the color picker to open. That did not happen on the next two rendered runs.

**Navigation equivalence**
- `bot_navigation_course --seeds=5`, before vs after: identical; 35/35, 0 stuck.
- `bot_navigation_course --seeds=20`, `dev`'s navigation vs mine: identical output; 140/140, 120 jumps.

**Soaks** (90 s, `--melee-start`, each client owning 1–2 lobby bots)
- All passed tank agreement, the score check and the server's conservation audit, with 0 logged engine or protocol errors.
- Faulted (120 ms RTT, 30 ms jitter, 3% loss), seeds 24 and 25: longest disagreement episode 0.55 s and 0.40 s.
- Clean, seeds 26 and 27 (27 with all bots on Hard): longest episode 0.10 s and 0.95 s.
- Creator leaves at 40 s, faulted, 3 clients, seed 28:
  - It passed, and the leaver's 2 bots kept their banks in the server standings.
  - Both survivors agree with those standings, and they no longer list the departed actors.

The exact commands are in `docs/network-soak.md`. I did not see the intermittent loading-readiness failure.

**Screenshots I looked at** (not committed)
- In `build/a8/menu-bots-shots/`:
  - `01_add_dialog.png` and `02_edit_dialog.png`: the add and edit dialogs.
  - `03_lobby_bots.png`: the lobby with a Hard bot in a custom sky-blue color and its Edit button.
  - `04_lobby_full.png`: the server's "That lobby is full." shown in the dialog.
  - `05_lobby_full_of_bots.png`: 16 actors that fit on screen, with Add bot disabled.
- Remote client's view, in `build/a8/remote3/`:
  - `crop_Bot1_004.0.png`: another client's bots in play, with score tags in each bot's color ("13" in yellow and "0" in blue).
  - `remote2/sheet.png`: the remote results screen listing "Bot2 bot 1" and "Bot2 bot 2".

**Cost** (headless, 1 human standing still, map seed 23)

| Bots | Wall p50 / p95 | Physics p50 | Brain time per think | Thinks per bot per second |
| --- | --- | --- | --- | --- |
| 0 | 16.7 / 17.4 ms | 2.0 ms | — | — |
| 3 | 17 / 57–109 ms | 18–19 ms | 1.1–1.5 ms | 120 |
| 7 | 21–155 / 300–840 ms | 24–35 ms | 1.1–1.8 ms | 55–80 |

- Ranges cover two runs, one before and one after the navigation savings.
- Rendered runs went at 1–2 frames a second from CPU rendering alone, so they can't show the bots' cost.
- The brain allocates 14–64 kB per think. That is the unchanged `BankingBot` LINQ and the physics query results; the runtime glue allocates nothing per tick.

## 3. Risks, open questions for the owner, and follow-ups

**Risks**
- **Cost (main risk).** About 90% of a bot's time is route search: up to `BotSearchPerTick` nodes per bot per tick, each costing dozens of physics sweeps. All bots run on their creator's main thread. On this VM, 3 bots hold 120 Hz but 7 do not. Bounding the cost regardless of bot count would change bot behavior, so it's the owner's decision. Options:
  - **(a)** One search budget per peer, shared by its bots: route planning gets slower when several bots plan at once.
  - **(b)** Bots think less often than every tick, holding their input in between.
  - **(c)** Limit how many bots one player can add.
  - The lead should first re-measure on real hardware.
- **Name rules.** Server validation is stricter for bot names than for player names: player names are still cleaned and accepted. Bot names also must not match a player's name, but a player may still rename themselves to a bot's name.

**Open questions for the owner**
- **Difficulty values.** Normal is the Lab's default (skill 0.6), and Easy 0.2 and Hard 1.0 are the Lab's existing values, so nothing plays differently. Resulting dials:

  | Profile | Skill | Aim error | Reaction | Aim turning |
  | --- | --- | --- | --- | --- |
  | Easy | 0.2 | 0.92 m | 0.44 s | ×0.48 |
  | Normal | 0.6 | 0.57 m | 0.32 s | ×0.74 |
  | Hard | 1.0 | 0.22 m | 0.20 s | ×1.0 |

  The `add_bots.sh`/`net_bots.sh` test bots default to skill 1, which is Hard. If "today's bots" meant those, Normal would need to be 1.0, and Hard would need new values beyond the current endpoints.
- **Player-facing text.** Please review the wording I wrote:
  - "Your bots play on this computer and leave with you."
  - "Slower to react, with looser aim." / "Steady reactions and aim." / "Quick to react, with tight aim."
  - "Edit", "Add bot", "(Hard bot)".
- **Release rule.** A release build can now start with one human and one bot, because the two-player rule counts bots. Is that intended?

**Follow-ups**
- Splitting goal selection out of `BankingBot` wasn't needed and wasn't done.
- `BankingBot`'s per-think allocations could be reduced; it needs its own behavior proof.
- Godot rendered audio needs `--audio-driver Dummy` on this VM.

## 4. Compatibility

- **Messages:** `AddActor`, `UpdateActor` and `RemoveActor` each gain a trailing UInt32 roster revision. `Protocol.Version` stays 2. Hello, Welcome and Rejected are unchanged.
- **Tuners:** the new dials (`BotEasySkill`, `BotNormalSkill`, `BotHardSkill`) are local and not in the snapshot. No shared tuner, tuner value, construction rule or world hash changed.
- **Behavior:** navigation results are identical, and the bot brain's decisions are unchanged.
- **Release:** the roster request change means the next release must be a minor release. A6 and A7 already require one.
