using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Tank;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev;

/// <summary>
/// Scripted test range for the tank: puncture, venting, detonation and a chain reaction, with screenshots.
/// Run: godot --fixed-fps 60 res://scenes/dev/tank_range.tscn -- --capture-dir=/tmp/range
/// </summary>
public partial class TankRange : Node3D
{
    private WorldContext world = null!;
    public override void _ExitTree() => world.Dispose();

    private Camera3D camera = null!;
    private readonly System.Collections.Generic.List<PropaneTank> tanks = new();

    public override void _Ready()
    {
        DevArgs.Setup();
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var dynamic = new Node3D { Name = "Dynamic" };
        AddChild(dynamic);

        world.SetContentRoot(dynamic);

        var env = new VoidEnvironment();
        AddChild(env);

        camera = new Camera3D { Fov = 60, Current = true };
        AddChild(camera);
        camera.LookAtFromPosition(new Vector3(DevArgs.GetFloat("cx", 3.2f), DevArgs.GetFloat("cy", 1.4f), DevArgs.GetFloat("cz", 4.5f)),
            new Vector3(0, 0.4f, 0));
        env.Follow = camera;
        if (DevArgs.Get("exposure") != null) env.Environment.TonemapExposure = DevArgs.GetFloat("exposure", 1f);
        if (DevArgs.Get("agxwhite") != null) env.Environment.TonemapAgxWhite = DevArgs.GetFloat("agxwhite", 6f);
        if (DevArgs.Get("agxcontrast") != null) env.Environment.TonemapAgxContrast = DevArgs.GetFloat("agxcontrast", 1f);
        if (DevArgs.Get("sun") != null) env.Sun.LightEnergy = DevArgs.GetFloat("sun", 2.6f);
        if (DevArgs.Get("ambient") != null) env.Environment.AmbientLightEnergy = DevArgs.GetFloat("ambient", 0.5f);
        if (DevArgs.Get("dump-textures") != null)
        {
            var dir = DevArgs.Get("capture-dir") ?? "user://";
            Fx.FxLibrary.ScorchTexture.GetImage().SavePng($"{dir}/tex_scorch.png");
            Fx.FxLibrary.BulletHoleTexture.GetImage().SavePng($"{dir}/tex_hole.png");
        }

        if (DevArgs.Get("formation") is { } name)
        {
            FormationRange(Enum.Parse<PileFormation>(name, true));
            return;
        }

        AddTank(new Vector3(0, 0, 0));
        AddTank(new Vector3(-2.2f, 0, -1.5f));
        AddTank(new Vector3(-2.7f, 0, -1.0f));
        AddTank(new Vector3(-2.0f, 0, -0.6f));
        for (var i = 0; i < 6; i++)
        {
            AddBox(new Vector3(1.5f + i * 0.45f, 0.2f, -1.2f), 0.4f);
        }

        var director = new CaptureDirector { QuitAfter = DevArgs.GetFloat("quit", 14f) };
        AddChild(director);
        switch (DevArgs.Get("scenario") ?? "full")
        {
            case "vent":
                if (DevArgs.Get("freeze") != null)
                {
                    tanks[0].FreezeMode = RigidBody3D.FreezeModeEnum.Static;
                    tanks[0].Freeze = true;
                }
                director.At(0.5f, () => Shoot(tanks[0]));
                for (var i = 0; i < 12; i++)
                {
                    director.ShotAt(0.7f + i * 0.5f, $"vent_{i:00}");
                    director.At(0.7f + i * 0.5f, () =>
                    {
                        if (IsInstanceValid(tanks[0]))
                        {
                            GD.Print($"[range] t0 spin {tanks[0].AngularVelocity.Length():0.0} rad/s speed {tanks[0].LinearVelocity.Length():0.0} m/s up {tanks[0].GlobalBasis.Y.Y:0.00}");
                        }
                    });
                }
                break;
            case "blast":
                director.At(0.5f, () => Shoot(tanks[0]));
                director.At(0.6f, () => Shoot(tanks[0]));
                float[] times = { 0.02f, 0.06f, 0.12f, 0.2f, 0.3f, 0.45f, 0.65f, 0.9f, 1.2f, 1.6f, 2.2f, 3f, 4f, 5.5f, 7f };
                for (var i = 0; i < times.Length; i++)
                {
                    director.ShotAt(0.6f + times[i], $"blast_{i:00}_{times[i]:0.00}");
                }
                break;
            case "look":
                director.At(0.5f, () => Shoot(tanks[1]));
                director.ShotAt(1.5f, "look");
                break;
            default:
                director.ShotAt(0.5f, "00_intact");
                director.At(1.0f, () => Shoot(tanks[0]));
                director.ShotAt(1.25f, "01_vent_start");
                director.ShotAt(1.8f, "02_venting");
                director.ShotAt(2.6f, "03_venting_later");
                director.At(3.5f, () => Shoot(tanks[0]));
                director.ShotAt(3.55f, "04_blast_0.05");
                director.ShotAt(3.65f, "05_blast_0.15");
                director.ShotAt(3.85f, "06_blast_0.35");
                director.ShotAt(4.3f, "07_blast_0.8");
                director.ShotAt(5.5f, "08_blast_2.0");
                director.ShotAt(8.0f, "09_aftermath");
                director.At(8.5f, () => Shoot(tanks[1]));
                director.At(9.0f, () => Shoot(tanks[1]));
                director.ShotAt(9.15f, "10_chain");
                director.ShotAt(9.5f, "11_chain");
                director.ShotAt(10.2f, "12_chain");
                director.ShotAt(13.5f, "13_chain_after");
                break;
        }
    }

