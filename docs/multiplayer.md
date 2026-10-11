# Multiplayer

How multiplayer works today: tank bank's rules, the networking model, hosting, bots and tests. It describes the
code; when the code changes, change this file with it. The game's lasting direction is in [design](../design/).

## Tank bank

A free-for-all in one suburb. **Every tank a player detonates is credited to that player as one point, which they
hold**, wherever the tank came from: the map, another player's drop, or their own. Players lose points only by being
hit or thrown. Every gain and loss of tanks follows from that rule; there are no special cases. Most points when the
clock runs out wins.

- A chain reaction credits every tank in it to whoever detonated the first one.
  A close blast acts as a shot: intact tanks puncture and keep venting; already-venting tanks detonate after a fuse.
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

## Appearance

The lobby accepts any opaque sRGB color, including duplicates and near-duplicates. Eight presets remain available;
**Custom color** offers a live character preview, twelve local preset swatches, hue/saturation/brightness strips
and six-digit hex entry. Click or drag the strips, or select rows with gamepad up/down and adjust with left/right
or the left stick; holding accelerates larger moves. A / Cross picks a focused preset. HSV editing retains hue
through gray and black. Use color sends and saves the preview's exact sRGB bytes; Cancel leaves the accepted choice
alone. Settings store the same record as the network. An old saved palette index migrates once, only when no
appearance record exists; a corrupt saved record uses a safe preset.

`AppearanceRecord` is immutable. `AppearanceCodec` is the only serializer, shared by settings, Hello, SetProfile,
LobbyState, MatchStart and MatchEnd/standings. The server accepts the requested appearance without substituting
colors and freezes each profile at match start, including profiles retained after departure. Characters, score
tags, lobby/results samples and dropped-tank glow use that accepted profile. Names/ranks/scores remain separate
identifiers when colors repeat; color has no team meaning.

Wire version 1 is enclosed by a little-endian int32 byte length (maximum 1024). Its header is version, red, green,
blue and optional-slot count, all uint8. Each slot is a uint8 tag, little-endian uint16 payload length, then payload.
There are at most 16 slots, each at most 64 bytes. Tags 1–5 reserve model/skin, outfit, hat, gun skin and bullet skin;
they hold nonempty ASCII catalog ids using letters, digits, underscores and hyphens, never asset paths. Known
slots round trip; unknown optional tags are skipped, leaving the base presentation. Duplicate/zero tags, invalid
ids, unsupported record versions, excess lengths/counts and truncated/trailing records are rejected. Cosmetic
catalog resolution, ownership checks and assets are future work; all optional cosmetics render the base today.
Adding an optional slot within these bounds does not change a containing message's shape.

Hello retains its existing protocol/version/build/name/palette-index/debug prefix and appends the record. A
compatible Hello without the record uses its valid legacy palette index. A custom color sends the nearest preset
as that fallback, while preserving its exact RGB bytes in the record. Incompatible builds are rejected before
interpreting extensions. Welcome and Rejected are unchanged.

Readability is presentation only: score tags select a contrasting outline, and lobby/results swatches have a dark
frame. Near-black/white bodies gain a subtle rim and shield glow gains neutral contrast support. The Appearance
tuners control these treatments; all eight presets keep their existing character/glow presentation. Stored and
accepted colors are never modified by contrast handling.

## Changes from single player in a match

- **Hitstop** is off. It changes `Engine.TimeScale`, which also slows the physics and would make the clients drift apart.
- **Pause on focus loss** is off. Esc or Start / Options opens a menu without stopping the game.
- **R** reloads instead of generating a new suburb. The left hand swaps the magazine and the empty one drops to the ground: it is only for show, so each game drops its own and nothing shoots, blasts or syncs it. An empty rifle locks its bolt open until the reload releases it.
- **The tuning panel** is debug-only.

Each match owns its world services and physics space; connection polling and server deadlines remain outside that
lifetime. [World services](world-services.md) describes injection, cleanup and the simulation/network clock boundary.

## Networking

Every client simulates the whole suburb, so the game responds instantly. Each moving object has one owner, which corrects the other copies. Small disagreements between screens are accepted in exchange for that responsiveness. This approach is called state synchronization with distributed authority.

**The server** runs no physics. It hosts the lobbies, relays messages within each match, keeps the score and the match clock, and settles ties: when two claims for the same thing arrive, the first one wins. It runs headless on a dedicated server, or inside a player's game.

