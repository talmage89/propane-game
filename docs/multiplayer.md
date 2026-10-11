# Multiplayer

How multiplayer works today: tank bank's rules, the networking model, hosting, bots and tests. It describes the
code; when the code changes, change this file with it. The game's lasting direction is in [design](../design/).

## Tank bank

A free-for-all in one suburb. **Every tank a player detonates is credited to that player as one point, which they
hold**, wherever the tank came from: the map, another player's drop, or their own. Players lose points only by being
hit or thrown. Every gain and loss of tanks follows from that rule; there are no special cases. Most points when the
clock runs out wins.

- A chain reaction credits every tank in it to whoever detonated the first one.
- A bullet hit drops 1 tank behind the victim, then a 0.5 s grace period before another hit can drop one.
- Being ragdolled drops 5 tanks where the player was thrown, even by their own blast. A ragdolled player can still be
  shot.
- Dropped tanks ignore bullets and chain reactions for 1 s, glowing in the dropper's color. A player with no points
  loses nothing.
- Fewer, bigger piles nearer the center keep the fighting close. The suburb's tanks are all there will be.
- Ammo is limited; ammo cans around the suburb come back after being taken.
- Each player's score floats above them in their color, in line of sight. A shooter sees a hit marker, a scorer a
  "+N", and a victim an arrow toward the shot.
- No health: nobody dies.

The counts and times are tuners (the Match, Tank bank, Multiplayer suburb, Ammo, HUD and Network groups of
`src/Core/Tuning.cs`).

## Match flow

1. **Main menu:** single player (the sandbox), multiplayer or quit.
2. **Multiplayer:** the player connects to a server, then creates a lobby or joins an existing one.
3. **Lobby:** each player sets a name and color. The creator sets the match length and starts the match.
4. **Match start:** the server sends the suburb's plan. Each client builds it, it materializes as in single player, and play begins when the timer starts.
5. **Match:** runs for the lobby's match length.
6. **End:** a results screen, then back to the lobby.

The server waits up to 20 seconds for loading acknowledgments before counting down anyway. The client's connection
buffers every current-match event until the match scene has initialized its bodies and HUD, then replays them in
receive order once. A late builder therefore receives Countdown, Go, scores and even End. Leaving or disconnecting
clears the buffer; a new MatchStart invalidates the old queue and receiver, and old scene cleanup cannot clear the
new queue. The inbox is capped at 4096 packets and 4 MiB of wire data; exceeding it disconnects with an explicit
loading-buffer message rather than continuing with missing events.

## Changes from single player in a match

- **Hitstop** is off. It changes `Engine.TimeScale`, which also slows the physics and would make the clients drift apart.
- **Pause on focus loss** is off. Esc or Start / Options opens a menu without stopping the game.
- **R** reloads instead of generating a new suburb. The left hand swaps the magazine and the empty one drops to the ground: it is only for show, so each game drops its own and nothing shoots, blasts or syncs it. An empty rifle locks its bolt open until the reload releases it.
- **The tuning panel** is debug-only.

## Networking

Every client simulates the whole suburb, so the game responds instantly. Each moving object has one owner, which corrects the other copies. Small disagreements between screens are accepted in exchange for that responsiveness. This approach is called state synchronization with distributed authority.

**The server** runs no physics. It hosts the lobbies, relays messages within each match, keeps the score and the match clock, and settles ties: when two claims for the same thing arrive, the first one wins. It runs headless on a dedicated server, or inside a player's game.

Packets are completely decoded into typed data before any state changes or relays. Unknown message types, wrong
directions, truncated or trailing payloads, invalid counts and ids, nonfinite numbers, invalid rotations and values
outside wire limits are dropped and logged. Packets are capped at 1 MiB, collections at 4096 entries (body batches
at 20), strings at 1024 UTF-8 bytes, and vector components at one million meters. These are wire safety ceilings;
the match's tuners still determine gameplay. Hello, Welcome and Rejected allow trailing extensions so incompatible
clients can still be told why they were rejected.

**The world.** The server generates the match's suburb and sends the whole plan (about 10 KB compressed) rather than just its seed. The generator's trigonometry can differ in the last bit between an Apple Silicon Mac and an x86 Linux machine, and a single flipped comparison would give the players different suburbs. Houses and other static parts never need syncing.

Construction choices are pure functions of that plan and the shared tuning. Fence style and color use explicit
integer mixing of the run's coordinates and seed, never a process-salted hash. `make test` hashes the built meshes,
transforms, body masses and collision shapes (fences included) in two fresh processes using one saved plan.

