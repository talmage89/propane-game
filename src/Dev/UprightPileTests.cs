using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev;

/// <summary>Generated upright tiers: two untouched minutes, a passing player, gathering and real disturbances.</summary>
public partial class UprightPileTests : Node3D
{
    private sealed record Fixture(string Name, PropaneTank[] Tanks, Transform3D[] Poses, Vector3 WalkStart, bool Shove)
    {
        public string Hit { get; set; } = "";
        public float PeakTilt { get; set; }
    }
    private readonly List<Fixture> fixtures = new();
    private readonly List<PropaneTank> tanks = new();
    private readonly ScriptedInput input = new();
    private readonly HashSet<PropaneTank> regathered = new();
    private WorldContext world = null!;
    private PlayerCharacter player = null!;
    private TankGather gather = null!;
    private PropaneTank[] loose = null!;
    private double elapsed;
    private int walk = -1;
    private Vector3 walkStart;
    private int passes;
    private bool hit, aftermath, gatherSiteCleared;
    private float maxDrift, maxTilt;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DevArgs.Setup();
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        world.SetContentRoot(this);
        ProcessPhysicsPriority = -10;
        InputSetup.Register();
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(6000, .2f, 200) }, Position = new Vector3(0, -.1f, 0) });
        AddChild(floor);
        try
        {
            Require(GetWorld3D().Space == world.Physics.Space, "fixture bodies and blast queries must share a space");
            var catalog = new GameCatalog();
            foreach (var formation in new[] { PileFormation.Square, PileFormation.Triangular })
            {
                foreach (var seed in new[] { 1, 23, 47 })
                {
                    var settings = SuburbPlans.Match(world.Tuning, 4) with
                    {
                        RowsWeight = 0,
                        TightWeight = 0,
                        SquareWeight = formation == PileFormation.Square ? 1 : 0,
                        TriangularWeight = formation == PileFormation.Triangular ? 1 : 0,
                    };
                    var plan = new SuburbGenerator(settings, catalog, seed).Generate();
                    foreach (var pile in plan.Tanks.GroupBy(t => t.Pile))
                    {
                        var origin = pile.First().Position;
                        AddFixture($"{formation} seed={seed} pile={pile.Key}", pile.Select(t => new Transform3D(
                            new Basis(Vector3.Up, t.Yaw) * Basis.FromEuler(t.Rotation),
                            new Vector3(t.Position.X - origin.X, t.Elevation, t.Position.Y - origin.Y))).ToArray());
                    }
                }
                // Include full and partial bases, and deeper tiers than the default generator supplies.
                foreach (var count in DevArgs.Get("deep") != null ? new[] { 4, 5, 6, 10, 14, 30, 64, 128 } : new[] { 4, 5, 6, 10, 14, 30 })
                {
                    AddFixture($"{formation} count={count}", PileBuilder.Build(formation, count,
                        new Vector3(world.Tuning.PileSpacing, world.Tuning.PileLayerHeight, world.Tuning.PileDepthSpacing),
                        world.Tuning.PileUprightLayerHeight).ToArray(), shove: count == 14);
                }
            }
            player = new PlayerCharacter { World = world, InputOverride = input }; AddChild(player);
            for (var time = 0; time < 120; time += 2)
                input.Hold(time, time + 1, new PlayerIntent { Move = Vector2.Right });
            // Loose neighbors beside a built pile must gather without recruiting any of its tanks.
            var edge = fixtures[0].Poses.Max(p => p.Origin.X) + 1;
            loose = new[] { AddTank(new Transform3D(Basis.Identity, new Vector3(edge, 0, 0)), false),
                AddTank(new Transform3D(Basis.Identity, new Vector3(edge + 1.5f, 0, 0)), false) };
            gather = new TankGather
            {
                World = world,
                Tanks = () => tanks.Where(IsInstanceValid),
                Publish = (tank, phase) =>
                {
                    if (phase == GatherPhase.Settled && !loose.Contains(tank)) regathered.Add(tank);
                }
            };
            AddChild(gather);
        }
        catch (Exception error) { Fail(error); }
    }

    private void AddFixture(string name, Transform3D[] poses, bool shove = false)
    {
        var offset = new Vector3(fixtures.Count * 25, 0, 0);
        poses = poses.Select(p => new Transform3D(p.Basis, p.Origin + offset)).ToArray();
        var members = poses.Select(p => AddTank(p, true)).ToArray();
        fixtures.Add(new Fixture(name, members, poses, new Vector3(poses.Min(p => p.Origin.X) - 3, 0, poses.Max(p => p.Origin.Z) + .8f), shove));
    }

    private PropaneTank AddTank(Transform3D pose, bool built)
    {
        var tank = PropaneTank.Create(world); AddChild(tank); tank.Transform = pose;
        tank.Sleeping = DevArgs.Get("awake") == null;
        if (built) tank.MarkPile();
        tanks.Add(tank);
        return tank;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (player == null) return;
        try
        {
            elapsed += delta;
            input.Advance((float)delta);
            if ((int)(elapsed / 30) != (int)((elapsed - delta) / 30))
                GD.Print($"[upright] simulated {elapsed:F1}s drift={maxDrift:F6}m tilt={maxTilt:F3}deg");
            if (!hit)
            {
                WalkAndCheckPiles();
                if (elapsed < 120) return;
                DisturbPiles();
            }
            if (!aftermath)
            {
                SampleToppling();
                if (elapsed < 130) return;
                BeginAftermath();
            }
            if (!gatherSiteCleared && elapsed >= 135) ClearGatherSite();
            if (elapsed < 180) return;
            CheckRegathered();
            ShutdownGuard.Quit(GetTree());
            SetPhysicsProcess(false);
        }
        catch (Exception error) { Fail(error); }
    }

    private void WalkAndCheckPiles()
    {
        var nextWalk = (int)(elapsed / 2);
        if (nextWalk != walk)
        {
            if (walk >= 0) Require(player.GlobalPosition.DistanceTo(walkStart) > 5, "player did not walk past pile");
            walk = nextWalk;
            walkStart = fixtures[walk % fixtures.Count].WalkStart;
            player.Teleport(walkStart, 0);
            passes++;
        }
        foreach (var fixture in fixtures)
        {
            for (var i = 0; i < fixture.Tanks.Length; i++)
            {
                var tank = fixture.Tanks[i];
                var pose = tank.GlobalTransform;
                var drift = pose.Origin.DistanceTo(fixture.Poses[i].Origin);
                var tilt = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(pose.Basis.Y.Y, -1, 1)));
                maxDrift = Mathf.Max(maxDrift, drift); maxTilt = Mathf.Max(maxTilt, tilt);
                if (drift > .006f || tilt > .5f)
                    throw new InvalidOperationException($"{fixture.Name} tank={i} drift={drift:F6}m tilt={tilt:F3}deg at {elapsed:F3}s");
                if (tank.Gathering) throw new InvalidOperationException($"{fixture.Name} original tank recruited into gather");
            }
        }
    }

    private void DisturbPiles()
    {
        Require(fixtures.All(f => f.Tanks.All(t => !t.Loose)), "original pile tank became loose");
        Require(passes > fixtures.Count, "player did not pass every pile");
        Require(loose.All(t => !t.Loose && !t.Gathering && t.Sleeping) && gather.Completed == 2, "loose neighbors did not gather beside pyramid");
        GD.Print($"[upright] STAND PASS {fixtures.Count} piles, {fixtures.Sum(f => f.Tanks.Length)} tanks, 120 simulated seconds, {passes - 1} player passes, max drift={maxDrift:F6}m tilt={maxTilt:F3}deg; two neighbors gathered");
        hit = true;
        // Each multi-tier fixture receives a shell shot, base detonation or intact shove.
        var index = 0;
        foreach (var fixture in fixtures.Where(f => f.Poses.Any(p => p.Origin.Y > 0)))
        {
            var tank = fixture.Tanks[0];
            var mode = fixture.Shove ? 2 : index++ % 2;
            fixture.Hit = new[] { "shot", "blast", "shove" }[mode];
            switch (mode)
            {
                case 0:
                    tank = fixture.Tanks.MaxBy(t => t.Position.Y)!;
                    var point = tank.GlobalTransform * (PropaneTank.LocalCenter + Vector3.Right * PileBuilder.Radius);
                    Require(tank.TakeShot(point, tank.GlobalBasis.X, -tank.GlobalBasis.X), "shell shot refused");
                    Require(tank.State == PropaneTank.TankState.Venting, "first shot did not puncture");
                    break;
                case 1: tank.Detonate(); break;
                case 2:
                    tank = fixture.Tanks.MaxBy(t => t.Position.Y)!;
                    tank.Disturb();
                    tank.ApplyCentralImpulse(tank.GlobalBasis.Z * world.Tuning.MeleePropImpulse);
                    Require(tank.State == PropaneTank.TankState.Intact, "shove punctured tank");
                    break;
            }
        }
        player.Teleport(new Vector3(-20, 0, -20), 0);
    }

    private void SampleToppling()
    {
        foreach (var fixture in fixtures.Where(f => f.Hit != ""))
        {
            foreach (var tank in fixture.Tanks.Where(IsInstanceValid))
                fixture.PeakTilt = Mathf.Max(fixture.PeakTilt, Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(tank.GlobalBasis.Y.Y, -1, 1))));
        }
    }

    private void BeginAftermath()
    {
        foreach (var fixture in fixtures.Where(f => f.Hit != ""))
            Require(fixture.PeakTilt > 30, $"{fixture.Name} count={fixture.Tanks.Length} did not topple after {fixture.Hit}, peak tilt={fixture.PeakTilt:F2}deg");
        GD.Print("[upright] TOPPLE PASS every multi-tier fixture tipped a surviving tank over 30 degrees after shell shot/base blast/intact shove");
        aftermath = true;
        // Keep the two intact shove piles. As in the game gather fixture, remove random blast fragments
        // after proving collapse so blocked departure paths cannot obscure the eligibility test.
        var survivors = fixtures.Where(f => f.Shove).SelectMany(f => f.Tanks).ToHashSet();
        foreach (var tank in tanks.Where(t => IsInstanceValid(t) && !survivors.Contains(t))) tank.QueueFree();
        tanks.RemoveAll(t => !survivors.Contains(t));
        foreach (var debris in GetChildren().OfType<DebrisBody>()) debris.QueueFree();
        foreach (var fixture in fixtures.Where(f => f.Shove))
        {
            var needed = 2 - fixture.Tanks.Count(t => t.Loose);
            foreach (var tank in fixture.Tanks.Where(t => !t.Loose).OrderByDescending(t => t.Position.Y).Take(Math.Max(needed, 0)))
            {
                tank.Disturb();
                tank.ApplyCentralImpulse(tank.GlobalBasis.Z * world.Tuning.MeleePropImpulse);
            }
        }
    }

    private void ClearGatherSite()
    {
        gatherSiteCleared = true;
        // A pile can block a scattered member's enclosing-sphere departure path. Clear the remaining
        // standing tanks for this eligibility proof; blocked paths are covered by GatherRange.
        foreach (var tank in tanks.Where(t => !t.Loose && !t.Gathering && !regathered.Contains(t)).ToArray())
        {
            tank.QueueFree();
            tanks.Remove(tank);
        }
    }

    private void CheckRegathered()
    {
        foreach (var fixture in fixtures.Where(f => f.Shove))
        {
            var rebuilt = fixture.Tanks.Where(regathered.Contains).ToArray();
            if (rebuilt.Length < 2)
            {
                throw new InvalidOperationException($"{fixture.Name} loose pile members did not gather: " +
                    string.Join("; ", fixture.Tanks.Where(IsInstanceValid).Select(t => $"{t.Position} loose={t.Loose} rest={t.AtRest} glide={t.Gathering}")));
            }

            Require(rebuilt.All(t => !t.Loose && !t.Gathering && t.Sleeping && t.State == PropaneTank.TankState.Intact), $"{fixture.Name} gathered members did not rest intact");
        }
        GD.Print($"[upright] GATHER PASS {regathered.Count} disturbed original pyramid tanks rebuilt as resting upright clusters");
    }

    private void Fail(Exception error)
    {
        GD.PushError($"[upright] FAIL {error.Message}");
        SetPhysicsProcess(false);
        ShutdownGuard.Quit(GetTree(), 1);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public override void _ExitTree() => world.Dispose();
}
