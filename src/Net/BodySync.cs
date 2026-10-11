using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Net;

/// <summary>
/// Keeps the tanks and cars in a match in step across players. Every player simulates every body. Each body has
/// one owner, whose copy is the truth: the owner sends its body's state while it moves, and a final resting state
/// when it stops; everyone else eases their copy toward the owner's, snapping when far off or solid geometry prevents recovery.
/// <para>
/// Ownership goes to whoever last disturbed a body (a shot, a puncture, a blast, a push or a drop). Claims travel
/// through the server, which grants groups atomically with new epochs or refuses them with canonical state.
/// Prediction is local; only a confirmed owner publishes movement. Epochs and per-body sequences order corrections.
/// The match host owns every body nobody has claimed.
/// </para>
/// </summary>
public sealed class BodySync
{
    public const uint MapTankBase = 0x1000_0000;
    public const uint DropTankBase = 0x2000_0000;
    public const uint CarBase = 0x3000_0000;

    private const double StaleAfter = 0.5;
    internal const double MaxExtrapolation = 0.3;

    public sealed class Entry
    {
        public required uint Id;
        public required ulong InstanceId;
        public required RigidBody3D Body;
        public required bool IsTank;
        public int Owner;
        public bool ClaimPending;
        internal bool ObserveWhilePending;
        public bool WasAwake;

        public bool HasTarget;
        public bool TargetRest;
        internal bool CheckTargetGeometry;
        public Transform3D Target;
        public Vector3 TargetLinear;
        public Vector3 TargetAngular;
        // TargetReceived includes sampling transit for extrapolation; Updated measures owner silence.
        public double TargetReceived;
        public double TargetUpdated;
        public uint Epoch = 1;
        public uint Sequence;
        public uint LastSequence;
        public uint LastGatherSequence;
        public bool PublishRequired;
        public bool PublishReliably;
        internal TrafficClass CheckpointTraffic = TrafficClass.ReliableBodyStates3;
        internal double LastReliablePublication;
        internal (int Sender, BodyState Body, bool IsRest, double Received, double Updated)? Future;
        internal (BodyState Body, bool IsRest)? Latest;
    }

    private readonly Tuning settings;
    private readonly Dictionary<uint, Entry> entries = new();
    private readonly Dictionary<ulong, Entry> byInstance = new();
    private double publicationTime;
    private double tankTimer;
    private double carTimer;

    public BodySync(Tuning tuning, int me, int host)
    {
        settings = tuning;
        Me = me;
        Host = host;
    }

    public void Clear() { entries.Clear(); byInstance.Clear(); }

    public int Me { get; }

    public int Host { get; set; }

    public IEnumerable<Entry> Entries => entries.Values;

    // Optional dev harness observer; ordinary matches allocate no trace strings.
    internal System.Action<Entry, string>? Trace { get; set; }

    public Entry? Get(uint id) => entries.GetValueOrDefault(id);

    public Entry? Find(GodotObject? body) => body is RigidBody3D rigid ? byInstance.GetValueOrDefault(rigid.GetInstanceId()) : null;

    public bool IsMine(Entry entry) => entry.Owner == Me || entry.ClaimPending;

    private bool ObservesOwner(Entry entry) => entry.Owner != Me && (!entry.ClaimPending || entry.ObserveWhilePending);

    internal static void MarkPending(Entry entry, bool predictMotion = true)
    {
        entry.ClaimPending = true;
        entry.ObserveWhilePending = !predictMotion;
        if (predictMotion)
        {
            entry.HasTarget = false;
            if (entry.Body is Tank.PropaneTank tank) { tank.CorrectingMotion = false; }
        }
    }

    public void Register(uint id, RigidBody3D body, bool isTank, int owner)
    {
        Remove(id);
        var entry = new Entry { Id = id, InstanceId = body.GetInstanceId(), Body = body, IsTank = isTank, Owner = owner, WasAwake = !body.Sleeping, PublishRequired = owner == Me };
        entries[id] = entry;
        byInstance[body.GetInstanceId()] = entry;
    }

    public void Remove(uint id)
    {
        if (entries.Remove(id, out var entry))
        {
            byInstance.Remove(entry.InstanceId);
        }
    }

    /// <summary>Rebinds a refused exploded prediction without losing its ordered correction stream.</summary>
    public void ReplaceBody(uint id, RigidBody3D body)
    {
        var entry = entries[id];
        byInstance.Remove(entry.InstanceId);
        entry.Body = body;
        entry.InstanceId = body.GetInstanceId();
        byInstance[entry.InstanceId] = entry;
    }

