using Godot;
using Propane.Core;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>
/// Scripted test range for the tank: puncture, venting, detonation and a chain reaction, with screenshots.
/// Run: godot --fixed-fps 60 res://scenes/dev/tank_range.tscn -- --capture-dir=/tmp/range
/// </summary>
public partial class TankRange : Node3D
{
    private Camera3D camera = null!;
    private readonly System.Collections.Generic.List<PropaneTank> tanks = new();

    public override void _Ready()
    {
        DevArgs.Setup();
        var effects = new Node3D { Name = "Effects" };
        AddChild(effects);
        var dynamic = new Node3D { Name = "Dynamic" };
        AddChild(dynamic);
        Spawn.SetEffectsRoot(effects);
        Spawn.SetWorldRoot(dynamic);

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

    private void AddTank(Vector3 position)
    {
        var tank = PropaneTank.Create();
        AddChild(tank);
        tank.GlobalPosition = position;
        tank.RotateY((float)GD.RandRange(0, Mathf.Tau));
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
