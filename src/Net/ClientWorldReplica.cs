using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Tank;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// Physical-session replica: body identity, reconstruction, correction, gather and publication integration.
/// Borrows the world and authority transactions; Match stops it before views/actors/native content retire.
/// Network timestamps use the injected monotonic clock. No processing node or independent scheduling.
/// </summary>
internal sealed class ClientWorldReplica : IDisposable
{
    private readonly WorldContext World;
    private readonly IMonotonicClock Clock;
    private readonly int me, matchId;
    private readonly Func<bool> playing;
    private readonly Func<int, uint> actorLife;
    private readonly Action<byte[]> sendBytes;
    private readonly Dictionary<uint, PropaneTank> tanks = new();
    private readonly HashSet<uint> exploded = new();
    private readonly HashSet<uint> predictedExplosions = new();
    private readonly Dictionary<int, RigidBody3D> looseProps = new();
    private readonly List<AmmoPickup> pickups = new();
    private readonly BodySync bodies;
    private readonly BodyPublisher bodyPublisher;
    private ClientAuthorityTransactions authority = null!;
    private Suburb suburb = null!;
    private bool disposed;

    public ClientWorldReplica(WorldContext world, IMonotonicClock clock, int peer, MatchSetup setup,
        Func<bool> playing, Func<int, uint> actorLife, Func<double, double> sampleTime, Action<NetWriter, TrafficClass> send,
        Action<byte[]> sendBytes)
    {
        World = world;
        Clock = clock;
        me = peer;
        matchId = setup.Id;
        this.playing = playing;
        this.actorLife = actorLife;
        this.sendBytes = sendBytes;
        bodies = new BodySync(world.Tuning, peer, setup.HostId);
        bodyPublisher = new BodyPublisher(bodies, setup.Id, sampleTime, send);
    }

    public BodySync Bodies => bodies;
    public IReadOnlyList<AmmoPickup> Pickups => pickups;
    // Retain List's allocation-free enumeration and synchronous mutation/version checks in the actor tick.
    internal List<AmmoPickup>.Enumerator EnumeratePickups() => pickups.GetEnumerator();
    public Dictionary<uint, PropaneTank>.ValueCollection BotTanks => tanks.Values;
    public IEnumerable<PropaneTank> LiveTanks => tanks.Values.Where(t => GodotObject.IsInstanceValid(t) && t.State != PropaneTank.TankState.Exploded);
    public void BindAuthority(ClientAuthorityTransactions transactions) => authority = transactions;
    private static bool IsInstanceValid(GodotObject? value) => GodotObject.IsInstanceValid(value);

    public void BuildPhysicalContent(Suburb content, SuburbPlan plan, bool hasPickups)
    {
        suburb = content;
        var gather = new TankGather
        {
            World = World,
            Name = "Gather",
            Tanks = () => LiveTanks,
            Enabled = () => playing() && bodies.Host == me,
            Grant = GrantGather,
            Publish = PublishGather
        };
        suburb.AddChild(gather);
        for (var i = 0; i < (hasPickups ? plan.AmmoSpots.Count : 0); i++)
        {
            var pickup = new AmmoPickup { Name = $"Ammo_{i}", Index = i };
            suburb.DynamicRoot.AddChild(pickup);
            pickup.Position = PlanCoordinates.ToWorld(plan.AmmoSpots[i]);
            pickups.Add(pickup);
        }
    }

    public void RegisterBodies(SuburbPlan plan, int host)
    {
        var mapTanks = suburb.MapTanks;
        for (var i = 0; i < mapTanks.Count; i++) Track(mapTanks[i], host);
        foreach (var body in suburb.DynamicRoot.GetChildren().OfType<RigidBody3D>().Where(b => b.HasMeta(Suburb.PlanIndexMeta)))
        {
            var index = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
            if (plan.Props[index].Model.StartsWith("car:")) bodies.Register(BodyIds.CarId(index), body, isTank: false, host);
            else looseProps[index] = body;
        }
    }

    public void Correct(double now) => bodies.Correct(now);
    public void PublishBodies(double now, IReadOnlySet<uint> pendingCreation) => bodyPublisher.Publish(now, pendingCreation);
    public bool HasTank(uint id) => tanks.ContainsKey(id);
    public bool Exploded(uint id) => exploded.Contains(id);
    public PropaneTank? RestingIntactTank(BodyAuthority body) => body.State == BodyState.State.Intact && body.Rest &&
        tanks.TryGetValue(body.Body.Id, out var tank) && IsInstanceValid(tank) ? tank : null;
    public void AddTank(PropaneTank tank) => suburb.AddTank(tank);
    public void TrackTank(PropaneTank tank, int owner) => Track(tank, owner);
    public RigidBody3D? LooseProp(int index) => looseProps.GetValueOrDefault(index);
    public void RemovePredictedTank(uint id)
    {
        if (!tanks.Remove(id, out var refused)) return;
        bodies.Remove(id);
        refused.Punctured -= OnTankPunctured;
        refused.Detonated -= OnTankDetonated;
        refused.QueueFree();
    }
    public void EndGather() { foreach (var tank in LiveTanks) tank.EndGather(); }
    public void ReceiveStates(int sender, BodyStatesMessage states, double now, double transit)
    {
        foreach (var body in states.Bodies) bodies.OnState(sender, body, rest: false, now, transit);
    }
    public void ReceiveRest(int sender, BodyRestMessage rest, double now)
    {
        bodies.OnState(sender, new BodyState(rest.Id, rest.Transform, Vector3.Zero, Vector3.Zero, rest.Epoch, rest.Sequence), rest: true, now);
        if (bodies.Get(rest.Id) is { } rested && rested.Owner == sender && rested.Epoch == rest.Epoch &&
            rested.LastSequence == rest.Sequence && rested.Body is PropaneTank restingTank)
        {
            restingTank.EndGather();
        }
    }

