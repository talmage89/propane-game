using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>
/// A multiplayer client driven by a bot, for testing matches without people. It connects, creates a lobby
/// (<c>--role=host</c>, starting once <c>--players=N</c> have joined) or joins the first one (<c>--role=join</c>),
/// then plays with <see cref="Bot"/>. It logs
/// scores and coarse tank digests. <c>--tank-check</c> adds frequent samples on the shared OS monotonic
/// clock for assertions across same-machine clients; <c>--tank-check-nudge=Name</c> injects a physical divergence.
/// Run: godot-mono res://scenes/dev/net_test.tscn -- --server=127.0.0.1 --role=host --players=2 [--capture-dir=$PWD/build/net-shots]
/// Add <c>--host-server</c> to run the server in the same process. <c>--lobby-bots=N</c> (or a list such as
/// <c>2,1</c>, one entry per <c>--name=BotI</c> client) adds that many lobby bots owned by each client before the start,
/// at <c>--lobby-bot-difficulty=0..2</c>; the host waits for all of them. <c>--leave-who=Name</c> limits
/// <c>--leave-at</c> to one client. <c>--human=idle</c> keeps this client's player still; <c>--look-at-bots</c> keeps it
/// still, facing the nearest remote lobby bot.
/// </summary>
public partial class NetTest : Node
{
    private IBotBrain bot = null!;
    private BotActivity playerActivity = new();
    private NetClient net = null!;
    private Match? match;
    private long matchAllocated;
    private TimeSpan matchGcPause;
    private readonly int[] matchCollections = new int[3];
    private CaptureDirector director = null!;
    private string role = "host";
    private int players = 2;
    private bool started;
    private float clock;
    private float digestTimer;
    private double tankCheckNext;
    private bool tankCheckFinal;
    private double motionTraceNext;
    private PropaneTank? nudgedTank;
    private Vector3 nudgedPosition;
    private float shotTimer;
    private int matches;
    private int matchesWanted = 1;
    private int tankSupply;
    private int lobbyBots;
    private int lobbyBotsTotal;
    private uint lobbyBotRequest;