    /// <summary>
    /// Takes charge of bodies this game just disturbed. Returns the poses to claim from the server (those not already
    /// this player's).
    /// </summary>
    public List<BodyState> Claim(IEnumerable<GodotObject> bodies)
    {
        var claimed = new List<BodyState>();
        foreach (var body in bodies)
        {
            if (Find(body) is not { } entry || entry.Owner == Me) { continue; }
            var pending = entry.ClaimPending;
            // An actual local disturbance supersedes a gather lease that was only observing its owner.
            MarkPending(entry);
            if (pending) { continue; }
            claimed.Add(Capture(entry));
        }
        return claimed;
    }

    /// <summary>Future grouped interactions include bodies already owned, and wait if any member is pending.</summary>
    public List<BodyState> ClaimGroup(IEnumerable<GodotObject> bodies, bool predictMotion = true)
    {
        var group = new Dictionary<uint, Entry>();
        foreach (var body in bodies)
        {
            if (Find(body) is not { } entry || entry.ClaimPending) { return new List<BodyState>(); }
            group[entry.Id] = entry;
        }
        var claims = new List<BodyState>();
        foreach (var entry in group.Values)
        {
            // A gather lease has not moved anything yet: keep its current owner's corrections
            // until the grant. Ordinary predicted impulses retain their existing local authority.
            MarkPending(entry, predictMotion);
            claims.Add(Capture(entry));
        }
        return claims;
    }

    public static BodyState Capture(Entry entry) => new(entry.Id, entry.Body.GlobalTransform,
        entry.Body.LinearVelocity, entry.Body.AngularVelocity, entry.Epoch, entry.Sequence);

    /// <summary>Server-ordered authority. Even the same peer's next grant starts a separate sequence space.</summary>
    public bool OnAuthority(BodyAuthority authority, double? now = null) => OnAuthority(authority, out _, now);

    public bool OnAuthority(BodyAuthority authority, out BodyAuthority resolved, double? now = null)
    {
        resolved = authority;
        // A publication orders motion, not lifecycle. A newer same-epoch pose must not hide a refusal.
        if (!entries.TryGetValue(authority.Body.Id, out var entry) || authority.Body.Epoch < entry.Epoch) { return false; }
        var newEpoch = authority.Body.Epoch > entry.Epoch;
        var priorSequence = entry.LastSequence;
        if (newEpoch)
        {
            entry.Epoch = authority.Body.Epoch;
            entry.Sequence = 0;
            entry.LastSequence = 0;
            entry.LastGatherSequence = 0;
            entry.HasTarget = false;
            entry.PublishRequired = authority.Owner == Me;
            entry.Latest = null;
        }
        entry.Owner = authority.Owner;
        if (entry.Owner == Me && entry.Body is Tank.PropaneTank tank) { tank.CorrectingMotion = false; }
        entry.ClaimPending = false;
        entry.ObserveWhilePending = false;
        entry.Sequence = System.Math.Max(entry.Sequence, authority.Body.Sequence);
        entry.LastSequence = System.Math.Max(entry.LastSequence, authority.Body.Sequence);
        if (entry.Latest is not { } latest || authority.Body.Sequence >= latest.Body.Sequence)
        {
            entry.Latest = (authority.Body, authority.Rest);
        }
        if (entry.Future is { } future && future.Body.Epoch <= entry.Epoch)
        {
            entry.Future = null;
            OnState(future.Sender, future.Body, future.IsRest, future.Updated, future.Updated - future.Received);
        }
        if (entry.Latest is { } newest) { resolved = authority with { Body = newest.Body, Rest = newest.IsRest }; }
        // The reliable grant/refusal is already canonical evidence. Do not wait for the new owner's
        // disposable stream before correcting an observer. An overtaking newer publication keeps its age.
        if (entry.Owner != Me && resolved.Body.Sequence == authority.Body.Sequence &&
            (newEpoch || authority.Body.Sequence > priorSequence || !entry.HasTarget))
        {
            var received = now ?? MonotonicClock.Now;
            SetTarget(entry, resolved.Body, resolved.Rest, received, received);
        }
        Trace?.Invoke(entry, $"authority owner={authority.Owner} epoch={authority.Body.Epoch} sequence={authority.Body.Sequence} rest={authority.Rest}");
        return true;
    }

    /// <summary>A moving or resting state. Ordering uses only the server's epoch and its owner's sequence.</summary>
    public void OnState(int sender, BodyState state, bool rest, double now, double transit = 0)
    {
        if (!entries.TryGetValue(state.Id, out var entry) || state.Epoch < entry.Epoch) { return; }
        var received = now;
        // Backdate moving targets by their estimated sampling age, bounded by our existing extrapolation limit.
        // This adjusts pose prediction only: clocks never decide which epoch or sequence wins.
        if (!rest) { now -= System.Math.Clamp(transit, 0, MaxExtrapolation); }
        if (state.Epoch > entry.Epoch)
        {
            // Unreliable motion can overtake the reliable grant. Retain one latest future state per body.
            if (entry.Future is not { } prior || state.Epoch > prior.Body.Epoch ||
                state.Epoch == prior.Body.Epoch && state.Sequence > prior.Body.Sequence)
            {
                entry.Future = (sender, state, rest, now, received);
            }
            return;
        }
        if (entry.Owner != sender || sender == Me || state.Sequence <= entry.LastSequence) { return; }
        entry.LastSequence = state.Sequence;
        entry.Latest = (state, rest);
        SetTarget(entry, state, rest, now, received);
        Trace?.Invoke(entry, $"receive sender={sender} epoch={state.Epoch} sequence={state.Sequence} rest={rest} transit={transit:0.000}");
    }