    private void FormationRange(PileFormation formation)
    {
        var ground = new StaticBody3D { CollisionLayer = Layers.World };
        ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20, 0.2f, 20) }, Position = new Vector3(0, -0.1f, 0) });
        AddChild(ground);
        var tuning = world.Tuning;
        var transforms = PileBuilder.Build(formation, (int)DevArgs.GetFloat("count", 14),
            new Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing), tuning.PileUprightLayerHeight);
        foreach (var transform in transforms)
        {
            var tank = PropaneTank.Create(world);
            AddChild(tank);
            tank.Transform = transform;
            tank.Sleeping = DevArgs.Get("awake") == null;
            tank.MarkPile();
            tanks.Add(tank);
        }
        camera.LookAtFromPosition(new Vector3(2.5f, 1.8f, 3f), new Vector3(0, 0.4f, 0));
        var initial = tanks.Select(t => t.Transform).ToArray();
        var chain = DevArgs.Get("chain");
        var punctured = new HashSet<PropaneTank>();
        var detonated = new HashSet<PropaneTank>();
        var inRange = new List<PropaneTank>();
        foreach (var tank in tanks)
        {
            tank.Punctured += t => punctured.Add(t);
            tank.Detonated += t => detonated.Add(t);
        }
        bool Require(bool condition, string message)
        {
            if (condition) return true;
            GD.PushError($"[formation] {formation}: {message}");
            ShutdownGuard.Quit(GetTree(), 1);
            return false;
        }
        var director = new CaptureDirector { QuitAfter = 12f };
        AddChild(director);
        director.ShotAt(0.2f, "00_placed");
        director.ShotAt(4f, "01_settled");
        director.At(4.2f, () =>
        {
            var drift = tanks.Select((t, i) => t.Position.DistanceTo(initial[i].Origin)).Max();
            var tilt = tanks.Select((t, i) => t.Basis.GetRotationQuaternion().AngleTo(initial[i].Basis.GetRotationQuaternion())).Max();
            GD.Print($"[formation] {formation} count {tanks.Count} drift {drift:0.0000} m rotation {Mathf.RadToDeg(tilt):0.00} deg");
            // Allow millimeters of Jolt contact settling, but fail on sliding, rolling or a collapsing tier.
            if (drift > 0.006f || tilt > Mathf.DegToRad(0.5f))
            {
                GD.PushError("[formation] moved before hit");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            if (chain != null)
            {
                if (!Require(chain is "fresh" or "primed", "chain must be fresh or primed")) return;
                if (!Require(tanks.Count > 1, "a chain check needs at least two tanks")) return;
                var center = tanks[0].GlobalTransform * PropaneTank.LocalCenter;
                // Use the same mass centers and effective range as Blast.Push, before any tank moves.
                inRange.AddRange(tanks.Skip(1).Where(t => center.DistanceTo(t.GlobalPosition +
                    PhysicsServer3D.BodyGetDirectState(t.GetRid()).CenterOfMass) <= Mathf.Min(tuning.ChainRadius, tuning.BlastRadius)));
                if (!Require(inRange.Count > 0, "no neighbor in chain range")) return;
                if (chain == "primed")
                {
                    foreach (var tank in tanks)
                    {
                        tank.TakeShot(tank.GlobalTransform * (PropaneTank.LocalCenter + Vector3.Right * PileBuilder.Radius),
                            tank.GlobalBasis.X, Vector3.Zero);
                    }
                }

                punctured.Clear();
                tanks[0].Detonate();
                if (!Require(inRange.All(t => t.State == PropaneTank.TankState.Venting), "neighbors must vent immediately after a blast")) return;
                if (!Require(detonated.SetEquals(new[] { tanks[0] }), "a venting neighbor detonated without waiting for its fuse")) return;
                if (chain == "fresh")
                {
                    if (!Require(punctured.SetEquals(inRange), "blast did not puncture exactly the intact neighbors in range")) return;
                    if (!Require(tanks.Skip(1).Except(inRange).All(t => t.State == PropaneTank.TankState.Intact), "an intact neighbor outside range changed state")) return;
                }
                GD.Print($"[formation] {formation} {chain}: {inRange.Count} neighbors in range, blast acts as one shot");
            }
        });
        if (DevArgs.Get("hit") is { } hit)
        {
            if (!Require(hit is "shot" or "blast" or "shove", "hit must be shot, blast or shove")) return;
            if (!Require(chain == null, "choose hit or chain, not both")) return;
            director.At(4.3f, () =>
            {
                if (hit == "shot")
                {
                    Shoot(tanks.MaxBy(t => t.Position.Y)!);
                }
                else if (hit == "blast")
                {
                    tanks[0].Detonate();
                }
                else if (hit == "shove")
                {
                    var tank = tanks.MaxBy(t => t.Position.Y)!;
                    tank.Disturb();
                    tank.ApplyCentralImpulse(tank.GlobalBasis.Z * tuning.MeleePropImpulse);
                }
            });
            director.ShotAt(4.4f, "02_hit");
            director.ShotAt(4.7f, "03_falling");
            director.ShotAt(5.3f, "04_falling");
            director.ShotAt(7f, "05_fallen");
        }
        if (chain != null)
        {
            director.ShotAt(4.4f, "02_chain");
            director.ShotAt(5.2f, "03_chain");
            // Timer runs in scaled physics time; allow a second for blast hitstop and frame scheduling.
            director.At(4.2f + Mathf.Max(tuning.ChainFuseMin, tuning.ChainFuseMax) * 1.15f + 1f, () =>
            {
                if (chain == "primed") Require(inRange.All(detonated.Contains), "a primed neighbor exceeded its fuse deadline");
            });
        }
        director.At(11f, () =>
        {
            if (chain == null) return;
            if (chain == "fresh")
            {
                if (!Require(detonated.SetEquals(new[] { tanks[0] }), "fresh neighbors detonated from only one blast")) return;
                if (!Require(inRange.All(t => IsInstanceValid(t) && t.State == PropaneTank.TankState.Venting), "fresh neighbors did not keep venting")) return;
            }
            else if (!Require(inRange.All(detonated.Contains), "a primed neighbor did not detonate after its fuse"))
            {
                return;
            }

            GD.Print($"[formation] {formation} {chain}: PASS; {inRange.Count} neighbors {(chain == "fresh" ? "still venting" : "detonated after fuse")}");
        });
    }

    private void AddTank(Vector3 position)
    {
        var tank = PropaneTank.Create(world);
        AddChild(tank);
        tank.GlobalPosition = position;
        tank.RotateY(world.Random.RandfRange(0, Mathf.Tau));
        tanks.Add(tank);
    }

    private void AddBox(Vector3 position, float size)
    {
        var body = new RigidBody3D { Mass = 3f, CollisionLayer = Layers.Props, CollisionMask = Layers.DynamicMask };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * size, Material = new StandardMaterial3D { AlbedoColor = new Color(0.75f, 0.55f, 0.35f) } } });
        AddChild(body);
        body.GlobalPosition = position;
    }

    private void Shoot(PropaneTank tank)
    {
        if (!IsInstanceValid(tank) || tank.State == PropaneTank.TankState.Exploded)
        {
            return;
        }
        var target = tank.GlobalTransform * PropaneTank.LocalCenter;
        var from = camera.GlobalPosition;
        var query = PhysicsRayQueryParameters3D.Create(from, from + (target - from) * 2f, Layers.Shootable);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0 && hit["collider"].As<GodotObject>() is PropaneTank hitTank)
        {
            hitTank.TakeShot(hit["position"].AsVector3(), hit["normal"].AsVector3(), (target - from).Normalized());
        }
    }
}
