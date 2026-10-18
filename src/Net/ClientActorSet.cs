using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Net;

/// <summary>Client actor attachments for one physical session. Entries retire synchronously through ActorRegistration.
/// Reads no input; Match invokes snapshot application and publication at the original physics boundaries.</summary>
internal sealed class ClientActorSet
{
    [Flags]
    private enum Impact : byte { None = 0, Hit = 1, Metal = 2, Decal = 4 }
    private readonly Dictionary<int, ClientActorReplication> replicas = new();
    private readonly HashSet<uint> unconfirmedDrops = new();
    private readonly WorldContext World;
    private readonly IMonotonicClock Clock;
    private readonly IReadOnlyList<ActorRuntime> actors;
    private readonly Func<int, PlayerCharacter?> Actor;
    private readonly Func<PlayerCharacter?> selected;
    private readonly Func<bool> playing;
    private readonly Func<IModeClient> clientMode;
    private readonly ClientWorldReplica worldReplica;
    private readonly int matchId, me;
    private readonly Action<byte[], TrafficClass> sendBytes;
    private readonly Action<NetWriter, TrafficClass> send;
    private readonly Action<PlayerCharacter> hitMarker;
    private readonly Action<PlayerCharacter, Vector3> hitDirection;
    private readonly Action<PlayerCharacter, int> ammoPopup;
    private readonly Action<PlayerCharacter?, string> tuningChanged;
    private readonly Func<double, double, double> transitAge;

    public ClientActorSet(WorldContext world, IMonotonicClock clock, IReadOnlyList<ActorRuntime> actors,
        Func<int, PlayerCharacter?> actor, Func<PlayerCharacter?> selected, Func<bool> playing, Func<IModeClient> clientMode,
        ClientWorldReplica worldReplica, int matchId, int peer, Action<byte[], TrafficClass> sendBytes,
        Action<NetWriter, TrafficClass> send, Action<PlayerCharacter> hitMarker, Action<PlayerCharacter, Vector3> hitDirection,
        Action<PlayerCharacter, int> ammoPopup, Action<PlayerCharacter?, string> tuningChanged, Func<double, double, double> transitAge)
    {
        World = world;
        Clock = clock;
        this.actors = actors;
        Actor = actor;
        this.selected = selected;
        this.playing = playing;
        this.clientMode = clientMode;
        this.worldReplica = worldReplica;
        this.matchId = matchId;
        me = peer;
        this.sendBytes = sendBytes;
        this.send = send;
        this.hitMarker = hitMarker;
        this.hitDirection = hitDirection;
        this.ammoPopup = ammoPopup;
        this.tuningChanged = tuningChanged;
        this.transitAge = transitAge;
    }

    public ClientActorReplication Add(PlayerCharacter actor)
    {
        var replica = new ClientActorReplication(actor);
        replicas.Add(actor.ActorId, replica);
        return replica;
    }
    public ClientActorReplication Get(ActorRuntime actor) => replicas[actor.Character.ActorId];
    public void Remove(PlayerCharacter actor)
    {
        if (replicas.Remove(actor.ActorId, out var replica)) replica.Dispose();
    }
    public void ApplySnapshots(double now)
    {
        foreach (var actor in actors.Where(a => a.Character.IsRemote)) Get(actor).ApplySnapshot(now);
    }
    public void PublishActors(float dt, double now)
    {
        foreach (var owned in actors.Where(a => !a.Character.IsRemote))
        {
            var replica = Get(owned);
            replica.SendTimer -= dt;
            if (replica.SendTimer > 0) continue;
            replica.SendTimer = Mathf.Max(0, replica.SendTimer + 1f / Mathf.Max(World.Tuning.PlayerSendRate, 1f));
            SendPlayerState(owned.Character, now);
        }
    }
    public IReadOnlySet<uint> PendingDrops()
    {
        unconfirmedDrops.Clear();
        foreach (var actor in actors.Where(a => !a.Character.IsRemote)) unconfirmedDrops.UnionWith(Get(actor).DropPredictions.PendingIds);
        return unconfirmedDrops;
    }
    public NetWriter ActorWriter(Msg type, PlayerCharacter actor) =>
        new(type, matchId, actor.ActorId, actor.LifeId, checked(++Get(actor.Runtime).ActionSequence));