    private static void SetTarget(Entry entry, BodyState state, bool rest, double sampled, double received)
    {
        entry.HasTarget = true;
        entry.TargetRest = rest;
        entry.CheckTargetGeometry = !rest;
        entry.Target = state.Transform;
        entry.TargetLinear = rest ? Vector3.Zero : state.Linear;
        entry.TargetAngular = rest ? Vector3.Zero : state.Angular;
        entry.TargetReceived = sampled;
        entry.TargetUpdated = received;
    }

    /// <summary>Puts a body exactly where its owner had it, moving as it was.</summary>
    public static void Snap(RigidBody3D body, Transform3D transform, Vector3 linear, Vector3 angular)
    {
        body.Sleeping = false;
        body.GlobalTransform = transform;
        body.LinearVelocity = linear;
        body.AngularVelocity = angular;
    }

    /// <summary>Eases every copy this player does not own toward its owner's latest state. Call every physics step.</summary>
    public void Correct(double now)
    {
        var tuning = settings;
        var blend = Mathf.Max(tuning.CorrectionBlendTime, 0.02f);
        foreach (var entry in entries.Values)
        {
            if (GodotObject.IsInstanceValid(entry.Body) && entry.Body is Tank.PropaneTank tank)
            {
                tank.CorrectingMotion = entry.HasTarget && ObservesOwner(entry) &&
                    (entry.TargetRest || now - entry.TargetUpdated <= StaleAfter);
            }
            if (!entry.HasTarget || !ObservesOwner(entry) || !GodotObject.IsInstanceValid(entry.Body))
            {
                continue;
            }
            var body = entry.Body;
            var age = now - entry.TargetReceived;
            if (!entry.TargetRest && now - entry.TargetUpdated > StaleAfter)
            {
                // The owner went quiet without a rest state (lost packets): leave the body to local physics.
                Trace?.Invoke(entry, $"stale age={age:0.000} quiet={now - entry.TargetUpdated:0.000}");
                entry.HasTarget = false;
                continue;
            }
            var lead = entry.TargetRest ? 0f : (float)System.Math.Min(age, MaxExtrapolation);
            var targetPosition = entry.Target.Origin + entry.TargetLinear * lead;
            var targetRotation = entry.Target.Basis.GetRotationQuaternion();
            if (lead > 0 && entry.TargetAngular.LengthSquared() > 0.0001f)
            {
                targetRotation = new Quaternion(entry.TargetAngular.Normalized(), entry.TargetAngular.Length() * lead) * targetRotation;
            }
            var current = body.GlobalTransform;
            var error = targetPosition - current.Origin;
            var rotationError = (targetRotation * current.Basis.Orthonormalized().GetRotationQuaternion().Inverse()).Normalized();
            var angle = rotationError.GetAngle();
            if (angle > Mathf.Pi)
            {
                angle -= Mathf.Tau;
            }
            var axis = Mathf.Abs(angle) > 0.0001f ? rotationError.GetAxis() : Vector3.Up;

            if (body is Tank.PropaneTank { Gathering: true })
            {
                // Construct motion is collision-checked by its owner; frozen observers follow the ordinary stream.
                var weight = Mathf.Clamp((float)body.GetPhysicsProcessDeltaTime() / blend, 0, 1);
                body.GlobalTransform = new Transform3D(new Basis(current.Basis.GetRotationQuaternion().Slerp(targetRotation, weight)),
                    current.Origin.Lerp(targetPosition, weight));
                continue;
            }
            if (entry.CheckTargetGeometry)
            {
                entry.CheckTargetGeometry = false;
                // Velocity steering cannot recover a copy trapped across a solid surface. Use the
                // actual owner sample here, not extrapolation through unobserved geometry. One query
                // per accepted snapshot; ordinary free-space corrections retain their smoothing.
                if (body.IsInsideTree() && current.Origin.DistanceSquaredTo(entry.Target.Origin) > .0001f)
                {
                    using var ray = PhysicsRayQueryParameters3D.Create(current.Origin, entry.Target.Origin,
                        Layers.World | Layers.Props | Layers.Floor, new Godot.Collections.Array<Rid> { body.GetRid() });
                    if (body.GetWorld3D().DirectSpaceState.IntersectRay(ray).Count > 0)
                    {
                        Snap(body, entry.Target, entry.TargetLinear, entry.TargetAngular);
                        Trace?.Invoke(entry, $"obstructed-snap age={age:0.000} error={error.Length():0.000}");
                        continue;
                    }
                }
            }
            if (error.Length() > tuning.SnapDistance)
            {
                Snap(body, new Transform3D(new Basis(targetRotation), targetPosition), entry.TargetLinear, entry.TargetAngular);
                Trace?.Invoke(entry, $"snap age={age:0.000} lead={lead:0.000} error={error.Length():0.000}");
            }
            else if (entry.TargetRest && (error.Length() < 0.01f && Mathf.Abs(angle) < 0.01f || age > blend * 3))
            {
                // A rest is the owner's last word until a newer state/epoch or a local claim replaces it.
                // Local collision/solver drift after sleep must not silently become authoritative.
                var pose = new Transform3D(new Basis(targetRotation), targetPosition);
                if (!body.Sleeping || !current.IsEqualApprox(pose))
                {
                    body.GlobalTransform = pose;
                    body.LinearVelocity = Vector3.Zero;
                    body.AngularVelocity = Vector3.Zero;
                    body.Sleeping = true;
                    Trace?.Invoke(entry, "settle");
                }
                continue;
            }
            else
            {
                body.Sleeping = false;
                body.LinearVelocity = entry.TargetLinear + error / blend;
                body.AngularVelocity = entry.TargetAngular + axis * (angle / blend);
            }
        }
    }

