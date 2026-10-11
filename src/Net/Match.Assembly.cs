using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Net;

public partial class Match
{
    private sealed class MatchSource(Match match) : ISessionWorldSource
    {
        public void Build(SessionContext session) => match.BuildContent();
    }
    private sealed class TankBankMode(Match match) : ISessionMode
    {
        public void Configure(SessionContext session)
        {
            match.me = match.Net.MyId;
            match.bodies = new BodySync(session.World.Tuning, match.me, match.Setup.HostId);
            session.OnStopping(match.StopReplication);
        }
        public void CreateActors(SessionContext session) => match.BuildActors();
        public void CreateViews(SessionContext session) => match.BuildViews();
        public void RegisterReplication(SessionContext session) => match.RegisterReplication();
        public void AttachInbox(SessionContext session)
        {
            session.OnDetaching(() => match.Net.DetachMatch(match.Setup.Id, match.OnMessage));
            match.Net.AttachMatch(match.Setup.Id, match.OnMessage);
        }
        public void Ready(SessionContext session)
        {
            match.AcknowledgeReady();
            match.Send(new NetWriter(Msg.Loaded, match.Setup.Id));
        }
    }

    private void BuildContent()
    {
        environment = new VoidEnvironment { Name = "Void" };
        World.Root.AddChild(environment);
        effects = World.EffectsRoot;
        plan = PlanCodec.Decode(Setup.Plan);
        var settings = SuburbPlans.Match(World.Tuning, Setup.Players.Count);
        suburb = Suburb.Build(World, plan, settings, networked: true);
        World.Root.AddChild(suburb);
        World.SetContentRoot(suburb.DynamicRoot);

        gather = new TankGather
        {
            World = World,
            Name = "Gather",
            Tanks = () => LiveTanks,
            Enabled = () => IsPlaying && bodies.Host == me,
            Grant = GrantGather,
            Publish = PublishGather
        };
        suburb.AddChild(gather);

        for (var i = 0; i < plan.AmmoSpots.Count; i++)
        {
            var pickup = new AmmoPickup { Name = $"Ammo_{i}", Index = i };
            suburb.DynamicRoot.AddChild(pickup);
            pickup.Position = Suburb.ToWorld(plan.AmmoSpots[i]);
            pickups.Add(pickup);
        }

    }

    private void BuildActors()
    {
        foreach (var (profile, slot) in Setup.Players)
        {
            profiles[profile.Id] = profile;
            scores[profile.Id] = 0;
            var spawn = plan.Spawns.Count > 0 ? plan.Spawns[slot % plan.Spawns.Count] : new SpawnPoint(plan.Spawn, plan.SpawnYaw);
            var position = Suburb.ToWorld(spawn.Position, 0.05f);
            if (profile.Id == me)
            {
                var player = new PlayerCharacter
                {
                    World = World,
                    Name = "Player",
                    PeerId = me,
                    BodyColor = profile.Appearance.Color,
                    Ammo = AmmoState.Starting(World.Tuning),
                    InputLocked = true,
                };
                Session.AddActor(player);
                player.Runtime.SpawnSlot = slot;
                player.Teleport(position, spawn.Yaw + Mathf.Pi);
            }
            else
            {
                var body = new PlayerCharacter
                {
                    World = World,
                    Name = $"Remote_{profile.Id}",
                    IsRemote = true,
                    PeerId = profile.Id,
                    BodyColor = profile.Appearance.Color,
                };
                Session.AddActor(body);
                body.Teleport(position, spawn.Yaw + Mathf.Pi);
                body.Runtime.IntentSource = new ReplicatedIntent(World.Tuning);
            }
        }
        if (!Session.OwnedActors.Any())
        {
            throw new InvalidOperationException("this player is not in the match");
        }
    }

    private void BuildViews()
    {
        if (CreateLocalView)
        {
            var view = Session.AddLocalView(player.Runtime, this, new DeviceInput(World.Tuning));
            environment.Follow = view.CameraRig.Camera;
            hud = view.Hud;
            hud.ShowTankCount(false);
        }
        matchHud = new MatchHud { World = World, Name = "MatchHud" };
        Session.AddView(this, matchHud);
        results = new ResultsScreen { Name = "Results" };
        Session.AddView(this, results);
        menu = new MatchMenu { Name = "Menu" };
        menu.ResumeRequested += CloseMenu;
        menu.LeaveRequested += () => World.Defer(() => LeaveRequested?.Invoke(), whilePaused: true);
        Session.AddView(this, menu);
        if (OS.IsDebugBuild() && Setup.CreatorDebug && Setup.HostId == me)
        {
            tuningPanel = new TuningPanel { Target = World.Tuning, Name = "Tuning", Note = "Changes apply to everyone in the match", UseDeclaredRanges = true };
            tuningPanel.ValueChanged += (name, value) => Send(new NetWriter(Msg.TuningSet, Setup.Id).String(name).Scalar(value));
            Session.AddView(this, tuningPanel);
        }

    }

    private void RegisterReplication()
    {
        var mapTanks = suburb.MapTanks;
        for (var i = 0; i < mapTanks.Count; i++)
        {
            Track(mapTanks[i], Setup.HostId);
        }
        foreach (var body in suburb.DynamicRoot.GetChildren().OfType<RigidBody3D>().Where(b => b.HasMeta(Suburb.PlanIndexMeta)))
        {
            var index = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
            if (plan.Props[index].Model.StartsWith("car:"))
            {
                bodies.Register(BodySync.CarId(index), body, isTank: false, Setup.HostId);
            }
            else
            {
                looseProps[index] = body;
            }
        }

        player.Fired += OnFired;
        player.Ragdolled += OnRagdolled;
        player.GotUp += OnGotUp;
        player.MeleeStarted += OnMeleeStarted;
        player.MeleeHit += OnMeleeHit;
        player.PushedBody += OnPushedBody;

        World.Events.LocalBlast += OnLocalBlast;
        Session.OnUnsubscribing(() =>
        {
            player.Fired -= OnFired;
            player.Ragdolled -= OnRagdolled;
            player.GotUp -= OnGotUp;
            player.MeleeStarted -= OnMeleeStarted;
            player.MeleeHit -= OnMeleeHit;
            player.PushedBody -= OnPushedBody;
            World.Events.LocalBlast -= OnLocalBlast;
        });
    }
    private void OnGotUp(Vector3 root, float yaw) => Send(new NetWriter(Msg.GetUp, Setup.Id).Vec3(root).Float(yaw));
    private void OnMeleeStarted(uint swing, Vector3 direction) => Send(new NetWriter(Msg.MeleeStart, Setup.Id).UInt(swing).Vec3(direction));
    private void OnPushedBody(RigidBody3D body) => Claim(new[] { body });

    private void AcknowledgeReady()
    {
        // The suburb materializes around the player, as in single player.
        suburb.SetRevealSide(1f);
        World.Reveal.Center = player.GlobalPosition;
        World.Reveal.Radius = 0f;
        Warmup.Run(World, World.Root, new Vector3(0, -400f, 0));

        CaptureMouse(true);
        ShowNotice("Waiting for everyone to arrive", 30f);
        // Loaded is sent after the handoff: Countdown, Go or even End may already be waiting in the inbox.
        GD.Print($"[match] seed {plan.Seed}: {plan.Lots.Count} lots, {plan.Tanks.Count} tanks, {pickups.Count} pickups, {Setup.Players.Count} players, host {Setup.HostId}, me {me}");
    }
}
