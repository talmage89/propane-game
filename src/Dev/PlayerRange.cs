using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>
/// Scripted player test: locomotion, hip fire, aimed fire, a tank blast that ragdolls the player, and the get-up.
/// Run: godot --fixed-fps 60 res://scenes/dev/player_range.tscn -- --capture-dir=/tmp/p [--scenario=...] [--view=side]
/// </summary>
public partial class PlayerRange : Node3D
{
    private readonly ScriptedInput script = new();
    private PlayerCharacter player = null!;
    private Camera3D? observer;
    private string view = "player";

    public override void _Ready()
    {
        DevArgs.Setup();
        InputSetup.Register();
        Core.DevLog.Enabled = DevArgs.Get("verbose-log") != null;
        var effects = new Node3D { Name = "Effects" };
        AddChild(effects);
        var dynamic = new Node3D { Name = "Dynamic" };
        AddChild(dynamic);
        Spawn.SetEffectsRoot(effects);
        Spawn.SetWorldRoot(dynamic);
        var env = new VoidEnvironment();
        AddChild(env);

        player = new PlayerCharacter { Name = "Player" };
        AddChild(player);
        player.InputOverride = script;
        env.Follow = player;

        view = DevArgs.Get("view") ?? "player";
        if (view != "player")
        {
            observer = new Camera3D { Fov = 40, Current = true };
            AddChild(observer);
        }

        var director = new CaptureDirector { QuitAfter = DevArgs.GetFloat("quit", 10f) };
        AddChild(director);
        var scenario = DevArgs.Get("scenario") ?? "stances";
        switch (scenario)
        {
            case "stances":
                AddTank(new Vector3(1.5f, 0, -9f));
                director.ShotAt(0.8f, "00_idle_hip");
                script.Hold(1.0f, 3.0f, new PlayerIntent { Aim = true });
                director.ShotAt(1.6f, "01_aimed");
                script.Fire(2.0f);
                director.ShotAt(2.04f, "02_aimed_fire");
                director.ShotAt(2.6f, "03_aimed_after");
                script.Fire(3.4f);
                director.ShotAt(3.44f, "04_hip_fire");
                script.Hold(4.0f, 6.0f, new PlayerIntent { Move = new Vector2(0, 1) });
                director.ShotAt(4.8f, "05_run");
                script.Hold(6.0f, 8.0f, new PlayerIntent { Move = new Vector2(0, 1), Sprint = true });
                director.ShotAt(7.2f, "06_sprint");
                script.Hold(8.0f, 9.5f, new PlayerIntent { Move = new Vector2(1, 0), Aim = true });
                director.ShotAt(9.0f, "07_strafe_aim");
                break;
            case "ragdoll":
                AddTank(new Vector3(0.6f, 0, -2.2f));
                AddTank(new Vector3(-3f, 0, -12f));
                director.At(0.6f, () => ShootTankDirect(0));
                director.At(0.9f, () => ShootTankDirect(0));
                for (var i = 0; i < 20; i++)
                {
                    director.ShotAt(0.95f + i * 0.3f, $"rag_{i:00}");
                }
                break;
        }
    }

    public override void _PhysicsProcess(double delta) => script.Advance((float)delta);

    public override void _Process(double delta)
    {
        if (observer == null)
        {
            return;
        }
        var focus = player.IsRagdolled ? player.CameraRig.Target : player.GlobalPosition + Vector3.Up * 1.0f;
        var offset = view switch
        {
            "front" => new Vector3(0.8f, 0.4f, -3.2f),
            "left" => new Vector3(-3.4f, 0.3f, 0.4f),
            "rightclose" => new Vector3(1.6f, 0.45f, -0.3f),
            "leftclose" => new Vector3(-1.6f, 0.45f, -0.3f),
            "top" => new Vector3(0.01f, 2.4f, 0.4f),
            _ => new Vector3(3.4f, 0.3f, 0.4f),
        };
        if (view.EndsWith("close") || view == "top")
        {
            focus = player.GlobalPosition + Vector3.Up * 1.3f;
        }
        observer.LookAtFromPosition(focus + offset, focus);
    }

    private readonly System.Collections.Generic.List<PropaneTank> tanks = new();

    private void AddTank(Vector3 position)
    {
        var tank = PropaneTank.Create();
        AddChild(tank);
        tank.GlobalPosition = position;
        tanks.Add(tank);
    }

    private void ShootTankDirect(int index)
    {
        var tank = tanks[index];
        if (!IsInstanceValid(tank))
        {
            return;
        }
        var center = tank.GlobalTransform * PropaneTank.LocalCenter;
        tank.TakeShot(center + Vector3.Back * 0.156f, Vector3.Back, Vector3.Forward);
    }
}
