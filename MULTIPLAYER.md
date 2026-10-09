# Propane — multiplayer design

Tank bank: a free-for-all for 2–5 players in one suburb. Detonating tanks scores points, and being shot spills them
back out as tanks for anyone to take. Most points when the timer runs out wins.

Status: **built, 2026-10-09** (see section 7 for how it is built and tested). The single-player sandbox in
[DESIGN.md](DESIGN.md) is unchanged, apart from the main menu in front of it.

## 1. Decided

**The scoring rule.** Every tank a player detonates is credited to that player as one point, which they hold. It doesn't matter where the tank came from: the map, another player's drop, or their own drop. Players lose points only through the loss rules below. Every gain and loss of tanks follows from this rule; there are no special cases.

| Area | Decision |
| --- | --- |
| Mode | Tank bank, every player for themselves, 2–5 players. |
| Scoring | One point for every tank a player detonates (see the rule above). A chain reaction credits every tank in it to whoever detonated the first one. |
| Own tanks | Not a special case. A player who detonates tanks they dropped earns them back, like any others. |
| Score display | A number above each player, in that player's colour, shown only when that player is in line of sight. |
| Health | None. Nobody dies. |
| Bullet hits | A hit drops 1 tank behind the victim. After a hit, the victim has a 0.5 s grace period before another hit can drop a tank. |
| Ragdoll | Being ragdolled drops 5 tanks where the player was when they were thrown. It counts even when the player's own blast threw them. |
| Hits while ragdolled | A ragdolled player can still be shot and lose tanks, with the same grace period. |
| Dropped tanks | Indestructible for 1 s after they drop: they ignore bullets and chain reactions. A player with no points loses nothing. |
| Venting | Unchanged: a punctured tank vents forever. |
| Match length | A timer that the lobby's creator sets, 2–3 minutes by default. Most points at the end wins. |
| Tank placement | Fewer, bigger piles nearer the centre of the suburb, so the fighting stays close. |
| Tank supply | The tanks the suburb starts with are all there will be. Nothing is resupplied. |
| Ammo | Limited ammo. Ammo pickups are placed around the suburb and respawn, appearing with the reveal effect. |
| Tank arrows | Kept, as in single player. |
| Identity | Each player picks a name and colour in the lobby. |
| Hit feedback | A hit marker for the shooter, a "+N" when you score, and an arrow showing which way a shot came from. |
| Flow | A main menu (single player, multiplayer). In multiplayer, a player creates a lobby or joins one, and the creator starts the match. No joining mid-match. |
| Tuning | Every new value is a tuner in `Tuning` (section 5). The tuning panel is only in debug builds, where any player may change values. Release builds use the baked values. |
| Platforms | macOS (Apple Silicon) is the first-class client and Linux comes second. Both play together. |
| Audio | None in the MVP. |

## 2. Match flow