    public void SubscribeOwned(PlayerCharacter actor)
    {
        var replica = Get(actor.Runtime);
        void Fired(ShotReport shot) { if (replica.IsSubscribed) OnFired(actor, shot); }
        void Ragdolled(Vector3 at, Vector3 velocity, Vector3 spin) { if (replica.IsSubscribed) OnRagdolled(actor, at, velocity, spin); }
        void GotUp(Vector3 root, float yaw) { if (replica.IsSubscribed) send(ActorWriter(Msg.GetUp, actor).Vec3(root).Float(yaw), TrafficClass.ReliableOrdered); }
        void MeleeStarted(uint swing, Vector3 direction) { if (replica.IsSubscribed) SendActor(actor, Msg.MeleeStart, new MeleeStartMessage(swing, direction), predict: true); }
        void MeleeHit(MeleeHit hit) { if (replica.IsSubscribed) OnMeleeHit(actor, hit); }
        void Pushed(RigidBody3D body) { if (replica.IsSubscribed) worldReplica.Claim(new[] { body }); }
        actor.Fired += Fired; actor.Ragdolled += Ragdolled; actor.GotUp += GotUp;
        actor.MeleeStarted += MeleeStarted; actor.MeleeHit += MeleeHit; actor.PushedBody += Pushed;
        replica.Subscribe(() =>
        {
            actor.Fired -= Fired; actor.Ragdolled -= Ragdolled; actor.GotUp -= GotUp;
            actor.MeleeStarted -= MeleeStarted; actor.MeleeHit -= MeleeHit; actor.PushedBody -= Pushed;
        });
    }

    private void OnFired(PlayerCharacter actor, ShotReport shot)
    {
        var impact = Impact.None;
        var victim = 0;
        var propIndex = -1;
        if (shot.Hit)
        {
            impact |= Impact.Hit;
            switch (shot.Collider)
            {
                case PropaneTank:
                    impact |= Impact.Metal;
                    break;
                case PlayerCharacter or PhysicalBone3D:
                    if (PlayerCharacter.Owning(shot.Collider) is { } hitPlayer && hitPlayer != actor)
                    {
                        victim = hitPlayer.ActorId;
                        if (actor == selected()) hitMarker(actor);
                    }
                    break;
                case RigidBody3D { Freeze: false } body:
                    if (body is DebrisBody)
                    {
                        impact |= Impact.Metal;
                    }
                    if (worldReplica.Bodies.Find(body) != null)
                    {
                        worldReplica.Claim(new[] { body });
                    }
                    else if (body.HasMeta(Suburb.PlanIndexMeta))
                    {
                        propIndex = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
                    }
                    break;
                default:
                    impact |= Impact.Decal;
                    break;
            }
        }
        SendActor(actor, Msg.Shot, new ShotMessage(shot.End, shot.Normal, shot.Direction, (byte)impact, victim, propIndex,
            Actor(victim)?.LifeId ?? 1), predict: true);
    }

    private void OnMeleeHit(PlayerCharacter actor, MeleeHit hit)
    {
        var victim = 0;
        var propIndex = -1;
        if (hit.Collider is PlayerCharacter target && target != actor)
        {
            victim = target.ActorId;
            if (actor == selected()) hitMarker(actor);
        }
        else if (hit.Collider is RigidBody3D body && worldReplica.Bodies.Find(body) == null && body.HasMeta(Suburb.PlanIndexMeta))
        {
            propIndex = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
        }
        // Tanks and synced cars already took the impulse and use the ordinary ownership/state path.
        if (victim != 0 || propIndex >= 0)
            SendActor(actor, Msg.MeleeContact, new MeleeContactMessage(hit.Swing, victim, propIndex, Actor(victim)?.LifeId ?? 1), predict: true);
    }

    private void OnRagdolled(PlayerCharacter actor, Vector3 at, Vector3 velocity, Vector3 spin)
    {
        send(ActorWriter(Msg.Ragdoll, actor).Vec3(at).Vec3(velocity).Vec3(spin), TrafficClass.ReliableOrdered);
        if (!World.IsAlive || Actor(actor.ActorId) == null) return;
        clientMode().Thrown(actor, at);
    }

    private void OnHitByShot(PlayerCharacter victim, Vector3 shooterPosition)
    {
        if (victim == selected()) hitDirection(victim, shooterPosition);
        clientMode().HitByShot(victim);
    }

    public void CheckPickups()
    {
        foreach (var actor in actors.Where(a => !a.Character.IsRemote)) CheckPickups(actor.Character);
    }
    public void CheckPickups(PlayerCharacter actor)
    {
        if (!playing() || actor.IsRagdolled || actor.Ammo == null)
        {
            return;
        }
        var position = actor.GlobalPosition;
        var near = false;
        using var pickups = worldReplica.EnumeratePickups();
        while (pickups.MoveNext())
        {
            var pickup = pickups.Current;
            if (!pickup.Available || pickup.ClaimPending)
            {
                continue;
            }
            var offset = pickup.GlobalPosition - position;
            if (new Vector2(offset.X, offset.Z).Length() > AmmoPickup.TakeRadius || Mathf.Abs(offset.Y) > 1.6f)
            {
                continue;
            }
            near = true;
            if (actor.Ammo.Reserve >= World.Tuning.MaxReserve)
            {
                if (!Get(actor.Runtime).AmmoFullShown)
                {
                    Get(actor.Runtime).AmmoFullShown = true;
                    if (actor == selected()) ammoPopup(actor, 0);
                }
                continue;
            }
            // It vanishes here at once; the server decides who gets the rounds if two players reach it together.
            pickup.ClaimPending = true;
            sendBytes(ActorWriter(Msg.ClaimPickup, actor).Int(pickup.Index).ToArray(), TrafficClass.ReliableOrdered);
        }
        if (!near)
        {
            Get(actor.Runtime).AmmoFullShown = false;
        }
    }

