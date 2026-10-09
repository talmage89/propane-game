# Propane — multiplayer design

Tank bank: a free-for-all for 2–5 players in one suburb. Detonating tanks scores points, and being shot spills them
back out as tanks for anyone to take. Most points when the timer runs out wins.

Status: **draft, 2026-10-08**. Not built. The single-player sandbox in [DESIGN.md](DESIGN.md) is unchanged.

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

**The world.** The generator builds the same suburb from the same seed, so the server only sends one number. Houses and other static parts never need syncing.

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
