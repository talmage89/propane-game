using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Dev.NetScenarios;
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
public partial class NetTest : Node, INetScenarioHost
{
    private NetScenarioRegistration registration = null!;
    private NetScenario? scenario;
    private IBotBrain bot = null!;
    private BotActivity playerActivity = new();
    private double kingLoneSeconds, kingSampleSeconds;
    private NetClient net = null!;
    private Match? match;
    private Action? finishMatch;
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
        try { registration = NetScenarioRegistry.Find(DevArgs.Get("scenario")); }
        catch (ArgumentException error)
        {
            GD.PushError(error.Message);
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
        roam = DevArgs.Get("lobby-roam") != null;
        roamSeconds = DevArgs.GetFloat("lobby-roam", 10);
        if (!roam) Tuning.SavedTemplate.LobbyStageTime = 0;
        net = new NetClient { Name = "Net", LobbyWorlds = roam };
        AddChild(net);
        director = new CaptureDirector { Name = "Director", QuitAfter = DevArgs.GetFloat("quit", 400f) };
        AddChild(director);
        scenario = registration.Factory?.Invoke(this);
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
            if (setup.ModeId == ModeRegistry.LobbyWorldId)
            {
                EnterLobbyWorld(setup);
                return;
            }
            appearanceMenus?.Show(UI.Menus.Screen.None);
            if (DevArgs.Get("scenario") == "appearance") { CheckAppearances(setup.Players.Select(p => p.Player)); }
            var delay = DevArgs.Get("delay-who") is not { } delayed || delayed == net.PlayerName ? DevArgs.GetFloat("match-delay", 0) : 0;
            if (scenario != null) delay = scenario.ConstructionDelay(delay);
            if (delay > 0)
            {
                Log($"delaying match {setup.Id} construction by {delay} s");
                await ToSignal(GetTree().CreateTimer(delay), SceneTreeTimer.SignalName.Timeout);
                if (net.ActiveMatchId != setup.Id || net.State != NetClient.Status.Connected) { return; }
            }
            EndLobbyWorld();
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
                BeforeInboxAttach = () => scenario?.BeforeInbox(match!)
            };
            finishMatch = () =>
            {
                if (match == null) return;
                if (scenario != null)
                {
                    if (!scenario.CheckCompletion(registration.Completion)) return;
                }
                else if (registration.Completion == NetScenarioCompletion.Assertions)
                {
                    ScenarioAssert(scenarioPassed, "scenario ended before all assertions passed");
                }
                if (bot is Bot smart)
                {
                    Log($"navigation finished: stuck={smart.StuckEvents} blacklist={smart.BlacklistedTargets} replans={smart.Replans} jumps={smart.Jumps}");
                }
                scenario?.Complete(match);
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
                {
                    Log(FormattableString.Invariant($"king crowd: seconds={kingSampleSeconds:F2} lone_share={kingLoneSeconds / Math.Max(kingSampleSeconds, 0.001):F4} minters={king.Minted.Count(m => m.Count > 0)}"));
                    Log($"finished king {match.Setup.Id}; minted " + string.Join(" ", match.Setup.Players.OrderBy(p => p.Player.Id).Select(p => $"{p.Player.Id}={king.MintedBy(p.Player.Id)}")));
                }
                Log($"finished match {match.Setup.Id}; final scores {ScoreLine()}");
                RemoveChild(match);
                match.QueueFree();
                scenario?.RetireMatch();
                match = null;
                matches++;
                started = matches >= matchesWanted;
                // Lobby Waiting may have arrived before this client's results timer finished.
                TryStartMatch();
                // A roaming run quits once it has roamed the world it returns to.
                if (matches >= matchesWanted && !roam)
                {
                    GetTree().CreateTimer(1.0).Timeout += () => ShutdownGuard.Quit(GetTree());
                }
            };
            match.Finished += finishMatch;
            kingStage = -1; kingCamera = null; kingCaptured.Clear();
            kingHeld = kingContested = kingWarned = kingMoved = kingPiled = false;
            match.AuthorityResolved += result =>
            {
                if (DevArgs.Get("tank-authority-trace") != null)
                {
                    var bodies = string.Join(" ", result.Bodies.Select(b => $"{b.Body.Id:x}:{b.Owner}:{b.Body.Epoch}:{b.Body.Sequence}:{b.State}:{b.Rest}"));
                    Log(FormattableString.Invariant($"authority-trace match {setup.Id} time {TankCheckTime():0.000000} peer {result.Peer} request {result.Request} reason {result.Reason} bodies {bodies}"));
                }
            };
            scenario?.BeginAssembly(match);
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
            if (bot is Bot clientBot)
            {
                clientBot.Policy.PointsDriven =
                BotProfile.Nearest(clientBot.Skill, match.World.Tuning) == BotDifficulty.Hard && match.World.Tuning.BotHardChasesPoints;
            }

