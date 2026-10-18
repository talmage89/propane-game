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
            session.ActorsChanged += match.RefreshPlayer;
            session.OnUnsubscribing(() => session.ActorsChanged -= match.RefreshPlayer);
            match.mode = match.Setup.Mode;
            match.hudPresenter = match.mode.CreateHud();
            match.bodies = new BodySync(session.World.Tuning, match.me, match.Setup.HostId);
            match.bodyPublisher = new BodyPublisher(match.bodies, match.Setup.Id, match.Net.BodySampleTime, match.Send);
            match.authority = new ClientAuthorityTransactions(match.me, match);
            session.OnStopping(match.StopReplication);
        }
        public void CreateActors(SessionContext session) => match.BuildActors();
        public void CreateViews(SessionContext session)
        {
            match.BuildViews();
            match.AttachBots();
            match.AttachLobby();
        }
        public void RegisterReplication(SessionContext session)
        {
            match.RegisterReplication();
            match.PreparePresentation();
        }
        public void AttachInbox(SessionContext session)
        {
            session.OnDetaching(() => match.Net.DetachMatch(match.Setup.Id, match.OnMessage));
            match.BeforeInboxAttach?.Invoke();
            match.Net.AttachMatch(match.Setup.Id, match.OnMessage);
        }
        public void Ready(SessionContext session)
        {
            match.loadingFrames = 2;
            match.ReadyLobby();
        }
    }

    private void BuildContent()
    {
        environment = new VoidEnvironment { Name = "Void" };
        World.Root.AddChild(environment);
        effects = World.EffectsRoot;
        plan = PlanCodec.Decode(Setup.Plan);
        var settings = Setup.Mode.Supply.Suburb(World.Tuning, Setup.Players.Count);
        // A void world (the lobby's) has no slab: its plan's content stands on the construct's own floor.
        suburb = Suburb.Build(World, plan, settings, networked: true, ground: Setup.Mode.World != Modes.ModeWorldSource.Void);
        World.Root.AddChild(suburb);
        World.SetContentRoot(suburb.DynamicRoot);
        if (IsLobby) BuildLobbyContent();

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

        for (var i = 0; i < (Setup.Mode.Supply.Pickups ? plan.AmmoSpots.Count : 0); i++)
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
            leaderboardRoster.Add(new LeaderboardEntry(profile.Id, profile.Name, profile.Appearance.Color));
            var spawn = plan.Spawns.Count > 0 ? plan.Spawns[slot % plan.Spawns.Count] : new SpawnPoint(plan.Spawn, plan.SpawnYaw);
            AddActor(profile, slot, Suburb.ToWorld(spawn.Position, 0.05f), spawn.Yaw + Mathf.Pi);
        }
        if (player == null)
        {
            throw new InvalidOperationException("this player is not in the match");
        }
    }

    /// <summary>One actor of the frozen roster (or, in the lobby world, one that joined it): owned here or a remote copy.</summary>
    private PlayerCharacter AddActor(PlayerProfile profile, int slot, Vector3 position, float yaw)
    {
        profiles[profile.Id] = profile;
        scores[profile.Id] = 0;
        var owned = profile.OwnerPeerId == me;
        var character = new PlayerCharacter
        {
            World = World,
            Name = $"Actor_{profile.Id}",
            IsRemote = !owned,
            ActorId = profile.Id,
            OwnerPeerId = profile.OwnerPeerId,
            LifeId = profile.LifeId,
            BodyColor = profile.Appearance.Color,
            Ammo = owned && LimitedAmmo ? AmmoState.Starting(World.Tuning) : null,
            InputLocked = owned && phase != Phase.Playing,
        };
        Session.AddActor(character);
        Session.AttachActor(character.Runtime, () => RetireActor(character));
        character.Runtime.SpawnSlot = slot;
        character.Runtime.AllocationSlot = profile.AllocationSlot;
        character.Teleport(position, yaw);
        if (!owned) character.Runtime.IntentSource = new ReplicatedIntent(World.Tuning);
        return character;
    }

    /// <summary>Rifles carry limited ammunition: the mode's policy, which the lobby world's tuner can switch on.</summary>
    private bool LimitedAmmo => mode.Supply.LimitedAmmo || IsLobby && !World.Tuning.LobbyInfiniteAmmo;

    private void BuildViews()
    {
        if (CreateLocalView && player != null)
        {
            var view = Session.AddLocalView(player.Runtime, this, new DeviceInput(World.Tuning));
            environment.Follow = view.CameraRig.Camera;
            hud = view.Hud;
            hud.ShowTankCount(false);
        }
        matchHud = new MatchHud { World = World, Name = "MatchHud" };
        Session.AddView(this, matchHud);
        matchHud.Show(PresentHud(tankGroups, false));
        BuildKingZone();
        results = new ResultsScreen { Name = "Results" };
        Session.AddView(this, results);
        menu = new MatchMenu { Name = "Menu", Lobby = IsLobby };
        menu.ResumeRequested += CloseMenu;
        menu.LeaveRequested += () => World.Defer(() => LeaveRequested?.Invoke(), whilePaused: true);
        Session.AddView(this, menu);
        if (OS.IsDebugBuild() && Setup.CreatorDebug && Setup.CreatorId == me)
        {
            tuningPanel = new TuningPanel
            {
                Target = World.Tuning,
                Name = "Tuning",
                Note = "Changes apply to everyone in the match",
                UseDeclaredRanges = true,
                LockedProperties = new[] { nameof(Tuning.TankCountMultiplier) }
            };
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

        foreach (var owned in Session.OwnedActors) SubscribeOwned(owned.Character);
        World.Events.LocalBlast += OnLocalBlast;
        Session.OnUnsubscribing(() => World.Events.LocalBlast -= OnLocalBlast);
    }

    /// <summary>What an actor this peer owns does goes out to everyone: shots, throws, get-ups, strikes and pushes.</summary>
    private void SubscribeOwned(PlayerCharacter actor)
    {
        var registration = Session.Registration(actor.Runtime);
        void Fired(ShotReport shot) { if (registration.IsSubscribed) OnFired(actor, shot); }
        void Ragdolled(Vector3 at, Vector3 velocity, Vector3 spin) { if (registration.IsSubscribed) OnRagdolled(actor, at, velocity, spin); }
        void GotUp(Vector3 root, float yaw) { if (registration.IsSubscribed) Send(ActorWriter(Msg.GetUp, actor).Vec3(root).Float(yaw)); }
        void MeleeStarted(uint swing, Vector3 direction) { if (registration.IsSubscribed) SendActor(actor, Msg.MeleeStart, new MeleeStartMessage(swing, direction), predict: true); }
        void MeleeHit(MeleeHit hit) { if (registration.IsSubscribed) OnMeleeHit(actor, hit); }
        void Pushed(RigidBody3D body) { if (registration.IsSubscribed) Claim(new[] { body }); }
        actor.Fired += Fired;
        actor.Ragdolled += Ragdolled;
        actor.GotUp += GotUp;
        actor.MeleeStarted += MeleeStarted;
        actor.MeleeHit += MeleeHit;
        actor.PushedBody += Pushed;
        Session.SubscribeActor(actor.Runtime, () =>
        {
            actor.Fired -= Fired; actor.Ragdolled -= Ragdolled; actor.GotUp -= GotUp;
            actor.MeleeStarted -= MeleeStarted; actor.MeleeHit -= MeleeHit; actor.PushedBody -= Pushed;
        });
    }

    private void RetireActor(PlayerCharacter actor)
    {
        RemoveBot(actor);
        botServices?.RemoveActor(actor);
        foreach (var bot in bots)
        {
            bot.Others.Remove(actor);
            bot.Brain.ForgetActor(actor);
        }
        remoteMelee.Remove(actor.ActorId);
        foreach (var key in receivedActions.Keys.Where(key => key.Actor == actor.ActorId).ToArray()) receivedActions.Remove(key);
        // Finite matches retain departed banks/profiles for standings and in-flight drops. Roaming has neither.
        if (IsLobby)
        {
            profiles.Remove(actor.ActorId);
            scores.Remove(actor.ActorId);
        }
    }

    private void PreparePresentation()
    {
        // The suburb materializes around the player, as in single player.
        suburb.SetRevealSide(1f);
        if (player != null) World.Reveal.Center = player.GlobalPosition;
        World.Reveal.Radius = 0f;
        Warmup.Run(World, World.Root, new Vector3(0, -400f, 0));

        CaptureMouse(true);
        if (!IsLobby) ShowNotice("Waiting for everyone to arrive", 30f);
        // Prepare loading presentation before the handoff, so buffered Countdown/Go/End remain authoritative.
        // The Ready stage schedules Loaded after replay and the first complete frame.
        GD.Print($"[match] seed {plan.Seed}: {plan.Lots.Count} lots, {plan.Tanks.Count} tanks, {pickups.Count} pickups, {Setup.Players.Count} players, host {Setup.HostId}, me {me}");
    }
}