**Tanks:**
- *Puncture:* the shooter's client punctures the tank at once. It then sends the cause to everyone: the hole position and normal, the jet's lean, and the bullet's push. Every client starts the same vent from the same starting point.
- *Ownership:* whoever punctured a venting tank owns it and sends its position and velocity at a set rate. Other clients ease toward that position, and snap only if they have drifted far.
- *Sleeping tanks:* intact tanks sleep, so they stay in sync without any messages.
- *Detonation:* whoever detonates the first tank in a chain runs the whole chain on their client and sends each explosion with its position. Other clients play those explosions and don't run chain reactions themselves for remote blasts.
- *Scoring:* the server credits the point only for a tank in its ledger that has not already exploded. If two players claim the same tank, the first claim wins. Map tanks are registered from the plan; client-created drops are registered atomically before being relayed. Exploded ids remain reserved for the whole match.

**Players:**
- Each client owns its own player's movement and sends it to the others, who show it smoothed. Player snapshots
  include discrete gait, support phase and weapon action alongside the blended rifle poses, so remote creep uses
  the walk clip and remote sprint uses the sprint clip. Protocol 2 adds these three bytes; this needs a minor release.
- The shooter's client decides whether a bullet hit a player.
- The victim's client decides whether a blast ragdolls them, because it knows exactly where they were.
- The victim's client spawns the dropped tanks and owns them until another player interacts with them.

A drop must contain unique, unused ids from that player's spawn slot, use that player's match color, and fit both
their bank and the drop reason's tuner. The server refuses the whole batch if any entry is invalid. Scoring and
spilling happen only while the server's clock is playing; late explosions can still be relayed for their effects
after time expires, but earn nothing. Known exploded body ids may appear in late snapshots or claims, which clients
ignore; body ids that never existed are refused. A client missing a server-accepted tank still plays its reported
blast, including throws, without running a chain or claiming credit locally.

Drops are predicted locally. The server echoes an accepted Drop to its sender as well as the other players, and
replies with `DropRefused` ids for a fully decoded batch it cannot accept. The sender removes only those pending
predictions and releases their bank reservation. This also handles legitimate drops overtaken by a live debug
drop-count change or the server's match deadline. Malformed packets still get no reply.

The ledger proves existence and prevents duplicate scoring; it does not prove hits, movement, proximity or blast
positions. Those remain decisions of the distributed client authorities.

Moving and final resting body states carry the owner's timestamp. Clients compare both against one ordering
boundary, retained after a correction finishes, so an unreliable snapshot sent before a reliable rest cannot
resume stale motion afterward. Genuinely newer movement still wakes the copy; changing owners resets the boundary
because sender clocks are process-local.

**Left to each client:** debris, small props and fence panels play out locally from the shared blasts. Cars are synced at a low rate, because players use them as cover.

**Platforms.** Windows, macOS and Linux builds of the same minor version play together (0.2.0 with 0.2.5). A version check when a player connects turns away builds of another minor version or protocol, and says which version is needed (see Versions and releases in the README).


## Playing