    public void SendPlayerState(PlayerCharacter actor, double now)
    {
        var sequence = checked(++Get(actor.Runtime).ReplicationSequence);
        var message = new NetMessage(Msg.PlayerState, matchId, 0, new PlayerStateMessage(now, actor.CaptureNetState()), actor.ActorId, actor.LifeId, sequence);
        sendBytes(MessageCodec.Encode(message, fromServer: false), TrafficClass.UnreliablePlayerPoses);
    }

    private void SendActor(PlayerCharacter actor, Msg type, object payload, bool predict = false)
    {
        var message = new NetMessage(type, matchId, me, payload, actor.ActorId, actor.LifeId, checked(++Get(actor.Runtime).ActionSequence));
        if (predict) OnRelay(message);
        sendBytes(MessageCodec.Encode(message, false), TrafficClass.ReliableOrdered);
    }

    public void OnRelay(NetMessage message)
    {
        var now = Clock.Seconds;
        var sender = message.Sender;
        var payload = message.Payload;
        var source = Actor(message.ActorId);
        if (Protocol.IsActorMessage(message.Type))
        {
            if (source == null || source.OwnerPeerId != sender || source.LifeId != message.LifeId) return;
            if (!Get(source.Runtime).Accept(message)) return;
        }
        var remote = source is { IsRemote: true } ? Get(source.Runtime) : null;
        switch (payload)
        {
            case PlayerStateMessage state:
                remote?.Snapshots?.Add(state.Time, state.State, now);
                break;
            case MeleeStartMessage strike:
                if (source != null) Get(source.Runtime).Melee = strike;
                break;
            case MeleeContactMessage contact:
                if (source == null || Get(source.Runtime).Melee is not { } swing || swing.Swing != contact.Swing) break;
                if (Actor(contact.Victim) is { IsRemote: false } victim && victim.LifeId == contact.VictimLifeId)
                {
                    victim.Runtime.LastHitTime = now;
                    victim.Runtime.LastHitBy = message.ActorId;
                    victim.ReceiveMelee(swing.Direction);
                    if (victim == selected() && source != null) hitDirection(victim, source.GlobalPosition);
                }
                else if (sender != me && contact.PropIndex >= 0 && worldReplica.LooseProp(contact.PropIndex) is { } pushed && GodotObject.IsInstanceValid(pushed) && !pushed.Freeze)
                {
                    pushed.ApplyCentralImpulse(swing.Direction * World.Tuning.MeleePropImpulse);
                }

                break;
            case ShotMessage shot:
                remote?.Character.RemoteShot(shot.End);
                var impact = (Impact)shot.Impact;
                if (sender != me && impact.HasFlag(Impact.Hit))
                {
                    ImpactEffect.Spawn(World, shot.End, shot.Normal, impact.HasFlag(Impact.Metal), impact.HasFlag(Impact.Decal));
                }
                if (sender != me && shot.PropIndex >= 0 && worldReplica.LooseProp(shot.PropIndex) is { } prop && GodotObject.IsInstanceValid(prop) && !prop.Freeze)
                {
                    prop.ApplyImpulse(shot.Direction * World.Tuning.BulletImpulse, shot.End - prop.GlobalPosition);
                }
                if (Actor(shot.Victim) is { IsRemote: false } hitVictim && hitVictim.LifeId == shot.VictimLifeId && source != null)
                {
                    hitVictim.Runtime.LastHitTime = now;
                    hitVictim.Runtime.LastHitBy = message.ActorId;
                    OnHitByShot(hitVictim, source.GlobalPosition + Vector3.Up * 1.4f);
                }
                break;
            case BodyStatesMessage states:
                var transit = transitAge(now, states.Time);
                worldReplica.ReceiveStates(sender, states, now, transit);
                break;
            case BodyRestMessage rest:
                worldReplica.ReceiveRest(sender, rest, now);
                break;
            case GatherMessage gathering:
                worldReplica.ReceiveGather(sender, gathering);
                break;
            case RagdollMessage ragdoll:
                remote?.Character.RemoteRagdoll(ragdoll.Position, ragdoll.Velocity, ragdoll.Spin);
                break;
            case GetUpMessage getUp:
                remote?.Character.RemoteGetUp(getUp.Position, getUp.Yaw);
                break;
            case TuningMessage tune:
                World.Tuning.Apply(new Godot.Collections.Dictionary { [tune.Name] = tune.Value });
                tuningChanged(remote?.Character, tune.Name);
                break;
        }
    }
}
