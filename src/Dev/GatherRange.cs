using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>Physical gather regression: separate clusters, singles, shields, vents, blockers, shot/bump/blast interrupts.</summary>
public partial class GatherRange : Node3D
{
    private readonly List<PropaneTank> tanks = new();
    private readonly Dictionary<PropaneTank, Transform3D> settled = new();
    private TankGather gather = null!;
    private PropaneTank interrupted = null!;
    private bool shot;
    private bool bump;
    private bool blast;
    private bool sawGlide;
    private bool cancellationPublishedBeforePuncture;
    private Transform3D singlePose;
    private Transform3D ventPose;
    private PropaneTank vent = null!;
    private PropaneTank single = null!;
    private PropaneTank shield = null!;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DevArgs.Setup();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var effects = new Node3D(); AddChild(effects);
        Spawn.SetEffectsRoot(effects); Spawn.SetWorldRoot(this);
        var env = new VoidEnvironment(); AddChild(env);
        var camera = new Camera3D { Current = true, Fov = 60 }; AddChild(camera);
        camera.LookAtFromPosition(new Vector3(10, 12, 19), new Vector3(1, 0.5f, 0)); env.Follow = camera;
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(80, 0.2f, 60) }, Position = new Vector3(0, -0.1f, 0) });
        floor.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 60), Material = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.65f, 0.6f) } } });
        AddChild(floor);
        if (DevArgs.Get("scenario") == "appearance") { Appearance(camera); return; }
        if (DevArgs.Get("scenario") == "grant-races") { GrantRaces(); return; }
        Tuning.Current.GatherDelay = 2;
        Tuning.Current.GatherScanInterval = 0.1f;
        Tuning.Current.GatherDuration = 3;
        Tuning.Current.GatherLift = 2.5f;
        // Two independent groups and a third whose first member will be shot while traversing.
        foreach (var x in new[] { -8f, 0f, 8f })
        {
            AddTank(new Vector3(x - 0.9f, 0, -0.4f));
            AddTank(new Vector3(x + 0.9f, 0, -0.4f));
            AddTank(new Vector3(x, 0, 1));
        }
        interrupted = tanks[6];
        single = AddTank(new Vector3(-16, 0, 0)); singlePose = single.Transform;
        vent = AddTank(new Vector3(-18, 0, 0));
        vent.PunctureRemote(PropaneTank.LocalCenter + Vector3.Right * 0.156f, Vector3.Right, 1);
        // Freeze this vent to measure that gathering never edits it, independently of its own jet's motion.
        vent.Freeze = true; ventPose = vent.Transform;
        shield = AddTank(new Vector3(-8, 0, 1.9f)); shield.MakeInvulnerable(30, Colors.Orange);
        // A low obstacle below the traverse leg; its footprint does not intersect the final cluster.
        Block(new Vector3(-8, 0.15f, 0.6f), new Vector3(0.4f, 0.3f, 0.1f), Layers.Props);
        // A house occupies the center and every search candidate of a separate pair: this pair must stay loose.
        Block(new Vector3(0, 1.5f, -9), new Vector3(2, 3, 3), Layers.World);
        AddTank(new Vector3(-1.4f, 0, -9)); AddTank(new Vector3(1.4f, 0, -9));
        // A player-shaped obstacle fills the centroid of this pair, forcing selection of an alternate center.
        Block(new Vector3(8, 0.9f, -9), new Vector3(0.6f, 1.8f, 0.6f), Layers.Player);
        AddTank(new Vector3(6.9f, 0, -9)); AddTank(new Vector3(9.1f, 0, -9));
        var bumpTest = AddTank(new Vector3(25, 0, 0));
        var blastTest = AddTank(new Vector3(30, 0, 0));
        gather = new TankGather
        {
            Tanks = () => tanks,
            Publish = (tank, phase) =>
        {
            if (tank == interrupted && phase == GatherPhase.Cancel) { cancellationPublishedBeforePuncture = tank.State == PropaneTank.TankState.Intact; }
            if (phase == GatherPhase.Glide) { sawGlide = true; GD.Print($"[gather] start {tanks.IndexOf(tank)}"); }
            if (phase == GatherPhase.Settled) { settled[tank] = tank.GlobalTransform; GD.Print($"[gather] settled {tanks.IndexOf(tank)}"); }
        }
        };
        AddChild(gather);
        var director = new CaptureDirector { QuitAfter = 12 }; AddChild(director);
        director.ShotAt(1, "00_scattered");
        director.At(1.8f, () => camera.LookAtFromPosition(new Vector3(3, 3, 6), new Vector3(0, 1, 0)));
        director.ShotAt(2.7f, "01_lift");
        director.ShotAt(3.5f, "02_glide"); director.ShotAt(4.7f, "03_settle");
        director.At(7, () => camera.LookAtFromPosition(new Vector3(10, 12, 19), new Vector3(1, 0.5f, 0)));
        director.ShotAt(8, "04_piles");
        director.At(10, () =>
        {
            Require(sawGlide && shot && cancellationPublishedBeforePuncture, "glide/shot/cancel-before-authority not exercised");
            Require(!interrupted.Gathering && interrupted.State == PropaneTank.TankState.Venting && !settled.ContainsKey(interrupted), "shot did not interrupt only its tank");
            Require(tanks.Take(6).All(settled.ContainsKey) && tanks.Skip(7).Take(2).All(settled.ContainsKey), "near and far groups did not gather");
            Require(single.Position.DistanceTo(singlePose.Origin) <= 0.006f && single.Loose, "single moved");
            Require(vent.Transform.IsEqualApprox(ventPose) && !vent.Gathering, "vent moved");
            Require(!settled.ContainsKey(shield) && shield.Loose && shield.Invulnerable, "shielded drop gathered");
            Require(!settled.ContainsKey(tanks[12]) && !settled.ContainsKey(tanks[13]), "blocked house path accepted");
            Require(settled.ContainsKey(tanks[14]) && settled.ContainsKey(tanks[15]), "did not select clear center around player");
            var drift = settled.Max(pair => pair.Key.GlobalPosition.DistanceTo(pair.Value.Origin));
            var angle = settled.Max(pair => pair.Key.GlobalBasis.GetRotationQuaternion().AngleTo(pair.Value.Basis.GetRotationQuaternion()));
            Require(drift <= 0.006f && angle <= Mathf.DegToRad(0.5f), $"unstable piles: drift {drift}, angle {angle}");
            GD.Print($"[gather] PASS {settled.Count} settled, drift {drift:0.0000} m tilt {Mathf.RadToDeg(angle):0.000} degrees; singles/vent/shield unchanged; blockers avoided");
        });
        // Additional actual impulses verify that freezing does not swallow a bump or blast push.
        director.At(10.5f, () =>
        {
            gather.Enabled = () => false;
            var tank = bumpTest; tank.BeginGather(); tank.Disturb(); tank.ApplyCentralImpulse(Vector3.Right * tank.Mass);
            bump = !tank.Gathering && !tank.Freeze;
            var pushed = blastTest; pushed.BeginGather();
            Blast.Detonate(this, pushed.GlobalPosition + Vector3.Up, null, chain: false);
            blast = !pushed.Gathering && !pushed.Freeze;
            Require(bump && blast, "bump or blast failed to release glide");
            GD.Print("[gather] PASS bump/blast release physics");
        });
    }

    public override void _PhysicsProcess(double delta)
    {
        if (gather == null || interrupted == null || shot || !interrupted.Gathering || interrupted.GlobalPosition.Y < 1) { return; }
        shot = true;
        var center = interrupted.GlobalTransform * PropaneTank.LocalCenter;
        interrupted.TakeShot(center + Vector3.Right * 0.156f, Vector3.Right, Vector3.Right);
        Require(!interrupted.Freeze && !interrupted.Gathering, "shot failed to release physics immediately");
    }

    private PropaneTank AddTank(Vector3 at)
    {
        var tank = PropaneTank.Create(); AddChild(tank); tank.Position = at; tank.Sleeping = true; tanks.Add(tank); return tank;
    }

    private void Block(Vector3 at, Vector3 size, uint layer)
    {
        var body = new StaticBody3D { Position = at, CollisionLayer = layer };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size, Material = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.5f, 0.65f) } } });
        AddChild(body);
    }

    private void Require(bool condition, string message)
    {
        if (!condition) { GD.PushError($"[gather] FAIL {message}"); GetTree().Quit(1); throw new InvalidOperationException(message); }
    }

    /// <summary>
    /// Force both presentation effects directly; production gathering excludes shielded tanks. Check that stopping
    /// either effect preserves the other, using custom, black and white appearances and the actual tank material.
    /// </summary>
    private void Appearance(Camera3D camera)
    {
        var colors = new[] { new AppearanceRecord(18, 171, 239), new AppearanceRecord(0, 0, 0), new AppearanceRecord(255, 255, 255) };
        Tuning.Current.DropInvulnerableTime = 1;
        Tuning.Current.GatherShimmer = 1;
        camera.LookAtFromPosition(new Vector3(0, 1.7f, 1.8f), new Vector3(0, 1.2f, 0));
        foreach (var (appearance, index) in colors.Select((color, i) => (color, i)))
        {
            var tank = AddTank(new Vector3((index - 1) * 0.6f, 1, 0));
            tank.MakeInvulnerable(Tuning.Current.DropInvulnerableTime, appearance.Color);
            tank.BeginGather();
        }
        var director = new CaptureDirector { QuitAfter = 2 }; AddChild(director);
        director.At(0.2f, () =>
        {
            foreach (var (tank, index) in tanks.Select((tank, i) => (tank, i)))
            {
                var mesh = tank.GetNode<MeshInstance3D>("Mesh");
                var glow = mesh.GetInstanceShaderParameter("shield_color").AsColor();
                var contrast = mesh.GetInstanceShaderParameter("shield_contrast").AsColor();
                Require(tank.Invulnerable && tank.Gathering && glow.A > 0 && new Color(glow, 1) == colors[index].Color,
                    "gather start lost the drop appearance or shield");
                Require(mesh.GetInstanceShaderParameter("gather_shimmer").AsSingle() == Tuning.Current.GatherShimmer,
                    "gather shimmer missing alongside shield");
                Require(index == 0 ? contrast.A == 0 : contrast.A > 0, "extreme-color shield contrast missing");
            }
            GD.Print("[gather appearance] PASS custom/black/white glow, contrast and shimmer coexist");
        });
        director.ShotAt(0.3f, "00_glow_and_shimmer");
        director.At(0.4f, () =>
        {
            foreach (var tank in tanks)
            {
                var mesh = tank.GetNode<MeshInstance3D>("Mesh");
                var glow = mesh.GetInstanceShaderParameter("shield_color").AsColor();
                var contrast = mesh.GetInstanceShaderParameter("shield_contrast").AsColor();
                tank.EndGather();
                Require(tank.Invulnerable && !tank.Gathering && glow.A > 0 && mesh.GetInstanceShaderParameter("shield_color").AsColor() == glow &&
                    mesh.GetInstanceShaderParameter("shield_contrast").AsColor() == contrast &&
                    mesh.GetInstanceShaderParameter("gather_shimmer").AsSingle() == 0, "gather end erased the shield or kept shimmer");
                tank.BeginGather();
            }
            GD.Print("[gather appearance] PASS gather completion preserves active shield");
        });
        director.At(1.4f, () =>
        {
            foreach (var tank in tanks)
            {
                var mesh = tank.GetNode<MeshInstance3D>("Mesh");
                Require(!tank.Invulnerable && tank.Gathering && mesh.GetInstanceShaderParameter("shield_color").AsColor().A == 0 &&
                    mesh.GetInstanceShaderParameter("shield_contrast").AsColor().A == 0 &&
                    mesh.GetInstanceShaderParameter("gather_shimmer").AsSingle() == Tuning.Current.GatherShimmer,
                    "shield expiry erased shimmer or retained glow/contrast");
            }
            GD.Print("[gather appearance] PASS shield expiry preserves active shimmer");
        });
        director.ShotAt(1.5f, "01_shimmer_after_shield");
    }

    private void GrantRaces()
    {
        Tuning.Current.GatherDelay = 0.1f;
        Tuning.Current.GatherScanInterval = 0.05f;
        Tuning.Current.GatherDuration = 1;
        AddTank(new Vector3(-0.8f, 0, 0)); AddTank(new Vector3(0.8f, 0, 0));
        var requests = 0;
        var lease = true;
        var stop = false;
        gather = new TankGather { Tanks = () => tanks, Enabled = () => !stop };
        gather.Grant = (members, done) =>
        {
            requests++;
            if (requests == 1) { members[0].Disturb(); done(_ => true); Require(!members.Any(t => t.Gathering), "disturbed pending grant started"); }
            else if (requests == 2) { AddTank(new Vector3(0, 0, 1.5f)); done(_ => true); Require(!members.Any(t => t.Gathering), "new member failed to cancel pending grant"); }
            else if (requests == 3) { members[0].Free(); done(_ => true); Require(!members.Skip(1).Any(t => t.Gathering), "freed pending member started"); }
            else if (requests == 4)
            {
                done(_ => lease);
                Require(members.All(t => t.Gathering), "valid atomic grant did not start");
                GetTree().CreateTimer(0.2f, processInPhysics: true).Timeout += () => { lease = false; stop = true; };
            }
            else { Require(false, "unexpected extra grant"); }
        };
        AddChild(gather);
        var director = new CaptureDirector { QuitAfter = 3 }; AddChild(director);
        director.At(2.5f, () =>
        {
            Require(requests == 4 && gather.Completed == 0 && gather.Interrupted == 2 && tanks.Where(IsInstanceValid).All(t => !t.Gathering && !t.Freeze),
                $"grant race proof incomplete: requests={requests}, interrupted={gather.Interrupted}");
            GD.Print("[gather] PASS grant races: disturbance, new member, freed member and lost epoch lease");
        });
    }
}