    /// <summary>Starts following a tank: its id, owner and what happens to it here.</summary>
    private void Track(PropaneTank tank, int owner, bool replace = false)
    {
        tanks[tank.NetId] = tank;
        if (replace) { bodies.ReplaceBody(tank.NetId, tank); }
        else { bodies.Register(tank.NetId, tank, isTank: true, owner); }
        tank.Punctured += OnTankPunctured;
        tank.Detonated += OnTankDetonated;
    }

    private void OnTankPunctured(PropaneTank tank)
    {
        var request = authority.NewRequest();
        var puncture = new PunctureMessage(tank.NetId, tank.GlobalTransform, tank.LinearVelocity, tank.AngularVelocity,
            tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign, Request: request, Cause: tank.ChainCause);
        authority.Puncture(puncture, tank.CauseActorId, actorLife(tank.CauseActorId));
    }

    private void OnTankDetonated(PropaneTank tank)
    {
        if (tank.DetonatedRemotely)
        {
            tanks.Remove(tank.NetId);
            bodies.Remove(tank.NetId);
            exploded.Add(tank.NetId);
            return;
        }
        // Keep the registry/epoch until the server resolves the prediction. A refusal can recreate this body.
        predictedExplosions.Add(tank.NetId);
        var request = authority.NewRequest();
        tank.BlastRequest = request;
        var explosion = new ExplodeMessage(tank.NetId, tank.GlobalTransform * PropaneTank.LocalCenter,
            Request: request, Cause: tank.ChainCause);
        authority.Explode(explosion, tank.CauseActorId, actorLife(tank.CauseActorId));
    }

    internal void OnLocalBlast(Vector3 center, IReadOnlyList<RigidBody3D> pushed, uint cause)
    {
        if (!World.IsAlive) { return; }
        Claim(pushed, cause);
        // Child punctures are queued during ChainHit, before the blast applies its impulse/spin.
        // Flush even if every pushed body is already owned or pending and Claim has nothing to send.
        authority.Flush();
    }

    /// <summary>One atomic request for all disturbed bodies, with each body's expected epoch and proposed pose.</summary>
    internal uint Claim(IEnumerable<GodotObject> disturbed, uint cause = 0, bool atomic = false, bool predictMotion = true)
    {
        var claims = atomic ? bodies.ClaimGroup(disturbed, predictMotion) : bodies.Claim(disturbed);
        return authority.Claim(claims, cause);
    }

    private void GrantGather(IReadOnlyList<PropaneTank> members, Action<Func<PropaneTank, bool>?> done) =>
        authority.GrantGather(bodies.ClaimGroup(members, predictMotion: false),
            granted => done(granted == null ? null : tank => granted(tank.NetId)));

    private void PublishGather(PropaneTank tank, GatherPhase phase)
    {
        if (!playing() || bodies.Host != me || bodies.Get(tank.NetId) is not { } entry || entry.Owner != me || entry.ClaimPending ||
            tank.State != PropaneTank.TankState.Intact) { return; }
        var state = BodySync.Capture(entry) with { Sequence = checked(++entry.Sequence) };
        sendBytes(MessageCodec.Encode(new NetMessage(Msg.Gather, matchId, 0, new GatherMessage(phase, new[] { state })), fromServer: false));
    }

    public void ReceiveGather(int sender, GatherMessage message)
    {
        foreach (var state in message.Bodies)
        {
            if (bodies.Get(state.Id) is not { } entry || entry.Owner != sender || entry.Epoch != state.Epoch ||
                state.Sequence <= entry.LastGatherSequence || entry.Body is not PropaneTank tank || tank.State != PropaneTank.TankState.Intact) { continue; }
            entry.LastGatherSequence = state.Sequence;
            bodies.OnState(sender, state, message.Phase == GatherPhase.Settled, Clock.Seconds);
            if (message.Phase == GatherPhase.Glide) { tank.BeginGather(); }
            else
            {
                tank.EndGather();
                if (message.Phase == GatherPhase.Settled)
                {
                    if (state.Sequence >= entry.LastSequence)
                    {
                        BodySync.Snap(tank, state.Transform, Vector3.Zero, Vector3.Zero);
                        tank.Sleeping = true;
                    }
                    tank.MarkPile(state.Transform);
                }
            }
        }
    }