Packets are completely decoded into typed data before any state changes or relays. Unknown message types, wrong
directions, truncated or trailing payloads, invalid counts and ids, nonfinite numbers, invalid rotations and values
outside wire limits are dropped and logged. Packets are capped at 1 MiB, collections at 4096 entries (body batches
at 20), strings at 1024 UTF-8 bytes, and vector components at one million meters. These are wire safety ceilings;
the match's tuners still determine gameplay. Hello, Welcome and Rejected allow trailing extensions so incompatible
clients can still be told why they were rejected.

**The world.** The server generates the match's suburb and sends the whole plan (roughly 10–30 KB compressed at 2–32 players) rather than just its seed. The generator's trigonometry can differ in the last bit between an Apple Silicon Mac and an x86 Linux machine, and a single flipped comparison would give the players different suburbs. The plan includes every tank's position, elevation, rotation and pile identity; clients do not reroll formations. Houses and other static parts never need syncing.

Construction choices are pure functions of that plan and the shared tuning. Fence style and color use explicit
integer mixing of the run's coordinates and seed, never a process-salted hash. `make test` hashes the built meshes,
transforms, body masses and collision shapes (fences and piles included) in two fresh processes using a saved plan
for each formation. Compressed plans are validated before match setup: inflation is capped at 8 MiB, collection
and string limits apply inside the plan, and starts must agree with the player assignments.

**Tanks:**
- *Puncture:* the shooter's client punctures the tank at once. The server accepts the first puncture and broadcasts
  its canonical hole, normal and swirl to everyone, including the contenders. A losing prediction replaces its
  vent parameters while preserving its current position and velocity; owner corrections reconcile its trajectory.
  An intact observer already moving from a relayed blast also keeps that motion when adopting the child vent;
  snapping to the puncture's pre-blast pose would erase the accepted push until owner publications arrive.
- *Ownership:* whoever punctured a venting tank owns it and sends its position and velocity at a set rate. Other clients ease toward that position, and snap only if they have drifted far.
- *Sleeping tanks:* intact tanks sleep, so they stay in sync without any messages.
- *Detonation:* whoever detonates the first tank in a chain runs the whole chain on their client and sends each explosion with its position. Other clients play those explosions and don't run chain reactions themselves for remote blasts.
- *Scoring:* the server credits the point only for a tank in its ledger that has not already exploded. If two players claim the same tank, the first claim wins. Map tanks are registered from the plan; client-created drops are registered atomically before being relayed. Exploded ids remain reserved for the whole match.

**Players:**
- Each client owns its own player's movement and sends it to the others, who show it smoothed. Player snapshots
  include discrete gait, support phase and weapon action alongside the blended rifle poses, so remote creep uses
  the walk clip and remote sprint uses the sprint clip. Protocol 2 adds these three bytes; this needs a minor release.
  Gaits share a distance-driven phase based on displayed motion. Feet retain world contacts on static ground,
  releasing and blending after support changes or snapshot corrections; foot positions are not sent.
- The shooter's client decides whether a bullet hit a player.
- The victim's client decides whether a blast ragdolls them, because it knows exactly where they were.
- The victim's client spawns the dropped tanks and owns them until another player interacts with them.

A drop must contain unique, unused ids from that player's spawn slot and fit both their bank and the drop reason's
tuner. Its glow uses the dropper's accepted match appearance; Drop contains no color field. The server refuses the
whole batch if any entry is invalid. Scoring, spilling and authority transitions happen only while the server's clock is playing. A late lifecycle prediction is refused and restored to the last authoritative state. Known exploded body ids may appear in late snapshots or claims, which clients
ignore; body ids that never existed are refused. A client missing a server-accepted tank still plays its reported
blast, including throws, without running a chain or claiming credit locally.

Drops are predicted locally. The server echoes an accepted Drop to its sender as well as the other players, and
replies with `DropRefused` ids for a fully decoded batch it cannot accept. The sender removes only those pending
predictions and releases their bank reservation. This also handles legitimate drops overtaken by a live debug
drop-count change or the server's match deadline. Malformed packets still get no reply.
The deadline check uses the server clock even when a packet is polled before that frame's phase transition.
Refusing a predicted root also cancels its pending child fuses, including already owned venting neighbors that
needed no ownership request. Children already sent still receive explicit refusals and restore their own bodies.

The ledger proves existence and prevents duplicate scoring; it does not prove hits, movement, proximity or blast
positions. Those remain decisions of the distributed client authorities.

