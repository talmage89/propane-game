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
    private sealed class MatchMode(Match match) : ISessionMode
    {
        public void Configure(SessionContext session)
        {
            match.me = match.Net.MyId;
            match.viewBinding = new MatchViewBinding(session, match, match.Net, match.Setup, match.profiles,
                match.CreateLocalView, match.Returning, match.me, () => match.IsPlaying, () => match.ModeView,
                () => match.LeaveRequested?.Invoke(), match.Send);
            session.ActorsChanged += match.viewBinding.RefreshPlayer;
            session.OnUnsubscribing(() => session.ActorsChanged -= match.viewBinding.RefreshPlayer);
            match.mode = Modes.ModeRegistry.Resolve(match.Setup);
            match.viewBinding.Configure(match.mode);
            session.OnStopping(match.StopReplication);
            match.clientMode = Modes.ModeRegistry.CreateClient(match.Setup, match, match);
            match.viewBinding.BindMode(match.ClientMode);
            match.worldReplica = new ClientWorldReplica(session.World, match.Clock, match.me, match.Setup,
                () => match.IsPlaying, id => match.Actor(id)?.LifeId ?? 0, match.Net.BodySampleTime, match.Send,
                bytes => match.Net.Send(bytes));
            match.authority = new ClientAuthorityTransactions(match.me, match);
            match.worldReplica.BindAuthority(match.authority);
            match.actorReplication = new ClientActorSet(session.World, match.Clock, session.Actors, match.Actor,
                () => match.player, () => match.IsPlaying, () => match.ClientMode, match.worldReplica, match.Setup.Id, match.me,
                (bytes, traffic) => match.Net.Send(bytes, traffic), match.Send, match.viewBinding.HitMarker,
                match.viewBinding.HitDirection, match.viewBinding.AmmoPopup, match.viewBinding.TuningChanged,
                (now, time) => match.Net.PingMs > 0 ? now + match.Net.ServerClockOffset - time : 0);
        }
        public void CreateActors(SessionContext session) => match.BuildActors();
        public void CreateViews(SessionContext session)
        {
            match.viewBinding.BuildViews();
            match.ownedBots = new OwnedBotSet(session.World, session.Actors, match.profiles, match.mode.CreateBot,
                match.plan.Bounds, match.Setup, match.Clock, () => match.IsPlaying, () => match.timeLeft, match.ClientMode, match.Scores,
                () => match.worldReplica.BotTanks, match.worldReplica.Pickups);
            match.ownedBots.AttachBots();
            if (match.mode.CreateBot != null) session.OnStopping(match.ownedBots.Dispose);
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
        var environment = new VoidEnvironment { Name = "Void" };
        World.Root.AddChild(environment);
        plan = PlanCodec.Decode(Setup.Plan);
        var settings = Modes.ModeRegistry.Resolve(Setup).Supply.Suburb(World.Tuning, Setup.Players.Count);
        // A void world (the lobby's) has no slab: its plan's content stands on the construct's own floor.
        suburb = Suburb.Build(World, plan, settings, networked: true, ground: Modes.ModeRegistry.Resolve(Setup).World != Modes.ModeWorldSource.Void);
        World.Root.AddChild(suburb);
        World.SetContentRoot(suburb.DynamicRoot);
        viewBinding.AttachContent(environment, suburb);
        if (IsLobby) viewBinding.BuildLobbyContent();

        worldReplica.BuildPhysicalContent(suburb, plan, mode.Supply.Pickups);
    }

    private void BuildActors()
    {
        foreach (var (profile, slot) in Setup.Players)
        {
            viewBinding.AddRosterEntry(profile);
            var spawn = plan.Spawns.Count > 0 ? plan.Spawns[slot % plan.Spawns.Count] : new SpawnPoint(plan.Spawn, plan.SpawnYaw);
            AddActor(profile, slot, PlanCoordinates.ToWorld(spawn.Position, 0.05f), spawn.Yaw + Mathf.Pi);
        }
        if (player == null)
        {
            throw new InvalidOperationException("this player is not in the match");
        }
    }

    /// <summary>Attach one frozen-roster actor or lobby arrival before subscribing its client actions.</summary>
    private PlayerCharacter AddActor(PlayerProfile profile, int slot, Vector3 position, float yaw)
    {
        profiles[profile.Id] = profile;
        ClientMode.ActorAdded(profile.Id);
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
        var replica = actorReplication.Add(character);
        replica.SpawnSlot = slot;
        replica.AllocationSlot = profile.AllocationSlot;
        character.Teleport(position, yaw);
        replica.AttachInput();
        return character;
    }

    private bool LimitedAmmo => ClientMode.LimitedAmmo(mode.Supply.LimitedAmmo);

    private void RegisterReplication()
    {
        worldReplica.RegisterBodies(plan, Setup.HostId);

        foreach (var owned in Session.OwnedActors) SubscribeOwned(owned.Character);
        World.Events.LocalBlast += OnLocalBlast;
        Session.OnUnsubscribing(() => World.Events.LocalBlast -= OnLocalBlast);
    }

    private void SubscribeOwned(PlayerCharacter actor)
    {
        actorReplication.SubscribeOwned(actor);
        Session.SubscribeActor(actor.Runtime, actorReplication.Get(actor.Runtime).Unsubscribe);
    }

    private void RetireActor(PlayerCharacter actor)
    {
        ownedBots?.RetireActor(actor);
        actorReplication.Remove(actor);
        // Finite matches retain departed profiles for standings and in-flight drops; roaming does not.
        if (IsLobby) profiles.Remove(actor.ActorId);
        clientMode?.ActorRemoved(actor.ActorId);
    }

    private void PreparePresentation()
    {
        viewBinding.Prepare();
        // Prepare loading presentation before the handoff, so buffered Countdown/Go/End remain authoritative.
        // The Ready stage schedules Loaded after replay and the first complete frame.
        GD.Print($"[match] seed {plan.Seed}: {plan.Lots.Count} lots, {plan.Tanks.Count} tanks, {Pickups.Count} pickups, {Setup.Players.Count} players, host {Setup.HostId}, me {me}");
    }
}
