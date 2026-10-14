using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Fx;
using Propane.Modes;
using Propane.Tank;
using Propane.World.Plan;

namespace Propane.Lab;

/// <summary>
/// King of the Cul-de-sac in the Lab: lays out the pile spots (in the void a ring with one in the middle; in a suburb
/// the plan's own piles, as a hosted match uses them), draws the zone through the shared <see cref="KingZone"/> from
/// the runner's <see cref="KingState"/>, and builds a fresh pile on clear ground beside each new spot, as a match's
/// world authority does (<see cref="PileSite"/>).
/// </summary>
public partial class KingArena : Node3D
{
    public required LabSession Lab { get; init; }
    private readonly List<Vector3> spots = new();
    private PrototypeRunner? runner;
    private KingZone zone = null!;

    public IReadOnlyList<Vector3> SpotPositions => spots;
    public IReadOnlyList<ModeSpot> Spots => spots.Select(s => new ModeSpot(s.X, s.Y, s.Z)).ToArray();
    /// <summary>Fresh piles that materialized at a new spot.</summary>
    public int Materialized => zone.Materialized;
    /// <summary>Zone moves whose spot had no clear ground for a fresh pile within reach.</summary>
    public int Unplaced { get; private set; }

    /// <summary>Lays out the void's pile spots, or takes a suburb plan's piles. Runs in the scenario's setup.</summary>
    public void Layout(SuburbPlan? suburb)
    {
        var tuning = Lab.World.Tuning;
        if (suburb != null)
        {
            spots.AddRange(KingRules.SpotsFrom(suburb).Select(KingZone.Point));
            return;
        }
        spots.Add(Vector3.Zero);
        for (var i = 0; i < tuning.KingSpots; i++)
        {
            var angle = i * Mathf.Tau / tuning.KingSpots;
            spots.Add(new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.KingSpotRing);
        }
        foreach (var spot in spots) Lab.AddPile(spot, tuning.KingPileCount);
    }

    /// <summary>Where the actors start: around the middle between the ring's spots, facing in.</summary>
    public (Vector3 Position, float Yaw) Spawn(int index, int count, Vector3? suburbSpawn, float suburbYaw)
    {
        if (suburbSpawn is { } start)
            return (start + new Vector3(index % 3 - 1, 0, index / 3) * 2.2f, suburbYaw);
        var tuning = Lab.World.Tuning;
        var angle = (index + 0.5f) * Mathf.Tau / Mathf.Max(1, count) + Mathf.Pi / Mathf.Max(3, tuning.KingSpots);
        var at = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.KingSpotRing * 0.55f + Vector3.Up * 0.05f;
        return (at, Mathf.Atan2(at.X, at.Z));
    }

    public override void _Ready()
    {
        zone = new KingZone
        {
            World = Lab.World,
            Name = "Zone",
            State = () => runner is { IsPlaying: true } ? runner.View as KingState : null,
            ActorColor = id => Lab.Session.Actor(id)?.Character.BodyColor,
        };
        AddChild(zone);
    }

    public void Bind(PrototypeRunner bound)
    {
        runner = bound;
        runner.Decided += command =>
        {
            if (command is ZoneMoved { Zone: > 0 } moved) Materialize(KingZone.Point(moved.Spot));
        };
    }

    /// <summary>A fresh pile builds itself on the clear ground nearest the zone's new spot.</summary>
    private void Materialize(Vector3 spot)
    {
        var tuning = Lab.World.Tuning;
        var actors = Lab.Session.Actors.Select(a => a.Character.GlobalPosition).ToArray();
        if (PileSite.Find(Lab.World, spot, tuning.KingPileCount, tuning.KingPileReach, tuning.KingPileClearance, actors) is not { } site)
        {
            Unplaced++;
            GD.Print($"[king] no clear ground for a fresh pile within {tuning.KingPileReach:0.#} m of {spot}");
            return;
        }
        var tanks = Lab.AddTanks(site);
        foreach (var tank in tanks) runner!.Track(tank);
        zone.Materialize(tanks, PileSite.Center(site));
    }
}