- **Main menu:** Single player, Multiplayer, Quit. Single player is the sandbox as before; its pause menu gains a Main menu button.
- **Multiplayer:** (or start the game with `-- --connect=host[:port]`) type a name and the server's address (`host` or `host:port`; the port defaults to 24680, UDP). **Host on this computer** runs a server inside the game and joins it, for playing without a dedicated server; the lobby screens then show the addresses others can connect to.
- **Lobbies:** create one or join one from the list (lobbies in a match can't be joined). In the lobby, everyone picks a name and a color (a color someone else has is grayed out), and the creator sets the match length and presses Start. A release build needs two players to start; a debug build may start alone, for testing.
- **In a match:** the clock and the tanks you have banked are at the top, ammo at the bottom right. R reloads (an empty magazine also reloads on the next trigger pull). Walking over an ammo can takes it, unless the reserve is full. Esc or Start / Options opens the controls with Resume, Leave match and Quit; the match keeps running behind it. Leaving a match also leaves the lobby.
- **After a match:** the results show for `ResultsTime`, then everyone is back in the lobby, ready for another.

## Playing against bots

`tools/dev/add_bots.sh -S host:port` starts bot players (4 by default) on that server, from a source checkout; without
`-S` they join a server on this computer. Create a lobby first: they join the first open lobby and play every match it
starts. `-k 0.5`
makes them easier (slower to turn, looser aim, slower to react), `-b simple` gives them the first, easy brain, and
`-x` stops them. Each uses about 7% of a core and 0.6 GB while it waits.

The bot (`src/Dev/Bot.cs`) plays to win:
- it sets tanks off from outside the throw radius, wider for a pile, puncturing and detonating in one burst, from a
  spot with a clear view that it finds and walks to;
- it runs from venting tanks that come close;
- it shoots players only when it pays: those holding a few tanks, those close by holding any, or whoever just shot it;
- holding more than the player shooting it, it breaks their line of sight instead of trading hits;
- holding 4 or more while leading, or late in the match, it stays out of everyone's sight and only goes for tanks
  with nobody near them;
- its aim is a person's, not perfect: it reacts after a moment, and its aim wanders and tightens while it tracks;
- it taps tanks, fires short bursts at players, and fetches ammo when low or close.

Measured over two-minute matches of four bots each, before and after (the server logs each player's tanks gained,
lost to hits and lost to throws at the end of a match): the first bot often threw itself with its own blasts and
ended with almost nothing; the current bots never throw themselves on purpose, and the leader usually ends with 8–17
banked. Against the first bot, two current bots ended 15 and 14 to 1 and 0, and 8 and 6 to 3 and 1.

## Hosting the server

The server is the game itself started with `-- --server` (and `--headless`). It runs no physics and uses little CPU.

- **Docker:** `tools/server/deploy.sh <ssh host> <dir>` exports the Linux build and runs it in Docker on that host,
  in `~/<dir>` (container `propane-server`, `restart: unless-stopped`, UDP 24680). Run it from the latest release's
  commit (`main`): it then plays with every patch of that minor version. Logs: `docker compose logs -f` in that folder.
- **Reaching it:** players connect to the machine's address on UDP 24680: its LAN address, its address on a VPN the
  players share, or, with UDP 24680 forwarded on the router, the public address.
- **From source:** `godot --headless --path . -- --server [--port=24680]`.

## Code

| Path | Contents |
| --- | --- |
| `src/Main.cs` | The root scene: menus over a backdrop, single player, the match, or server mode. |
| `src/Net/LobbyServer.cs` | The server: lobbies, match flow and clock, relaying, scores, first-claim ties, pickups. |
| `src/Net/NetClient.cs`, `NetTransport.cs`, `Protocol.cs` | The client connection, the ENet transport (with simulated lag for tests), the message types. |
| `src/Net/MessageCodec.cs`, `TankLedger.cs` | Complete, typed wire decoding and the server's tank existence/lifecycle ledger. |
| `src/Net/MatchInbox.cs` | Per-match buffering until scene initialization, with generation-safe cleanup. |
| `src/Net/BuildInfo.cs`, `UpdateCheck.cs` | Versions and who plays together; the launch check for a newer release. |
| `src/Net/Match.cs` | A match on one player's machine: everything this player sends, and playing what the others send. |
| `src/Net/BodySync.cs` | Ownership and corrections for tanks and cars. |
| `src/Net/SnapshotBuffer.cs` | Smooth playback of other players, `InterpolationDelay` behind. |
| `src/Net/PlanCodec.cs` | Packs a suburb plan for sending. |
| `src/Net/AmmoPickup.cs` | The ammo can and its build-up reveal. |
| `src/UI/Menus.cs`, `MatchHud.cs`, `MatchMenu.cs`, `ResultsScreen.cs` | Front end, match HUD, pause menu, results. |

Details settled while building, within the decisions above:

- **Remote players** are full copies of the character (animation, rifle, ragdoll) driven by the states their own game sends. A thrown player's ragdoll falls on its own on every screen, steered after the real one; it gets up where the real one got up. Shots hit other players' bodies and limbs. Players pass through each other.
- **Ownership** goes to whoever last disturbed a body (a shot, a puncture, a blast, a push, a drop); the match's creator owns everything nobody has touched, and a player's bodies pass to the creator (or the next player) if they leave. Claims go through the server, which echoes them to everyone in one order, so all players agree on each owner. A handoff clears the former owner's correction target and clock comparison. The new owner sends its final state even if its copy was already asleep, so nobody is left correcting toward the former owner forever.
- **Dropped tanks** glow in the dropper's color while they ignore bullets and chain reactions.
- **Tuning in a match:** everyone plays on the creator's values: the baked ones in a release build, the creator's live ones in a debug build. Only that original creator's debug build gets F1 and may change the match's values. Hosting succession does not transfer this permission. Hello appends a debug-build flag, captured at match start; this is a client capability declaration, not remote build attestation. Tuning crosses the wire as named bool/int/float scalars, with exact exported types and inclusive declared ranges checked before applying the whole snapshot or a change. `FixedSeed` accepts the full int32 range. The player's own values come back after the match.
- **Starting values** (all tuners): 40 tanks in piles of 5–12 within about 40 m of the center; a 30-round magazine, 60 in reserve, at most 150, a 1.6 s reload; 8 ammo cans of 30 rounds that come back after 20 s; a 2:30 match after a 4 s countdown; results for 10 s.

## Testing

| Command | Checks |
| --- | --- |
| `make test` | Plain C# assertion suites (every wire message, malformed payloads, server ledger and permissions, lifecycle buffering and ownership), score-checker regressions, movement, gamepad menu, device switching and platform checks, generator checks (100 seeds), and separate-process built-world hashes. Add suites under `src/Dev/UnitTests/` and register them with `TestRunner`; any failure prints its name and exits nonzero. |
| `godot --headless res://scenes/dev/generator_tests.tscn -- --seeds=100` | Match suburbs too: a start per player, spread apart and clear; ammo spots; tanks nearer the center; the plan unchanged through `PlanCodec`. |
| `tools/dev/net_bots.sh -n 3 -l 60 -- --net-lag=120 --net-jitter=30 --net-loss=3` | A local server and bot players (`scenes/dev/net_test.tscn`) that run to tanks, shoot them and each other, and fetch ammo, under simulated lag and loss. `-w N` shows N of them in windows; `-S host:port` uses an existing server. |
| `tools/dev/compare_tanks.py out/bot*.log` | Where each bot had every tank at the same match times: counts, states and drift. |
| `tools/dev/check_scores.py out/server.log out/bot*.log` | Server conservation audit and identical per-player final scores on every bot; exits nonzero on a missing audit or disagreement. |
| `net_bots.sh -n 2 -w 2 -s "--start-score=8" -- --scenario=duel --capture-dir=DIR` | Scripted close-up: hits with the grace period, dropped tanks, a blast that throws the victim, from both sides. |
| `net_bots.sh -n 2 -l 45 -w 1 -- --scenario=movement --capture-dir=DIR` | Scripted remote creep, sprint, air aim suppression and held-aim landing through real snapshots; exits 1 on missing states. |
| `net_bots.sh -n 1 -w 1 -- --scenario=pickup --capture-dir=DIR` | Taking an ammo can and its return. |
| `net_bots.sh -n 2 -w 2 -l 10 -s "--start-score=8" -- --scenario=drop-refusal --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir=DIR` | The creator lowers the hit-drop count while a hit is in flight; the victim rolls back the refused drop, both clients keep one accepted tank and a bank of 7. |
| `net_bots.sh -n 4 -l 120 -b smart,simple,smart,simple` | Bot brains against each other; the server log ends each match with tanks gained / lost to hits / lost to throws. Add `--bot-log` to log what each bot is doing. |
| `net_bots.sh -n 2 -l 10 -- --match-delay=26` | Builds after the server's loading timeout and Go; the ready log must already say `playing True`. Use `--matches=2` without the delay to exercise another round on the same connection. |
| `godot res://scenes/dev/menu_test.tscn -- --capture-dir=DIR` | Clicks through every menu, a solo match, leaving it, and single player and back. |
| `menu_test.tscn -- --scenario=gamepad --capture-dir=DIR` | Joypad-only navigation, name/address defaults, hosting, browsing and joining, color and match length, single-player pause/resume, a solo debug match, overlays and results. Asserts focus and outcomes; also runs headlessly in `make test`, using the checkout's hosting port. |
| `menu_test.tscn -- --scenario=devices --capture-dir=DIR` | Live device-aware controls and reload hints, keyboard pause, mouse Resume, disconnect repeat cancellation and focus recovery. Also runs headlessly in `make test`. |
| `menu_test.tscn -- --scenario=update --update-check --fake-version=0.0.9 --capture-dir=DIR` | The offer of a newer release, and a newer server (`-- --server --port=24993 --fake-version=9.9.0`) asking for an update. |

The duel needs two players, `--start-score=8`, default tank-bank tuners and at least 15 seconds of play. It checks
that three player hits spill exactly two tanks through the grace period, then follows one dropped tank continuously
and retries missed shots until it explodes. Blast and throw captures are timed from the observed events, so lag does
not move them out of frame. The pickup scenario checks that a can adds its rounds exactly once, vanishes and returns;
allow at least `PickupRespawnTime + PickupRevealTime * 1.2 + 6` seconds. Both scenarios exit nonzero if a stage fails
or the match ends before all checks complete. These assertions also run headlessly.

Results, 2026-10-09: with three bots under 120 ms round trip, 30 ms jitter and 3% loss, every bot agreed on which tanks existed and their states throughout; resting tanks matched exactly and venting tanks to within about half a meter. The same held through the Docker server on another machine, over the LAN and over a VPN. When the match's creator left mid-match, the others played on in step. A client with a different protocol was turned away with a message.

Protocol 2 adds match ids, scalar tuning, timestamps on final body rests, drop refusal replies and discrete player
movement states, and appends the debug flag to Hello. These changes share one unreleased protocol bump; the next
release must be a minor release. Every match-scoped request, event and relay carries its match id, and either side
ignores a different id.
The server ends each match with a bank audit: initial test scores + detonated map tanks + re-detonated drops - spills
must equal total banked, including the banks of departed players retained in the final standings.
