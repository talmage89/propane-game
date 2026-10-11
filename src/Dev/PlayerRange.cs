using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>
/// Scripted player test: locomotion, hip fire, aimed fire, reloads, a tank blast that ragdolls the player, and the get-up.
/// Run: godot --fixed-fps 60 res://scenes/dev/player_range.tscn -- --capture-dir=/tmp/p [--scenario=...] [--view=side]
/// Views: player, side, front, left, top, rightclose, leftclose, rifleleft, rifleright.
/// </summary>
public partial class PlayerRange : Node3D
{
    private readonly ScriptedInput script = new();
    private PlayerCharacter player = null!;
    private Camera3D? observer;
    private string view = "player";

    public override void _Ready()
    {
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        ProcessPhysicsPriority = -10;
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
            case "movement":
            case "sprint-repro":
                var repro = scenario == "sprint-repro";
                if (repro)
                {
                    // Discovery sequence, including the second release after a recent shot.
                    script.Hold(0.5f, 9.5f, new PlayerIntent { Move = new Vector2(0, 1), Sprint = true });
                    script.Jump(1.0f);
                    script.Hold(1.15f, 2.0f, new PlayerIntent { Aim = true });
                    script.Hold(6.5f, 7.2f, new PlayerIntent { Aim = true });
                    script.Fire(6.8f);
                }
                else
                {
                    script.Hold(0.3f, 6.0f, new PlayerIntent { Move = new Vector2(0, 1) });
                    script.Hold(1.3f, 6.0f, new PlayerIntent { Sprint = true });
                    script.Hold(2.3f, 4.8f, new PlayerIntent { Aim = true });
                    script.Jump(3.3f);
                    director.ShotAt(0.9f, "move_00_default");
                    director.ShotAt(2.0f, "move_01_sprint");
                    director.ShotAt(3.0f, "move_02_creep");
                    director.ShotAt(3.65f, "move_03_air_aim_held");
                    director.ShotAt(4.6f, "move_04_landed_creep");
                    director.ShotAt(5.2f, "move_05_release_sprint");
                    director.QuitAfter = 6.5f;
                }
                var probe = new MovementProbe { Player = player, Input = script };
                AddChild(probe);
                director.At(repro ? 9f : 6f, () => probe.Finish(!repro));
                break;
            case "reload":
                // A match's magazine: empty it (the bolt locks open), reload from empty, then a reload with rounds
                // left. Times in the clips are scaled to the 1.6 s reload.
                player.Ammo = new AmmoState { Magazine = 3, Reserve = 60 };
                script.Hold(1.0f, 1.6f, new PlayerIntent { FireHeld = true });
                director.ShotAt(1.7f, "r00_locked");
                script.Reload(1.8f);
                foreach (var t in new[] { 0.3f, 0.45f, 0.6f, 0.9f, 1.2f, 1.45f, 1.7f, 1.88f, 2.1f })
                {
                    director.ShotAt(1.8f + t / 1.5f, $"r1_{t:0.00}");
                }
                script.Fire(3.8f);
                script.Fire(4.1f);
                script.Reload(4.6f);
                foreach (var t in new[] { 0.45f, 0.9f, 1.3f, 1.7f })
                {
                    director.ShotAt(4.6f + t / 1.25f, $"r2_{t:0.00}");
                }
                director.ShotAt(6.8f, "r3_after");
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
        if (view.StartsWith("rifle"))
        {
            // Close on the rifle from either side of the character, for the hands, the action and the reload.
            var rifle = player.GetNode<Rifle>("Rifle");
            focus = rifle.GlobalTransform * new Vector3(0, 0, -0.12f);
            var side = view == "rifleleft" ? -1f : 1f;
            offset = new Basis(Vector3.Up, player.FacingYaw) * new Vector3(0.8f * side, 0.2f, -0.45f);
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