The ledger also owns each body's peer and monotonically increasing epoch. Every accepted ownership or lifecycle
claim increments that body's epoch, including another grant to the same peer; departure transfers get new epochs.
Movement and reliable rests carry a per-body sequence within the epoch. Neither side compares owner clocks:
old epochs and duplicate/older sequences cannot replace a newer state or wake a completed rest. A client retains
one newest future state per body when an unreliable publication overtakes its reliable grant.
Moving batches use sampling timestamps on the sender's ping-estimated server clock. The server bounds these to
the existing 0.3 s extrapolation window; zero means no ping yet and falls back to relay time. Receivers use their
own server clock estimate to include both transport legs in bounded motion extrapolation. This also survives
motion overtaking its grant. Rest settling uses receipt time; clocks never order states.

`Claim` requests carry expected epochs and proposed poses for the complete group. The server validates every member
before granting any, so later grouped interactions can include bodies already owned without a partial handoff.
A local group with a pending member is left unpredicted for its caller to retry. Prediction never publishes movement until a grant is
confirmed. `Puncture` and `Explode` use the same expected-epoch path; each request has a peer-local request ID.

`Authority` broadcasts accepted canonical state and replies only to the claimant on refusal. Reasons distinguish
stale epochs, exploded tanks, wrong phases, incompatible lifecycle transitions, refused chain causes and unknown
bodies. Replies carry the current owner, epoch, sequence, lifecycle, last reported pose/rest and canonical vent or
explosion center. A refused prediction restores that state, recreating a locally exploded tank when necessary.
A losing puncture adopts the canonical vent while preserving current motion; the owner's stream corrects its
trajectory normally.
The pose is the owner's last publication, not server-simulated physics. Refusal cancels pending chain fuses; an old
fuse callback cannot detonate a restored lifecycle. Effects already played locally remain
transient; reconciliation does not rewind debris or player motion.
Newer movement in the same epoch cannot suppress lifecycle reconciliation. Restoration uses the newest known
pose and preserves its correction stream, even when the predicted explosion already freed the tank's node.

Chain punctures, delayed detonations and ownership claims for pushed bodies carry the initiating explosion's
request ID. The root is sent before its children, even when waiting for a puncture grant; the server accepts a child
only if that peer's root explosion was accepted. A losing root cannot score a child chain or grant its pushed bodies.
Local predictions still play immediately. A queued second shot resolves after its first-shot grant, using that
new epoch. Refused dependent predictions also receive their own authoritative replies.
Moving ownership and puncture proposals capture their current pose when sent, including after a preceding grant
unblocks them. Chain punctures wait until the complete blast push before sending, so their canonical motion includes
that impulse and spin. An already freed predicted tank retains its original proposal; explosion centers always stay
at the position of the predicted event.

Loose tanks gather in both modes through `TankGather`: intact, unshielded tanks outside their built/gathered pile
connect within `GatherRadius` (3 m) and wait `GatherDelay` (20 s), with at least `GatherMinimum` (2) members. Motion,
disturbance and membership changes restart that wait. The creator (or current world-authority successor) requests
an atomic `Claim` for the complete group, including tanks it already owns. Nothing glides before that grant. The
component rechecks eligibility, membership and clearance after the reply, then uses the granted epochs as its lease.
A refused grant or changed member leaves the group alone; ownership/lifecycle changes cancel an active glide.

Gathered piles use `PileBuilder`'s upright cluster with the existing pile spacing: it supports every count without
upper tiers depending on a tank that might be interrupted. Site checks require flat floor support, an empty footprint
and clear enclosing-sphere sweeps for lift, traverse and settle. Players, houses and other obstacles block the path;
low obstacles can sit under the traverse. The path is checked again each physics tick. A shot, blast push, player
bump or puncture releases that tank to ordinary physics immediately and publishes cancellation before queuing
its ownership/lifecycle request. Finished tanks sleep with ordinary collisions and track their pile poses;
displacement/rotation out of the tuned tolerance makes them loose again.

Reliable `Gather` relays carry a phase (cancel/glide/settled) and body poses with epochs/sequences. Only the current
world authority may send them for its intact owned tanks during play. Body sync publishes the glide normally;
observers freeze gravity while following that stream, and the reliable completion supplies exact pose and pile
membership. Presentation has separate sequence tracking so newer disposable motion cannot hide a reliable start
or rewind a newer pose. Handoffs and ordered final body rests release frozen observers, and stale epochs cannot
finish an abandoned pile.

The drop shield glow expires before a tank becomes eligible to gather. Gather shimmer adds to shield emission and
contrast without replacing them; each effect clears only its own shader parameters. The presentation-only
`gather_range.tscn -- --scenario=appearance` fixture forces both on custom/black/white tanks with amplified shimmer,
checks independent completion and expiry, and captures their coexistence with `--capture-dir=DIR`. It does not change
gather eligibility. Run this fixture without `--fixed-fps`: shield expiry uses wall time. The network gather scenario
checks accepted drop colors, shield expiry, glide shimmer and its cleanup on both clients, and captures a close-up
from the host.