1. **Main menu:** single player (today's sandbox) or multiplayer.
2. **Multiplayer:** the player connects to a server, then creates a lobby or joins an existing one.
3. **Lobby:** each player sets a name and colour. The creator sets the match length and starts the match.
4. **Match start:** the server sends the suburb seed. Each client builds the suburb, which materialises as it does today, and play begins when the timer starts.
5. **Match:** runs for the lobby's match length.
6. **End:** a results screen, then back to the lobby.

## 3. Changes from single player in a match

- **Hitstop** is off. It changes `Engine.TimeScale`, which also slows the physics and would make the clients drift apart.
- **Pause on focus loss** is off. Esc opens a menu without stopping the game.
- **R** reloads instead of generating a new suburb.
- **The tuning panel** is debug-only (see section 1).

## 4. Networking

Every client simulates the whole suburb, so the game responds instantly. Each moving object has one owner, which corrects the other copies. Small disagreements between screens are accepted in exchange for that responsiveness. This approach is called state synchronization with distributed authority.

**The server** runs no physics. It hosts the lobbies, relays messages within each match, keeps the score and the match clock, and settles ties: when two claims for the same thing arrive, the first one wins. It can run headless on the `games` home server, or inside a player's game during development.

**The world.** The server generates the match's suburb and sends the whole plan (about 10 KB compressed) rather than just its seed. The generator's trigonometry can differ in the last bit between an Apple Silicon Mac and an x86 Linux machine, and a single flipped comparison would give the players different suburbs. Houses and other static parts never need syncing.

**Tanks:**
- *Puncture:* the shooter's client punctures the tank at once. It then sends the cause to everyone: the hole position and normal, the jet's lean, and the bullet's push. Every client starts the same vent from the same starting point.
- *Ownership:* whoever punctured a venting tank owns it and sends its position and velocity at a set rate. Other clients ease toward that position, and snap only if they have drifted far.
- *Sleeping tanks:* intact tanks sleep, so they stay in sync without any messages.
- *Detonation:* whoever detonates the first tank in a chain runs the whole chain on their client and sends each explosion with its position. Other clients play those explosions and don't run chain reactions themselves for remote blasts.
- *Scoring:* the server credits the point. If two players claim the same tank, the first claim wins.

**Players:**
- Each client owns its own player's movement and sends it to the others, who show it smoothed.
- The shooter's client decides whether a bullet hit a player.
- The victim's client decides whether a blast ragdolls them, because it knows exactly where they were.
- The victim's client spawns the dropped tanks and owns them until another player interacts with them.

**Left to each client:** debris, small props and fence panels play out locally from the shared blasts. Cars are synced at a low rate, because players use them as cover.

**Platforms.** Mac and Linux builds from the same commit play together. A version check when a player connects turns away mismatched builds.

## 5. Tuning

These tuners are added to `Tuning`, alongside the existing ones, under new panel groups. Values given in this document are the defaults; the rest are set in playtesting. All existing tuners (rifle, blast, player, camera and so on) also apply in multiplayer.

**Match**

| Tuner | Controls |
| --- | --- |
| `MatchLengthDefault` | The match length a new lobby starts with (2–3 min). |
| `MatchLengthMin`, `MatchLengthMax`, `MatchLengthStep` | The range and step of the lobby's match-length setting. |
| `MaxPlayers` | Lobby size (5). |
| `CountdownTime` | The countdown between the creator pressing start and the timer starting. |
| `ResultsTime` | How long the results screen shows before returning to the lobby. |

**Tank bank**

| Tuner | Controls |
| --- | --- |
| `HitDropCount` | Tanks dropped per bullet hit (1). |
| `HitGraceTime` | The grace period after a hit before another hit can drop tanks (0.5 s). |
| `RagdollDropCount` | Tanks dropped when ragdolled (5). |
| `DropInvulnerableTime` | How long dropped tanks ignore bullets and chain reactions (1 s). |
| `DropScatter` | How far apart dropped tanks land. |
| `DropToss` | How hard dropped tanks are thrown out as they spawn. |
| `HitDropDistance` | How far behind the victim a bullet-hit drop lands. |

**Multiplayer suburb**

| Tuner | Controls |
| --- | --- |
| `MpTankCount` | Tanks per suburb in a match. |
| `MpClusterSizeMin`, `MpClusterSizeMax` | Pile sizes in a match. |
| `MpCentreBias` | How strongly piles are pulled toward the suburb's centre. |
| `MpCentreRadius` | The radius of the central area that piles favour. |

**Ammo**

| Tuner | Controls |
| --- | --- |
| `MagazineSize` | Rounds per magazine. |
| `StartingReserve` | Reserve rounds at match start. |
| `MaxReserve` | The most reserve rounds a player can carry. |
| `ReloadTime` | How long a reload takes. |
| `PickupCount` | Ammo pickups per suburb. |
| `PickupAmount` | Rounds per pickup. |
| `PickupRespawnTime` | Time before a taken pickup reappears. |
| `PickupRevealTime` | How long the reveal effect takes when a pickup reappears. |

**HUD**

| Tuner | Controls |
| --- | --- |
| `ScoreTagRange` | The farthest a player's score is shown, in line of sight. |
| `ScoreTagScale` | The size of the score above players. |
| `HitMarkerTime` | How long the hit marker shows. |
| `ScorePopupTime` | How long a "+N" shows. |
| `HitDirectionTime` | How long the incoming-shot arrow shows. |

**Network**

| Tuner | Controls |
| --- | --- |
| `PlayerSendRate` | Player state updates per second. |
| `TankSendRate` | Venting-tank corrections per second. |
| `CarSendRate` | Car corrections per second. |
| `InterpolationDelay` | How far behind real time remote players are shown, for smoothing. |
| `CorrectionBlendTime` | How quickly a drifted object eases back toward its owner's position. |
| `SnapDistance` | How far an object may drift before it snaps instead of easing. |

## 6. Open questions

None at the moment.

## 7. How it is built

### Playing

- **Main menu:** Single player, Multiplayer, Quit. Single player is the sandbox as before; its pause menu gains a Main menu button.
- **Multiplayer:** type a name and the server's address (`host` or `host:port`; the port defaults to 24680, UDP). **Host on this computer** runs a server inside the game and joins it, for playing without the home server; the lobby screens then show the addresses others can connect to.
- **Lobbies:** create one or join one from the list (lobbies in a match can't be joined). In the lobby, everyone picks a name and a colour (a colour someone else has is greyed out), and the creator sets the match length and presses Start. A release build needs two players to start; a debug build may start alone, for testing.
- **In a match:** the clock and the tanks you have banked are at the top, ammo at the bottom right. R reloads (an empty magazine also reloads on the next trigger pull). Walking over an ammo can takes it, unless the reserve is full. Esc opens the controls with Resume, Leave match and Quit; the match keeps running behind it. Leaving a match also leaves the lobby.
- **After a match:** the results show for `ResultsTime`, then everyone is back in the lobby, ready for another.

### Hosting the server

The server is the game itself started with `-- --server` (and `--headless`). It runs no physics and uses little CPU.

- **Home server:** `tools/server/deploy.sh` exports the Linux build and runs it in Docker on the `games` machine
  (`~/games/propane/server-1`, container `propane-server`, `restart: unless-stopped`, UDP 24680). Run it again after
  changing the game: clients from a different commit are turned away. Logs: `docker compose logs -f` in that folder.
- **Reaching it:** on the home network, `192.168.0.53`. Over Tailscale, the machine's tailnet address (share the
  machine with friends in the Tailscale admin console). From the internet, forward UDP 24680 on the router to
  192.168.0.53 and give friends the public address.
- **From source:** `godot --headless --path . -- --server [--port=24680]`.

### Code

| Path | Contents |
| --- | --- |
| `src/Main.cs` | The root scene: menus over a backdrop, single player, the match, or server mode. |
| `src/Net/LobbyServer.cs` | The server: lobbies, match flow and clock, relaying, scores, first-claim ties, pickups. |
| `src/Net/NetClient.cs`, `NetTransport.cs`, `Protocol.cs` | The client connection, the ENet transport (with simulated lag for tests), the message types and the version check. |
| `src/Net/Match.cs` | A match on one player's machine: everything this player sends, and playing what the others send. |
| `src/Net/BodySync.cs` | Ownership and corrections for tanks and cars. |
| `src/Net/SnapshotBuffer.cs` | Smooth playback of other players, `InterpolationDelay` behind. |
| `src/Net/PlanCodec.cs` | Packs a suburb plan for sending. |
| `src/Net/AmmoPickup.cs` | The ammo can and its build-up reveal. |
| `src/UI/Menus.cs`, `MatchHud.cs`, `MatchMenu.cs`, `ResultsScreen.cs` | Front end, match HUD, Esc menu, results. |

Details settled while building, within the decisions above:

- **Remote players** are full copies of the character (animation, rifle, ragdoll) driven by the states their own game sends. A thrown player's ragdoll falls on its own on every screen, steered after the real one; it gets up where the real one got up. Shots hit other players' bodies and limbs. Players pass through each other.
- **Ownership** goes to whoever last disturbed a body (a shot, a puncture, a blast, a push, a drop); the match's creator owns everything nobody has touched, and a player's bodies pass to the creator (or the next player) if they leave. Claims go through the server, which echoes them to everyone in one order, so all players agree on each owner.
- **Dropped tanks** glow in the dropper's colour while they ignore bullets and chain reactions.
- **Tuning in a match:** everyone plays on the creator's values: the baked ones in a release build, the creator's live ones in a debug build. In a debug build, F1 opens the panel, and a change applies to every player in the match. The player's own values come back after the match.
- **Starting values** (all tuners): 40 tanks in piles of 5–12 within about 40 m of the centre; a 30-round magazine, 60 in reserve, at most 150, a 1.6 s reload; 8 ammo cans of 30 rounds that come back after 20 s; a 2:30 match after a 4 s countdown; results for 10 s.

### Testing

| Command | Checks |
| --- | --- |
| `godot --headless res://scenes/dev/generator_tests.tscn -- --seeds=100` | Match suburbs too: a start per player, spread apart and clear; ammo spots; tanks nearer the centre; the plan unchanged through `PlanCodec`. |
| `tools/dev/net_bots.sh -n 3 -l 60 -- --net-lag=120 --net-jitter=30 --net-loss=3` | A local server and bot players (`scenes/dev/net_test.tscn`) that run to tanks, shoot them and each other, and fetch ammo, under simulated lag and loss. `-w N` shows N of them in windows; `-S host:port` uses an existing server. |
| `tools/dev/compare_tanks.py out/bot*.log` | Where each bot had every tank at the same match times: counts, states and drift. |
| `net_bots.sh -n 2 -w 2 -s "--start-score=8" -- --scenario=duel --capture-dir=DIR` | Scripted close-up: hits with the grace period, dropped tanks, a blast that throws the victim, from both sides. |
| `net_bots.sh -n 1 -w 1 -- --scenario=pickup --capture-dir=DIR` | Taking an ammo can and its return. |
| `godot res://scenes/dev/menu_test.tscn -- --capture-dir=DIR` | Clicks through every menu, a solo match, leaving it, and single player and back. |

Results, 2026-10-09: with three bots under 120 ms round trip, 30 ms jitter and 3% loss, every bot agreed on which tanks existed and their states throughout; resting tanks matched exactly and venting tanks to within about half a metre. The same held through the Docker server on `games`, over the LAN and over Tailscale. When the match's creator left mid-match, the others played on in step. A client with a different protocol was turned away with a message.