            playerActivity = new BotActivity();
            kingLoneSeconds = kingSampleSeconds = 0;
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
            scenarioPassed = scenarioFailed = false;
            appearanceTime = -1;
            appearanceDropped = appearanceGlowChecked = appearanceResultsChecked = false;
            scenario?.AfterAssembly(match);
            Log($"match {setup.Id} ready with {setup.Players.Count} players; playing {match.IsPlaying}, over {match.IsOver}");
        };
        var name = DevArgs.Get("name") ?? (role == "host" ? "HostBot" : $"Bot{OS.GetProcessId() % 1000}");
        var color = (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1);
        var appearance = DevArgs.Get("appearance") is { } hex ? ParseAppearance(hex) : AppearanceRecord.FromPalette(color);
        // Give a hosted server a moment to open its port.
        GetTree().CreateTimer(DevArgs.Get("host-server") != null ? 0.3 : 0.05).Timeout += () => net.Connect(address, port, name, appearance);
    }

    public override void _ExitTree()
    {
        (bot as IDisposable)?.Dispose();
        worldBot?.Dispose();
        scenario?.Dispose();
    }

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
            lobby.Members.Count >= players + lobbyBotsTotal && Roamed)
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
        ProcessLobbyWorld((float)delta);
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
        if (match.IsPlaying && match.BotKing is KingBotView crowdState)
        {
            kingSampleSeconds += dt;
            if (crowdState.Holder != 0) kingLoneSeconds += dt;
        }
        if (scenario?.Tick(match, dt) == true) { }
        else if (DevArgs.Get("scenario") == "king")
        {
            KingScenario(match);
        }
        else if (DevArgs.Get("scenario") == "king-late")
        {
            KingLateScenario(match);
        }
        else if (DevArgs.Get("scenario") == "appearance")
        {
            AppearanceScenario(match, dt);
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

    private static string Vector(Vector3 v) => FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");

    // No instance override exists until the new tank's first process update. Its material's
    // shield is transparent; converting Nil directly to Color instead fabricates opaque black.
    internal static Color ReadShieldColor(Variant value) => value.VariantType == Variant.Type.Nil ? Colors.Transparent : value.AsColor();

    private bool scenarioPassed;
    private bool scenarioFailed;

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

    NetClient INetScenarioHost.Net => net;
    CaptureDirector INetScenarioHost.Director => director;
    string INetScenarioHost.Role => role;
    int INetScenarioHost.Players => players;
    float INetScenarioHost.MatchClock => clock;
    SceneTree INetScenarioHost.Tree => GetTree();
    bool INetScenarioHost.InTree => IsInsideTree();
    void INetScenarioHost.Attach(Node node) => AddChild(node);
    SignalAwaiter INetScenarioHost.AwaitSignal(GodotObject source, StringName signal) => ToSignal(source, signal);
    void INetScenarioHost.Log(string text) => Log(text);
    void INetScenarioHost.Fail(string text)
    {
        GD.PushError(text);
        ShutdownGuard.Quit(GetTree(), 1);
    }

    private void Log(string text) => GD.Print($"[nettest {net.PlayerName} {Time.GetTicksMsec() / 1000.0:0.00}] {text}");
}