Blast shape queries page past full 512-result pages by excluding returned bodies. Large piles cannot silently truncate
pushes or chain contacts; `scenes/dev/blast_query_tests.tscn` checks 1,100 bodies and duplicate shapes.

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
- it taps tanks, fires short bursts at players, and fetches ammo when low or close;
- it keeps travel goals stable, finds capsule-clear routes around houses and props, and plans fence jumps from the
  live player jump physics, including a runway and clear landing; a direct gate is preferred to a jump;
- it avoids tank piles and vent clearance zones, replans around changed obstacles, then tries alternate routes
  before temporarily excluding a target; ammo targets use that same recovery;
- it smooths walking turns, sprints on longer clear stretches, and releases aim for a runway and flight.

`src/Navigation` takes collision queries, actor capabilities/state and a goal, and returns world movement intent.
It has no `Match` dependency. [Bot navigation](bot-navigation.md) describes its assumptions, local difficulty dials
and seeded regression course. `Tuning.Bot...` properties are local C# dials (also accepted by `--tune.Bot...` in dev
harnesses); they are deliberately absent from exported match tuning and its snapshot schema. Each world copies
those local values alongside the creator's shared movement tuning. `--bot-log` logs cumulative stuck,
blacklist, replan and jump counts. The simple brain remains the original benchmark.

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
| `src/Net/NetClient.cs`, `NetTransport.cs`, `Protocol.cs` | The client connection, the ENet adapter, the message types. |
| `src/Net/IPacketTransport.cs`, `ProtocolPeerMap.cs`, `PacketTransports.cs`, `FaultPacketTransport.cs` | Message transport contract, Protocol 2 identity bridge/backend selection, deterministic network faults. |
| `src/Net/MessageCodec.cs`, `TankLedger.cs` | Complete, typed wire decoding and the server's tank existence/lifecycle ledger. |
| `src/Net/MatchInbox.cs` | Per-match buffering until scene initialization, with generation-safe cleanup. |
| `src/Net/BuildInfo.cs`, `UpdateCheck.cs` | Versions and who plays together; the launch check for a newer release. |
| `src/Net/Match.cs` | A match on one player's machine: everything this player sends, and playing what the others send. |
| `src/Net/BodySync.cs` | Ownership and corrections for tanks and cars. |
| `src/Net/SnapshotBuffer.cs` | Smooth playback of other players, `InterpolationDelay` behind. |
| `src/Net/PlanCodec.cs` | Packs a suburb plan for sending. |
| `src/Net/AmmoPickup.cs` | The ammo can and its build-up reveal. |
| `src/UI/Menus.cs`, `MatchHud.cs`, `MatchMenu.cs`, `ResultsScreen.cs` | Front end, match HUD, pause menu, results. |

### Transport contract

`NetClient` and `LobbyServer` accept an `IPacketTransport` before entering the tree. `PacketTransports` selects
ENet for production; the unit suites inject an in-memory loopback with a manual monotonic clock. Gameplay sends
`ReliableOrdered`, `UnreliablePlayerPoses` or `UnreliableBodyStates`, never backend channel numbers. ENet maps all
three to channel zero with the original reliability modes; ping and clock samples remain disposable/unreliable.
On receive, the legacy channel cannot distinguish the two unreliable classes; the ENet adapter recognizes body
state messages by their existing type byte. No packet layout, player id, tuner or gameplay rule changed.

Connection handles are opaque objects and are never reused. `ProtocolPeerMap` is the compatibility bridge between
these handles and the existing wire player ids (still ENet peer ids). The client learns its player id from Welcome.
Server-issued peer/actor identities are deferred to A6. Discovery, lobbies and invites are application services;
`PacketEndpoint.Address` is backend-interpreted, with an ENet hostname/IP and port today.

Every transport callback runs inside main-thread `Poll`. Receive memory is borrowed until the callback returns;
copy it before retaining. Client/server decoding copies it, and typed messages retained by `MatchInbox` own their
data. A successful send consumes/copies the caller's bytes before returning, but only means accepted. Failed sends
accept nothing and return `Closed`, `Oversized` or `Backpressure`. Disconnect/close invalidate handles immediately,
discard buffered packets, and defer notification to Poll; old-handle callbacks cannot drop a new connection.
Required reliable sends that cannot be accepted terminate the connection with an explicit error instead of silently
losing control/events. Unreliable samples may be discarded under pressure.

