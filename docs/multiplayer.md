# Multiplayer

How multiplayer works today: Tank Bank and King of the Cul-de-sac, the networking model, hosting, bots and tests. It describes the
code; when the code changes, change this file with it. The game's lasting direction is in [design](../design/).

`Protocol.Version` is **3** (`src/Net/Protocol.cs`), shipped with 0.4.0; 0.3.0 shipped 2 and 0.2.0 shipped 1. A section
labeled with an earlier protocol records which protocol its message changes joined, and shipped with that release.

Hard bots in Tank Bank and King compare banking and spilling leaders' points with the available zone income;
Easy and Normal retain their objective-first choices. See [bot policies](bot-navigation.md#hard-points-policy).

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
- A live leaderboard in the top-left corner shows the top five (rank, color, name, bank), ties sharing a rank in
  roster order as on the results screen. The player's own row is highlighted, and shows below a divider with its true
  rank when they are outside the five. Rows slide to a new rank; a climb flashes and a gain pops "+N". Each player can
  hide it from the match menu (kept in their settings).
- No health: nobody dies.

The counts and times are tuners (the Match, Tank bank, Multiplayer suburb, Ammo, Match HUD and Network groups of
`src/Core/Tuning.70.Match.cs`, and the HUD group of `Tuning.60.World.cs`). The rules themselves are `TankBankRules`,
pure logic run by the server's mode runner and by the Lab duel; [modes.md](modes.md) describes the framework, the
spill tickets and the mode envelope.

## Match flow

1. **Main menu:** single player (the sandbox), multiplayer or quit.
2. **Multiplayer:** the player connects to a server, then creates a lobby or joins an existing one.
3. **Lobby:** joining puts the player in the lobby's [world](#lobby-world-a12-protocol-3), a void where everyone who joined roams, shoots and throws tanks while waiting. Its panel (Tab, or View on a controller) holds the lobby: each player sets a name and color and may add bots. The creator chooses Tank Bank (default) or King of the Cul-de-sac, its length, **Tank supply** (every integer from 1× to 8×, default 1×) and, for King, mint pace, then starts. Everyone sees supply; only the waiting creator can change it.
4. **Stage:** the stage screen shows the coming mode, its settings and everyone's card over the washed-out world and counts down `LobbyStageTime` (5 s); the creator can take the start back. Then the world goes.
5. **Match start:** the server sends the suburb's plan. Each client builds it, it materializes as in single player, and play begins when the timer starts.
6. **Match:** runs for the lobby's match length.
7. **End:** a results screen, then everyone is back in a fresh lobby world, membership kept.

The server waits up to 20 seconds for loading acknowledgments before counting down anyway. The client's connection
buffers every current-match event until the match scene has initialized its bodies and HUD, then replays them in
receive order once. A late builder therefore receives Countdown, Go, scores and even End. Leaving or disconnecting
clears the buffer; a new MatchStart invalidates the old queue and receiver, and old scene cleanup cannot clear the
new queue. The inbox is capped at 4096 packets and 4 MiB of wire data; exceeding it disconnects with an explicit
loading-buffer message rather than continuing with missing events.

`LobbyServer` owns each lobby's current run routes. A plain `ServerRun` owns the corresponding match or roaming
world, including a disposable live configuration adapter. Application-owned ServerSettings creates that adapter
and owns its native tuning conversion; each run exclusively owns and retires the result. Delivery, loading status and lobby transitions use synchronous callbacks;
the run cannot access the server's connection or lobby dictionaries. Match actor identities and creator debug
capability freeze at start; roaming actors follow roster snapshots at each lobby-state broadcast. The facade detaches
routing before retiring tuning on results return, an empty lobby, staging replacement, identity exhaustion,
construction failure or shutdown. Retirement is idempotent and preserves terminal packet order. Runs tick at the
existing server process boundary; there is no additional node or scheduler.

## Appearance

The lobby accepts any opaque sRGB color, including duplicates and near-duplicates. Eight presets remain available;
**Custom color** offers a live character preview, twelve local preset swatches, hue/saturation/brightness strips
and six-digit hex entry. Click or drag the strips, or select rows with gamepad up/down and adjust with left/right
or the left stick; holding accelerates larger moves. A / Cross picks a focused preset. HSV editing retains hue
through gray and black. Use color sends and saves the preview's exact sRGB bytes; Cancel leaves the accepted choice
alone. Settings store the same record as the network. An old saved palette index migrates once, only when no
appearance record exists; a corrupt saved record uses a safe preset.

`Core.AppearanceRecord` is immutable; `Core.AppearancePalette` owns the shared presets and legacy fallback. `Core.AppearanceCodec` is the only serializer, shared by settings, Hello, SetProfile,
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

**Tank supply.** The lobby settings carry an int32 multiplier after the mode id and mint pace in SetMode and
LobbyState. Values outside 1–8, missing fields and trailing bytes are rejected, just as invalid mint steps are.
Switching modes preserves supply and mint pace. Mouse −/+ buttons change supply; focus its value and use
keyboard arrows, D-pad or left stick. Bounds disable the corresponding button, and focus survives lobby updates.
The roster preview uses default tuning and says so, because guests do not have the creator's debug tuning yet.

At start the creator copies its tuning and applies the lobby choice before sending its snapshot; its saved
offline multiplier stays unchanged. The server independently applies the choice before suburb generation and before
sending MatchStart's tuning snapshot. Every peer, including a late builder, constructs the transmitted plan with
that shared tuning. Lobby settings lock in Loading and stay locked through the match. Offline F1 still exposes
TankCountMultiplier; the match panel omits it (including from Defaults), and the server ignores live debug
attempts to change it. Tank supply remains the lobby choice for the entire match.

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
- *Ownership:* whoever punctured a venting tank owns it and sends its position and velocity at a set rate. Other clients ease toward that position, snapping if they drift far or solid geometry blocks recovery to the actual owner sample.
- *Sleeping tanks:* the owner publishes an initial pose and a reliable final rest when it stops. These states
  also relay during loading and buffer until each client has built its bodies. Observers retain that resting
  target until newer movement, an epoch change or a local predicted claim replaces it; local collisions cannot
  permanently displace a resting copy after its owner's final publication.
- *Detonation:* whoever detonates the first tank in a chain runs the whole chain on their client and sends each explosion with its position. Other clients play those explosions and don't run chain reactions themselves for remote blasts.
- *Scoring:* the server credits the point only for a tank in its ledger that has not already exploded. If two players claim the same tank, the first claim wins. Map tanks are registered from the plan; client-created drops are registered atomically before being relayed. Exploded ids remain reserved for the whole match.
  The live tank count is maintained on successful registration and detonation; cars do not count. Canonical records,
  sorted live-body snapshots and departure transfers retain their existing traversal.

**Players:**
- Each client owns its own player's movement and sends it to the others, who show it smoothed. Player snapshots
  include discrete gait, support phase and weapon action alongside the blended rifle poses, so remote creep uses
  the walk clip and remote sprint uses the sprint clip. These three bytes joined Protocol 2, a minor release (0.3.0).
  Gaits share a distance-driven phase based on displayed motion. Feet retain world contacts on static ground,
  releasing and blending after support changes or snapshot corrections; foot positions are not sent.
- The shooter's client decides whether a bullet hit a player.
- The victim's client decides whether a blast ragdolls them, because it knows exactly where they were.
- The victim's client spawns the dropped tanks and owns them until another player interacts with them.

A drop (a Tank Bank Spill event in the [mode envelope](modes.md#the-envelope)) must contain unique, unused ids
from that player's allocation slot and fit both their unreserved bank and the drop reason's tuner. Its glow uses the
dropper's accepted match appearance; the spill contains no color field. The server refuses the
whole batch if any entry is invalid. Scoring, spilling and authority transitions happen only while the server's clock is playing. A late lifecycle prediction is refused and restored to the last authoritative state. Known exploded body ids may appear in late snapshots or claims, which clients
ignore; body ids that never existed are refused. A client missing a server-accepted tank still plays its reported
blast, including throws, without running a chain or claiming credit locally.

Drops are predicted locally. The rules reserve the bank for the spill's ticket, the ledger registers the batch, and
the server broadcasts the accepted spill (SpillAccepted) to its sender as well as the other players after the debit
(Score). It replies with SpillRefused ids for a fully decoded batch it cannot accept. The sender removes only those pending
predictions and releases their bank reservation. This also handles legitimate drops overtaken by a live debug
drop-count change or the server's match deadline. Malformed packets still get no reply.
The deadline check uses the server clock even when a packet is polled before that frame's phase transition.
Refusing a predicted root also cancels its pending child fuses, including already owned venting neighbors that
needed no ownership request. Children already sent still receive explicit refusals and restore their own bodies.

The ledger proves existence and prevents duplicate scoring; it does not prove hits, movement, proximity or blast
positions. Those remain decisions of the distributed client authorities.

The ledger also owns each body's peer and monotonically increasing epoch. Every accepted ownership or lifecycle
claim increments that body's epoch, including another grant to the same peer; departure transfers get new epochs.
Moving publications use monotonic network time for send rates and checkpoint deadlines, so slow or catch-up
physics cannot stretch freshness intervals or burst old samples. They include a reliable checkpoint every 0.25 s
plus up to one normal send interval, and immediately
when publication is required. The server preserves its delivery class on the second leg; other moving samples
remain disposable. Native ENet throttling can suppress those samples beyond injected loss. Checkpoints and
reliable rests carry the same per-body sequence within the epoch. Neither side compares owner clocks:
old epochs and duplicate/older sequences cannot replace a newer state or wake a completed rest. A client retains
one newest future state per body when a motion publication overtakes its reliable grant.
Moving batches use sampling timestamps on the sender's ping-estimated server clock. The server bounds these to
the existing 0.3 s extrapolation window; zero means no ping yet and falls back to relay time. Receivers use their
own server clock estimate to include both transport legs in bounded motion extrapolation. This also survives
motion overtaking its grant. Rest settling uses receipt time; clocks never order states.
The reliable canonical grant/refusal supplies an observer correction target before the owner publishes again;
an overtaking newer publication keeps its sampling and receipt times. Motion expires after 0.5 s without a
receipt, independently of sampling transit. Fresh vent observers use the owner's velocities and retain
rotation-error steering through the spin limiter. They do not add their own jet impulse after that correction;
vent presentation and age still advance. Owners, stale observers and predicted local claims keep the ordinary
jet force and gameplay spin limit. A local claim or grant releases observer force suppression immediately.
Linear velocity describes a body's mass center, which can differ from its node origin. Correction extrapolates
and steers that center, rotates the local offset, and converts back to a node transform for a snap. Rotation error
alone must not translate an already aligned mass center. Custom tank centers and native automatic centers use
the same model; captures still compare the original node positions.

`Claim` requests carry expected epochs and proposed poses for the complete group. The server validates every member
before granting any, so later grouped interactions can include bodies already owned without a partial handoff.
A local group with a pending member is left unpredicted for its caller to retry. Prediction never publishes movement until a grant is
confirmed. `Puncture` and `Explode` use the same expected-epoch path; each request has a peer-local request ID.
Predicted drops likewise defer body publications until the server's creation echo; a bank debit alone does not
confirm creation. Deferred bodies retain their required first checkpoint or rest. This prevents independent
body lanes from overtaking registration and invalidating a batch of otherwise registered bodies.

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

Each match owns one plain `BodyPublisher`, called at the existing physics callback position after actor states
and before pickup checks. `BodySync.Collect` clears and fills its reusable moving/rested buffers. The publisher
groups moving entries into reusable traffic buffers in first-encounter order, preserving member order and the
existing `Protocol.MaxBodyStates` batch boundaries. It samples transforms and velocities only while serializing
each batch, with a fresh server-clock sampling conversion per batch. Packet writers and transport copy ownership
are unchanged. Every send still checks world lifetime synchronously; retirement during a send suppresses later
delivery while the already-collected entries retain their sequence progression. Buffers release entry references
after each publication, including exceptions, while retaining capacity for the next tick.

Loose tanks gather in both modes through `TankGather`: intact, unshielded tanks outside their built/gathered pile
connect within `GatherRadius` (3 m) and wait `GatherDelay` (20 s), with at least `GatherMinimum` (2) members. Motion,
disturbance and membership changes restart that wait. The creator (or current world-authority successor) requests
an atomic `Claim` for the complete group, including tanks it already owns. Nothing glides before that grant. The
component rechecks eligibility, membership and clearance after the reply, then uses the granted epochs as its lease.
A refused grant or changed member leaves the group alone; ownership/lifecycle changes cancel an active glide.

Built square and triangular pyramids stand upright on ordinary collar/foot contacts and start asleep, just like
other built tanks. Their tier rise is `PileUprightLayerHeight`; side-laid rows retain `PileLayerHeight`.
[The upright pile proof](upright-piles.md) covers stability, player passes and disturbances. This shared construction
change and tuner require a minor release; it shipped within Protocol 2 (0.3.0).

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

**Platforms.** Windows, macOS and Linux builds of the same minor version play together (0.2.0 with 0.2.5). A version check when a player connects turns away builds of another minor version or protocol, and says which version is needed (see [Versions and releases](development.md#versions-and-releases) in the developer guide).


## Playing

- **Main menu:** Single player, Multiplayer, Quit. Single player is the sandbox as before; its pause menu gains a Main menu button.
- **Multiplayer:** (or start the game with `-- --connect=host[:port]`) type a name and the server's address (`host` or `host:port`; the port defaults to 24680, UDP). **Host on this computer** runs a server inside the game and joins it, for playing without a dedicated server; the lobby screens then show the addresses others can connect to.
- **Lobbies:** create one or join one from the list (lobbies in a match can't be joined). Joining puts you in the lobby world; Tab (View on a controller) opens the lobby panel beside it, and Esc, B or Close puts it away. In the panel, everyone picks a name and any color and may add bots, and the creator sets mode, length and King mint pace, then presses Start. A release build needs two actors (players or bots) to start; a debug build may start alone, for testing.
- **In a match:** the clock and the tanks you have banked are at the top, ammo at the bottom right. R reloads (an empty magazine also reloads on the next trigger pull). Walking over an ammo can takes it, unless the reserve is full. Esc or Start / Options opens the controls with Resume, Leave match and Quit; the match keeps running behind it. Leaving a match also leaves the lobby.
- **After a match:** the results show for `ResultsTime`, then everyone is back in the lobby world, ready for another.

## Playing against bots

**Add bot** in the lobby opens a dialog with a name (the first free "Bot N"), a color (the presets, or **Custom color**
with the appearance picker) and Easy, Normal or Hard. Any lobby member can add bots; each bot's row shows its
difficulty and, for its owner only, **Edit**, which reopens the dialog with **Remove bot**. Bots count toward the
lobby's capacity, so Add bot is disabled when the lobby is full. The dialog works with the mouse, the keyboard (Tab,
arrows, Enter, Esc cancels) and a controller (B cancels); a refused request keeps it open with the server's reason.
The lobby section [below](#lobby-bots-a8-protocol-2) describes the runtime and validation.

From a source checkout, `tools/dev/add_bots.sh -S host:port` still starts separate bot processes (4 by default) on that
server; without `-S` they join a server on this computer. Create a lobby first: they join the first open lobby and play
every match it starts. `-k 0.5` makes them easier (slower to turn, looser aim, slower to react), `-b simple` gives them
the first, easy brain, and `-x` stops them. Each uses about 7% of a core and 0.6 GB while it waits. These test tools
play at skill 1 unless told otherwise, the Hard profile.

The Tank Bank brain (`BankingBot`, `src/Bots/BankingBot.cs`) plays to win:
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
blacklist, replan and jump counts. The simple brain remains the original benchmark. Measured bot comparisons from the
first bot package are in the [multiplayer verification records](evidence/multiplayer-verification.md#bot-comparison-date-not-recorded).

## Hosting the server

The server is the game itself started with `-- --server` (and `--headless`). It runs no physics and uses little CPU.

- **Docker:** `tools/server/deploy.sh <ssh host> <dir>` exports the Linux build and runs it in Docker on that host,
  in `~/<dir>` (container `propane-server`, `restart: unless-stopped`, UDP 24680). It requires a clean checkout whose
  HEAD is the version's matching release tag (`v<config/version>`) contained in local `main`. Fetch release history and
  use a separate release checkout; it refuses unreleased work and does not select or promise the latest remote release.
  The exported assembly/PCK must pass inventory before SSH starts; `game/release-id.txt` records tag and commit.
  It plays with every patch of that minor version. Logs: `docker compose logs -f` in that folder.
- **Reaching it:** players connect to the machine's address on UDP 24680: its LAN address, its address on a VPN the
  players share, or, with UDP 24680 forwarded on the router, the public address.
- **From source:** `godot --headless --path . -- --server [--port=24680]`.

## Code

| Path | Contents |
| --- | --- |
| `src/Main.cs` | The root scene: menus over a backdrop, single player, the match, or server mode. |
| `src/Net/LobbyServer.cs`, `LobbyServer.World.cs` | Connections, lobby admission/roster, staging, transport and application diagnostics. |
| `src/Net/ServerRun.cs` | One match or roaming lifetime: actors, action admission, relay policy, ledger, pickups, readiness, mode and tuning. |
| `src/Net/LobbyServer.World.cs`, `LobbyWire.cs`, `Match.Lobby.cs`, `src/Modes/LobbyRules.cs` | The lobby world: the server's run, its pile stock, a peer's world, and its pure rules. |
| `src/Net/ModeRunner.cs`, `ModeEnvelope.cs`, `src/Modes` | The mode runner, the fixed mode envelope and pure mode rules ([modes.md](modes.md)). |
| `src/Net/NetClient.cs`, `NetTransport.cs`, `Protocol.cs` | The client connection, the ENet adapter, the message types. |
| `src/Net/IPacketTransport.cs`, `ProtocolPeerMap.cs`, `PacketTransports.cs`, `FaultPacketTransport.cs` | Message transport contract, Protocol 2 identity bridge/backend selection, deterministic network faults. |
| `src/Net/MessageCodec.cs`, `TankLedger.cs` | Complete, typed wire decoding and the server's tank existence/lifecycle ledger. |
| `src/Net/MatchInbox.cs` | Per-match buffering until scene initialization, with generation-safe cleanup. |
| `src/Net/BuildInfo.cs`, `UpdateCheck.cs` | Versions and who plays together; the launch check for a newer release. |
| `src/Net/Match.cs` | Callback, inbox and lifetime coordination for one peer; the [ownership table](architecture.md#composition-roots) names its replica, actor, bot, mode and view owners. |
| `src/Net/IModeClient.cs`, `BankClientAdapter.cs`, `KingClientAdapter.cs`, `LobbyClientAdapter.cs`, `SilentClientAdapter.cs` | Session-owned mode state, envelope consequences and plain presentation feeds, selected by the compiled mode bundle. |
| `src/Net/ClientAuthorityTransactions.cs` | Session-owned causal requests, reservations, expected epochs, sent roots and gather request bookkeeping. |
| `src/Net/BodySync.cs` | Ownership and corrections for tanks and cars. |
| `src/Net/SnapshotBuffer.cs` | Smooth playback of other players, `InterpolationDelay` behind. |
| `src/Net/PlanCodec.cs` | Packs a suburb plan for sending. |
| `src/World/AmmoPickup.cs` | The ammo can and its build-up reveal. |
| `src/UI/Menus.cs`, `MatchHud.cs`, `MatchMenu.cs`, `ResultsScreen.cs` | Front end, match HUD, pause menu, results. |
| `src/UI/StageScreen.cs`, `LobbyCard.cs`, `src/Fx/LobbyFloor.cs` | The lobby world's stage screen, its lobby card and its floor (pads, ring, title). |

### Client authority ownership

Match creates `ClientAuthorityTransactions` during session configuration. The existing session stop callback
retires it after inbox detachment and event unsubscription, including failed construction. Disposal is idempotent:
it detaches pending gather listeners, clears requests/reservations/causal roots and releases the replica collaborator.
A replacement match gets a fresh owner and request sequence. Empty flushes allocate nothing; claim selectors
exist only while constructing a proposal at send time. Actor action sequences and `DropPredictions` remain
in each ClientActorReplication attachment; BodySync retains correction inside ClientWorldReplica, which owns
physical lookup/reconstruction/publication. Match supplies synchronous forwarding and wire sends.
Remote snapshot delivery and application follow the actor's current input source. A temporary override or absent
source suspends the old buffer; a replacement replicated source receives and applies its own snapshots.

Resolution is synchronous: finish the request and release reservations, update dependent expected epochs,
reconcile canonical bodies, flush eligible requests, notify listeners in registration order, then remove gather
membership. Proposals are sampled when sent. Root requests precede children; refused children retain their original
epochs and receive explicit refusals. Gather grants keep their lease membership through notification and revalidate
the granted epoch before moving a tank. A synchronous send can retire the session; an in-flight resolution still
finishes its notification, while the disposed owner retains no queued work or callbacks.

### Client mode ownership

Match validates and sequences mode envelopes, then calls its bundle-selected `IModeClient` synchronously. Bank
owns scores and spill confirmation/prediction policy; King composes Bank with its received zone state and shared
pile placement/birth mechanics. Lobby explicitly owns roster/staging/refill/stock policy and panel-input decisions;
Sandbox explicitly selects a silent adapter. HUD presenters and floor/zone nodes remain presentation bindings.
The existing replication stop callback disposes the adapter after inbox detachment/unsubscription, clearing mode
state and borrowed physical/view collaborators. Actor action sequences/drop predictions, clock domains, command
order, health finalization, round replacement, protocol and mode eligibility are unchanged.

### Decode and failure boundaries

Connection owners borrow immutable `PacketCodec` inputs captured by composition: schema resolution, fixed lobby
eligibility and tuner validation. Wire parsing never reads the composition catalog or native Tuning schema itself.
`MessageCodec` remains a composition convenience used by Dev fixtures and release-reference probes; it delegates
to the current catalog's codec. Production Match authority/mode sends and replica gather/pose/action sends use `NetClient.Codec`,
the same captured instance as that connection's receive path. Captured connection codecs survive later scoped catalog restoration without changing their schemas.

Expected malformed/truncated/out-of-bounds/unknown-schema failures are logged and discarded before dispatch.
Expected run admission rejections stay at their original checks, preserving earlier sequence consumption and
rejection order. An exception from a handler, including `InvalidDataException`, is an internal owner failure;
diagnostics include its type and stack, never a malformed-packet accusation. NetClient retires its connection and
inbox through its ordinary drop path, including a failure during delayed replay. Cleanup continues past transport
or offline-observer failures and reports those causes separately.

LobbyServer contains receive, tick and departure failures to their affected lobby: routes detach before normal
run disposal, staging clears and the lobby phase returns to Waiting. It attempts a notice and state update, keeps
peers connected and does not automatically create another run or retry a partial transaction. The creator can
start again. Notification failure is logged without interrupting unrelated lobbies. A transport's ordinary returned
send failures retain their existing policy; unexpected thrown send failures inside dispatch follow owner retirement.
Create/join explicitly track the affected lobby through departure and admission, so a later construction or
destination failure cannot retire the previous lobby after a successful departure. This does not provide rollback,
reconnect/resume or automatic recovery of a client scene after a server run failure.

### Transport contract

`TrafficReliability` defines independent ordering lane indices/count and reliability. ENet maps these indices to
channels; simulated faults use the same neutral contract, with no dependency on the ENet adapter. Server metrics
format plain transport/inbox samples through `TransportMetrics`; composition samples client state separately.

`NetClient` requires an `IPacketTransport` and `PacketCodec` before entering the tree; Main selects both production
inputs. Its parameterless Godot constructor rejects missing inputs, as LobbyServer's does. `LobbyServer` borrows its
transport, codec and application settings through composition. `PacketTransports` selects ENet for production; the unit suites inject an in-memory loopback with a manual monotonic clock. Gameplay sends
`ReliableOrdered`, three `ReliableBodyStates` lanes, `UnreliablePlayerPoses` or `UnreliableBodyStates`, never backend
channel numbers. ENet keeps control and disposable traffic on channel zero; reliable body checkpoints rotate per
body through channels one to three, so a checkpoint retry cannot block control. Ordering is per connection and
reliable class, with no ordering across classes. On receive, the adapter uses native reliability/channel for checkpoints and the
existing type byte to distinguish disposable body states. Epoch/sequence validation handles overtaking motion.
No application packet layout, player id, tuner or gameplay rule changed.

Connection handles are opaque objects and are never reused. `ProtocolPeerMap` allocates server-issued peer ids
independently of ENet's native ids; the client learns its peer id from Welcome. The server-owned roster assigns
separate actor ids, and a peer may own several actors. Discovery, lobbies and invites are application services;
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
Native endpoints normally use a 3 s minimum/8 s maximum ACK timeout. The server extends only the builder's
connection to 20/30 s before sending MatchStart, and the client does so before invoking its construction callback.
`IPacketTransport.SetLoading` carries this policy through decorators; backends without native ACK deadlines may
ignore it. Clients report `Loaded` after their first complete frame, so initial physics and shader work retain
loading grace. Loaded restores the short timeout at both ends, including a late Loaded after countdown or Go.
Leaving construction also restores it. The ENet adapter restores it only after a hold as long as the load itself
(MatchStart to Loaded) plus 1 s: ENet doubles a reliable command's retry interval on every unacknowledged send,
without a cap, so commands a stalled loader lost (its receive buffer overflows while it does not service the socket)
retry up to that long after it resumes. Ending the grace at Loaded let the next such retry meet the short timeout,
which declares the peer lost instead of resending. A silent failed endpoint therefore does not keep a live-match
player present for the construction grace beyond that hold. Explicit disconnects remain immediate. ENet's retry
schedule can defer the check until its next retransmission; these are ACK timeout parameters, not exact wall-clock
disconnect times.

`NetClient.Diagnostics` exposes the local connection counters; server logs include per-connection payload
packets/bytes accepted out and received in, RTT, managed queue
packets/bytes, drops and last transport poll cost every ten seconds. These are message counters, excluding UDP
headers, ENet retransmissions and ACKs. Native ENet queue depth is unavailable through Godot; the log says so,
rather than treating the zero managed queue as the native queue. ENet native reliable-loss ratio is shown when
available. Diagnostics and id bindings last through the disconnect callback, then are released. ENet send acceptance
reflects Godot PutPacket, which exposes no native send-buffer limit.

A settled intact observer can remain wedged in floor contact even while its
awake owner sends fresh near-zero motion. If both copies stay within the
existing gather rest speed/spin thresholds and pose error persists for three
correction blend times, correction applies the owner pose directly. Owner
motion restarts that interval. This affects only the remote intact copy; it
does not mark an owner stream resting, sleep the copy, or change gather rules.

Clients service the packet transport from both process and physics callbacks.
Catch-up physics can continue while process frames are scarce; delivering
reliable ownership/checkpoints and ENet ACKs there prevents a slow rendered
frame from stalling an otherwise active network stream. Both paths use one
monotonic ping deadline, so extra polls do not increase the ping rate.

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
independent reliable control and body lanes to retain their ordering and disable Nagle to retain ENet's immediate flush. Steam can retain remote
reason codes and queue statistics that Godot's ENet wrapper cannot. No Steam SDK, lobby, invite or relay implementation
is included.

Details settled while building, within the decisions above:

- **Remote players** are full copies of the character (animation, rifle, ragdoll) driven by the states their own game sends. A thrown player's ragdoll falls on its own on every screen, steered after the real one; it gets up where the real one got up. Shots hit other players' bodies and limbs. Players pass through each other.
- **Ownership** goes to whoever last disturbed a body (a shot, a puncture, a blast, a push, a drop); the match's creator owns everything nobody has touched, and a player's bodies pass to the creator (or the next player) if they leave. Claims go through the server as atomic groups with expected epochs; accepted grants are broadcast in one order. A handoff resets the sequence boundary and supplies the new canonical observer target. The new owner sends its final state even if its copy was already asleep, so nobody is left correcting toward the former owner forever.
- **Dropped tanks** glow in the dropper's color while they ignore bullets and chain reactions.
- **Tuning in a match:** everyone plays on the creator's values: the baked ones in a release build, the creator's live ones in a debug build. Only that original creator's debug build gets F1 and may change the match's values. Hosting succession does not transfer this permission. Hello appends a debug-build flag, captured at match start; this is a client capability declaration, not remote build attestation. Tuning crosses the wire as named bool/int/float scalars, with exact exported types and inclusive declared ranges checked before applying the whole snapshot or a change. `FixedSeed` accepts the full int32 range. Each match owns a separate validated tuning instance; it never replaces the application saved template. The panel targets that match instance, and Save also updates the application template for future worlds.
- **Starting values** (all tuners): 20 base tanks plus 4 per player; pile limits of 5–12 plus floor(0.25 × players), near the central 40 m; a 190 m base map plus 4 m per player, with clear starts at least 12 m apart; a 30-round magazine, 60 in reserve, at most 150, a 1.6 s reload; 3 base ammo cans plus 1 per player, of 30 rounds that come back after 20 s; a 2:30 match after a 4 s countdown; results for 10 s.

## Testing

| Command | Checks |
| --- | --- |
| `make test` | Plain C# assertion suites (every wire message, malformed payloads, server ledger and permissions, lifecycle buffering and ownership, transport faults/reconnect/lifetime, pure mode rules and the mode envelope), the scripted `mode_test` rules harness, score-checker regressions, movement, gamepad menu, device switching and platform checks, generator checks (20 seeds plus large layouts and forced formations), and separate-process built-world hashes. `make test-full` adds deeper layout, navigation and lifetime sweeps. Add suites under `src/Dev/UnitTests/` and mark them `[UnitSuite]` for discovery; any failure prints its name and exits nonzero. |
| `godot --headless res://scenes/dev/generator_tests.tscn -- --seeds=100` | All four formations; 2, 8, 16, 24 and 32 players; exact supply counts, clear separated reachable starts, hull overlaps and supports, and exact `PlanCodec` round trips. |
| `tools/dev/net_bots.sh -n 3 -l 60 -- --net-lag=120 --net-jitter=30 --net-loss=3` | A local server and bot players (`scenes/dev/net_test.tscn`) that run to tanks, shoot them and each other, and fetch ammo, under simulated lag and loss. `-w N` shows N of them in windows; `-S host:port -k` uses an existing server (audit it separately with its server log). `-o DIR` picks the log directory (default `build/net`); one that already holds `*.log` files is refused unless `-f` replaces them, and the server, every bot and their child processes are stopped when the script ends or is interrupted. Tank agreement and score checks run automatically; either failure fails the command. `-k` skips checks, `-t clean` uses tighter tank limits. |
| `tools/dev/compare_tanks.py [--profile clean] out/bot*.log` | Same-machine shared monotonic samples interpolated to a 50 ms grid, all peer pairs, bounded lifecycle/moving/resting disagreement and settled final agreement. Missing samples, gaps or incomplete matches fail. `--details` prints disagreement intervals. Replaces the coarse countdown and tank-path comparers. See [calibration](evidence/network-soak.md) for limits and observed failures on dev. |
| `tools/dev/check_scores.py out/server.log out/bot*.log` | Server conservation audit and identical per-player final scores on every bot; exits nonzero on a missing audit or disagreement. |
| `python3 -B tools/dev/check_liveness.py --output=build/liveness [--silent=server]` | Stops a native client or server process during play without sending Leave; asserts detection within 10 s. Run after `make build`; separate-process logs stay in the chosen directory. `--project` and `--maximum` allow historical baseline measurements. |
| `net_bots.sh -n 2 -l 45 -s "--start-score=8" -- --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1 --net-lag=120 --net-jitter=30 --net-loss=3` | Two bot hits spill two close tanks from the joiner; both clients require an intact stable gathered pile owned by the world authority. Scatter/toss are fixture overrides to keep the drops within the gather radius. Add `-w 2` and `--capture-dir=DIR` to verify both clients' shield colors and shimmer as well as the glide; headless clients assert gameplay state without shader readback. |
| `net_bots.sh -n 2 -w 2 -s "--start-score=8" -- --scenario=duel --capture-dir=DIR` | Scripted close-up: hits with the grace period, dropped tanks, a blast that throws the victim, from both sides. |
| `net_bots.sh -n 2 -l 35 -w 1 -- --scenario=movement --assert-motion --assert-feet --assert-grip` | Remote creep, sprint, air aim suppression, held-aim landing, hip/aimed bursts and walking reload through real snapshots; final contact and rotation assertions. Headless runs need at least 18 seconds; rendered runs allow 35 seconds for loading and capture stalls. |
| `net_bots.sh -n 2 -l 35 -w 1 -- --scenario=movement --assert-grip --view=player --capture-dir=DIR` | Observer's real local camera follows the remote body; captures hip/aimed firing and the reload contact. `--view=player` on the melee fixture also follows the victim through knockdown and get-up handoff from the attacker's camera. |
| `net_bots.sh -n 2 -l 45 -w 1 -- --scenario=movement --assert-grip --view=rifleright --capture-dir=DIR` | Final-pose wrist, sampled hand penetration and eye-line assertions on the remote character; close-ups and `grip.csv` from the host. |
| `net_bots.sh -n 1 -w 1 -- --scenario=pickup --capture-dir=DIR` | Taking an ammo can and its return. |
| `net_bots.sh -n 2 -w 2 -l 20 -- --scenario=puncture-race --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir=DIR` | Two opposed bullet contacts on simulation tick 180; both copies must still be intact. Every client asserts canonical hole, normal, swirl and lean, and bounded owner correction error over time. A third bot observes without shooting. |
| `python3 -B tools/dev/compare_vents.py out/bot*.log` | Identical canonical vent parameters and at least 100 overlapping, time-aligned trajectory samples per pair; exits nonzero if maximum drift exceeds the default snap distance (3 m, a test tolerance). |
| `net_bots.sh -n 2 -l 30 -- --scenario=vent-contact --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace` | Eight isolated owner-started vents spin on clear floor. Asserts canonical ownership and vent state, logs actual collider contacts, speed and spin, and checks agreement through the final grid. |
| `godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/body_correction_tests.tscn` | Native integration asserts zero extra jet impulse for fresh observers, ordinary owner thrust, and immediate thrust restoration for stale, predicted and granted ownership. |
| `net_bots.sh -n 6 -l 90 -s '--tune.MaxPlayers=6' -- --melee-start --net-lag=120 --net-jitter=30 --net-loss=3` | Six-player agreement probe. The explicit debug server tuner raises admission capacity before anyone connects; release admission and tuner defaults are unchanged. |
| `net_bots.sh -n 2 -w 2 -l 10 -s "--start-score=8" -- --scenario=drop-refusal --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir=DIR` | The creator lowers the hit-drop count while a hit is in flight; the victim rolls back the refused drop, both clients keep one accepted tank and a bank of 7. |
| `net_bots.sh -n 4 -l 120 -b smart,simple,smart,simple` | Bot brains against each other; the server log ends each match with tanks gained / lost to hits / lost to throws. Add `--bot-log` to log what each bot is doing. |
| `net_bots.sh -k -n 2 -l 10 -- --match-delay=26` | Skips the complete-match audit for this intentionally incomplete capture. Builds after the server's loading timeout and Go; the ready log must already say `playing True`. Use `--matches=2` without the delay to exercise another round on the same connection. |
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

Dated soak and melee results are in the [multiplayer verification records](evidence/multiplayer-verification.md). The
[tank assertion and correction work](evidence/network-corrections.md) added the automatic failure checks and the
replication fixes found by repeated soaks, and the [independent integration review](evidence/network-integration.md)
records the A4 integration, native regressions, remaining capacity limits and verification matrix.

Protocol 2 also changes **Hello** (trailing appearance record), **SetProfile**, **LobbyState**, **MatchStart** and
**MatchEnd** (one appearance record in each profile/standing), and **Drop** (removes the redundant palette index).
Appearance adds shared presentation tuners. It stayed within Protocol 2, released as the minor release 0.3.0.
Welcome and Rejected retain their existing layouts.

Protocol 2 adds the reliable `Gather` relay, authority request IDs, grouped epoch grants, per-body publication
sequences and canonical/refused lifecycle reconciliation, as well as match ids, scalar tuning, timestamps on final
body rests, drop refusal replies and discrete player movement states, and appends the debug flag to Hello. These
changes share one protocol bump, to 2, released as the minor release 0.3.0. Every match-scoped request,
event and relay carries its match id, and either side ignores a different id.
The server ends each match with a bank audit: initial test scores + detonated map tanks + re-detonated drops - spills
must equal total banked, including the banks of departed players retained in the final standings.

## Melee (R6, Protocol 2)

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
progress; interpolation cannot rewind across a new swing or release. These two messages, the snapshot field and
the shared tuners joined Protocol 2 and required a **minor release** (0.3.0).

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
accepted swing/contact logs show actual use. The launcher audits that run automatically with both scripts and all four bot logs.
`make test` runs the pure resolver/geometry/contact/server/wire assertions, real wall/tank/prop contact fixture,
input bindings and device reference checks, plus the local strike grip/rotation probe. `make test-full` adds ten
reload cancellations spanning early/late progress in both reload clips. The package's dated verification is in the
[multiplayer verification records](evidence/multiplayer-verification.md#melee-verification-2026-10-11).

## Participants (A6, Protocol 2)

A connection handle stays inside the transport. The server issues a monotonic positive **PeerId**, independent of
ENet's identifiers. A lobby owns a revisioned participant roster: **ActorId**, **OwnerPeerId**, Human/Bot kind,
name, appearance, versioned controller configuration, presence, allocation slot and **LifeId**. Actor ids and
allocation slots are never reused in that lobby session, including across matches. Spawn slots are a separate,
frozen match mapping. Drop entities use the actor's allocation slot, never its spawn position. Allocation fails
before an id, life, epoch or publication counter can wrap. Local view ids are process-local and never transmitted.
Returning to the waiting lobby advances surviving actors' lives for their next round, with one roster revision;
actor ids and allocation slots remain stable. If a round completion or departure encounters exhausted life or
roster-revision space, the server retires that lobby and rejects its peers with a reconnect notice; subsequent
ticks and fresh lobbies remain usable.
Ragdoll recovery is the same life, not a respawn.

MatchViewBinding caches Match's local player in session actor order: the first owned Human, otherwise the first
owned actor.
Actor insertion/removal refreshes the cache synchronously before node initialization/retirement; a lobby profile
kind replacement refreshes it before presentation changes. Session stopping clears it before actors are freed.
Ownership is fixed on each character at construction; body-authority or lobby-creator handover does not retarget
the local view. A rebuilt match selects from its new roster. Initial construction requires an owned actor.
An empty roster (possible through direct harness removal) has no local HUD/results feedback or King pile reporter;
score/hit readers return neutral values. `Match.Player` requires an owned actor and rejects access outside that
lifecycle explicitly. Lab constructs its own session rather than a Match.

One peer may own several actors. Each has its own input, aim, ammo, pose/action sequence, grace and predictions;
there is one shared world and one body synchronizer per peer. The server owns bank and standings. Attacker owners
report bullet/melee contacts; victim owners apply grace, blast distance/throws and spills. Shared body ownership
and correction stay per peer. Delayed chains carry the initiating actor unchanged; the ledger validates that a
causal request belongs to the same actor as its accepted root. Another actor on the same peer cannot borrow credit.
Same-peer hits follow the same actor event handler as network hits. Predicted contacts and reliable echoes share
an action id and are applied once. Body publication waits for creation confirmation for every owned actor's
drops, including bots; bank confirmation alone does not release that wait.
Profile updates, including the legacy `SetProfile` path, cannot edit the roster during a match.

Roster requests are reliable `AddActor(RequestId, Kind, Name, Appearance, ControllerConfig)`,
`UpdateActor(RequestId, ActorId, Name, Appearance, ControllerConfig)` and `RemoveActor(RequestId, ActorId)`.
Only the owning peer may update/remove an actor. The server replies with
`ActorRequestResult(RequestId, Accepted, Revision, Reason)` and broadcasts full
`RosterState(LobbyId, Revision, Actors)`. Repeated requests do not mutate the roster; stale revisions are ignored.
Controller configuration is three bytes: schema version (1), Device/Bot kind, and difficulty (0–2; Device uses 0).
A8 adds the lobby bot controls and runtime brains, and a trailing roster revision on each request (below).

Wire changes in this package:

- `LobbyList` advertises the tested actor ceiling; each summary adds its selected capacity and counts actors.
- `SetCapacity(Int32)` changes a waiting lobby's capacity for its creator, from 2 through the tested ceiling.
- `LobbyState` adds capacity, roster revision and the peer membership list; its entries are full actor profiles.
- A full actor profile is `ActorId, OwnerPeerId, Kind, Name, Appearance, ControllerConfig, Present,
  AllocationSlot, LifeId`. Actor/peer/slot ids are Int32; revision/life/action counters are UInt32.
- `MatchStart` adds the roster revision, original creator peer and frozen peer capabilities before its actor/spawn
  list. It freezes accepted profiles, allocation slots, lives, unique starts and initial world authority. Loading
  acknowledgments and broadcasts operate once per participating peer; generation and supplies count actors.
- `PlayerState`, `Shot`, `MeleeStart`, `MeleeContact`, `Puncture`, `Explode`, `Drop`, `Ragdoll`, `GetUp` and
  `ClaimPickup` add `ActorId, LifeId, ActionId` after MatchId, before the server-inserted sender peer. Poses use a
  separate sequence stream from reliable actor actions. Both ends reject stale lives and repeated sequences.
- `Shot` and `MeleeContact` add the victim's LifeId. Targets, `Score`, `PickupTaken` and `MatchEnd` standings use
  actor ids. `Drop` relays the actor envelope; accepted appearance comes from that actor's frozen profile.
- Canonical `Authority` bodies add `CauseActorId`; body owners and authority request identities remain peers.
  The refusal enum adds `DuplicateRequest` for a reused accepted explosion request.
  `PlayerLeft` identifies a departed peer and successor world authority; clients remove every actor of that peer.

The server retains departed actors' banks for conservation and final standings, transfers surviving body ownership
with new epochs, and may transfer lobby creator controls. Original debug-tuning capability does not transfer.
The wire safety ceiling is 64 actors/peers, separate from capacity and the color presets. Hello, Welcome and Rejected
retain their layouts. These changes joined Protocol 2 and required a minor release (0.3.0).

The repeatable separate-client capacity gate and its measured evidence are in [network-soak.md](evidence/network-soak.md).

## Modes (A7, Protocol 2)

Tank Bank now runs on the mode framework described in [modes.md](modes.md), with identical rules. Wire changes:

- `ModeEvent` (15), `ModeCommand` (51) and `ModeSnapshot` (52) carry all mode-specific traffic in one fixed envelope.
- `Score`, `Drop` and `DropRefused` are retired into Tank Bank's Score, Spill, SpillAccepted and SpillRefused kinds;
  their numbers (40, 71, 47) stay unused. Round start sends one bank snapshot instead of restating nonzero scores.
- `MatchStart` appends the mode id (UInt8) and schema version (UInt16).

Hello, Welcome and Rejected are unchanged. These changes joined Protocol 2 and required a minor release (0.3.0).
No tuner, construction rule or world hash changed.

## Lobby bots (A8, Protocol 2)

A bot is an ordinary roster actor of kind Bot, owned by the peer that added it. Its owner's game simulates it exactly
like its own player: the same `PlayerCharacter`, owner-authoritative physics, actor envelope, action sequences, hit
grace, spills and pickups. The only difference is its intent, which the mode's goal policy (`ModeDefinition.CreateBot`,
`BankingBot` for Tank Bank, `KingBot` for King) chooses each physics tick from that peer's view of the match, through the shared navigation
and aim (`src/Net/OwnedBotSet.cs`). A bot has no camera, audio listener, HUD, hit marker or score popup. Other peers
receive its poses and actions like a human's and cannot tell them apart beyond the roster's kind. Each brain is seeded
from the match and actor ids, so it never draws from the world's shared random stream.

**Difficulty.** The controller's difficulty byte selects a named profile of local bot dials (`src/Bots/BotDifficulty.cs`):

| Profile | Skill dial | Skill | Aim error | Reaction | Aim turning factor |
| --- | --- | ---: | ---: | ---: | ---: |
| Easy | `BotEasySkill` | 0.2 | 0.92 m | 0.44 s | 0.48 |
| Normal | `BotNormalSkill` | 0.6 | 0.57 m | 0.32 s | 0.74 |
| Hard | `BotHardSkill` | 1.0 | 0.22 m | 0.20 s | 1.00 |

Skill blends the existing Easy/Hard endpoints (`BotAimError...`, `BotReaction...`) and scales aim turning
(`BotAimTurnSharpness` times 0.35–1). Aim error is the wobble around a fought player's aim point once the aim has
settled; reaction is the delay before firing at a new opponent. Normal is the Lab duel's default skill (0.6), and
Easy and Hard are the Lab's existing Easy and Hard. Easy and Normal keep objective-first goals; Hard compares
expected point gains and leader spills in Tank Bank and King ([bot policies](bot-navigation.md#hard-points-policy)).
Navigation and physics are identical at every difficulty. The difficulty dials are local C# properties like the
existing navigation and points dials: they are copied into each world but never enter the match snapshot, so the owner's profile values decide
its own bots and nothing else changes. King's new crowd tactics use shared dials in the **Bots** group
([crowd tactics](bot-navigation.md#king-crowd-tactics)); difficulty still controls aim and Hard's points choices.

**Validation.** `AddActor`, `UpdateActor` and `RemoveActor` gain a trailing UInt32: the roster revision the request was
made against. In a waiting lobby the server refuses, without changing anything, a request on any other revision ("The
lobby changed"), a client's add of anything but a bot, a change to an actor another peer owns, an add beyond the lobby's
capacity, and a bot name that is not 1–16 UTF-16 units of printable letters, digits, punctuation, symbols and plain
spaces without surrounding spaces, or that matches any present actor's name ignoring case. During a match the roster
is frozen. Malformed controller bytes, kinds, appearance records, zero ids and truncated or trailing data fail
decoding before the roster. The client checks the name first and shows the server's reason for any refusal.

**Departure.** A bot departs with its owner through the same path as a human: leaving the lobby, a crash or a timeout
removes all of the peer's actors (`RemovePeer`), and in a match each one's `ActorLeft` keeps its bank in the standings
and the conservation audit, while body ownership passes to the new host. Survivors remove every actor of the departed
peer on `PlayerLeft`, and the owners of other bots refresh their brains' view of the others.

**Cost.** Brains share one cached navigation grid and process-wide query/expansion budgets, with rotating priority.
Sight and goal selection run less often than steering; the hot paths reuse buffers and query nodes.
[Bot navigation](bot-navigation.md) describes the cache, dynamic checks and allocation limits. `Match.BotCost` counts thinks, total/worst tick seconds, allocated bytes and queries;
`net_test` logs them at the end of a match, plus tick percentiles and allocations after ten simulated seconds of warm-up. Measurements are in [network-soak.md](evidence/network-soak.md#lobby-bots-a8).

**Tests.** `BotTests` covers the requests (add, edit, remove, ownership, capacity, stale revisions, names, malformed
input), the profiles, the owner's runtime and departure. `menu_test --scenario=bots` adds, edits, cancels and removes
bots by keyboard and by controller, with a second client watching, and checks a full lobby's refusal; it runs in
`make test`. `net_test --lobby-bots=N` (or `2,1`, one count per `BotI` client, at `--lobby-bot-difficulty=0..2`) gives
each client lobby bots before the host starts; `--leave-at=S --leave-who=BotI` makes one client leave, and
`net_bots.sh` then omits that client's log from the completion checks while the survivors and the server's audit still
run them.

Hello, Welcome and Rejected are unchanged. The roster requests changed shape, so these changes joined Protocol 2
and required a minor release (0.3.0). No shared tuner, construction rule or world hash changed.

## King online (Protocol 3)

The lobby creator chooses **Tank Bank** or **King of the Cul-de-sac**, and everyone sees the choice. King adds
**Mint pace**, 0.5–10 seconds per tank in 0.5 s steps (default 2 s), and uses the lobby length as its round length.
Mouse −/+ controls and a keyboard/controller Left/Right value control change the pace. Switching to Tank Bank hides
it and preserves its value for switching back. Only the creator can change either setting, only while waiting;
unsupported modes and off-step/nonfinite paces fail decoding. Tank Bank keeps its rules, supply and ammo policy.

King plays the existing suburb with unlimited ammo and no cans, like its Lab prototype. Its moving zone, lone holds,
contests, minted banks, warnings, fresh piles and conservation are described in [modes.md](modes.md#the-envelope).
The server samples accepted player poses; every peer's HUD, beam/ring and bots read its Zone snapshot. Lobby bots
use KingBot automatically. The test clients select it with `net_bots.sh ... -- --mode=king [--mint-seconds=2]`.
`--scenario=king` is a two-client scripted hold/contest/move/pile proof (allow at least 55 s); rendered clients capture
hold, contest, warning, materializing/settled pile and results. `-n 3 -l 110 -k -- --mode=king --scenario=king-late --tune.PlayerSendRate=20 --delay-who=Bot3 --match-delay=70`
delays one builder past a move to
exercise buffered catch-up. The late client asserts the moved zone at readiness; run the score checker on all clients,
and continuous tank comparisons on the undelayed clients (the late client intentionally lacks full-play coverage); the lobby's existing waiting-only admission and lack of in-match reconnect are unchanged.

`check_scores.py` checks King conservation including minted tanks and compares every client's final bank and per-actor
mint totals with the server. `menu_test --scenario=king` tests mode, pace and round-length controls with real mouse,
keyboard and controller events, a guest observer, endpoints and focus preservation. The input scenario also exercises
the new mode/pace controls before its ordinary gameplay proof.

SetMode (16), LobbyList's mode id and LobbyState's mode/settings change message shapes; the mode envelope adds
King schema 4/1. King's shared zone, pile and presentation dials are now exported tuning values, retaining their Lab
controls. `Protocol.Version` is **3**, superseding the Protocol 2 entries above (released in 0.3.0); this required a
**minor release**, 0.4.0. Hello, Welcome and Rejected retain their layouts. Ring Out, Short Fuse and Dissolve Run remain Lab-only.

[King online verification](evidence/king-online.md) records the soaks, delayed builder, device controls, reviewed captures
and known vent outlier.

## Lobby supply probes

`net_test` and `tools/dev/net_bots.sh` accept `--tank-supply=N` after `--`, with integer N from 1 to 8.
Without it, the harness uses its local `TankCountMultiplier` tuner as the lobby choice; an explicit supply wins
over `--tune.TankCountMultiplier`. Normal player-created lobbies always start at 1×. Both human players and lobby
bots count toward the scaled supply, regardless of which peer owns them.

```sh
tools/dev/net_bots.sh -n 4 -l 90 -t clean -o "$PWD/build/lobby-tanks-net-clean4" -- --tank-supply=4 --melee-start
tools/dev/net_bots.sh -n 4 -l 90 -o "$PWD/build/lobby-tanks-net-fault4" -- --tank-supply=4 --melee-start --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 4 -l 90 -t clean -o "$PWD/build/lobby-tanks-net-king4" -- --mode=king --tank-supply=4 --melee-start
godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/menu_test.tscn -- --scenario=king --capture-dir="$PWD/build/lobby-tanks-menu"
```

The King menu probe also covers tank supply in both lobby modes and the Lab with real mouse, keyboard and
controller events, bounds, guest presentation, mode switching and draft retention. `LobbySupplyTests` covers every
wire step, malformed choices, creator snapshot conflicts, server generation with lobby bots, phase locks, and a
late client assembling the same tank identities and poses after buffered Go.

These trailing lobby settings fields are part of Protocol 3 (released in 0.4.0). No further protocol bump was made;
Hello, Welcome and Rejected are unchanged.

[Lobby tank supply verification](evidence/lobby-tanks.md) records the gates, reviewed captures, native matrix and stock-soak variability.

## Lobby world (A12, Protocol 3)

A waiting lobby is a place, not a menu: everyone who joined roams a shared void world, shooting and throwing tanks while
they wait. It is the destination a Steam invite will open later.

**The world.** It plays the **Lobby** mode (id 7, schema 1; [modes.md](modes.md)) on a hand-built void plan: no ground
lots or cars, `LobbyPiles` piles of `LobbyPileCount` tanks on marked pads round a `LobbyPileRing` ring, and arrival
spots round a `LobbySpawnRing` ring by allocation slot. Nothing scores and nothing spills; rifles never run dry
(`LobbyInfiniteAmmo`; bots carry a magazine whose reserve stays full, since they play by it). A pile whose last
intact tank has left its spot (`LobbyPileReach`) refills after `LobbyRefillDelay`, materializing over
`LobbyMaterializeSeconds` on clear ground (`LobbyPileClearance`), unless its tanks, with those already on their way,
would take the world past `LobbyTankLimit`. The server's own dials build the world, with the lobby's choices applied.
The lobby's title stands on the floor, turned to each arrival; the lobby card at the top left names the lobby and its
settings, and name tags float over everyone.

**The server.** `LobbyServer.World.cs` opens each world as a plain `ServerRun`, like a match, with its own id from the match id
sequence, its own ledger and its own relays; the creator is its body authority (a successor takes over, as for a
match). Unlike a match, its membership follows the lobby's roster live. Each arrival, at creation, on joining or on
returning from a match, gets its own world start: MatchStart (the void plan), an Authority snapshot of every live body
(request 0) and MatchGo, so a joiner sees the world as it is. Every peer reconciles its actors with each LobbyState:
arrivals are added, departures removed, renames and colors follow; a peer gives the bots it owns their brains as they
arrive. Messages name their run: `RunFor` takes only the lobby's current match or current world, so after a start the
closed world's traffic is stale and dropped, and in the Waiting phase only the world's own relays run.

**Start.** The creator's start (from the panel) puts the lobby in the **Staging** phase for `LobbyStageTime` (5 s by
default, capped at 10 s) from the creator's snapshot; 0 starts at once. The stage screen washes the world out behind
the coming mode, its settings and every member's card and counts down; the creator can take it back with its button,
Esc or B (**CancelStart**). While staging, settings, the roster and joining are locked and everyone stands still; the
creator leaving or disconnecting cancels it. Then the world closes and the match loads as before, the stage keeping
its lineup on screen while the suburb builds. After the results, the server opens a fresh world and everyone returns
to it, membership kept.

**The panel.** The lobby menu, docked at the right over the world: Tab or View opens it and Esc, B, View again or
Close puts it away; it holds names, colors, bots, mode, length, tank supply and King's mint pace, with the same
authority as before. Esc in the world opens its pause menu, whose Leave lobby returns to the lobby list.

**Wire.** LobbyState gains trailing **StartsIn** (Float32, 0–10, nonzero only while staging) and **WorldId** (Int32,
nonzero only while waiting or staging); LobbyPhase gains Staging (5). The client message **CancelStart** (17) is empty.
The world's stock and refills use LobbyWire on the mode envelope, reusing King's pile messages. This is part of
Protocol 3 (released in 0.4.0): no further bump, and Hello, Welcome and Rejected are unchanged.

**Dev flags.** `--classic-lobby` keeps the old lobby menu over the backdrop until the owner has reviewed the world's
look: a client started with it ignores its lobby's world, and a server started with it runs none. `net_test` stays out
of worlds (and starts without a stage) unless `--lobby-roam=S` puts it in them: each client's bot roams S seconds
before every match and after the last returns it.

**Tests.** `LobbyWorldTests` covers the rules and layout, joining while roaming, leaving and rejoining, bots, settings
authority, staging, cancel and start, stale world traffic, the return after results, the creator's departure,
disconnects during the countdown and loading, refills and a client's world. `menu_test --scenario=lobby` plays it
through the front end by keyboard, controller and mouse, with a second client, a bot, a canceled and a real start, a
match and the return; rendered with `--capture-dir`, it captures the review shots. The other menu scenarios run with
`--classic-lobby`. Network runs:

```sh
tools/dev/net_bots.sh -n 2 -l 20 -t clean -o "$PWD/build/lobby-net-2c" -- --lobby-roam=10 --matches=2 --lobby-bots=1
tools/dev/net_bots.sh -n 4 -l 25 -o "$PWD/build/lobby-net-4f" -- --lobby-roam=10 --matches=2 --lobby-bots=1 --net-lag=120 --net-jitter=30 --net-loss=3
```
