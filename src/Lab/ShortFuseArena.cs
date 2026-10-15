using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Lab;

/// <summary>
/// Short Fuse's presenter: start points spread around the suburb's rim, and what death looks like. A dead body goes
/// limp, lies for a moment and leaves the round to a hidden ledge far below; the local player then watches whoever
/// the rules say (<see cref="ShortFuseRules.SpectateTarget"/>). It holds no rules.
/// </summary>
public partial class ShortFuseArena : Node3D
{
    /// <summary>Where the dead wait, far below the slab and out of every blast's reach.</summary>
    private static readonly Vector3 Ledge = new(0, -600, 0);

    public required LabSession Lab { get; init; }
    private readonly List<(Vector3 Position, float Yaw)> spawns = new();
    private readonly List<PlayerCharacter> parked = new();
    private PrototypeRunner? runner;

    public IReadOnlyList<(Vector3 Position, float Yaw)> Spawns => spawns;
    /// <summary>Bodies that have left the round, for tests.</summary>
    public IReadOnlyList<PlayerCharacter> Parked => parked;

    /// <summary>Lays out the round: one start per player around the rim, assigned by round so nobody starts in the same place twice running.</summary>
    public void Layout(SuburbPlan plan, int count, uint round)
    {
        spawns.Clear();
        var points = RimSpawns(plan, count, (ulong)plan.Seed, Lab.World.Tuning.MpStartSpacing);
        for (var i = 0; i < count; i++)
        {
            var p = points[(i + (int)round - 1) % points.Count];
            var center = plan.Bounds.GetCenter();
            spawns.Add((Suburb.ToWorld(p, 0.05f), Mathf.Atan2(p.X - center.X, p.Y - center.Y)));
        }
        var ledge = new StaticBody3D { Name = "SpectatorLedge", CollisionLayer = Layers.World | Layers.Floor, CollisionMask = 0, Position = Ledge };
        ledge.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 1, 60) }, Position = Vector3.Down * 0.5f });
        AddChild(ledge);
    }

    public (Vector3 Position, float Yaw) Spawn(int index) => spawns[index % spawns.Count];

    /// <summary>
    /// Start points on the roads nearest the slab's rim, clear of cars and tanks, spread as far apart as they can be:
    /// the first at a seeded angle on the outermost band, each next one farthest from those chosen. Pure over the plan.
    /// </summary>
    public static IReadOnlyList<Vector2> RimSpawns(SuburbPlan plan, int count, ulong seed, float spacing)
    {
        var center = plan.Bounds.GetCenter();
        var inner = plan.Bounds.Grow(-4f);
        var cars = plan.Props.Where(p => p.Model.StartsWith("car:")).Select(p => p.Position).ToList();
        var candidates = new List<Vector2>();
        foreach (var road in plan.Roads)
        {
            for (var t = 2f; t <= road.Length - 2f; t += 2f)
            {
                var p = road.A + road.Direction * t;
                if (inner.HasPoint(p) && cars.All(c => c.DistanceTo(p) > 5f) && plan.Tanks.All(k => k.Position.DistanceTo(p) > 8f)) candidates.Add(p);
            }
        }
        if (candidates.Count == 0) candidates.Add(plan.Spawn);
        // The rim: how far a candidate is toward the slab's edge, as a fraction of the half-size along its direction.
        var half = plan.Bounds.Size / 2;
        float Rim(Vector2 p) => Mathf.Max(Mathf.Abs(p.X - center.X) / half.X, Mathf.Abs(p.Y - center.Y) / half.Y);
        var outer = candidates.Max(Rim);
        var band = candidates.Where(p => Rim(p) >= outer * 0.7f).ToList();
        if (band.Count < count) band = candidates;
        var random = new ModeRandom(seed);
        var angle = random.Unit() * Mathf.Tau;
        var aim = center + new Vector2(Mathf.Cos((float)angle), Mathf.Sin((float)angle)) * half.Length();
        var chosen = new List<Vector2> { band.OrderBy(p => p.DistanceTo(aim)).First() };
        while (chosen.Count < count)
        {
            // Prefer the rim, but a large roster may exhaust its well-separated starts. The other clear road
            // points can still keep actors apart; widening the candidates changes no suburb geometry or supply.
            var open = band.Where(p => !chosen.Contains(p)).OrderByDescending(p => chosen.Min(c => c.DistanceTo(p))).ToList();
            if (open.Count == 0 || chosen.Min(c => c.DistanceTo(open[0])) < spacing)
                open = candidates.Where(p => !chosen.Contains(p)).OrderByDescending(p => chosen.Min(c => c.DistanceTo(p))).ToList();
            // A tiny suburb can still run out of clear points; preserve the existing repeat fallback.
            chosen.Add(open.Count > 0 ? open[0] : chosen[chosen.Count % band.Count]);
        }
        return chosen;
    }

    public void Bind(PrototypeRunner bound)
    {
        runner = bound;
        runner.Decided += command =>
        {
            if (command is ActorDied died && Lab.Session.Actor(died.VictimId) is { } actor) Die(actor.Character);
        };
    }

    /// <summary>The body goes limp where it is, lies for a moment, then leaves for the ledge, hidden.</summary>
    private void Die(PlayerCharacter actor)
    {
        actor.InputLocked = true;
        // Deferred: a death can be declared inside a physics callback of the very body being moved.
        Lab.World.Defer(() =>
        {
            if (IsInstanceValid(actor)) actor.Collapse();
        });
        Lab.World.Schedule(this, Lab.World.Tuning.ShortFuseCorpseSeconds, () =>
        {
            if (!IsInstanceValid(actor)) return;
            parked.Add(actor);
            actor.Teleport(Ledge + new Vector3((parked.Count - 1) * 2.5f - 10f, 0.05f, 0), 0);
            actor.Visible = false;
        });
    }

    public override void _Process(double delta)
    {
        if (runner?.Rules is not ShortFuseRules rules) return;
        var player = Lab.Player;
        if (!parked.Contains(player)) return;
        var target = rules.SpectateTarget(player.ActorId);
        player.Spectating = target != 0 && target != player.ActorId ? Lab.Session.Actor(target)?.Character : null;
    }
}