Disconnect reasons distinguish application rejection, timeout, transport failure and local close, with a nullable
backend diagnostic code and a UI detail. The existing Rejected packet carries version incompatibility; lobby-full
notices remain notices on an otherwise live connection. `NetClient.LastDisconnect` retains the typed cause while
`StatusChanged` supplies its detail to the existing menu error. Godot's `ENetMultiplayerPeer` discards ENet event data
and does not distinguish an established peer timeout from a remote close: these report `TransportFailure` with an
unavailable code. A failed connection/Hello deadline reports `Timeout`. Local reasons and open errors keep any known
code. Supporting more precise ENet remote reasons would require a lower-level adapter and is deferred.

`NetClient.Diagnostics` exposes the local connection counters; server logs include per-connection payload
packets/bytes accepted out and received in, RTT, managed queue
packets/bytes, drops and last transport poll cost every ten seconds. These are message counters, excluding UDP
headers, ENet retransmissions and ACKs. Native ENet queue depth is unavailable through Godot; the log says so,
rather than treating the zero managed queue as the native queue. ENet native reliable-loss ratio is shown when
available. Diagnostics and id bindings last through the disconnect callback, then are released. ENet send acceptance
reflects Godot PutPacket, which exposes no native send-buffer limit.

`FaultPacketTransport` decorates any backend with an injected monotonic clock and seeded random schedule. Lag and
jitter delay both directions; loss applies only to outgoing unreliable messages, preserving the existing CLI's loss
meaning. Reliable order is independent per connection and direction. Its per-connection incoming and outgoing
queues each cap at 4096 packets/4 MiB (also bounding its advertised payload size); a reliable receive overflow fails
the connection, and unreliable overflow counts a drop. Outgoing pressure returns `Backpressure`; accepted reliable
packets retry backend pressure in order.
These are transport test limits, not gameplay tuners. `--net-lag=120`, `--net-jitter=30`, `--net-loss=3` still mean
60 ms added each way, 0–30 ms extra jitter, and 3% unreliable loss on the configured endpoint. `--net-seed=1` is the
default reproducible seed. `--net-reliable-stall=500` adds 500 ms to reliable messages in each direction, allowing
poses/body samples to overtake them. Server arguments configure the server decorator; bot arguments configure each
client. A fault schedule is deterministic for a fixed seed, clock and sequence of calls, not for unrecorded real-time
matches.

