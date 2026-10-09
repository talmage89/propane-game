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
/// then plays: it runs to tanks and shoots them, shoots other players it can see, and fetches ammo when low. It logs
/// scores and, every few seconds, where it has every tank, so runs on several machines can be compared.
/// Run: godot res://scenes/dev/net_test.tscn -- --server=127.0.0.1 --role=host --players=2 [--capture-dir=/tmp/n]
/// Add <c>--host-server</c> to run the server in the same process.
/// </summary>
public partial class NetTest : Node
{
    private readonly BotInput bot = new();
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
            Log($"status {status} {reason}");
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
                if (role == "host" && net.IsCreator && !started && lobby.Members.Count >= players && lobby.Phase == LobbyServer.LobbyPhase.Waiting)
                {
                    started = true;
                    net.SetMatchLength(DevArgs.GetFloat("length", 60));
                    GetTree().CreateTimer(0.5).Timeout += net.StartMatch;
                }
            }
        };
        net.NoticeReceived += text => Log($"notice: {text}");
        net.MatchStarting += setup =>
        {
            match?.QueueFree();
            match = new Match { Name = "Match", Net = net, Setup = setup };
            match.Finished += () =>
            {
                Log($"finished; final scores {ScoreLine()}");
                match.QueueFree();
                match = null;
                matches++;
                started = false;
                if (matches >= matchesWanted)
                {
                    GetTree().CreateTimer(1.0).Timeout += () => GetTree().Quit();
                }
            };
            AddChild(match);
            match.Player.InputOverride = bot;
            clock = 0;
            Log($"match starting with {setup.Players.Count} players");
        };
        var name = DevArgs.Get("name") ?? (role == "host" ? "HostBot" : $"Bot{OS.GetProcessId() % 1000}");
        var color = (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1);
        // Give a hosted server a moment to open its port.
        GetTree().CreateTimer(DevArgs.Get("host-server") != null ? 0.3 : 0.05).Timeout += () => net.Connect(address, port, name, color);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (match == null || !IsInstanceValid(match))
        {
            return;
        }
        var dt = (float)delta;
        clock += dt;
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
        else
        {
            bot.Think(match, dt);
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
    private readonly ScriptedInput duelInput = new();

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
        // Then the dropped tanks: once to puncture, once to set off.
        foreach (var t in new[] { 4.0f, 4.6f })
        {
            if (Once(t - 0.3f, dt))
            {
                var dropped = match.LiveTanks.Where(k => k.NetId >= BodySync.DropTankBase && k.NetId < BodySync.CarBase)
                    .OrderBy(k => k.GlobalPosition.DistanceTo(player.GlobalPosition)).FirstOrDefault();
                if (dropped != null)
                {
                    AimAt(player, dropped.GlobalTransform * PropaneTank.LocalCenter);
                    Log($"duel: aiming at dropped tank {dropped.NetId:x} {dropped.State} invulnerable {dropped.Invulnerable}");
                }
            }
            if (Once(t, dt))
            {
                duelInput.Fire(duelInput.Now + 0.001f);
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
            duelInput.Hold(duelInput.Now, duelInput.Now + 0.55f, new PlayerIntent { Move = new Vector2(0, 1f) });
        }
        if (Once(1.2f, dt))
        {
            player.Teleport(player.GlobalPosition + new Vector3(3f, 0, 0), player.FacingYaw);
        }
        if (Once(1.4f, dt))
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
        }
        foreach (var t in new[] { 1.5f, 2.5f })
        {
            if (Once(t, dt))
            {
                director.Screenshot($"pickup_1_taken_{t:0.0}");
                Log($"pickup available {pickup.Available}, ammo {player.Ammo!.Magazine}/{player.Ammo.Reserve}");
            }
        }
        var respawn = Tuning.Current.PickupRespawnTime;
        var reveal = Tuning.Current.PickupRevealTime;
        foreach (var f in new[] { 0.15f, 0.4f, 0.65f, 0.9f, 1.2f })
        {
            var t = 1.0f + respawn + reveal * f;
            if (Once(t, dt))
            {
                director.Screenshot($"pickup_2_reveal_{f:0.00}");
            }
        }
    }

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

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
        Log($"digest clock {match.TimeLeft:0.0} me {self.GlobalPosition.X:0.0},{self.GlobalPosition.Z:0.0}{(self.IsRagdolled ? "R" : "")} ammo {self.Ammo?.Magazine}/{self.Ammo?.Reserve} scores {ScoreLine()} remotes {remotes}");
        Log($"tanks clock {match.TimeLeft:0.0} n {match.LiveTanks.Count()}{tanks}");
    }

    private string ScoreLine() => match == null ? "" : string.Join(" ", match.Scores.Select(s => $"{s.Key}={s.Value}"));

    private void Log(string text) => GD.Print($"[nettest {net.PlayerName} {Time.GetTicksMsec() / 1000.0:0.00}] {text}");

    /// <summary>
    /// The bot's brain: picks a target (another player it can see, a tank, or ammo when low), runs at it and shoots.
    /// </summary>
    private sealed class BotInput : IPlayerInputSource
    {
        private PlayerIntent intent;
        private Node3D? target;
        private Vector3 targetPoint;
        private float retarget;
        private float stuck;
        private float blocked;
        private Vector3 lastPosition;
        private readonly RandomNumberGenerator rng = new();

        public PlayerIntent Read()
        {
            var read = intent;
            intent.FirePressed = false;
            intent.JumpPressed = false;
            intent.ReloadPressed = false;
            return read;
        }

        public void Think(Match match, float dt)
        {
            var player = match.Player;
            intent = new PlayerIntent();
            if (!match.IsPlaying || player.IsRagdolled)
            {
                return;
            }
            var ammo = player.Ammo!;
            retarget -= dt;
            if (retarget <= 0 || target == null || !GodotObject.IsInstanceValid(target) || target is PropaneTank { State: PropaneTank.TankState.Exploded })
            {
                retarget = 0.6f;
                var previous = target;
                target = Choose(match, ammo);
                if (target != previous)
                {
                    blocked = 0;
                }
            }
            if (target == null)
            {
                return;
            }
            targetPoint = target switch
            {
                PlayerCharacter p => p.IsRagdolled ? p.PelvisPosition : p.GlobalPosition + Vector3.Up * 1.1f,
                PropaneTank t => t.GlobalTransform * PropaneTank.LocalCenter,
                _ => target.GlobalPosition + Vector3.Up * 0.3f,
            };
            var to = targetPoint - player.GlobalPosition;
            var flat = new Vector3(to.X, 0, to.Z);
            var distance = flat.Length();
            var wantRange = target switch
            {
                AmmoPickup => 0f,
                PlayerCharacter => 12f,
                _ => 9f,
            };

            // Face the target: camera yaw so its forward runs along the flat direction, pitch from the camera.
            var camera = player.CameraRig.Camera;
            var view = targetPoint - camera.GlobalPosition;
            var yaw = Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z));
            var pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1)));
            player.CameraRig.SetOrientation(yaw, pitch);

            // In range but unable to hit it (a fence or house in the way): close in, then give up on it.
            if (blocked > 1.2f)
            {
                wantRange = 1.5f;
            }
            if (blocked > 4f)
            {
                blocked = 0;
                target = match.LiveTanks.Where(t => !t.Invulnerable && t != target).OrderBy(_ => rng.Randf()).FirstOrDefault();
                return;
            }
            if (distance > wantRange)
            {
                intent.Move = new Vector2(0, 1);
                intent.Sprint = distance > wantRange + 10f;
            }
            else if (target is not AmmoPickup)
            {
                intent.Aim = true;
                intent.Move = new Vector2(rng.RandfRange(-1, 1) * 0.4f, 0);
            }

            // Fire when the crosshair is on the target.
            if (target is not AmmoPickup && distance < 45f)
            {
                var space = player.GetWorld3D().DirectSpaceState;
                var forward = -camera.GlobalBasis.Z;
                var start = camera.GlobalPosition + forward * 2.5f;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(start, start + forward * 60f, Layers.ShotMask));
                var collider = hit.Count > 0 ? hit["collider"].As<GodotObject>() : null;
                var onTarget = collider == target || target is PlayerCharacter pc && PlayerCharacter.Owning(collider) == pc;
                if (onTarget)
                {
                    intent.FirePressed = true;
                    intent.FireHeld = true;
                    blocked = 0;
                }
                else if (distance <= wantRange + 1f)
                {
                    blocked += dt;
                }
            }
            if (ammo.Magazine < 3 && !intent.FireHeld && ammo.Reserve > 0)
            {
                intent.ReloadPressed = true;
            }

            // Unstick: jump and sidestep if barely moving while trying to.
            var moved = player.GlobalPosition.DistanceTo(lastPosition);
            lastPosition = player.GlobalPosition;
            stuck = intent.Move.Y > 0 && moved < 0.02f ? stuck + dt : 0;
            if (stuck > 0.5f)
            {
                intent.JumpPressed = true;
                intent.Move = new Vector2(rng.Randf() < 0.5f ? -1 : 1, 0.5f);
                if (stuck > 2.5f)
                {
                    retarget = 0;
                    stuck = 0;
                }
            }
        }

        private Node3D? Choose(Match match, AmmoState ammo)
        {
            var player = match.Player;
            var here = player.GlobalPosition;
            if (ammo.Magazine + ammo.Reserve < 25)
            {
                var pickup = match.Pickups.Where(p => p.Available).OrderBy(p => p.GlobalPosition.DistanceTo(here)).FirstOrDefault();
                if (pickup != null)
                {
                    return pickup;
                }
            }
            var space = player.GetWorld3D().DirectSpaceState;
            var visible = match.RemoteBodies.Where(b => b.GlobalPosition.DistanceTo(here) < 30f &&
                space.IntersectRay(PhysicsRayQueryParameters3D.Create(here + Vector3.Up * 1.5f, b.GlobalPosition + Vector3.Up * 1.2f, Layers.World)).Count == 0)
                .OrderBy(b => b.GlobalPosition.DistanceTo(here)).FirstOrDefault();
            if (visible != null && rng.Randf() < 0.5f)
            {
                return visible;
            }
            return match.LiveTanks.Where(t => !t.Invulnerable).OrderBy(t => t.GlobalPosition.DistanceTo(here) + rng.RandfRange(0, 6)).FirstOrDefault();
        }
    }
}
