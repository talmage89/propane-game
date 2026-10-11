using System.Linq;
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

        var scenario = DevArgs.Get("scenario") ?? "stances";
        var director = new CaptureDirector { QuitAfter = DevArgs.GetFloat("quit", scenario == "grip" ? (DevArgs.Get("grip-sweep") != null ? 39.2f : 15.2f) : 10f) };
        GripProbe? grip = null;
        if (DevArgs.Get("grip-metrics") != null || DevArgs.Get("assert-grip") != null)
            grip = GripProbe.Attach(player, scenario == "grip");
        switch (scenario)
        {
            case "creep":
                // Two full strides after acceleration and gait/rig blends settle; 12 frames per stride.
                var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>()
                    .Single(s => s.FindBone("foot_l") >= 0);
                var animation = player.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>()
                    .Single(p => p.HasAnimation("Walk"));
                var period = (float)animation.GetAnimation("Walk").Length * 0.98f / Tuning.Current.CreepSpeed;
                var end = 2f + period * 2;
                script.Hold(0.25f, end + 0.5f, new PlayerIntent { Move = Vector2.Down, Aim = true });
                var stride = new StrideProbe { Player = player, SampleFrom = 2f, SampleUntil = end };
                skeleton.AddChild(stride);
                for (var i = 0; i <= 24; i++)
                {
                    var label = $"creep_{i:00}";
                    director.At(2f + period * i / 12, () => CaptureAfterDraw(director, label));
                }
                director.At(end + 0.1f, stride.Finish);
                director.QuitAfter = end + 0.3f;
                GD.Print($"[creep] speed={Tuning.Current.CreepSpeed:0.00} clip-scale={Tuning.Current.CreepSpeed / 0.98f:0.000} stride={period:0.000}s");
                break;
            case "grip":
                player.Ammo = new AmmoState { Magazine = 3, Reserve = 60 };
                script.Hold(1, 4, new PlayerIntent { Aim = true });
                script.Hold(2, 2.6f, new PlayerIntent { FireHeld = true });
                script.Reload(4.1f);
                script.Fire(6);
                script.Reload(6.4f);
                script.Hold(8.3f, 9.5f, new PlayerIntent { Move = Vector2.Down });
                script.Hold(9.5f, 10.7f, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                script.Hold(10.7f, 14.8f, new PlayerIntent { Move = Vector2.Right, Aim = true });
                director.At(11.6f, () => player.CameraRig.SetOrientation(0, -75));
                director.At(12.8f, () => player.CameraRig.SetOrientation(0, 45));
                director.At(14, () => player.CameraRig.SetOrientation(0, -10));
                foreach (var (t, label) in new (float, string)[]
                {
                    (0.8f, "carry"), (1.7f, "aim"), (2.04f, "fire"), (3.8f, "empty"),
                    (4.5f, "empty_swap"), (5.3f, "bolt"), (6.05f, "ready"), (6.8f, "tactical_swap"),
                    (7.5f, "tactical_seat"), (9, "run"), (10.2f, "sprint"), (11.4f, "strafe"),
                    (12.3f, "pitch_down"), (13.5f, "pitch_up"), (14.7f, "return"),
                })
                {
                    director.ShotAt(t, label);
                }

                if (DevArgs.Get("grip-sweep") != null)
                {
                    // Extend the same inputs through pitch changes in every hold and firing at both limits.
                    director.At(15.2f, () => player.Ammo = new AmmoState { Magazine = 30, Reserve = 90 });
                    script.Hold(15.2f, 17.8f, new PlayerIntent { Aim = true });
                    script.Hold(16, 16.4f, new PlayerIntent { FireHeld = true });
                    script.Hold(17, 17.4f, new PlayerIntent { FireHeld = true });
                    script.Hold(20, 23, new PlayerIntent { Move = Vector2.Down });
                    script.Hold(23, 26, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                    director.At(26, () => player.Ammo = new AmmoState { Magazine = 30, Reserve = 90 });
                    script.Hold(26, 28.9f, new PlayerIntent { FireHeld = true });
                    script.Hold(29, 32, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                    script.Reload(29.5f);
                    foreach (var start in new[] { 15.7f, 18f, 20.5f, 23.5f, 26.5f })
                    {
                        director.At(start, () => player.CameraRig.SetOrientation(0, -75));
                        director.At(start + 1, () => player.CameraRig.SetOrientation(0, 45));
                    }
                    director.At(29, () => player.CameraRig.SetOrientation(0, -10));
                    director.At(33.2f, () =>
                    {
                        player.CameraRig.SetOrientation(0, -75);
                        player.Ammo = new AmmoState { Magazine = 0, Reserve = 90 };
                    });
                    script.Reload(33.5f);
                    director.At(36.2f, () =>
                    {
                        player.CameraRig.SetOrientation(0, 45);
                        player.Ammo = new AmmoState { Magazine = 5, Reserve = 90 };
                    });
                    script.Reload(36.5f);
                    foreach (var (t, label) in new (float, string)[]
                    {
                        (16.04f, "aim_down_fire"), (17.04f, "aim_up_fire"),
                        (18.6f, "carry_down"), (19.6f, "carry_up"), (21.1f, "run_down"), (22.1f, "run_up"),
                        (24.1f, "sprint_down"), (25.1f, "sprint_up"), (27.1f, "ready_down_fire"), (28.1f, "ready_up_fire"),
                        (30.3f, "sprint_reload"), (31.8f, "sprint_return"), (34.5f, "reload_down"), (37.3f, "reload_up"),
                    })
                    {
                        director.ShotAt(t, label);
                    }
                }
                break;
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
        // Scenarios such as creep set their own duration; finish diagnostics against that final duration.
        if (grip != null) director.At(director.QuitAfter - 0.1f, grip.Finish);
        // Ready sorts the complete timeline, including diagnostics and pitch changes.
        AddChild(director);
    }

    public override void _PhysicsProcess(double delta) => script.Advance((float)delta);

    private async void CaptureAfterDraw(CaptureDirector director, string label)
    {
        if (DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsInsideTree()) director.Screenshot(label);
    }

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