The contract was checked against [Steam's ISteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets):
message boundaries, send acceptance/errors, reliable ordering within a lane and connection status/end reasons fit.
A future adapter must marshal status callbacks into Poll, release native receive messages after the borrowed callback,
advertise its own message-size limit (not ENet's 16 MiB), and map Steam send-buffer limits to Backpressure. Use one
reliable lane to retain today's ordering and disable Nagle to retain ENet's immediate flush. Steam can retain remote
reason codes and queue statistics that Godot's ENet wrapper cannot. No Steam SDK, lobby, invite or relay implementation
is included.

Details settled while building, within the decisions above:

- **Remote players** are full copies of the character (animation, rifle, ragdoll) driven by the states their own game sends. A thrown player's ragdoll falls on its own on every screen, steered after the real one; it gets up where the real one got up. Shots hit other players' bodies and limbs. Players pass through each other.
- **Ownership** goes to whoever last disturbed a body (a shot, a puncture, a blast, a push, a drop); the match's creator owns everything nobody has touched, and a player's bodies pass to the creator (or the next player) if they leave. Claims go through the server as atomic groups with expected epochs; accepted grants are broadcast in one order. A handoff clears the former owner's correction target and sequence boundary. The new owner sends its final state even if its copy was already asleep, so nobody is left correcting toward the former owner forever.
- **Dropped tanks** glow in the dropper's color while they ignore bullets and chain reactions.
- **Tuning in a match:** everyone plays on the creator's values: the baked ones in a release build, the creator's live ones in a debug build. Only that original creator's debug build gets F1 and may change the match's values. Hosting succession does not transfer this permission. Hello appends a debug-build flag, captured at match start; this is a client capability declaration, not remote build attestation. Tuning crosses the wire as named bool/int/float scalars, with exact exported types and inclusive declared ranges checked before applying the whole snapshot or a change. `FixedSeed` accepts the full int32 range. Each match owns a separate validated tuning instance; it never replaces the application saved template. The panel targets that match instance, and Save also updates the application template for future worlds.
- **Starting values** (all tuners): 20 base tanks plus 4 per player; pile limits of 5–12 plus floor(0.25 × players), near the central 40 m; a 190 m base map plus 4 m per player, with clear starts at least 12 m apart; a 30-round magazine, 60 in reserve, at most 150, a 1.6 s reload; 3 base ammo cans plus 1 per player, of 30 rounds that come back after 20 s; a 2:30 match after a 4 s countdown; results for 10 s.

## Testing

| Command | Checks |
| --- | --- |
| `make test` | Plain C# assertion suites (every wire message, malformed payloads, server ledger and permissions, lifecycle buffering and ownership, transport faults/reconnect/lifetime), score-checker regressions, movement, gamepad menu, device switching and platform checks, generator checks (100 seeds), and separate-process built-world hashes. Add suites under `src/Dev/UnitTests/` and mark them `[UnitSuite]` for discovery; any failure prints its name and exits nonzero. |
| `godot --headless res://scenes/dev/generator_tests.tscn -- --seeds=100` | All four formations; 2, 8, 16, 24 and 32 players; exact supply counts, clear separated reachable starts, hull overlaps and supports, and exact `PlanCodec` round trips. |
| `tools/dev/net_bots.sh -n 3 -l 60 -- --net-lag=120 --net-jitter=30 --net-loss=3` | A local server and bot players (`scenes/dev/net_test.tscn`) that run to tanks, shoot them and each other, and fetch ammo, under simulated lag and loss. `-w N` shows N of them in windows; `-S host:port` uses an existing server. |
| `tools/dev/compare_tanks.py out/bot*.log` | Where each bot had every tank at the same match times: counts, states and drift. |
| `python3 -B tools/dev/compare_tank_paths.py out/bot*.log` | Add `--tank-trajectories` to a same-machine bot run: roughly 60 ms wall timestamps, linear interpolation to common times, playing drift distributions and final identity/state assertions. Skips intervals over 200 ms and lifecycle changes. Independent match countdowns in the coarse digest can differ; use this probe for drift comparisons. |
| `tools/dev/check_scores.py out/server.log out/bot*.log` | Server conservation audit and identical per-player final scores on every bot; exits nonzero on a missing audit or disagreement. |
| `net_bots.sh -n 2 -l 45 -s "--start-score=8" -- --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1 --net-lag=120 --net-jitter=30 --net-loss=3` | Two bot hits spill two close tanks from the joiner; both clients require an intact stable gathered pile owned by the world authority. Scatter/toss are fixture overrides to keep the drops within the gather radius. Add `-w 1` and `--capture-dir=DIR` to see the glide. |
| `net_bots.sh -n 2 -w 2 -s "--start-score=8" -- --scenario=duel --capture-dir=DIR` | Scripted close-up: hits with the grace period, dropped tanks, a blast that throws the victim, from both sides. |
| `net_bots.sh -n 2 -l 35 -w 1 -- --scenario=movement --assert-motion --assert-feet --assert-grip` | Remote creep, sprint, air aim suppression, held-aim landing, hip/aimed bursts and walking reload through real snapshots; final contact and rotation assertions. Headless runs need at least 18 seconds; rendered runs allow 35 seconds for loading and capture stalls. |
| `net_bots.sh -n 2 -l 45 -w 1 -- --scenario=movement --assert-grip --view=rifleright --capture-dir=DIR` | Final-pose wrist, sampled hand penetration and eye-line assertions on the remote character; close-ups and `grip.csv` from the host. |
| `net_bots.sh -n 1 -w 1 -- --scenario=pickup --capture-dir=DIR` | Taking an ammo can and its return. |
| `net_bots.sh -n 2 -w 2 -l 20 -- --scenario=puncture-race --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir=DIR` | Two opposed bullet contacts on simulation tick 180; both copies must still be intact. Every client asserts canonical hole, normal, swirl and lean, and bounded owner correction error over time. A third bot observes without shooting. |
| `python3 -B tools/dev/compare_vents.py out/bot*.log` | Identical canonical vent parameters and at least 100 overlapping, time-aligned trajectory samples per pair; exits nonzero if maximum drift exceeds the default snap distance (3 m, a test tolerance). |
| `net_bots.sh -n 2 -w 2 -l 10 -s "--start-score=8" -- --scenario=drop-refusal --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir=DIR` | The creator lowers the hit-drop count while a hit is in flight; the victim rolls back the refused drop, both clients keep one accepted tank and a bank of 7. |
| `net_bots.sh -n 4 -l 120 -b smart,simple,smart,simple` | Bot brains against each other; the server log ends each match with tanks gained / lost to hits / lost to throws. Add `--bot-log` to log what each bot is doing. |
| `net_bots.sh -n 2 -l 10 -- --match-delay=26` | Builds after the server's loading timeout and Go; the ready log must already say `playing True`. Use `--matches=2` without the delay to exercise another round on the same connection. |
| `godot res://scenes/dev/menu_test.tscn -- --capture-dir=DIR` | Clicks through every menu, a solo match, leaving it, and single player and back. |
| `menu_test.tscn -- --scenario=gamepad --capture-dir=DIR` | Joypad-only navigation, name/address defaults, hosting, browsing and joining, color and match length, single-player pause/resume, a solo debug match, overlays and results. Asserts focus and outcomes; also runs headlessly in `make test`, using the checkout's hosting port. |
| `menu_test.tscn -- --scenario=mouse --capture-dir=DIR` | Actual mouse/keyboard color entry, exact preview/accepted/saved bytes, lobby sample, solo debug character and real server results. Runs headlessly in `make test`. |
| `net_bots.sh -n 3 -w 3 -l 10 -o OUT -s "--start-score=8" -c 12ABEF,12ABEF,F6F6F6 -- --scenario=appearance --capture-dir=DIR` | Duplicate custom colors plus near-white through lobby, MatchStart, local/remote bodies, accepted drop glow and real results; every client asserts consistency. Screenshots use bot-name prefixes. Repeat with `000000,0000FF,FFFFFF` for dark/saturated/white contrast. |
| `menu_test.tscn -- --scenario=devices --capture-dir=DIR` | Live device-aware controls and reload hints, keyboard pause, mouse Resume, disconnect repeat cancellation and focus recovery. Also runs headlessly in `make test`. |
| `menu_test.tscn -- --scenario=update --update-check --fake-version=0.0.9 --capture-dir=DIR` | The offer of a newer release, and a newer server (`-- --server --port=24993 --fake-version=9.9.0`) asking for an update. |
| `menu_test.tscn -- --scenario=rejection --server-address=127.0.0.1:24993 --capture-dir=DIR` | Run against `-- --server --port=24993 --fake-version=9.9.0`. Asserts the typed application rejection, exact version-mismatch message and download button in the real connection UI; exits nonzero on failure. |

The duel needs two players, `--start-score=8`, default tank-bank tuners and at least 15 seconds of play. It checks
that three player hits spill exactly two tanks through the grace period, then follows one dropped tank continuously
and retries missed shots until it explodes. Blast and throw captures are timed from the observed events, so lag does
not move them out of frame. The pickup scenario checks that a can adds its rounds exactly once, vanishes and returns;
allow at least `PickupRespawnTime + PickupRevealTime * 1.2 + 6` seconds. Both scenarios exit nonzero if a stage fails
or the match ends before all checks complete. These assertions also run headlessly.

Results, 2026-10-09: with three bots under 120 ms round trip, 30 ms jitter and 3% loss, every bot agreed on which tanks existed and their states throughout; resting tanks matched exactly and venting tanks to within about half a meter. The same held through the Docker server on another machine, over the LAN and over a VPN. When the match's creator left mid-match, the others played on in step. A client with a different protocol was turned away with a message.

Protocol 2 also changes **Hello** (trailing appearance record), **SetProfile**, **LobbyState**, **MatchStart** and
**MatchEnd** (one appearance record in each profile/standing), and **Drop** (removes the redundant palette index).
Appearance adds shared presentation tuners. It keeps the unreleased `Protocol.Version` at 2; the next release must
be minor. Welcome and Rejected retain their existing layouts.

Protocol 2 adds the reliable `Gather` relay, authority request IDs, grouped epoch grants, per-body publication
sequences and canonical/refused lifecycle reconciliation, as well as match ids, scalar tuning, timestamps on final
body rests, drop refusal replies and discrete player movement states, and appends the debug flag to Hello. These
changes share one unreleased protocol bump; the next release must be a minor release. Every match-scoped request,
event and relay carries its match id, and either side ignores a different id.
The server ends each match with a bank audit: initial test scores + detonated map tanks + re-detonated drops - spills
must equal total banked, including the banks of departed players retained in the final standings.

## Melee (R6, unreleased Protocol 2)

F, either mouse thumb button, or right-stick click starts a grounded rifle-butt strike. The resolver owns the
0.24 s windup, 0.16 s contact window and 0.4 s recovery, followed by a 0.65 s cooldown. It cancels sprint and reload,
suppresses aim, fire and jumping, and selects a forward lunge during windup/contact and a stop during recovery.
The direction is latched from camera yaw at acceptance. Ground loss or a throw cancels contact; the cooldown survives
that interruption. Held movement/aim/sprint resume when recovery ends. All timings, geometry, launch/shove impulses
and pose values are in the **Melee** tuning group.

The attacker's client queries a sphere throughout the contact window and clips candidates to its forward sector
(default 1.65 m reach, 50-degree half-angle). A ray to the candidate must reach its surface without hitting another
collider. Multiple capsule/bone/prop shapes share one swing key. Every victim/body can be struck once per swing.
A tank receives only disturbance and a shove, including cancellation of gathering: melee never calls its shot,
puncture, chain or detonation path. Synced tanks/cars use the existing claim and body-state publication path; loose
props receive a relayed shove.

`MeleeStart` carries a monotonically increasing swing id and horizontal direction; `MeleeContact` carries that id,
exactly one player or loose-prop target. Both are reliable and ordered. The server validates
play/deadline, membership, known targets, increasing ids, receipt-clock cooldown, contact expiry and unique targets.
Its per-swing contact cap bounds message rate. `MeleeRelayGrace` allows receipt jitter to compress consecutive starts
and extends contact expiry; it cannot admit starts faster than the cooldown itself. The server runs no hit physics.
The victim applies the launch through the existing ragdoll path, sends Ragdoll/GetUp, and predicts the ordinary
five-tank drop. A zero-bank victim still falls; an already-down victim receives a push without starting another drop.
Bullets remain able to hit ragdolls.

PlayerState gains one byte of normalized melee progress, before gait/support/weapon. Remote poses consume that
progress; interpolation cannot rewind across a new swing or release. Protocol.Version stays **2** as directed for
this unreleased development protocol. These two messages, snapshot field and shared tuners require a **minor release**.

The smart bot chooses a nearby visible opponent when it is not aiming at a tank. Its approach reach derives from
melee range plus the lunge distance. It can stun an empty-bank opponent and uses the resolver's grounded/cooldown
availability. `net_test` logs actual accepted swings and contacts, rather than just requested inputs.

Reproduce the duel and conservation proof after `make build`:

```sh
tools/dev/net_bots.sh -o /tmp/wpmelee-duel -n 2 -w 0 -l 25 -s '--start-score=5' -- --scenario=melee --assert-grip --assert-motion --net-lag=120 --net-jitter=30 --net-loss=3
python3 -B tools/dev/check_scores.py /tmp/wpmelee-duel/server.log /tmp/wpmelee-duel/bot1.log /tmp/wpmelee-duel/bot2.log
python3 -B tools/dev/compare_tanks.py /tmp/wpmelee-duel/bot1.log /tmp/wpmelee-duel/bot2.log
```

The joiner strikes the host twice in a clear fixture outside the suburb. Both clients assert the first fall,
exactly five drops and a get-up, then a zero-bank fall with no extra drops and another get-up. Each match resets the
proof. Use `-w 1 --view=side --capture-dir=/tmp/wpmelee-remote` for the victim's remote side captures.
Allow 35 seconds for rendered runs. The melee harness reads frames back asynchronously and encodes PNGs at exit
so screenshots do not skip contact or stall the connection. For a four-bot 90-second soak, use
`-n 4 -w 0 -l 90 -- --melee-start --bot-log --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3`.
`--melee-start` places smart bots in close pairs once, then their ordinary brains control the entire match; the
accepted swing/contact logs show actual use. Audit that run with the same two scripts and all four bot logs.
`make test` runs the pure resolver/geometry/contact/server/wire assertions, real wall/tank/prop contact fixture,
input bindings and device reference checks, plus the local strike grip/rotation probe. `make test-full` adds ten
reload cancellations spanning early/late progress in both reload clips.

Verified on Linux, 2026-10-11: the lagged duel spilled exactly five tanks once, stunned the empty bank without a
second drop, and completed both get-ups. Both clients agreed with the bank audit (10 initial minus 5 spilled = 5).
The final 90-second close-pair soak recorded 14 accepted swings and eight player contacts across four smart bots,
including strikes while repositioning for tanks, followed by ordinary navigation, tank shooting and scoring. All four
final standings agreed: 20 map gains + 34 drop gains - 50 spills = 4 banked. The 407 tank comparisons ended with
identical ids/states and at most 0.165 m drift. During active chains the diagnostic reported transient differences
up to five states and 5.862 m drift; it is a
comparison report, not a threshold assertion. Those transients converged; this package does not revise body sync.