    public BodySync.Entry? FindBody(uint id) => bodies.Get(id);

    public BodyState? CurrentProposal(uint id) => bodies.Get(id) is { } entry && IsInstanceValid(entry.Body)
        ? BodySync.Capture(entry) : null;


    public void CancelRoot(uint request)
    {
        // Already owned venting neighbors need no Claim, but can still have this root's fuse.
        foreach (var tank in LiveTanks.Where(t => t.ChainCause == request)) { tank.CancelChainFuse(); }
    }

    public (BodyAuthority Authority, BodySync.Entry? Entry)? Adopt(BodyAuthority authority, bool gatherGrant)
    {
        var id = authority.Body.Id;
        if (exploded.Contains(id)) { return null; }
        var entry = bodies.Get(id);
        if (!gatherGrant && entry != null && authority.Body.Epoch > entry.Epoch &&
            entry.Body is PropaneTank changed && IsInstanceValid(changed)) { changed.Disturb(); }
        if (entry != null && !bodies.OnAuthority(authority, out authority, Clock.Seconds)) { return null; }
        return (authority, entry);
    }

    public void Explode(BodyAuthority authority)
    {
        var id = authority.Body.Id;
        if (predictedExplosions.Remove(id))
        {
            tanks.Remove(id);
            bodies.Remove(id);
            exploded.Add(id);
        }
        else { PlayExplosion(id, authority.Center); }
    }

    public void Restore(BodyAuthority authority, BodySync.Entry? entry, ClaimRefusal reason, bool finishedPuncture)
    {
        var id = authority.Body.Id;
        if (id >= BodyIds.CarBase)
        {
            if (entry != null && reason != ClaimRefusal.None)
            {
                BodySync.Snap(entry.Body, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);
                entry.Body.Sleeping = authority.Rest;
            }
            return;
        }
        var recreated = !tanks.TryGetValue(id, out var tank) || !IsInstanceValid(tank) || tank.State == PropaneTank.TankState.Exploded;
        if (recreated)
        {
            var pilePose = tank?.PilePose;
            if (tank != null && IsInstanceValid(tank) && !tank.IsQueuedForDeletion()) { tank.QueueFree(); }
            tank = PropaneTank.Create(World);
            tank.NetId = id;
            suburb.AddTank(tank);
            if (pilePose is { } pose) { tank.MarkPile(pose); }
            Track(tank, authority.Owner, replace: entry != null);
            if (entry == null) { bodies.OnAuthority(authority, Clock.Seconds); }
        }
        if (reason != ClaimRefusal.None) { tank!.CancelChainFuse(); }
        var wasIntact = tank!.State == PropaneTank.TankState.Intact;
        if (authority.State == BodyState.State.Intact) { tank.RestoreIntact(); }
        // A losing puncture changes the vent, not its current trajectory. The confirmed owner's stream
        // corrects motion normally; the later refusal must not rewind adoption to the cached claim pose.
        var adoptingVent = finishedPuncture && !recreated && !wasIntact && authority.State == BodyState.State.Venting;
        // A remote root blast can already have pushed an intact copy before its canonical child puncture.
        // The grant's pose predates that push. Keep existing motion until the confirmed owner's stream arrives;
        // only a resting intact observer needs initialization from the puncture's pose.
        var initializeVent = wasIntact && authority.State == BodyState.State.Venting && tank.Sleeping;
        if (recreated || initializeVent || reason != ClaimRefusal.None && !adoptingVent)
        {
            BodySync.Snap(tank, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);
            tank.Sleeping = authority.Rest;
        }
        if (authority.Vent is { } vent && authority.State == BodyState.State.Venting) { tank.PunctureRemote(vent.Hole, vent.Normal, vent.Swirl); }
        predictedExplosions.Remove(id);
    }

    public void RemoveUnknown(uint id)
    {
        bodies.Remove(id);
        if (tanks.Remove(id, out var unknown) && IsInstanceValid(unknown)) { unknown.QueueFree(); }
        predictedExplosions.Remove(id);
    }


    private void PlayExplosion(uint id, Vector3 center)
    {
        if (tanks.TryGetValue(id, out var known) && IsInstanceValid(known) && known.State != PropaneTank.TankState.Exploded)
        {
            known.DetonateRemote(center);
        }
        else
        {
            tanks.Remove(id);
            bodies.Remove(id);
            exploded.Add(id);
            var ground = World.Physics.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(center, center + Vector3.Down * 30f, Layers.World | Layers.Floor));
            ExplosionEffect.Create(World, center, ground.Count > 0 ? ground["position"].AsVector3().Y : 0f);
            Blast.Detonate(World, center, null, chain: false);
        }
    }


    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var tank in tanks.Values.Where(IsInstanceValid))
        {
            tank.Punctured -= OnTankPunctured;
            tank.Detonated -= OnTankDetonated;
        }
        bodies.Clear();
        tanks.Clear();
        looseProps.Clear();
        pickups.Clear();
    }
}