    public override void _Ready()
    {
        DevArgs.Setup();
        // Several test windows share one screen; a window the compositor is not showing would otherwise block on vsync.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 60;
        DevLog.Enabled = DevArgs.Get("verbose-log") != null;
        role = DevArgs.Get("role") ?? "host";
        players = (int)DevArgs.GetFloat("players", 2);
        matchesWanted = (int)DevArgs.GetFloat("matches", 1);
        tankSupply = Tuning.SavedTemplate.TankCountMultiplier;
        if (DevArgs.Get("tank-supply") is { } supply && !int.TryParse(supply, out tankSupply) ||
            tankSupply is < Tuning.TankSupplyMinimum or > Tuning.TankSupplyMaximum)
        {
            GD.PushError("[nettest] --tank-supply needs an integer from 1 to 8");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        ParseLobbyBots();
        var address = DevArgs.Get("server") ?? "127.0.0.1";
        var port = (int)DevArgs.GetFloat("port", Protocol.DefaultPort);
        if (DevArgs.Get("host-server") != null)
        {
            var server = new LobbyServer { Name = "Server", Port = port };
            AddChild(server);
            server.Start();
        }
        InputSetup.Register();
        // Match soaks keep their timing: these clients stay out of the lobby world and start without a stage unless a
        // lobby run asks for both (--lobby-roam).
        var roam = DevArgs.Get("lobby-roam") != null;
        if (!roam) Tuning.SavedTemplate.LobbyStageTime = 0;
        net = new NetClient { Name = "Net", LobbyWorlds = roam };
        AddChild(net);
        director = new CaptureDirector { Name = "Director", QuitAfter = DevArgs.GetFloat("quit", 400f) };
        AddChild(director);
        net.StatusChanged += (status, reason) =>
        {
            Log($"status {status} {reason}" + (net.RejectedBy != null ? $" (server {net.RejectedBy})" : ""));
            if (status == NetClient.Status.Connected && role == "host")
            {
                net.CreateLobby();
            }
            if (status == NetClient.Status.Offline && reason != null)
            {
                ShutdownGuard.Quit(GetTree(), 2);
            }
        };
        net.LobbiesChanged += () =>
        {
            if (role == "join" && net.Lobby == null && net.Lobbies.FirstOrDefault(l => l.Phase == LobbyServer.LobbyPhase.Waiting) is { } lobby)
            {
                net.JoinLobby(lobby.Id);
            }
        };
        if (DevArgs.Get("scenario") == "appearance")
        {
            appearanceMenus = new UI.Menus { Net = net, Visible = false };
            AddChild(appearanceMenus);
        }
        net.LobbyChanged += () =>
        {
            // The server retires replication before each client's delayed results timer ends.
            // Finish the live trajectory here; still check exact completion identities/rest poses below.
            if (!tankCheckFinal && match?.IsOver == true && net.Lobby?.Phase == LobbyServer.LobbyPhase.Waiting)
            {
                LogTankCheck(final: true);
            }
            if (net.Lobby is { } lobby)
            {
                if (role == "host" && net.IsCreator && lobby.Phase == LobbyServer.LobbyPhase.Waiting && lobby.Capacity < players + lobbyBotsTotal)
                    net.SetCapacity(players + lobbyBotsTotal);
                AddLobbyBot();
                Log($"lobby {lobby.Id}: {string.Join(", ", lobby.Members.Select(m => $"{m.Name}/{m.Appearance.Color.ToHtml(false)}"))}, {lobby.Phase}");
                if (DevArgs.Get("scenario") == "appearance" && lobby.Phase == LobbyServer.LobbyPhase.Waiting && lobby.Members.Count == players)
                {
                    CheckAppearances(lobby.Members);
                    appearanceMenus!.Show(UI.Menus.Screen.Lobby);
                    GetTree().CreateTimer(0.15).Timeout += () => director.Screenshot($"{net.PlayerName}_appearance_lobby");
                }
                TryStartMatch();
            }
        };
        net.NoticeReceived += text => Log($"notice: {text}");
        net.ActorRequestCompleted += result =>
        {
            if (result.RequestId != lobbyBotRequest) return;
            lobbyBotRequest = 0;
            Log(result.Accepted ? $"lobby bot added at revision {result.Revision}" : $"lobby bot refused: {result.Reason}");
            // An accepted bot's roster arrives next and adds the following one; a refusal retries now.
            if (!result.Accepted) AddLobbyBot();
        };
        net.MatchStarting += async setup =>
        {
            appearanceMenus?.Show(UI.Menus.Screen.None);
            if (DevArgs.Get("scenario") == "appearance") { CheckAppearances(setup.Players.Select(p => p.Player)); }
            var delay = DevArgs.Get("delay-who") is not { } delayed || delayed == net.PlayerName ? DevArgs.GetFloat("match-delay", 0) : 0;
            if (DevArgs.Get("scenario") == "capacity" && DevArgs.Get("name") == $"Bot{players}") delay = 26;
            if (delay > 0)
            {
                Log($"delaying match {setup.Id} construction by {delay} s");
                await ToSignal(GetTree().CreateTimer(delay), SceneTreeTimer.SignalName.Timeout);
                if (net.ActiveMatchId != setup.Id || net.State != NetClient.Status.Connected) { return; }
            }
            if (match != null)
            {
                RemoveChild(match);
                match.QueueFree();
            }
            if (DevArgs.Get("tank-check-load-stall") == net.PlayerName)
            {
                // Native transport regression: a legitimate construction frame can outlast an 8 s timeout.
                Log("tank-check loading stall 9s");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(9));
            }
            matchAllocated = GC.GetTotalAllocatedBytes();
            matchGcPause = GC.GetTotalPauseDuration();
            for (var generation = 0; generation < matchCollections.Length; generation++) matchCollections[generation] = GC.CollectionCount(generation);
            (bot as IDisposable)?.Dispose();
            // The transmitted definition chooses the policy. The original simple benchmark remains Tank Bank only.
            bot = setup.ModeId == ModeRegistry.TankBankId && DevArgs.Get("brain") == "simple" ? new SimpleBot()
                : new Bot(setup.Mode) { Skill = DevArgs.GetFloat("bot-skill", 1f) };
            match = new Match
            {
                Name = "Match",
                Net = net,
                Setup = setup,
                CreateLocalView = DisplayServer.GetName() != "headless",
                BeforeInboxAttach = DevArgs.Get("scenario") == "capacity" ? () => PrepareCapacity(setup) : null
            };
            match.Finished += () =>
            {
                if (DevArgs.Get("scenario") is "duel" or "pickup" or "drop-refusal" or "puncture-race" or "appearance" or "gather" or "melee" or "body-stream" or "vent-contact" or "king" or "king-late")
                {
                    ScenarioAssert(scenarioPassed, "scenario ended before all assertions passed");
                }
                if (DevArgs.Get("scenario") == "movement" && role == "host" && !movementProved)
                {
                    GD.PushError("[net movement] FAIL match ended before movement proof completed");
                    ShutdownGuard.Quit(GetTree(), 1);
                    return;
                }
                if (bot is Bot smart)
                {
                    Log($"navigation finished: stuck={smart.StuckEvents} blacklist={smart.BlacklistedTargets} replans={smart.Replans} jumps={smart.Jumps}");
                }
                if (DevArgs.Get("scenario") == "capacity") FinishCapacity();
                if (match.BotCost is { Bots: > 0 } cost)
                {
                    Log(FormattableString.Invariant($"lobby bots {cost.Bots}: {cost.Thinks} thinks, mean {cost.Seconds * 1000 / Math.Max(cost.Thinks, 1):0.000} ms, {cost.Bytes / Math.Max(cost.Thinks, 1)} B allocated per think"));
                    var warm = match.WarmBotCost;
                    Log(FormattableString.Invariant($"bot ticks {cost.Ticks}: mean_ms={cost.Seconds * 1000 / Math.Max(cost.Ticks, 1):F3} worst_ms={cost.Worst * 1000:F3} queries={cost.Queries} rays={cost.Rays}"));
                    Log(FormattableString.Invariant($"bot warm ticks {warm.Ticks}: mean_ms={warm.Seconds * 1000 / Math.Max(warm.Ticks, 1):F3} p50_ms={match.BotTickPercentile(0.5):F2} p95_ms={match.BotTickPercentile(0.95):F2} p99_ms={match.BotTickPercentile(0.99):F2} worst_ms={warm.Worst * 1000:F3} bytes={warm.Bytes} allocating_ticks={warm.AllocatingTicks} peak_bytes={warm.PeakBytes}"));
                    foreach (var actor in match.Session.OwnedActors)
                    {
                        if (match.BotBrain(actor.Character.ActorId) is BankingBot bank)
                            Log($"bot navigation {actor.Character.ActorId}: stuck={bank.StuckEvents} blacklist={bank.BlacklistedTargets} replans={bank.Replans} jumps={bank.Jumps}");
                    }
                    if (match.BotNavigationSaturation is { Ticks: > 0 } saturation)
                        Log(FormattableString.Invariant($"bot navigation budget: ticks={saturation.Ticks} query_limited={saturation.QueryLimited * 100.0 / saturation.Ticks:0.0}% expansion_limited={saturation.ExpansionLimited * 100.0 / saturation.Ticks:0.0}%"));
                    LogBotActivity(match);
                }
                if (playerActivity.Windows > 0) Log($"player bot activity: {playerActivity.Report()}");
                if (DevArgs.Get("frame-metrics") != null)
                {
                    Log(FormattableString.Invariant($"match gc: allocated_bytes={GC.GetTotalAllocatedBytes() - matchAllocated} pause_ms={(GC.GetTotalPauseDuration() - matchGcPause).TotalMilliseconds:F3} collections={GC.CollectionCount(0) - matchCollections[0]}/{GC.CollectionCount(1) - matchCollections[1]}/{GC.CollectionCount(2) - matchCollections[2]}"));
                }
                Digest();
                if (!tankCheckFinal) { LogTankCheck(final: true); }
                LogTankCheck(completion: true);
                if (match.ModeView is KingState king)
                    Log($"finished king {match.Setup.Id}; minted " + string.Join(" ", match.Setup.Players.OrderBy(p => p.Player.Id).Select(p => $"{p.Player.Id}={king.MintedBy(p.Player.Id)}")));
                Log($"finished match {match.Setup.Id}; final scores {ScoreLine()}");
                RemoveChild(match);
                match.QueueFree();
                match = null;
                matches++;
                started = matches >= matchesWanted;
                // Lobby Waiting may have arrived before this client's results timer finished.
                TryStartMatch();
                if (matches >= matchesWanted)
                {
                    GetTree().CreateTimer(1.0).Timeout += () => ShutdownGuard.Quit(GetTree());
                }
            };
            kingStage = -1; kingCamera = null; kingCaptured.Clear();
            kingHeld = kingContested = kingWarned = kingMoved = kingPiled = false;
            raceTick = raceSamples = 0;
            raceVent = null;
            raceCamera = null;
            raceWorst = 0;
            ventContactTick = 0;
            match.AuthorityResolved += result =>
            {
                if (DevArgs.Get("tank-authority-trace") != null)
                {
                    var bodies = string.Join(" ", result.Bodies.Select(b => $"{b.Body.Id:x}:{b.Owner}:{b.Body.Epoch}:{b.Body.Sequence}:{b.State}:{b.Rest}"));
                    Log(FormattableString.Invariant($"authority-trace match {setup.Id} time {TankCheckTime():0.000000} peer {result.Peer} request {result.Request} reason {result.Reason} bodies {bodies}"));
                }
                if (DevArgs.Get("scenario") != "puncture-race") { return; }
                var canonical = result.Bodies.FirstOrDefault(b => b.Body.Id == BodySync.MapTankId(0));
                if (canonical?.State != TankLedger.State.Venting) { return; }
                if (raceVent != null) { ScenarioAssert(raceVent == canonical.Vent, "race: canonical vent changed during reconciliation"); }
                raceVent = canonical.Vent;
            };
            AddChild(match);
            if (delay >= 70 && setup.ModeId == ModeRegistry.KingId)
            {
                ScenarioAssert(match.IsPlaying && match.ModeView is KingState { Zone: >= 1 },
                    "King late builder did not replay the current moved zone before readiness");
                Log($"King catch-up at readiness: zone {(match.ModeView as KingState)?.Zone}, minted " +
                    string.Join(" ", (match.ModeView as KingState)?.Minted.Select(m => $"{m.ActorId}={m.Count}") ?? Array.Empty<string>()));
            }
            if (DevArgs.Get("tank-check-ready-stall") == net.PlayerName)
            {
                // Readiness can be sent from packet delivery while construction still blocks native ACK service.
                Log("tank-check readiness callback stall 9s");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(9));
            }
            if (DevArgs.Get("gather-trace") != null && match.World.Root.FindChild("Gather", true, false) is TankGather gatherProbe)
            {
                gatherProbe.Trace = text => Log($"gather-trace time {TankCheckTime():0.000000} {text}");
            }

            if (DevArgs.Get("tank-trace") != null)
            {
                match.Bodies.Trace = (entry, reason) =>
                {
                    if (!entry.IsTank || !IsInstanceValid(entry.Body)) { return; }
                    if (DevArgs.Get("tank-trace") is { Length: > 0 } selected && entry.Id.ToString("x") != selected) { return; }
                    var body = entry.Body;
                    var p = body.GlobalPosition;
                    var target = entry.Target.Origin;
                    var angle = entry.HasTarget ? body.GlobalBasis.Orthonormalized().GetRotationQuaternion().AngleTo(entry.Target.Basis.GetRotationQuaternion()) : 0;
                    Log(FormattableString.Invariant($"tank-trace match {match.Setup.Id} time {TankCheckTime():0.000000} id {entry.Id:x} {reason} owner={entry.Owner} epoch={entry.Epoch} sequence={entry.Sequence} last={entry.LastSequence} pending={entry.ClaimPending} sleeping={body.Sleeping} target={entry.HasTarget}/{entry.TargetRest} pos={p.X:0.000},{p.Y:0.000},{p.Z:0.000} goal={target.X:0.000},{target.Y:0.000},{target.Z:0.000} speed={body.LinearVelocity.Length():0.000} goal-speed={entry.TargetLinear.Length():0.000} spin={body.AngularVelocity.Length():0.000} goal-spin={entry.TargetAngular.Length():0.000} angle={angle:0.000} gather={(body as PropaneTank)?.Gathering} revision={(body as PropaneTank)?.Disturbance}"));
                };
            }
            match.Player.InputOverride = bot;
            playerActivity = new BotActivity();
            if (DisplayServer.GetName() == "headless")
            {
                ScenarioAssert(match.Session.LocalViews.Count == 0 && match.FindChildren("*", "Camera3D", owned: false).Count == 0,
                    "headless bot match constructed a camera");
                Log("camera-free actor: no local views or cameras");
            }
            if (DevArgs.Get("melee-start") != null)
            {
                // Stage before countdown so snapshots settle before the ordinary brains choose any targets.
                // Pairs by actor id, lobby bots included; each owner stages the second actor of each pair it owns.
                var peers = setup.Players.OrderBy(p => p.Player.Id).ToList();
                foreach (var owned in match.Session.OwnedActors.Select(a => a.Character).ToList())
                {
                    var slot = peers.FindIndex(p => p.Player.Id == owned.ActorId);
                    if (slot % 2 == 0) continue;
                    var anchor = match.Session.Actor(peers[slot - 1].Player.Id)!.Character;
                    var yaw = anchor.FacingYaw;
                    var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
                    // Use a normal clear spawn so navigation still has useful routes after the initial knockdowns.
                    owned.Teleport(anchor.GlobalPosition + forward * 1.8f, yaw + Mathf.Pi);
                }
            }
            match.Player.MeleeStarted += (swing, _) => Log($"melee swing {swing}");
            match.Player.MeleeHit += hit => Log($"melee contact {hit.Swing} victim {PlayerCharacter.Owning(hit.Collider)?.ActorId ?? 0}");
            clock = 0;
            tankCheckNext = 0;
            tankCheckFinal = false;
            nudgedTank = null;
            duelTime = -1;
            gatherPositions.Clear();
            gatherShields.Clear();
            gatherGlides.Clear();
            meleeThrows = meleeGetups = 0;
            meleeFirstDown = meleeSecondDown = false;
            meleeCaptured.Clear();
            duelInput = new ScriptedInput();
            duelTank = null;
            duelBlastAt = duelThrowAt = -1;
            pickupTakenAt = -1;
            pickupMovedAway = false;
            scenarioPassed = scenarioFailed = false;
            appearanceTime = -1;
            appearanceDropped = appearanceGlowChecked = appearanceResultsChecked = false;
            // Movement proof is per match, just like the duel/pickup assertions on a reused connection.
            movementCamera = null;
            remoteFeet = null;
            remoteGrip = null;
            remoteMotion = null;
            sawRemoteReload = false;
            sawRemoteCreep = sawRemoteSprint = sawRemoteAir = sawRemoteRestoredAim = movementProved = false;
            capturedCreep = capturedSprint = capturedLanding = capturedRelease = false;
            remoteGait = PlayerGait.Default;
            remoteGaitTime = 0;
            Log($"match {setup.Id} ready with {setup.Players.Count} players; playing {match.IsPlaying}, over {match.IsOver}");
        };
        var name = DevArgs.Get("name") ?? (role == "host" ? "HostBot" : $"Bot{OS.GetProcessId() % 1000}");
        var color = (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1);
        var appearance = DevArgs.Get("appearance") is { } hex ? ParseAppearance(hex) : AppearanceRecord.FromPalette(color);
        // Give a hosted server a moment to open its port.
        GetTree().CreateTimer(DevArgs.Get("host-server") != null ? 0.3 : 0.05).Timeout += () => net.Connect(address, port, name, appearance);
    }

    public override void _ExitTree() => (bot as IDisposable)?.Dispose();

    /// <summary>Each owned bot's idle share and statuses, then the mean and worst share (see <see cref="BotActivity"/>).</summary>
    private void LogBotActivity(Match match)
    {
        double sum = 0, worst = 0;
        var count = 0;
        var longest = 0;
        foreach (var actor in match.Session.OwnedActors)
        {
            if (match.BotActivityOf(actor.Character.ActorId) is not { } activity) continue;
            Log(FormattableString.Invariant($"bot activity {actor.Character.ActorId} skill={match.BotBrain(actor.Character.ActorId)!.Skill:0.0}: {activity.Report()}"));
            sum += activity.IdleShare;
            worst = Math.Max(worst, activity.IdleShare);
            longest = Math.Max(longest, activity.LongestIdle);
            count++;
        }
        if (count > 0) Log(FormattableString.Invariant($"bot idle: bots={count} mean={sum * 100 / count:0.0}% worst={worst * 100:0.0}% longest={longest} s"));
    }

    private void ParseLobbyBots()
    {
        if (DevArgs.Get("lobby-bots") is not { } text) return;
        var counts = text.Split(',').Select(int.Parse).ToArray();
        var index = int.TryParse(new string((DevArgs.Get("name") ?? "").Where(char.IsAsciiDigit).ToArray()), out var n) ? n - 1 : 0;
        // One entry applies to every client; a list gives each named client its own count.
        lobbyBots = counts.Length == 1 ? counts[0] : index >= 0 && index < counts.Length ? counts[index] : 0;
        lobbyBotsTotal = counts.Length == 1 ? counts[0] * players : counts.Take(players).Sum();
    }

    /// <summary>Adds this client's lobby bots one request at a time; a stale revision simply retries.</summary>
    private void AddLobbyBot()
    {
        if (lobbyBotRequest != 0 || net.Lobby is not { Phase: LobbyServer.LobbyPhase.Waiting } lobby) return;
        var mine = lobby.Members.Count(m => m.OwnerPeerId == net.MyId && m.Kind == ActorKind.Bot);
        if (mine >= lobbyBots || matches > 0) return;
        var difficulty = (byte)DevArgs.GetFloat("lobby-bot-difficulty", 1);
        lobbyBotRequest = net.AddActor(ActorKind.Bot, $"{net.PlayerName} bot {mine + 1}", AppearanceRecord.FromPalette(net.MyId + mine + 3),
            ControllerConfig.ForBot(difficulty));
    }

    private void TryStartMatch()
    {
        if (role == "host" && net.IsCreator && !started && matches < matchesWanted &&
            net.Lobby is { Phase: LobbyServer.LobbyPhase.Waiting } lobby && lobby.Peers.Count >= players &&
            lobby.Members.Count >= players + lobbyBotsTotal)
        {
            started = true;
            net.SetMode(new ModeSettings(DevArgs.Get("mode") == "king" ? ModeRegistry.KingId : ModeRegistry.TankBankId,
                DevArgs.GetFloat("mint-seconds", (float)KingRules.MintPaceDefault),
                tankSupply));
            net.SetMatchLength(DevArgs.GetFloat("length", 60));
            GetTree().CreateTimer(0.5).Timeout += net.StartMatch;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (match == null || !IsInstanceValid(match))
        {
            return;
        }
        var dt = (float)delta;
        clock += dt;
        // --send-tune=Name=value: the host changes a value as the debug panel would, a few seconds in.
        if (role == "host" && DevArgs.Get("send-tune") is { } tune && clock >= 3f && clock - dt < 3f)
        {
            var split = tune.IndexOf('=');
            var name = tune[..split];
            var value = float.Parse(tune[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            match.World.Tuning.Set(name, value);
            net.Send(new NetWriter(Msg.TuningSet, match.Setup.Id).String(name).Scalar(value));
            Log($"sent tuning {name} = {value}");
        }
        var leaveAt = DevArgs.GetFloat("leave-at", -1);
        if (leaveAt > 0 && clock >= leaveAt && (DevArgs.Get("leave-who") ?? net.PlayerName) == net.PlayerName)
        {
            Log("leaving the match on purpose");
            ShutdownGuard.Quit(GetTree());
            return;
        }
        if (DevArgs.Get("scenario") == "king")
        {
            KingScenario(match);
        }
        else if (DevArgs.Get("scenario") == "king-late")
        {
            KingLateScenario(match);
        }
        else if (DevArgs.Get("scenario") == "capacity")
        {
            CapacityScenario(dt);
        }
        else if (DevArgs.Get("scenario") is "duel" or "drop-refusal" or "gather")
        {
            Duel(match, dt);
        }
        else if (DevArgs.Get("scenario") == "appearance")
        {
            AppearanceScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "melee")
        {
            MeleeScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "puncture-race")
        {
            PunctureRace(match);
        }
        else if (DevArgs.Get("scenario") == "body-stream")
        {
            BodyStreamScenario(match);
        }
        else if (DevArgs.Get("scenario") == "vent-contact")
        {
            VentContactScenario(match);
        }
        else if (DevArgs.Get("scenario") == "movement")
        {
            MovementScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "pickup")
        {
            PickupScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "watch" && role == "host")
        {
            Watch(match, dt);
        }
        else if (DevArgs.Get("look-at-bots") != null && match.Session.Actors.Select(a => a.Character).Where(b => b != match.Player && match.Setup.Players.Any(p => p.Player.Id == b.ActorId &&
                     p.Player.Kind == ActorKind.Bot)).OrderBy(b => b.GlobalPosition.DistanceTo(match.Player.GlobalPosition)).FirstOrDefault() is { } watched)
        {
            // --look-at-bots: a still observer facing the nearest bot, including actors this peer owns.
            match.Player.Spectating = watched;
            AimAt(match.Player, watched.GlobalPosition + Vector3.Up * 1.2f);
        }
        else if (DevArgs.Get("human") != "idle") // --human=idle: a still player, so frame costs measure lobby bots alone.
        {
            bot.Think(match, dt);
            if (bot is Bot policy) playerActivity.Sample(match.IsPlaying, match.Player, policy.Policy, match.BotKing, dt);
            if (DevArgs.Get("bot-log") != null && Mathf.PosMod(clock, 2f) < dt)
            {
                Log($"bot: {bot.Status}" + (bot is Bot smart ? $"; stuck={smart.StuckEvents} blacklist={smart.BlacklistedTargets} replans={smart.Replans} jumps={smart.Jumps}" : ""));
            }
        }

        // A physical divergence fixture, opt-in on one named peer. Holding the pose defeats corrections
        // intentionally; it tests the verifier through real scene state, rather than falsifying its log.
        if (DevArgs.Get("tank-check-nudge") == net.PlayerName && match.IsPlaying && clock >= 20)
        {
            if (nudgedTank == null)
            {
                nudgedTank = match.LiveTanks.FirstOrDefault(t => t.State == PropaneTank.TankState.Intact && t.AtRest);
                if (nudgedTank != null)
                {
                    nudgedPosition = nudgedTank.GlobalPosition + Vector3.Right * 2;
                    Log($"tank-check injected nudge {nudgedTank.NetId:x}");
                }
            }
            if (IsInstanceValid(nudgedTank) && nudgedTank!.State == PropaneTank.TankState.Intact)
            {
                nudgedTank.GlobalPosition = nudgedPosition;
                nudgedTank.LinearVelocity = nudgedTank.AngularVelocity = Vector3.Zero;
                nudgedTank.Sleeping = true;
            }
        }
        var sampleTime = TankCheckTime();
        if (DevArgs.Get("tank-check") != null && !tankCheckFinal && sampleTime >= tankCheckNext)
        {
            tankCheckNext = sampleTime + 0.05;
            LogTankCheck();
        }
        digestTimer -= dt;
        if (digestTimer <= 0)
        {
            digestTimer = DevArgs.GetFloat("digest-every", 5f);
            Digest();
        }
        shotTimer -= dt;
        var every = DevArgs.GetFloat("shot-every", 0f);
        if (every > 0 && shotTimer <= 0)
        {
            shotTimer = every;
            director.Screenshot($"{net.PlayerName}_{clock:000.0}");
        }
    }

    private int raceTick;
    private int raceSamples;
    private float raceWorst;
    private VentParameters? raceVent;
    private Camera3D? raceCamera;

    /// <summary>
    /// Injects two opposed bullet contacts on the same simulation tick, while both copies are intact. This isolates
    /// the authority race from rifle spread/aim. A third client, when present, observes without shooting.
    /// </summary>
    private void PunctureRace(Match match)
    {
        if (!match.IsPlaying) { return; }
        raceTick++;
        var id = BodySync.MapTankId(0);
        var tank = match.LiveTanks.SingleOrDefault(t => t.NetId == id);
        ScenarioAssert(tank != null, "race: test tank disappeared");
        if (tank == null) { return; }
        if (raceTick == 1)
        {
            match.Player.InputOverride = new ScriptedInput();
            var road = match.Suburb.Plan.Roads.OrderByDescending(r => r.Length).First();
            var spot = World.Suburb.ToWorld((road.A + road.B) / 2, 8);
            BodySync.Snap(tank, new Transform3D(Basis.Identity, spot), Vector3.Zero, Vector3.Zero);
            match.Player.Teleport(spot + new Vector3(20, -7.9f, 0), 0);
            raceCamera = new Camera3D { Current = true, Fov = 55 };
            match.AddChild(raceCamera);
        }
        if (raceTick == 180 && (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1) < 2)
        {
            ScenarioAssert(tank.State == PropaneTank.TankState.Intact && match.Bodies.Get(id)!.Epoch == 1, "race: contender received a puncture before its simultaneous contact");
            var normal = role == "host" ? Vector3.Right : Vector3.Left;
            var hole = PropaneTank.LocalCenter + normal * 0.15f;
            tank.TakeShot(tank.ToGlobal(hole), tank.GlobalBasis * normal, -(tank.GlobalBasis * normal), match.Player.ActorId);
            Log($"race contact tick {raceTick} hole {Vector(tank.HoleLocal)} swirl {tank.SwirlSign}");
        }
        if (raceCamera != null)
        {
            raceCamera.GlobalPosition = tank.GlobalPosition + new Vector3(2, 1.4f, 3);
            raceCamera.LookAt(tank.GlobalTransform * PropaneTank.LocalCenter, Vector3.Up);
        }
        if (raceTick is 210 or 300 or 600 or 1200) { director.Screenshot($"{net.PlayerName}_race_{raceTick}"); }
        // At 120 physics ticks/s, allow lagged reliable delivery before checking convergence.
        if (raceTick < 720) { return; }
        ScenarioAssert(raceVent != null && tank.State == PropaneTank.TankState.Venting, "race: no canonical puncture after the race");
        if (raceVent == null) { return; }
        ScenarioAssert(tank.HoleLocal == raceVent.Hole && tank.HoleNormalLocal == raceVent.Normal && tank.SwirlSign == raceVent.Swirl,
            "race: contender retained different canonical vent parameters");
        var lean = raceVent.Normal.Rotated(Vector3.Up, Mathf.DegToRad(match.World.Tuning.VentSwirlDegrees) * raceVent.Swirl).Normalized();
        ScenarioAssert(tank.JetDirectionLocal.IsEqualApprox(lean), "race: jet lean differs from canonical vent");
        var entry = match.Bodies.Get(id)!;
        if (entry.Owner != net.MyId && entry.HasTarget)
        {
            var lead = (float)Math.Clamp(MonotonicClock.Now - entry.TargetReceived, 0, 0.3);
            var drift = tank.GlobalPosition.DistanceTo(entry.Target.Origin + entry.TargetLinear * lead);
            raceWorst = Mathf.Max(raceWorst, drift);
            ScenarioAssert(drift <= match.World.Tuning.SnapDistance + 0.5f, $"race: trajectory escaped owner corrections ({drift:0.000} m)");
        }
        if (raceTick % 6 == 0)
        {
            raceSamples++;
            Log($"race path time {MonotonicClock.Now + net.ServerClockOffset:0.000} pos {Vector(tank.GlobalPosition)}");
        }
        if (raceTick == 720)
        {
            Log($"race params owner {entry.Owner} epoch {entry.Epoch} hole {Vector(tank.HoleLocal)} normal {Vector(tank.HoleNormalLocal)} lean {Vector(tank.JetDirectionLocal)} swirl {tank.SwirlSign}");
        }
        if (raceTick == 1320)
        {
            ScenarioAssert(raceSamples >= 100 && !entry.ClaimPending, "race: insufficient trajectory samples or unresolved claim");
            scenarioPassed = !scenarioFailed;
            Log($"puncture-race: assertions passed, {raceSamples} path samples, max correction error {raceWorst:0.000} m");
        }
    }

    private static string Vector(Vector3 v) => FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");

    // No instance override exists until the new tank's first process update. Its material's
    // shield is transparent; converting Nil directly to Color instead fabricates opaque black.
    internal static Color ReadShieldColor(Variant value) => value.VariantType == Variant.Type.Nil ? Colors.Transparent : value.AsColor();

    private readonly Dictionary<uint, Vector3> gatherPositions = new();
    private readonly HashSet<uint> gatherShields = new();
    private readonly HashSet<uint> gatherGlides = new();
    private float duelTime = -1;
    private ScriptedInput duelInput = new();
    private uint? duelTank;
    private float duelShotLeft;
    private float duelBlastAt = -1;
    private float duelThrowAt = -1;
    private bool scenarioPassed;
    private bool scenarioFailed;
    private int duelInitialScore;
    private float pickupTakenAt = -1;
    private bool pickupMovedAway;

    /// <summary>
    /// A scripted close-up of the tank bank: the host shoots the other player (dropping tanks behind them, with the
    /// grace period between hits), then shoots the dropped tanks, whose blast throws the victim, spilling more.
    /// Both take screenshots. Run with two bots, one windowed or both, and the server's --start-score.
    /// </summary>
    private void Duel(Match match, float dt)
    {
        var player = match.Player;
        if (!match.IsPlaying)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
            player.Ragdolled += (_, _, _) => duelThrowAt = duelTime;
            if (role == "host")
            {
                // Shouldered, for a tight spread.
                duelInput.Hold(0, 999, new PlayerIntent { Aim = true });
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var road = match.Suburb.Plan.Roads.OrderByDescending(r => r.Length).First();
        var mid = (road.A + road.B) / 2f;
        var shooterSpot = World.Suburb.ToWorld(mid - road.Direction * 7f, 0.05f);
        var victimSpot = World.Suburb.ToWorld(mid + road.Direction * 7f, 0.05f);
        var host = role == "host";
        var refusal = DevArgs.Get("scenario") == "drop-refusal";
        var gathering = DevArgs.Get("scenario") == "gather";
        if (gathering)
        {
            var dropper = match.Setup.Players.Single(p => p.Player.OwnerPeerId != match.Bodies.Host).Player;
            foreach (var tank in match.LiveTanks.Where(IsDrop))
            {
                var mesh = tank.GetNode<MeshInstance3D>("Mesh");
                var glow = ReadShieldColor(mesh.GetInstanceShaderParameter("shield_color"));
                if (tank.Invulnerable)
                {
                    ScenarioAssert(!tank.Gathering, "gather: shielded drop gathered");
                    // The dummy renderer cannot read back shader instance uniforms.
                    if (DisplayServer.GetName() == "headless") { gatherShields.Add(tank.NetId); }
                    else if (glow.A > 0)
                    {
                        ScenarioAssert(new Color(glow, 1) == dropper.Appearance.Color,
                            FormattableString.Invariant($"gather: shielded drop lost accepted appearance: tank {tank.NetId:x} glow={glow.R:R},{glow.G:R},{glow.B:R},{glow.A:R} expected={dropper.Appearance.Color.R:R},{dropper.Appearance.Color.G:R},{dropper.Appearance.Color.B:R}"));
                        gatherShields.Add(tank.NetId);
                    }
                }
                if (!tank.Gathering) { continue; }
                ScenarioAssert(!tank.Invulnerable, "gather: invulnerable drop entered glide");
                if (DisplayServer.GetName() != "headless")
                {
                    ScenarioAssert(glow.A == 0 && mesh.GetInstanceShaderParameter("shield_contrast").AsColor().A == 0 &&
                        mesh.GetInstanceShaderParameter("gather_shimmer").AsSingle() == match.World.Tuning.GatherShimmer,
                        "gather: expired shield glow/contrast remained or shimmer missing");
                }
                if (host && player.View != null)
                {
                    var camera = player.View.CameraRig.Camera;
                    player.CameraRig.ProcessMode = ProcessModeEnum.Disabled;
                    var center = tank.GlobalTransform * PropaneTank.LocalCenter;
                    camera.LookAtFromPosition(center + new Vector3(1.5f, 1, 2), center);
                    if (gatherGlides.Count == 0)
                    {
                        GetTree().CreateTimer(0.2).Timeout += () => director.ScreenshotAsync("gather_host_shimmer");
                    }
                }
                gatherGlides.Add(tank.NetId);
            }
        }
        if (gathering && Once(3.7f, dt))
        {
            // Leave the spilled tanks alone; the creator must acquire the dropper's entire group.
            player.Teleport((host ? shooterSpot : victimSpot) + Vector3.Right * 8, 0);
        }
        if (gathering && Once(35, dt))
        {
            var drops = match.LiveTanks.Where(IsDrop).ToArray();
            ScenarioAssert(drops.Length == 2 && drops.All(t => !t.Loose && !t.Gathering && t.State == PropaneTank.TankState.Intact), "gather: dropped group never became an intact pile");
            foreach (var tank in drops)
            {
                gatherPositions[tank.NetId] = tank.GlobalPosition;
                ScenarioAssert(match.Bodies.Get(tank.NetId)?.Owner == match.Bodies.Host, "gather: dropper retained ownership");
                if (DisplayServer.GetName() != "headless")
                {
                    ScenarioAssert(tank.GetNode<MeshInstance3D>("Mesh").GetInstanceShaderParameter("gather_shimmer").AsSingle() == 0,
                        "gather: settled drop retained shimmer");
                }
                Log($"gather final {tank.NetId:x} {tank.GlobalPosition}");
            }
        }
        if (gathering && Once(40, dt))
        {
            ScenarioAssert(gatherShields.Count == 2 && gatherGlides.Count == 2, $"gather: did not observe both drop shields and glides (shields {gatherShields.Count}, glides {gatherGlides.Count})");
            ScenarioAssert(gatherPositions.Count == 2 && match.LiveTanks.Where(IsDrop).All(t =>
                gatherPositions.TryGetValue(t.NetId, out var position) && position.DistanceTo(t.GlobalPosition) <= 0.006f && t.AtRest), "gather: final pile unstable");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed)
            {
                Log(DisplayServer.GetName() == "headless"
                    ? "gather: gameplay assertions passed; both shields expired and drops acquired, settled and stable"
                    : "gather: assertions passed; two accepted-color shields expired; both drops shimmered, acquired, settled and stable on this client");
            }
            director.ScreenshotAsync($"gather_{role}_settled");
        }
        if (gathering && host && duelTime is > 18 and < 32 && Mathf.PosMod(duelTime, 1) < dt)
        {
            var drops = match.LiveTanks.Where(IsDrop).ToArray();
            if (drops.Length > 0) { AimAt(player, drops[0].GlobalTransform * PropaneTank.LocalCenter); }
            director.ScreenshotAsync($"gather_{role}_{duelTime:00}");
        }
        if (Once(0.8f, dt)) { duelInitialScore = match.MyScore; }
        if (refusal && host && Once(2.55f, dt))
        {
            // The second victim hit is already in flight. With 120 ms lag, this creator change reaches the server
            // before the victim's old-count Drop, while reliable order delivers Shot before TuningSet to the victim.
            match.World.Tuning.HitDropCount = 0;
            net.Send(new NetWriter(Msg.TuningSet, match.Setup.Id).String(nameof(Tuning.HitDropCount)).Scalar(0));
        }
        if (Once(3.5f, dt))
        {
            var victimId = host ? match.RemoteBodies.First().ActorId : match.Player.ActorId;
            ScenarioAssert(match.Scores[victimId] == (refusal ? 7 : 6) && match.LiveTanks.Count(IsDrop) == (refusal ? 1 : 2),
                refusal ? "drop-refusal: rejected prediction changed the bank or left a phantom tank" : "duel: three hits must spill exactly two tanks (one hit inside grace)");
            if (refusal)
            {
                scenarioPassed = !scenarioFailed;
                director.Screenshot($"{(host ? "shooter" : "victim")}_drop_refused");
                if (scenarioPassed) { Log("drop-refusal: assertions passed"); }
            }
        }
        if (!refusal && !gathering && Once(14f, dt))
        {
            ScenarioAssert(host ? duelBlastAt >= 0 && match.MyScore > duelInitialScore : duelThrowAt >= 0 && match.MyScore <= 1,
                host ? "duel: dropped tank never detonated and scored" : "duel: blast never threw the victim and spilled five tanks");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) { Log("duel: assertions passed"); }
        }
        var eventAt = host ? duelBlastAt : duelThrowAt;
        if (eventAt >= 0)
        {
            foreach (var offset in new[] { 0.15f, 0.8f, 1.8f })
            {
                if (Once(eventAt + offset, dt)) { director.Screenshot($"{(host ? "shooter_blast" : "victim_throw")}_{offset:0.00}"); }
            }
        }
        if (Once(0.2f, dt))
        {
            var mine = host ? shooterSpot : victimSpot;
            var other = host ? victimSpot : shooterSpot;
            var to = other - mine;
            var yaw = Mathf.Atan2(-to.X, -to.Z);
            player.Teleport(mine, yaw);
            player.Aim.SetOrientation(Mathf.RadToDeg(yaw) + (host ? 0 : 160), -6f);
        }
        if (!host)
        {
            foreach (var t in new[] { 2.5f, 4f, 5.2f, 6.5f, 8f })
            {
                if (Once(t, dt))
                {
                    director.Screenshot($"victim_{t:00.0}");
                }
            }
            return;
        }
        var victim = match.RemoteBodies.FirstOrDefault();
        if (victim == null)
        {
            return;
        }
        // Hits at 1.5 and 1.7 (inside the grace period: only one drop), then 2.5 (another drop).
        foreach (var t in new[] { 1.5f, 1.7f, 2.5f })
        {
            if (Once(t - 0.3f, dt))
            {
                AimAt(player, victim.GlobalPosition + Vector3.Up * 1.2f);
            }
            if (Once(t, dt))
            {
                duelInput.Fire(duelInput.Now + 0.001f);
                Log($"duel: shot at victim at {t}");
            }
        }
        if (Once(2.0f, dt) || Once(3.2f, dt))
        {
            director.Screenshot($"shooter_{duelTime:00.0}");
        }
        if (refusal || gathering) { return; }
        if (DevArgs.Get("duel-blocked") != null && Once(3.7f, dt))
        {
            var dropped = match.LiveTanks.Where(IsDrop).OrderBy(k => k.NetId).First();
            var away = dropped.GlobalPosition - victim.GlobalPosition;
            away.Y = 0;
            ScenarioAssert(away.Length() > .2f, "duel: blocked-lane fixture has no drop offset");
            player.Teleport(victim.GlobalPosition - away.Normalized() * 14f, player.FacingYaw);
            var hit = DuelRay(match, dropped, player.GlobalPosition);
            ScenarioAssert(PlayerCharacter.Owning(hit) == victim, "duel: fixture did not put the victim in the firing lane");
            Log("duel: victim-blocked firing lane staged");
        }
        // Drops keep moving under lag and can land behind the victim. Keep a clear firing lane on the road;
        // blindly chasing the nearest drop can shoot the victim until their bank is empty instead.
        if (duelTime >= 4f && duelBlastAt < 0)
        {
            var target = match.LiveTanks.FirstOrDefault(k => k.NetId == duelTank);
            if (target != null && DuelRay(match, target, player.GlobalPosition) != target)
            {
                duelTank = null;
                target = null;
            }
            if (duelTank == null)
            {
                var lanes = new[] { shooterSpot, World.Suburb.ToWorld(mid + road.Direction * 21f, 0.05f) };
                foreach (var dropped in match.LiveTanks.Where(IsDrop).Where(k => !k.Invulnerable)
                             .OrderBy(k => k.GlobalPosition.DistanceTo(player.GlobalPosition)))
                {
                    var lane = lanes.FirstOrDefault(at => DuelRay(match, dropped, at) == dropped, Vector3.Inf);
                    if (!lane.IsFinite()) continue;
                    if (player.GlobalPosition.DistanceTo(lane) > .1f) player.Teleport(lane, player.FacingYaw);
                    target = dropped;
                    duelTank = target.NetId;
                    duelShotLeft = 0.3f; // Let the rifle align after changing the lane or target.
                    target.Detonated += _ => duelBlastAt = duelTime;
                    Log($"duel: tracking dropped tank {target.NetId:x} from a clear firing lane");
                    break;
                }
            }
            if (target != null)
            {
                AimAt(player, target.GlobalTransform * PropaneTank.LocalCenter);
                duelShotLeft -= dt;
                if (duelShotLeft <= 0 && !target.Invulnerable)
                {
                    duelShotLeft = 0.4f;
                    duelInput.Fire(duelInput.Now + 0.001f);
                    Log($"duel: shot at tracked tank {target.NetId:x} {target.State}");
                }
            }
        }
        foreach (var t in new[] { 4.3f, 4.75f, 5.2f, 6.5f })
        {
            if (Once(t, dt))
            {
                director.Screenshot($"shooter_{t:00.0}");
            }
        }
    }

    private static GodotObject? DuelRay(Match match, PropaneTank target, Vector3 standing)
    {
        using var ray = PhysicsRayQueryParameters3D.Create(standing + Vector3.Up * 1.4f,
            target.GlobalTransform * PropaneTank.LocalCenter, Layers.ShotMask,
            new Godot.Collections.Array<Rid> { match.Player.GetRid() });
        var hit = match.World.Physics.DirectSpaceState.IntersectRay(ray);
        return hit.Count > 0 ? hit["collider"].As<GodotObject>() : null;
    }

    /// <summary>Walks into the nearest ammo pickup and films it vanishing and building back up.</summary>
    private void PickupScenario(Match match, float dt)
    {
        var player = match.Player;
        if (!match.IsPlaying || match.Pickups.Count == 0)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var pickup = match.Pickups[0];
        if (Once(0.2f, dt))
        {
            // Spend some rounds first so the pickup has room to add them.
            player.Ammo!.Reserve = 10;
            var at = pickup.GlobalPosition;
            var start = at + new Vector3(3.5f, 0.05f, 0);
            player.Teleport(start, Mathf.Atan2(-(at - start).X, -(at - start).Z));
            AimAt(player, at + Vector3.Up * 0.3f);
        }
        if (Once(0.6f, dt))
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            director.Screenshot("pickup_0_before");
            duelInput.Hold(duelInput.Now, duelInput.Now + 5f, new PlayerIntent { Move = new Vector2(0, 1f) });
        }
        if (duelTime >= 0.6f && !pickupMovedAway)
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            if (pickup.ClaimPending || !pickup.Available)
            {
                // Stop only after actually reaching the can, and move out before it can respawn. The old fixed
                // walking interval could end short of the take radius, yet still capture a "taken" frame.
                pickupMovedAway = true;
                duelInput = new ScriptedInput();
                player.InputOverride = duelInput;
                player.Teleport(pickup.GlobalPosition + new Vector3(3.5f, 0.05f, 0), player.FacingYaw);
                AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            }
        }
        if (!pickup.Available && pickupTakenAt < 0)
        {
            pickupTakenAt = duelTime;
            ScenarioAssert(player.Ammo!.Reserve == 10 + match.World.Tuning.PickupAmount,
                "pickup: can did not add its rounds exactly once");
        }
        if (Once(5f, dt)) { ScenarioAssert(pickupTakenAt >= 0, "pickup: player never took the can"); }
        if (pickupTakenAt < 0) { return; }
        foreach (var offset in new[] { 0.15f, 1f })
        {
            if (Once(pickupTakenAt + offset, dt))
            {
                director.Screenshot($"pickup_1_taken_{offset:0.00}");
                Log($"pickup available {pickup.Available}, ammo {player.Ammo!.Magazine}/{player.Ammo.Reserve}");
                ScenarioAssert(!pickup.Available && player.Ammo.Reserve == 10 + match.World.Tuning.PickupAmount,
                    "pickup: can returned early or changed ammo while absent");
            }
        }
        var respawn = match.World.Tuning.PickupRespawnTime;
        var reveal = match.World.Tuning.PickupRevealTime;
        foreach (var f in new[] { 0.15f, 0.4f, 0.65f, 0.9f, 1.2f })
        {
            var t = pickupTakenAt + respawn + reveal * f;
            if (Once(t, dt))
            {
                director.Screenshot($"pickup_2_reveal_{f:0.00}");
                if (f == 1.2f)
                {
                    ScenarioAssert(pickup.Available, "pickup: can did not respawn");
                    scenarioPassed = !scenarioFailed;
                    if (scenarioPassed) { Log("pickup: assertions passed"); }
                }
            }
        }
    }

    /// <summary>The host follows another (playing) bot at a few meters and films it: running, aiming, shooting, thrown.</summary>
    private void Watch(Match match, float dt)
    {
        var player = match.Player;
        var other = match.RemoteBodies.FirstOrDefault();
        if (!match.IsPlaying || other == null)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
        }
        duelTime += dt;
        var target = other.IsRagdolled ? other.PelvisPosition : other.GlobalPosition + Vector3.Up;
        if (player.GlobalPosition.DistanceTo(other.GlobalPosition) > 9f)
        {
            // Keep up: drop in a few meters behind and to the side of them.
            var back = other.GlobalPosition + new Vector3(Mathf.Sin(other.FacingYaw), 0, Mathf.Cos(other.FacingYaw)) * 5f + new Vector3(1.5f, 0.3f, 0);
            player.Teleport(back, player.FacingYaw);
        }
        AimAt(player, target);
        if (Mathf.PosMod(duelTime, 1.5f) < dt)
        {
            director.Screenshot($"watch_{duelTime:000.0}");
        }
    }

    private int meleeThrows, meleeGetups;
    private bool meleeFirstDown, meleeSecondDown;
    private readonly HashSet<string> meleeCaptured = new();

    private void MeleeScenario(Match match, float dt)
    {
        if (!match.IsPlaying) return;
        var victim = role == "host";
        var player = match.Player;
        var other = match.RemoteBodies.FirstOrDefault();
        if (other == null) return;
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
            player.Ragdolled += (_, _, _) => meleeThrows++;
            player.GotUp += (_, _) => meleeGetups++;
            if (!victim) { duelInput.Melee(2); duelInput.Melee(12); }
            if (victim && DevArgs.Get("view") == "side")
            {
                movementCamera = new Camera3D { Fov = 40, Current = true };
                match.AddChild(movementCamera);
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var attacker = victim ? other : player;
        var target = victim ? player : other;
        var origin = new Vector3(match.World.Tuning.MpMapSizeBase + match.World.Tuning.MpMapSizePerPlayer * match.RemoteBodies.Count() + 30, 0.05f, 0);
        if (Once(.25f, dt) || Once(10.5f, dt))
        {
            player.Teleport(origin + (victim ? Vector3.Forward * 1.8f : Vector3.Zero), victim ? Mathf.Pi : 0);
            player.Aim.SetOrientation(victim ? 180 : 0, -6);
            if (remoteMotion != null) remoteMotion.SkipNextPose = true;
        }
        if (duelTime > 1 && remoteGrip == null && DevArgs.Get("assert-grip") != null) remoteGrip = GripProbe.Attach(attacker);
        if (duelTime > 1 && remoteMotion == null && DevArgs.Get("assert-motion") != null) remoteMotion = MotionProbe.Attach(attacker);
        if (movementCamera != null)
        {
            var focus = attacker.GlobalPosition + Vector3.Up;
            movementCamera.LookAtFromPosition(focus + new Vector3(4, .6f, -1.2f), focus);
        }
        if (!victim && DevArgs.Get("view") == "player" && target.IsRagdolled)
        {
            AimAt(player, target.GlobalPosition + Vector3.Up);
            var label = target.ResolvedState.Lifecycle == PlayerLifecycle.GettingUp ? "remote_getup" : "remote_down";
            if (meleeCaptured.Add(label)) CaptureMelee(label);
            var targetRig = target.FindChildren("RifleRig", owned: false).OfType<RifleRig>().Single();
            if (targetRig.Weight > .5f && targetRig.Weight < .9f && meleeCaptured.Add("remote_getup_handoff"))
                CaptureMelee("remote_getup_handoff");
        }
        if (duelTime < 8) meleeFirstDown |= target.IsRagdolled;
        if (duelTime > 12) meleeSecondDown |= target.IsRagdolled;
        if (attacker.ResolvedState.Weapon == WeaponAction.Melee)
        {
            var swing = duelTime < 8 ? 1 : 2;
            var frame = (int)(attacker.MeleeProgress * 12);
            var label = $"melee_{role}_{swing}_{frame:00}";
            if (meleeCaptured.Add(label)) CaptureMelee(label);
        }
        if (Once(4, dt))
        {
            ScenarioAssert(meleeFirstDown && match.Scores[target.ActorId] == 0 && match.LiveTanks.Count(IsDrop) == 5,
                "melee: first knockdown must spill exactly five tanks from a five-tank bank");
            if (victim) ScenarioAssert(meleeThrows == 1, "melee: repeated active contacts threw victim twice");
        }
        if (Once(10, dt))
        {
            ScenarioAssert(!target.IsRagdolled && meleeFirstDown, "melee: first victim never got up");
            if (victim) ScenarioAssert(meleeGetups == 1, "melee: missing first get-up");
        }
        if (Once(14, dt))
        {
            ScenarioAssert(meleeSecondDown && match.Scores[target.ActorId] == 0 && match.LiveTanks.Count(IsDrop) == 5,
                "melee: zero-bank victim must ragdoll without another drop");
            if (victim) ScenarioAssert(meleeThrows == 2, "melee: missing zero-bank throw");
        }
        if (Once(22, dt))
        {
            ScenarioAssert(!target.IsRagdolled && meleeSecondDown, "melee: zero-bank victim never got up");
            if (victim) ScenarioAssert(meleeGetups == 2, "melee: missing zero-bank get-up");
            remoteGrip?.Finish(); remoteMotion?.Finish();
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) Log("melee: assertions passed; five tanks spilled, zero-bank stun, both get-ups");
        }
    }

    private async void CaptureMelee(string label)
    {
        if (DisplayServer.GetName() == "headless" || DevArgs.Get("capture-dir") == null) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsInsideTree()) director.ScreenshotAsync(label);
    }

    private Camera3D? movementCamera;
    private GripProbe? remoteGrip;
    private LocomotionProbe? remoteFeet;
    private MotionProbe? remoteMotion;
    private bool sawRemoteReload;
    private bool sawRemoteCreep;
    private bool sawRemoteSprint;
    private bool sawRemoteAir;
    private bool sawRemoteRestoredAim;
    private bool movementProved;
    private PlayerGait remoteGait;
    private float remoteGaitTime;
    private bool capturedCreep;
    private bool capturedSprint;
    private bool capturedLanding;
    private bool capturedRelease;

    /// <summary>Two-player gait/stance proof on the unobstructed void floor, seen through real snapshots.</summary>
    private void MovementScenario(Match match, float dt)
    {
        if (!match.IsPlaying) return;
        if (duelTime < 0)
        {
            duelTime = 0;
            match.Player.InputOverride = duelInput;
            match.Player.Teleport(new Vector3(role == "host" ? 20 : 0, 0.05f, -200), 0);
            if (role != "host")
            {
                duelInput.Hold(0.5f, 8.5f, new PlayerIntent { Move = new Vector2(0, 1) });
                duelInput.Hold(1.5f, 8.5f, new PlayerIntent { Sprint = true });
                duelInput.Hold(1.5f, 3.0f, new PlayerIntent { Aim = true });
                duelInput.Hold(4.5f, 7.5f, new PlayerIntent { Aim = true });
                duelInput.Jump(5.0f);
                duelInput.Hold(8.6f, 9.3f, new PlayerIntent { FireHeld = true });
                duelInput.Hold(9.5f, 11.2f, new PlayerIntent { Aim = true });
                duelInput.Hold(10, 10.7f, new PlayerIntent { FireHeld = true });
                duelInput.Hold(11.4f, 13.8f, new PlayerIntent { Move = Vector2.Down * 0.3f });
                duelInput.Reload(11.6f);
            }
            else
            {
                if (DevArgs.Get("view") != "player")
                {
                    movementCamera = new Camera3D { Fov = 40, Current = true };
                    match.AddChild(movementCamera);
                }
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        if (role != "host") return;
        var other = match.RemoteBodies.FirstOrDefault();
        if (other == null) return;
        if (remoteGrip == null && (DevArgs.Get("grip-metrics") != null || DevArgs.Get("assert-grip") != null))
        {
            remoteGrip = GripProbe.Attach(other);
        }
        // The spawn-to-void relocation also replaces the aim target and body yaw. Measure the movement
        // sequence after that controlled staging has settled; the matrix measures ordinary corrections.
        if (remoteFeet == null && duelTime >= 1 && (DevArgs.Get("feet-metrics") != null || DevArgs.Get("assert-feet") != null))
            remoteFeet = LocomotionProbe.Attach(other);
        if (remoteMotion == null && duelTime >= 1 && DevArgs.Get("assert-motion") != null)
            remoteMotion = MotionProbe.Attach(other);
        sawRemoteReload |= other.ResolvedState.Weapon == WeaponAction.Reload;
        if (remoteFeet != null) remoteFeet.Label = other.ResolvedState.Gait.ToString();
        var focus = other.GlobalPosition + Vector3.Up;
        var offset = new Vector3(4, 0.5f, -1.5f);
        var view = DevArgs.Get("view");
        if (view is "rifleleft" or "rifleright")
        {
            var rifle = other.GetNode<Rifle>("Rifle");
            focus = rifle.GlobalTransform * new Vector3(0, 0, -0.12f);
            offset = new Basis(Vector3.Up, other.FacingYaw) * new Vector3(view == "rifleleft" ? -0.8f : 0.8f, 0.2f, -0.45f);
        }
        if (DevArgs.Get("view") == "player")
        {
            // Review through the observer's actual local view, close enough to judge both wrists.
            match.Player.Teleport(other.GlobalPosition + new Vector3(2, .05f, -3), 0);
            AimAt(match.Player, focus);
        }
        else
        {
            movementCamera!.LookAtFromPosition(focus + offset, focus);
        }

        var remoteRig = other.FindChildren("RifleRig", owned: false).OfType<RifleRig>().Single();
        if (remoteRig.Reload > .4f && remoteRig.Reload < .7f && meleeCaptured.Add("remote_10_reload"))
            CaptureRemoteFrame("remote_10_reload");
        var state = other.ResolvedState;
        var speed = new Vector2(other.Velocity.X, other.Velocity.Z).Length();
        remoteGaitTime = remoteGait == state.Gait ? remoteGaitTime + dt : 0;
        remoteGait = state.Gait;
        if (!sawRemoteCreep && state.Gait == PlayerGait.Creep && state.EffectiveAim && Math.Abs(speed - match.World.Tuning.CreepSpeed) < 0.15f)
        {
            sawRemoteCreep = true;
            CaptureRemoteFrame("remote_00_creep");
            Log($"remote creep: {state.Support}/{state.Weapon} speed={speed:0.00}");
        }
        if (!sawRemoteSprint && state.Gait == PlayerGait.Sprint && speed > match.World.Tuning.SprintSpeed - 0.15f)
        {
            sawRemoteSprint = true;
            CaptureRemoteFrame("remote_01_sprint");
            Log($"remote sprint: {state.Support}/{state.Weapon} speed={speed:0.00}");
        }
        if (!sawRemoteAir && sawRemoteCreep && sawRemoteSprint &&
            state.Support == PlayerSupport.Airborne && !state.EffectiveAim && other.AimAmount == 0)
        {
            sawRemoteAir = true;
            CaptureRemoteFrame("remote_02_air");
            Log($"remote air: {state.Gait}/{state.Weapon}");
        }
        if (sawRemoteAir && state.Grounded && state.EffectiveAim && state.Gait == PlayerGait.Creep)
        {
            sawRemoteRestoredAim = true;
        }
        // Each client can start its script on a different frame. Label screenshots from observed remote states,
        // after the gait/rig blends settle, rather than the observer's script clock.
        if (remoteGaitTime > 0.6f && state.Gait == PlayerGait.Creep && other.AimAmount > 0.99f)
        {
            if (!capturedCreep)
            {
                capturedCreep = true;
                CaptureRemoteFrame("remote_03_creep_settled");
            }
            if (sawRemoteAir && !capturedLanding)
            {
                capturedLanding = true;
                CaptureRemoteFrame("remote_06_landed_creep");
            }
        }
        if (remoteGaitTime > 0.6f && state.Gait == PlayerGait.Sprint && state.Grounded)
        {
            if (!capturedSprint)
            {
                capturedSprint = true;
                CaptureRemoteFrame("remote_04_sprint_settled");
            }
            if (capturedLanding && !capturedRelease)
            {
                capturedRelease = true;
                CaptureRemoteFrame("remote_07_release_sprint");
            }
        }
        if (!movementProved && duelTime >= 14.5f)
        {
            movementProved = true;
            remoteGrip?.Finish();
            remoteFeet?.Finish();
            remoteMotion?.Finish();
            if (DevArgs.Get("assert-motion") != null && (!sawRemoteReload || remoteMotion?.RecoilFrames < 10))
            {
                GD.PushError("[net movement] FAIL missing remote reload or recoil");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            if (!sawRemoteCreep || !sawRemoteSprint || !sawRemoteAir || !sawRemoteRestoredAim ||
                !capturedCreep || !capturedSprint || !capturedLanding || !capturedRelease)
            {
                GD.PushError("[net movement] FAIL missing remote gait/support");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            Log("[net movement] PASS remote creep, sprint, air aim suppression, restored landing aim, bursts and walking reload");
        }
    }

    private async void CaptureRemoteFrame(string label)
    {
        if (DisplayServer.GetName() == "headless") return;
        // Physics runs before animation/render. Read the frame after drawing the state that named it.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsInsideTree()) director.Screenshot(label);
    }

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

    private static bool IsDrop(PropaneTank tank) => tank.NetId is >= BodySync.DropTankBase and < BodySync.CarBase;

    private void ScenarioAssert(bool condition, string failure)
    {
        if (condition) { return; }
        scenarioFailed = true;
        GD.PushError(failure);
        ShutdownGuard.Quit(GetTree(), 1);
    }

    private static void AimAt(PlayerCharacter player, Vector3 point)
    {
        var view = point - player.Aim.EyeOrigin;
        player.Aim.SetOrientation(Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1))));
    }

    /// <summary>Scores and where this game has each tank, keyed by match clock, for comparing players' views.</summary>
    private void Digest()
    {
        if (match == null)
        {
            return;
        }
        var tanks = new StringBuilder();
        foreach (var tank in match.LiveTanks.OrderBy(t => t.NetId))
        {
            var p = tank.GlobalPosition;
            tanks.Append($" {tank.NetId:x}:{(tank.State == PropaneTank.TankState.Venting ? "V" : "I")}:{p.X:0.00},{p.Y:0.00},{p.Z:0.00}");
        }
        var remotes = string.Join(" ", match.RemoteBodies.Select(b => $"{b.ActorId}@{b.GlobalPosition.X:0.0},{b.GlobalPosition.Z:0.0}{(b.IsRagdolled ? "R" : "")}"));
        foreach (var body in match.RemoteBodies)
        {
            var bones = body.FindChildren("Physical_*", "PhysicalBone3D", owned: false).OfType<PhysicalBone3D>().ToList();
            if (bones.Count > 0)
            {
                var far = bones.Max(b => b.GlobalPosition.DistanceTo(body.GlobalPosition));
                Log($"bones of {body.ActorId}: farthest {far:0.00} m from the body, ragdolled {body.IsRagdolled}, layer {bones[0].CollisionLayer}");
            }
        }
        var self = match.Player;
        Log($"tuning BlastRadius {match.World.Tuning.BlastRadius} PickupRespawnTime {match.World.Tuning.PickupRespawnTime}");
        Log(CapacityMetrics.Sample("client", net));
        Log($"perf fps {Engine.GetFramesPerSecond():0} process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.0} ms physics {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:0.0} ms bodies {Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects)}");
        Log($"digest clock {match.TimeLeft:0.0} me {self.GlobalPosition.X:0.0},{self.GlobalPosition.Z:0.0}{(self.IsRagdolled ? "R" : "")} ammo {self.Ammo?.Magazine}/{self.Ammo?.Reserve} scores {ScoreLine()} remotes {remotes}");
        Log($"tanks clock {match.TimeLeft:0.0} n {match.LiveTanks.Count()}{tanks}");
    }

    private string ScoreLine() => match == null ? "" : string.Join(" ", match.Scores.Select(s => $"{s.Key}={s.Value}"));

    private PropaneTank? streamTank;
    private Vector3 streamOrigin;
    private float streamStarted;

    // Native replication fixture: straight flight isolates packet delivery from collisions/vent chaos.
    // Only the host drives the owner; observers must learn motion through the actual transport.
    private void BodyStreamScenario(Match current)
    {
        if (streamTank == null)
        {
            streamTank = current.LiveTanks.OrderBy(t => t.NetId).First();
            streamOrigin = streamTank.GlobalPosition;
            streamStarted = clock;
            streamTank.GravityScale = 0;
            streamTank.LinearDamp = streamTank.AngularDamp = 0;
            streamTank.CollisionMask = 0;
        }
        if (role == "host")
        {
            var flight = Mathf.Min(clock - streamStarted, 15);
            streamTank.GlobalPosition = streamOrigin + Vector3.Right * (flight * 8);
            streamTank.LinearVelocity = flight < 15 ? Vector3.Right * 8 : Vector3.Zero;
            streamTank.AngularVelocity = Vector3.Zero;
            streamTank.Sleeping = flight >= 15;
        }
        if (!scenarioPassed && clock - streamStarted >= 10)
        {
            ScenarioAssert(streamTank.GlobalPosition.DistanceTo(streamOrigin) > 40,
                "body-stream: observer did not receive owner movement");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) { Log("body-stream: authoritative movement received"); }
        }
    }

    // Stopwatch uses the OS monotonic clock shared by processes on this machine. Match.TimeLeft is a
    // packet-corrected countdown, unsuitable for alignment. This dev probe is for net_bots' local clients.
    private static double TankCheckTime() => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    private void LogTankCheck(bool final = false, bool completion = false)
    {
        if (match == null || DevArgs.Get("tank-check") == null) { return; }
        var sampleTime = TankCheckTime();
        var tanks = new StringBuilder();
        var count = 0;
        foreach (var tank in match.LiveTanks.OrderBy(t => t.NetId))
        {
            var p = tank.GlobalPosition;
            var resting = tank.AtRest && !tank.Gathering;
            tanks.Append(FormattableString.Invariant($" {tank.NetId:x}:{(tank.State == PropaneTank.TankState.Venting ? "V" : "I")}:{(resting ? "R" : "M")}:{p.X:0.000},{p.Y:0.000},{p.Z:0.000}"));
            count++;
        }
        var phase = match.IsPlaying ? "P" : match.IsOver ? "E" : "L";
        var kind = completion ? "completion" : final ? "final" : "check";
        Log(FormattableString.Invariant($"tank-{kind} match {match.Setup.Id} time {sampleTime:0.000000} phase {phase} final {final || completion} length {match.Setup.Length:0.000} n {count}{tanks}"));
        if (final) { tankCheckFinal = true; }
        if (DevArgs.Get("tank-motion-trace") != null && (final || sampleTime >= motionTraceNext))
        {
            motionTraceNext = sampleTime + .25;
            LogMotionTrace(sampleTime);
        }
    }

    private void LogMotionTrace(double sampleTime)
    {
        if (match == null) { return; }
        var now = match.Clock.Seconds;
        var d = net.Diagnostics;
        Log(FormattableString.Invariant($"net-trace time {sampleTime:0.000000} throttle={d.NativeThrottle:0.000}/{d.NativeThrottleLimit:0.000} rtt={d.RoundTripMilliseconds:0.000} variance={d.NativeRttVariance:0.000}"));
        foreach (var entry in match.Bodies.Entries)
        {
            if (!entry.IsTank || !IsInstanceValid(entry.Body) || entry.Body.Sleeping || entry.HasTarget && entry.TargetRest) { continue; }
            var body = entry.Body;
            var p = body.GlobalPosition;
            var center = body.GlobalTransform * body.CenterOfMass;
            var lead = (float)Math.Clamp(now - entry.TargetReceived, 0, BodySync.MaxExtrapolation);
            var sampledCenter = entry.Target * body.CenterOfMass;
            var goalCenter = entry.HasTarget ? sampledCenter + entry.TargetLinear * lead : center;
            var rotation = entry.Target.Basis.GetRotationQuaternion();
            if (lead > 0 && entry.TargetAngular.LengthSquared() > .0001f)
            {
                rotation = new Quaternion(entry.TargetAngular.Normalized(), entry.TargetAngular.Length() * lead) * rotation;
            }
            var goal = entry.HasTarget ? goalCenter - new Basis(rotation) * body.CenterOfMass : p;
            var barrier = "none";
            if (entry.HasTarget && center.DistanceSquaredTo(sampledCenter) > .0001f)
            {
                using var ray = PhysicsRayQueryParameters3D.Create(center, sampledCenter,
                    Layers.World | Layers.Props | Layers.Floor, new Godot.Collections.Array<Rid> { body.GetRid() });
                var hit = match.World.Physics.DirectSpaceState.IntersectRay(ray);
                if (hit.Count > 0 && hit["collider"].As<GodotObject>() is Node collider) { barrier = collider.Name; }
            }
            var angle = entry.HasTarget ? body.GlobalBasis.Orthonormalized().GetRotationQuaternion().AngleTo(entry.Target.Basis.GetRotationQuaternion()) : 0;
            Log(FormattableString.Invariant($"motion-trace match {match.Setup.Id} time {sampleTime:0.000000} id {entry.Id:x} owner={entry.Owner} epoch={entry.Epoch} sequence={entry.Sequence} last={entry.LastSequence} pending={entry.ClaimPending} target={entry.HasTarget} sample-age={now - entry.TargetReceived:0.000} quiet={now - entry.TargetUpdated:0.000} pos={p.X:0.000},{p.Y:0.000},{p.Z:0.000} goal={goal.X:0.000},{goal.Y:0.000},{goal.Z:0.000} center={center.X:0.000},{center.Y:0.000},{center.Z:0.000} goal-center={goalCenter.X:0.000},{goalCenter.Y:0.000},{goalCenter.Z:0.000} speed={body.LinearVelocity.Length():0.000} spin={body.AngularVelocity.Length():0.000} goal-spin={entry.TargetAngular.Length():0.000} angle={angle:0.000} barrier={barrier}"));
        }
    }

    private void Log(string text) => GD.Print($"[nettest {net.PlayerName} {Time.GetTicksMsec() / 1000.0:0.00}] {text}");
}
