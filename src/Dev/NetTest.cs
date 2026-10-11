using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>
/// A multiplayer client driven by a bot, for testing matches without people. It connects, creates a lobby
/// (<c>--role=host</c>, starting once <c>--players=N</c> have joined) or joins the first one (<c>--role=join</c>),
/// then plays with <see cref="Bot"/>. It logs
/// scores and, every few seconds, where it has every tank, so runs on several machines can be compared.
/// Run: godot res://scenes/dev/net_test.tscn -- --server=127.0.0.1 --role=host --players=2 [--capture-dir=/tmp/n]
/// Add <c>--host-server</c> to run the server in the same process.
/// </summary>
public partial class NetTest : Node
{
    private IBotBrain bot = null!;
    private NetClient net = null!;
    private Match? match;
    private CaptureDirector director = null!;
    private string role = "host";
    private int players = 2;
    private bool started;
    private float clock;
    private float digestTimer;
    private float shotTimer;
    private int matches;
    private int matchesWanted = 1;

    public override void _Ready()
    {
        DevArgs.Setup();
        // Several test windows share one screen; a window the compositor is not showing would otherwise block on vsync.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 60;
        DevLog.Enabled = DevArgs.Get("verbose-log") != null;
        role = DevArgs.Get("role") ?? "host";
        // --brain=simple plays the first, easy bot; the default is the stronger one.
        bot = DevArgs.Get("brain") == "simple" ? new SimpleBot() : new Bot { Skill = DevArgs.GetFloat("bot-skill", 1f) };
        players = (int)DevArgs.GetFloat("players", 2);
        matchesWanted = (int)DevArgs.GetFloat("matches", 1);
        var address = DevArgs.Get("server") ?? "127.0.0.1";
        var port = (int)DevArgs.GetFloat("port", Protocol.DefaultPort);
        if (DevArgs.Get("host-server") != null)
        {
            var server = new LobbyServer { Name = "Server", Port = port };
            AddChild(server);
            server.Start();
        }
        InputSetup.Register();
        net = new NetClient { Name = "Net" };
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
                GetTree().Quit(2);
            }
        };
        net.LobbiesChanged += () =>
        {
            if (role == "join" && net.Lobby == null && net.Lobbies.FirstOrDefault(l => l.Phase == LobbyServer.LobbyPhase.Waiting) is { } lobby)
            {
                net.JoinLobby(lobby.Id);
            }
        };
        net.LobbyChanged += () =>
        {
            if (net.Lobby is { } lobby)
            {
                Log($"lobby {lobby.Id}: {string.Join(", ", lobby.Members.Select(m => $"{m.Name}/{Protocol.ColorNames[m.Color]}"))}, {lobby.Phase}");
                TryStartMatch();
            }
        };
        net.NoticeReceived += text => Log($"notice: {text}");
        net.MatchStarting += async setup =>
        {
            var delay = DevArgs.GetFloat("match-delay", 0);
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
            match = new Match { Name = "Match", Net = net, Setup = setup };
            match.Finished += () =>
            {
                if (DevArgs.Get("scenario") is "duel" or "pickup")
                {
                    ScenarioAssert(scenarioPassed, "scenario ended before all assertions passed");
                }
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
                    GetTree().CreateTimer(1.0).Timeout += () => GetTree().Quit();
                }
            };
            AddChild(match);
            match.Player.InputOverride = bot;
            clock = 0;
            duelTime = -1;
            duelInput = new ScriptedInput();
            duelTank = null;
            duelBlastAt = duelThrowAt = -1;
            pickupTakenAt = -1;
            pickupMovedAway = false;
            scenarioPassed = scenarioFailed = false;
            Log($"match {setup.Id} ready with {setup.Players.Count} players; playing {match.IsPlaying}, over {match.IsOver}");
        };
        var name = DevArgs.Get("name") ?? (role == "host" ? "HostBot" : $"Bot{OS.GetProcessId() % 1000}");
        var color = (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1);
        // Give a hosted server a moment to open its port.
        GetTree().CreateTimer(DevArgs.Get("host-server") != null ? 0.3 : 0.05).Timeout += () => net.Connect(address, port, name, color);
    }

    private void TryStartMatch()
    {
        if (role == "host" && net.IsCreator && !started && matches < matchesWanted &&
            net.Lobby is { Phase: LobbyServer.LobbyPhase.Waiting } lobby && lobby.Members.Count >= players)
        {
            started = true;
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
            Tuning.Current.Set(name, value);
            net.Send(new NetWriter(Msg.TuningSet, match.Setup.Id).String(name).Scalar(value));
            Log($"sent tuning {name} = {value}");
        }
        var leaveAt = DevArgs.GetFloat("leave-at", -1);
        if (leaveAt > 0 && clock >= leaveAt)
        {
            Log("leaving the match on purpose");
            GetTree().Quit();
            return;
        }
        if (DevArgs.Get("scenario") == "duel")
        {
            Duel(match, dt);
        }
        else if (DevArgs.Get("scenario") == "pickup")
        {
            PickupScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "watch" && role == "host")
        {
            Watch(match, dt);
        }
        else
        {
            bot.Think(match, dt);
            if (DevArgs.Get("bot-log") != null && Mathf.PosMod(clock, 2f) < dt)
            {
                Log($"bot: {bot.Status}");
            }
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
        if (Once(0.8f, dt)) { duelInitialScore = match.MyScore; }
        if (Once(3.5f, dt))
        {
            var victimId = host ? match.RemoteBodies.First().PeerId : net.MyId;
            ScenarioAssert(match.Scores[victimId] == 6 && match.LiveTanks.Count(IsDrop) == 2,
                "duel: three hits must spill exactly two tanks (one hit inside grace)");
        }
        if (Once(14f, dt))
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
            player.CameraRig.SetOrientation(Mathf.RadToDeg(yaw) + (host ? 0 : 160), -6f);
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
        // Drops keep moving under lag. Follow one tank every frame, retrying until its detonation is observed;
        // a fixed aim/fire gap used to miss the first shot and puncture a different tank with the second.
        if (duelTime >= 4f && duelBlastAt < 0)
        {
            if (duelTank == null)
            {
                var dropped = match.LiveTanks.Where(IsDrop).Where(k => !k.Invulnerable)
                    .OrderBy(k => k.GlobalPosition.DistanceTo(player.GlobalPosition)).FirstOrDefault();
                if (dropped != null)
                {
                    duelTank = dropped.NetId;
                    duelShotLeft = 0.3f; // Let the rifle align before the first shot.
                    dropped.Detonated += _ => duelBlastAt = duelTime;
                    Log($"duel: tracking dropped tank {dropped.NetId:x}");
                }
            }
            var target = match.LiveTanks.FirstOrDefault(k => k.NetId == duelTank);
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
            ScenarioAssert(player.Ammo!.Reserve == 10 + Tuning.Current.PickupAmount,
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
                ScenarioAssert(!pickup.Available && player.Ammo.Reserve == 10 + Tuning.Current.PickupAmount,
                    "pickup: can returned early or changed ammo while absent");
            }
        }
        var respawn = Tuning.Current.PickupRespawnTime;
        var reveal = Tuning.Current.PickupRevealTime;
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

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

    private static bool IsDrop(PropaneTank tank) => tank.NetId is >= BodySync.DropTankBase and < BodySync.CarBase;

    private void ScenarioAssert(bool condition, string failure)
    {
        if (condition) { return; }
        scenarioFailed = true;
        GD.PushError(failure);
        GetTree().Quit(1);
    }

    private static void AimAt(PlayerCharacter player, Vector3 point)
    {
        var camera = player.CameraRig.Camera;
        var view = point - camera.GlobalPosition;
        player.CameraRig.SetOrientation(Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1))));
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
        var remotes = string.Join(" ", match.RemoteBodies.Select(b => $"{b.PeerId}@{b.GlobalPosition.X:0.0},{b.GlobalPosition.Z:0.0}{(b.IsRagdolled ? "R" : "")}"));
        foreach (var body in match.RemoteBodies)
        {
            var bones = body.FindChildren("Physical_*", "PhysicalBone3D", owned: false).OfType<PhysicalBone3D>().ToList();
            if (bones.Count > 0)
            {
                var far = bones.Max(b => b.GlobalPosition.DistanceTo(body.GlobalPosition));
                Log($"bones of {body.PeerId}: farthest {far:0.00} m from the body, ragdolled {body.IsRagdolled}, layer {bones[0].CollisionLayer}");
            }
        }
        var self = match.Player;
        Log($"tuning BlastRadius {Tuning.Current.BlastRadius} PickupRespawnTime {Tuning.Current.PickupRespawnTime}");
        Log($"perf fps {Engine.GetFramesPerSecond():0} process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.0} ms physics {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:0.0} ms bodies {Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects)}");
        Log($"digest clock {match.TimeLeft:0.0} me {self.GlobalPosition.X:0.0},{self.GlobalPosition.Z:0.0}{(self.IsRagdolled ? "R" : "")} ammo {self.Ammo?.Magazine}/{self.Ammo?.Reserve} scores {ScoreLine()} remotes {remotes}");
        Log($"tanks clock {match.TimeLeft:0.0} n {match.LiveTanks.Count()}{tanks}");
    }

    private string ScoreLine() => match == null ? "" : string.Join(" ", match.Scores.Select(s => $"{s.Key}={s.Value}"));

    private void Log(string text) => GD.Print($"[nettest {net.PlayerName} {Time.GetTicksMsec() / 1000.0:0.00}] {text}");
}
