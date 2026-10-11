using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.World.Plan;

namespace Propane.Tank;

/// <summary>One detector and glide runner for both modes. Only granting a lease and publishing presentation differ.</summary>
public partial class TankGather : Node3D
{
    private sealed class Flight
    {
        public required PropaneTank Tank;
        public required Transform3D From;
        public required Transform3D To;
        public required ulong Revision;
        public required Func<bool> Owns;
        public required Godot.Collections.Array<Rid> Exclude;
        public float Age;
    }

    private readonly GatherGroups groups = new();
    private readonly List<Flight> flights = new();
    private readonly HashSet<ulong> pending = new();
    private float scanAge;
    public Func<IEnumerable<PropaneTank>> Tanks { get; set; } = () => Array.Empty<PropaneTank>();
    public Func<bool> Enabled { get; set; } = () => true;
    // The callback receives a per-tank epoch lease after the complete atomic grant, or null on refusal.
    public Action<IReadOnlyList<PropaneTank>, Action<Func<PropaneTank, bool>?>> Grant { get; set; } = (_, done) => done(_ => true);
    public Action<PropaneTank, GatherPhase> Publish { get; set; } = (_, _) => { };
    public int Completed { get; private set; }
    public int Interrupted { get; private set; }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        var tuning = Tuning.Current;
        foreach (var flight in flights.ToArray())
        {
            var tank = flight.Tank;
            if (!Enabled() || !IsInstanceValid(tank) || !tank.Gathering || tank.State != PropaneTank.TankState.Intact ||
                tank.Disturbance != flight.Revision || !flight.Owns())
            {
                Cancel(flight);
                continue;
            }
            flight.Age += dt;
            var progress = Mathf.Min(flight.Age / tuning.GatherDuration, 1);
            var from = flight.From * PropaneTank.LocalCenter;
            var to = flight.To * PropaneTank.LocalCenter;
            var center = GatherGroups.Path(from, to, tuning.GatherLift, progress);
            if (!ClearSegment(tank.GlobalTransform * PropaneTank.LocalCenter, center, flight.Exclude))
            {
                tank.Disturb();
                Cancel(flight);
                continue;
            }
            var rotation = flight.From.Basis.GetRotationQuaternion().Slerp(flight.To.Basis.GetRotationQuaternion(), progress * progress * (3 - 2 * progress));
            var basis = new Basis(rotation);
            var previous = tank.GlobalPosition;
            tank.GlobalTransform = new Transform3D(basis, center - basis * PropaneTank.LocalCenter);
            tank.LinearVelocity = (tank.GlobalPosition - previous) / dt;
            tank.AngularVelocity = Vector3.Zero;
            tank.Sleeping = false;
            if (progress >= 1)
            {
                tank.Disturbed -= OnDisturbed;
                tank.EndGather();
                tank.GlobalTransform = flight.To;
                tank.LinearVelocity = Vector3.Zero;
                tank.AngularVelocity = Vector3.Zero;
                tank.Sleeping = true;
                tank.MarkPile();
                flights.Remove(flight);
                Completed++;
                Publish(tank, GatherPhase.Settled);
            }
        }
        scanAge += dt;
        if (scanAge < tuning.GatherScanInterval) { return; }
        var elapsed = scanAge;
        scanAge = 0;
        var live = Tanks().Where(IsInstanceValid).ToDictionary(t => t.GetInstanceId());
        var samples = live.Values.Select(t => new GatherGroups.Sample(t.GetInstanceId(), t.GlobalTransform * PropaneTank.LocalCenter,
            Enabled() && Eligible(t) && !pending.Contains(t.GetInstanceId()), t.Disturbance));
        foreach (var group in groups.Advance(samples, tuning.GatherRadius, tuning.GatherMinimum, tuning.GatherDelay, elapsed))
        {
            var members = group.Select(s => live[s.Id]).ToArray();
            if (Plan(members) is not { } targets) { continue; }
            foreach (var tank in members) { pending.Add(tank.GetInstanceId()); }
            var revisions = members.Select(t => t.Disturbance).ToArray();
            var pendingIds = group.Select(s => s.Id).ToArray();
            Grant(members, owns =>
            {
                foreach (var id in pendingIds) { pending.Remove(id); }
                if (!IsInsideTree() || !Enabled() || owns == null || members.Where((t, i) => !IsInstanceValid(t) || !Eligible(t) || t.Disturbance != revisions[i] || !owns(t)).Any()) { return; }
                // Revalidate after network latency: actors and obstacles may have moved while the grant was pending.
                if (!SameGroup(members) || !Validate(members, targets)) { return; }
                var exclude = Exclude(members);
                for (var i = 0; i < members.Length; i++)
                {
                    var tank = members[i];
                    flights.Add(new Flight
                    {
                        Tank = tank,
                        From = tank.GlobalTransform,
                        To = targets[i],
                        Revision = tank.Disturbance,
                        Owns = () => owns(tank),
                        Exclude = exclude
                    });
                    tank.Disturbed += OnDisturbed;
                    tank.BeginGather();
                    Publish(tank, GatherPhase.Glide);
                }
            });
        }
    }

    private bool SameGroup(IReadOnlyList<PropaneTank> members)
    {
        var tuning = Tuning.Current;
        var current = new GatherGroups().Advance(Tanks().Where(IsInstanceValid).Select(t =>
            new GatherGroups.Sample(t.GetInstanceId(), t.GlobalTransform * PropaneTank.LocalCenter, Eligible(t), t.Disturbance)),
            tuning.GatherRadius, tuning.GatherMinimum, 0, 0);
        var ids = members.Select(t => t.GetInstanceId()).Order().ToArray();
        return current.Any(g => g.Select(s => s.Id).SequenceEqual(ids));
    }

    internal static bool Eligible(PropaneTank tank) => tank.State == PropaneTank.TankState.Intact && !tank.Invulnerable &&
        !tank.Gathering && tank.Loose && tank.AtRest;

    private void OnDisturbed(PropaneTank tank)
    {
        var flight = flights.Find(f => f.Tank == tank);
        if (flight != null) { Cancel(flight); }
    }

    private void Cancel(Flight flight)
    {
        if (!flights.Remove(flight)) { return; }
        flight.Tank.Disturbed -= OnDisturbed;
        Interrupted++;
        if (!IsInstanceValid(flight.Tank)) { return; }
        flight.Tank.EndGather();
        Publish(flight.Tank, GatherPhase.Cancel);
    }

    public override void _ExitTree()
    {
        foreach (var flight in flights.ToArray()) { Cancel(flight); }
    }

    private Transform3D[]? Plan(IReadOnlyList<PropaneTank> members)
    {
        var tuning = Tuning.Current;
        // An upright cluster has no dependent upper supports: interrupting one member cannot strand another.
        var local = PileBuilder.Build(PileFormation.Cluster, members.Count,
            new Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing));
        Transform3D[]? chosen = null;
        GatherGroups.SelectCenter(members.Select(t => t.GlobalTransform * PropaneTank.LocalCenter).ToArray(),
            tuning.GatherCenterSearch, tuning.GatherSearchRings, tuning.GatherSearchDirections, candidate =>
            {
                var floor = Ground(candidate);
                if (floor == null) { return null; }
                var targets = local.Select(t => new Transform3D(t.Basis, t.Origin + floor.Value)).ToArray();
                if (!Validate(members, targets)) { return null; }
                chosen = targets;
                return floor;
            });
        return chosen;
    }

    private Vector3? Ground(Vector3 at)
    {
        var tuning = Tuning.Current;
        var hit = GroundHit(at);
        return hit.Count > 0 && hit["normal"].AsVector3().Y >= tuning.GatherGroundNormal ? hit["position"].AsVector3() : null;
    }

    private Godot.Collections.Dictionary GroundHit(Vector3 at)
    {
        var reach = Tuning.Current.GatherGroundRay;
        return GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
            at + Vector3.Up * reach, at + Vector3.Down * reach, Layers.Floor));
    }

    private bool Validate(IReadOnlyList<PropaneTank> members, IReadOnlyList<Transform3D> targets)
    {
        var exclude = Exclude(members);
        var tuning = Tuning.Current;
        var space = GetWorld3D().DirectSpaceState;
        for (var i = 0; i < members.Count; i++)
        {
            var target = targets[i];
            var ground = Ground(target.Origin);
            if (ground == null || Mathf.Abs(ground.Value.Y - target.Origin.Y) > tuning.GatherGroundTolerance) { return false; }
            if (space.IntersectShape(new PhysicsShapeQueryParameters3D
            {
                Shape = GameAssets.TankShape,
                Transform = target,
                Exclude = exclude,
                CollisionMask = Obstacles
            }, 1).Count != 0) { return false; }
            var from = members[i].GlobalTransform * PropaneTank.LocalCenter;
            var to = target * PropaneTank.LocalCenter;
            var height = Mathf.Max(from.Y, to.Y) + tuning.GatherLift;
            var raisedFrom = new Vector3(from.X, height, from.Z);
            var raisedTo = new Vector3(to.X, height, to.Z);
            if (!ClearSegment(from, raisedFrom, exclude) || !ClearSegment(raisedFrom, raisedTo, exclude) || !ClearSegment(raisedTo, to, exclude)) { return false; }
        }
        return true;
    }

    private const uint Obstacles = Layers.World | Layers.Props | Layers.Tanks | Layers.Player | Layers.Ragdoll | Layers.Debris;

    private bool ClearSegment(Vector3 from, Vector3 to, Godot.Collections.Array<Rid> exclude)
    {
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = Mathf.Sqrt(PileBuilder.Height * PileBuilder.Height / 4 + PileBuilder.Radius * PileBuilder.Radius) + Tuning.Current.GatherClearance },
            Transform = new Transform3D(Basis.Identity, from),
            Motion = to - from,
            Exclude = exclude,
            CollisionMask = Obstacles
        };
        var space = GetWorld3D().DirectSpaceState;
        if (space.IntersectShape(query, 1).Count > 0) { return false; }
        return space.CastMotion(query)[0] >= 1;
    }

    private Godot.Collections.Array<Rid> Exclude(IEnumerable<PropaneTank> tanks)
    {
        var excluded = new Godot.Collections.Array<Rid>();
        foreach (var tank in tanks)
        {
            excluded.Add(tank.GetRid());
            // The enclosing sweep sphere extends below the tank's foot; ignore its flat support, not houses/props.
            var floor = GroundHit(tank.GlobalPosition);
            if (floor.Count > 0 && !excluded.Contains(floor["rid"].AsRid())) { excluded.Add(floor["rid"].AsRid()); }
        }
        return excluded;
    }
}