    /// <summary>
    /// The states this player owes the others this step: moving tanks and cars at their send rates, and a final
    /// resting state for each that has just stopped.
    /// </summary>
    public (List<Entry> Moving, List<Entry> Rested) Collect(double dt)
    {
        var tuning = settings;
        publicationTime += dt;
        tankTimer -= dt;
        carTimer -= dt;
        var sendTanks = tankTimer <= 0;
        var sendCars = carTimer <= 0;
        if (sendTanks)
        {
            tankTimer += 1.0 / Mathf.Max(tuning.TankSendRate, 0.5f);
            tankTimer = System.Math.Max(tankTimer, 0);
        }
        if (sendCars)
        {
            carTimer += 1.0 / Mathf.Max(tuning.CarSendRate, 0.5f);
            carTimer = System.Math.Max(carTimer, 0);
        }
        var moving = new List<Entry>();
        var rested = new List<Entry>();
        foreach (var entry in entries.Values)
        {
            if (!GodotObject.IsInstanceValid(entry.Body))
            {
                continue;
            }
            // Native frozen gather bodies can sleep while their owner animates the transform.
            // A rest packet ends gather presentation on receivers, so publish motion until completion.
            var awake = !entry.Body.Sleeping || entry.Body is Tank.PropaneTank { Gathering: true };
            if (entry.Owner == Me && !entry.ClaimPending)
            {
                if (awake && (entry.PublishRequired || (entry.IsTank ? sendTanks : sendCars)))
                {
                    // ENet can throttle disposable packets to zero after RTT variance. Checkpoints
                    // share the existing state/sequence stream and survive that suppression. No extra
                    // samples: reserve an ordinary publication at half the correction silence bound.
                    entry.PublishReliably = entry.PublishRequired || publicationTime - entry.LastReliablePublication >= StaleAfter / 2;
                    if (entry.PublishReliably)
                    {
                        entry.LastReliablePublication = publicationTime;
                        // Rotate per body, not per batch: a stable batch layout must not pin a
                        // body behind the retransmission of one older checkpoint.
                        entry.CheckpointTraffic = TrafficReliability.NextBodyCheckpoint(entry.CheckpointTraffic);
                    }
                    entry.PublishRequired = false;
                    moving.Add(entry);
                    Trace?.Invoke(entry, "publish-moving");
                }
                else if (!awake && (entry.PublishRequired || entry.WasAwake))
                {
                    entry.PublishRequired = false;
                    entry.LastReliablePublication = publicationTime;
                    rested.Add(entry);
                    Trace?.Invoke(entry, "publish-rest");
                }
            }
            entry.WasAwake = awake;
        }
        return (moving, rested);
    }

    /// <summary>Id of a map tank by its index in the plan.</summary>
    public static uint MapTankId(int index) => MapTankBase + (uint)index;

    /// <summary>Id of a car by its index in the plan's props.</summary>
    public static uint CarId(int propIndex) => CarBase + (uint)propIndex;

    /// <summary>Id of the n-th tank a player drops; the spawn slot keeps players' ids apart.</summary>
    public static uint DropTankId(int slot, int counter) => DropTankBase + ((uint)slot << 20) + (uint)(counter & 0xFFFFF);
}
