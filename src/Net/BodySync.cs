using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Tank;

namespace Propane.Net;

/// <summary>
/// Keeps the tanks and cars in a match in step across players. Every player simulates every body. Each body has
/// one owner, whose copy is the truth: the owner sends its body's state while it moves, and a final resting state
/// when it stops; everyone else eases their copy toward the owner's, snapping only when far off.
/// <para>
/// Ownership goes to whoever last disturbed a body (a shot, a puncture, a blast, a push or a drop). Claims travel
/// through the server, which echoes each claim to every player including the claimer, so all players apply them in
/// the same order and agree on the owner. Until a claim's echo arrives, the claimer treats the body as its own.
/// The match host owns every body nobody has claimed.
/// </para>
/// </summary>
public sealed class BodySync
{
    public const uint MapTankBase = 0x1000_0000;
    public const uint DropTankBase = 0x2000_0000;
    public const uint CarBase = 0x3000_0000;

    private const double StaleAfter = 0.5;
    private const double MaxExtrapolation = 0.3;

    public sealed class Entry
    {
        public required uint Id;
        public required RigidBody3D Body;
        public required bool IsTank;
        public int Owner;
        public bool ClaimPending;
        public bool WasAwake;

        public bool HasTarget;
        public bool TargetRest;
        public Transform3D Target;
        public Vector3 TargetLinear;
        public Vector3 TargetAngular;
        public double TargetReceived;
        public double LastSenderTime;
    }

    private readonly Dictionary<uint, Entry> entries = new();
    private readonly Dictionary<ulong, Entry> byInstance = new();
    private double tankTimer;
    private double carTimer;

    public BodySync(int me, int host)
    {
        Me = me;
        Host = host;
    }

    public int Me { get; }

    public int Host { get; set; }

    public IEnumerable<Entry> Entries => entries.Values;

    public Entry? Get(uint id) => entries.GetValueOrDefault(id);

    public Entry? Find(GodotObject? body) => body is RigidBody3D rigid ? byInstance.GetValueOrDefault(rigid.GetInstanceId()) : null;

    public bool IsMine(Entry entry) => entry.Owner == Me || entry.ClaimPending;

    public void Register(uint id, RigidBody3D body, bool isTank, int owner)
    {
        var entry = new Entry { Id = id, Body = body, IsTank = isTank, Owner = owner, WasAwake = !body.Sleeping };
        entries[id] = entry;
        byInstance[body.GetInstanceId()] = entry;
    }

    public void Remove(uint id)
    {
        if (entries.Remove(id, out var entry))
        {
            byInstance.Remove(entry.Body.GetInstanceId());
        }
    }

    /// <summary>
    /// Takes charge of bodies this game just disturbed. Returns the ids to claim from the server (those not already
    /// this player's).
    /// </summary>
    public List<uint> Claim(IEnumerable<GodotObject> bodies)
    {
        var claimed = new List<uint>();
        foreach (var body in bodies)
        {
            if (Find(body) is not { } entry || entry.Owner == Me && !entry.ClaimPending)
            {
                continue;
            }
            if (!entry.ClaimPending)
            {
                entry.ClaimPending = true;
                claimed.Add(entry.Id);
            }
            entry.HasTarget = false;
        }
        return claimed;
    }

    /// <summary>A claim, as the server ordered it (including the echo of this player's own).</summary>
    public void OnClaim(int owner, IEnumerable<uint> ids)
    {
        foreach (var id in ids)
        {
            if (entries.TryGetValue(id, out var entry))
            {
                entry.Owner = owner;
                if (owner == Me)
                {
                    entry.ClaimPending = false;
                }
                else if (entry.ClaimPending)
                {
                    // Someone else's claim came after ours: theirs stands. Our echo may still follow and take it back.
                    entry.ClaimPending = false;
                }
            }
        }
    }

    /// <summary>A player left: everything they owned passes to the (possibly new) host.</summary>
    public void OwnerLeft(int left, int newHost)
    {
        Host = newHost;
        foreach (var entry in entries.Values.Where(e => e.Owner == left))
        {
            entry.Owner = newHost;
            entry.HasTarget = false;
        }
    }

    /// <summary>A moving state from a body's owner.</summary>
    public void OnState(int sender, double senderTime, uint id, Transform3D transform, Vector3 linear, Vector3 angular, double now)
    {
        if (!entries.TryGetValue(id, out var entry) || entry.Owner != sender || sender == Me || entry.ClaimPending)
        {
            return;
        }
        if (entry.HasTarget && !entry.TargetRest && senderTime <= entry.LastSenderTime)
        {
            return;
        }
        entry.LastSenderTime = senderTime;
        entry.HasTarget = true;
        entry.TargetRest = false;
        entry.Target = transform;
        entry.TargetLinear = linear;
        entry.TargetAngular = angular;
        entry.TargetReceived = now;
    }

    /// <summary>A body came to rest on its owner's side.</summary>
    public void OnRest(int sender, uint id, Transform3D transform, double now)
    {
        if (!entries.TryGetValue(id, out var entry) || entry.Owner != sender || sender == Me || entry.ClaimPending)
        {
            return;
        }
        entry.HasTarget = true;
        entry.TargetRest = true;
        entry.Target = transform;
        entry.TargetLinear = Vector3.Zero;
        entry.TargetAngular = Vector3.Zero;
        entry.TargetReceived = now;
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
        var tuning = Tuning.Current;
        var blend = Mathf.Max(tuning.CorrectionBlendTime, 0.02f);
        foreach (var entry in entries.Values)
        {
            if (!entry.HasTarget || IsMine(entry) || !GodotObject.IsInstanceValid(entry.Body))
            {
                continue;
            }
            var body = entry.Body;
            var age = now - entry.TargetReceived;
            if (!entry.TargetRest && age > StaleAfter)
            {
                // The owner went quiet without a rest state (lost packets): leave the body to local physics.
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

            if (error.Length() > tuning.SnapDistance)
            {
                Snap(body, new Transform3D(new Basis(targetRotation), targetPosition), entry.TargetLinear, entry.TargetAngular);
            }
            else if (entry.TargetRest && (error.Length() < 0.01f && Mathf.Abs(angle) < 0.01f || age > blend * 3))
            {
                // Settled where the owner's copy settled.
                body.GlobalTransform = new Transform3D(new Basis(targetRotation), targetPosition);
                body.LinearVelocity = Vector3.Zero;
                body.AngularVelocity = Vector3.Zero;
                body.Sleeping = true;
                entry.HasTarget = false;
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
        var tuning = Tuning.Current;
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
            var awake = !entry.Body.Sleeping;
            if (IsMine(entry))
            {
                if (awake && (entry.IsTank ? sendTanks : sendCars))
                {
                    moving.Add(entry);
                }
                else if (!awake && entry.WasAwake)
                {
                    rested.Add(entry);
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
