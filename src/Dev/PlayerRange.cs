using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>
/// Scripted player test: locomotion, hip fire, aimed fire, reloads, a tank blast that ragdolls the player, and the get-up.
/// Run: godot-mono --fixed-fps 60 res://scenes/dev/player_range.tscn -- --capture-dir=$PWD/build/player-range [--scenario=...] [--view=side]
/// Views: player, side, front, left, top, rightclose, leftclose, rifleleft, rifleright.
/// </summary>
public partial class PlayerRange : Node3D
{
    private WorldContext world = null!;
    public override void _ExitTree() => world.Dispose();

    private readonly ScriptedInput script = new();
    private PlayerCharacter player = null!;
    private Camera3D? observer;
    private CameraRig? reviewCamera;
    private string view = "player";

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        ProcessPhysicsPriority = -10;
        DevArgs.Setup();
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        if (ulong.TryParse(DevArgs.Get("effects-seed"), out var effectsSeed)) world.Random.Seed = effectsSeed;
        InputSetup.Register();
        Core.DevLog.Enabled = DevArgs.Get("verbose-log") != null;
        var dynamic = new Node3D { Name = "Dynamic" };
        AddChild(dynamic);

        world.SetContentRoot(dynamic);
        var env = new VoidEnvironment();
        AddChild(env);

        var scenario = DevArgs.Get("scenario") ?? "stances";
        player = new PlayerCharacter { World = world, Name = "Player", IsRemote = scenario == "feet" };
        AddChild(player);
        if (!player.IsRemote) LocalView.Attach(player, this).Hud.ShowHud(false);
        player.InputOverride = script;
        env.Follow = player;

        view = DevArgs.Get("view") ?? (scenario == "feet" ? "side" : "player");
        if (view == "player" && player.IsRemote)
        {
            reviewCamera = new CameraRig { World = world, Name = "ReviewCamera" };
            AddChild(reviewCamera);
            reviewCamera.SetOrientation(0, -10);
        }
        else if (view != "player")
        {
            observer = new Camera3D { Fov = 40, Current = true };
            AddChild(observer);
        }

        var director = new CaptureDirector { QuitAfter = DevArgs.GetFloat("quit", scenario == "grip" ? (DevArgs.Get("grip-sweep") != null ? 39.2f : 15.2f) : 10f) };
        GripProbe? grip = null;
        if (DevArgs.Get("grip-metrics") != null || DevArgs.Get("assert-grip") != null)
            grip = GripProbe.Attach(player, scenario == "grip");
        var motion = DevArgs.Get("motion-metrics") != null || DevArgs.Get("assert-motion") != null
            ? MotionProbe.Attach(player, scenario == "motion") : null;
        switch (scenario)
        {
            case "bot-recoil":
                world.Random.Seed = 19019;
                player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 30, Reserve = 0 };
                script.Hold(0, 41, new PlayerIntent { Aim = DevArgs.GetFloat("distance", 20) > 9 });
                var magazines = System.Math.Clamp((int)DevArgs.GetFloat("magazines", 10), 1, 10);
                for (var magazine = 0; magazine < magazines; magazine++)
                    script.Hold(magazine * 4 + 1, magazine * 4 + 3.8f, new PlayerIntent { FireHeld = true });
                AddChild(new BotRecoilProbe { Player = player });
                director.QuitAfter = magazines * 4 + 2;
                break;
            case "recoil":
                world.Random.Seed = 19019;
                player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = world.Tuning.MagazineSize, Reserve = 0 };
                player.Aim.SetOrientation(0, DevArgs.GetFloat("pitch", -10));
                var stance = DevArgs.Get("stance") ?? "aim";
                script.Hold(.1f, 9.9f, new PlayerIntent { Aim = stance != "hip", Move = stance == "creep" ? Vector2.Down : Vector2.Zero });
                if (stance == "air") script.Jump(.8f);
                script.Hold(DevArgs.GetFloat("burst-start", 1), 4.6f, new PlayerIntent { FireHeld = true });
                var recoil = RecoilProbe.Attach(player, director);
                director.At(9.8f, recoil.Finish);
                director.QuitAfter = 10;
                break;
            case "melee-reloads":
                director.QuitAfter = 31;
                for (var episode = 0; episode < 10; episode++)
                {
                    var start = episode * 3;
                    var empty = episode >= 5;
                    director.At(start + .05f, () => player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = empty ? 0 : 5, Reserve = 90 });
                    script.Reload(start + .2f);
                    script.Melee(start + .2f + (0.1f + episode % 5 * .2f) * world.Tuning.ReloadTime);
                    director.At(start + 2.9f, () =>
                    {
                        if (player.Ammo!.Reloading || player.Ammo.Magazine != (empty ? 0 : 5) || player.Ammo.Reserve != 90)
                        {
                            GD.PushError("[melee-reloads] FAIL canceled reload transferred ammo or remained active");
                            ShutdownGuard.Quit(GetTree(), 1);
                        }
                    });
                }
                break;
            case "melee":
                player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 5, Reserve = 90 };
                script.Melee(1);
                script.Hold(2.5f, 3.4f, new PlayerIntent { Aim = true });
                script.Melee(3);
                script.Hold(4.5f, 5, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                script.Melee(5);
                script.Reload(6.5f);
                script.Melee(7);
                director.QuitAfter = 9;
                for (var swing = 0; swing < 4; swing++)
                {
                    for (var frame = 0; frame < 66; frame++)
                    {
                        var name = $"melee_{swing}_{frame:000}";
                        director.At(0.9f + swing * 2 + frame / 60f, () => CaptureAfterDraw(director, name));
                    }
                }

                break;
            case "motion":
                player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 30, Reserve = 90 };
                script.Hold(0.5f, 3, new PlayerIntent { Move = Vector2.Down * 0.3f });
                script.Hold(3, 5.5f, new PlayerIntent { Move = Vector2.Down });
                script.Hold(5.5f, 8, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                script.Hold(8, 11, new PlayerIntent { Move = Vector2.Down, Aim = true });
                script.Hold(11.5f, 12.4f, new PlayerIntent { FireHeld = true });
                script.Hold(13.5f, 15, new PlayerIntent { Aim = true });
                script.Hold(14, 14.9f, new PlayerIntent { FireHeld = true });
                script.Hold(15.5f, 18, new PlayerIntent { Move = Vector2.Down * 0.3f });
                script.Reload(15.6f);
                director.At(18.5f, () => player.RemoteRagdoll(player.GlobalPosition, new Vector3(0, 2, -1), Vector3.Zero));
                script.Hold(20, 25, new PlayerIntent { Move = Vector2.Down });
                director.QuitAfter = 26;
                foreach (var (start, seconds, label) in new (float, float, string)[]
                {
                    (1.5f, 1, "walk"), (4, 1, "run"), (6.5f, 1, "sprint"), (9.5f, 1, "creep"),
                    (11.4f, 1.8f, "hip"), (13.9f, 1.8f, "aim"), (15.5f, 2.5f, "reload"), (19, 6, "getup"),
                })
                {
                    if (DevArgs.Get("capture-group") is string group && group != label && !(group == "recoil" && (label == "hip" || label == "aim"))) continue;
                    for (var frame = 0; frame < (int)(seconds * 60); frame++)
                    {
                        var name = $"{label}_{frame:000}";
                        director.At(start + frame / 60f, () => CaptureAfterDraw(director, name));
                    }
                }
                break;
            case "feet-events":
                var events = LocomotionProbe.Attach(player, true);
                var timing = JumpTimingProbe.Attach(player, true);
                events.Label = "events";
                script.Hold(0.5f, 2, new PlayerIntent { Move = Vector2.Down, Aim = true });
                script.Hold(2.3f, 4.3f, new PlayerIntent { Move = Vector2.Up, Aim = true });
                script.Hold(4.3f, 6, new PlayerIntent { Move = Vector2.Right, Aim = true });
                script.Hold(6.3f, 8.3f, new PlayerIntent { Aim = true });
                // A half-turn exceeds the controller's standing twist, so the body turns and the feet step.
                var turnLook = Mathf.Pi / world.Tuning.MouseSensitivity / 1.2f;
                script.Look(6.5f, 7.7f, new Vector2(turnLook, 0));
                script.Look(7.7f, 8.9f, new Vector2(-turnLook, 0));
                director.At(6.5f, () => events.SampleTurns = true);
                director.At(8.3f, () => { events.SampleTurns = false; events.AssertTurns(); });
                script.Hold(8.5f, 13.5f, new PlayerIntent { Move = Vector2.Down });
                script.Hold(9.2f, 10.3f, new PlayerIntent { Sprint = true });
                script.Hold(10.3f, 11.4f, new PlayerIntent { Aim = true });
                script.Hold(11.4f, 13.5f, new PlayerIntent { Aim = true });
                script.Jump(11.6f);
                script.Jump(14.5f);
                script.Hold(16, 19.5f, new PlayerIntent { Move = Vector2.Down, Aim = true });
                // Walk down from a slab onto a curb, ramp and void floor. Character physics stays untouched.
                director.At(15.7f, () =>
                {
                    AddFootSurface(new Vector3(0, 0.12f, -3), new Vector3(3, 0.24f, 6));
                    AddFootSurface(new Vector3(0, 0.06f, -6.4f), new Vector3(3, 0.12f, 0.8f));
                    AddFootSurface(new Vector3(0, 0.03f, -7.3f), new Vector3(3, 0.12f, 1), 6);
                });
                director.At(15.8f, () => { player.Teleport(new Vector3(0, 0.24f, -4.5f), 0); events.SkipNextPose = true; if (motion != null) motion.SkipNextPose = true; });
                director.At(19.7f, () => { player.Teleport(new Vector3(0.05f, 0.24f, -6), Mathf.Pi / 2); events.SkipNextPose = true; if (motion != null) motion.SkipNextPose = true; });
                director.At(20.6f, () => { player.Teleport(new Vector3(0, 0, -8.5f), Mathf.Pi / 2); events.SkipNextPose = true; if (motion != null) motion.SkipNextPose = true; });
                script.Hold(20.7f, 21.5f, new PlayerIntent { Aim = true });
                script.Look(20.7f, 21.5f, new Vector2(Mathf.Pi / 2 / world.Tuning.MouseSensitivity / 0.8f, 0));
                script.Hold(21.6f, 24.5f, new PlayerIntent { Move = Vector2.Up, Aim = true });
                director.QuitAfter = 25;
                for (var frame = 0; frame < 25 * 12; frame++)
                {
                    var label = $"event_{frame:000}";
                    director.At(frame / 12f, () => CaptureAfterDraw(director, label));
                }
                director.At(24.9f, () => { events.Finish(); timing.Finish(); });
                break;
            case "feet":
                var feet = LocomotionProbe.Attach(player);
                AddChild(new LocomotionMatrix { Player = player, Probe = feet });
                director.QuitAfter = LocomotionMatrix.Episodes * LocomotionMatrix.EpisodeTime + 0.1f;
                for (var episode = 0; episode < LocomotionMatrix.Episodes; episode++)
                {
                    for (var frame = 0; frame < 25; frame++)
                    {
                        var label = $"{(PlayerGait)(episode / 8)}_{episode % 8 * 45:000}_{frame:00}";
                        if (DevArgs.Get("capture-group") is string episodeGroup && !label.StartsWith(episodeGroup, System.StringComparison.Ordinal)) continue;
                        director.At(episode * LocomotionMatrix.EpisodeTime + 1.033333f + frame / 15f, () => CaptureAfterDraw(director, label));
                    }
                }
                director.At(director.QuitAfter - 0.05f, () => feet.Finish(24));
                break;
            case "creep":
                // Two full strides after acceleration and gait/rig blends settle; 12 frames per stride.
                var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>()
                    .Single(s => s.FindBone("foot_l") >= 0);
                var period = world.Tuning.WalkStrideLength / world.Tuning.CreepSpeed;
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
                GD.Print($"[creep] speed={world.Tuning.CreepSpeed:0.00} stride-length={world.Tuning.WalkStrideLength:0.000}m stride={period:0.000}s");
                break;
            case "grip":
                player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 3, Reserve = 60 };
                script.Hold(1, 4, new PlayerIntent { Aim = true });
                script.Hold(2, 2.6f, new PlayerIntent { FireHeld = true });
                script.Reload(4.1f);
                script.Fire(6);
                script.Reload(6.4f);
                script.Hold(8.3f, 9.5f, new PlayerIntent { Move = Vector2.Down });
                script.Hold(9.5f, 10.7f, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                script.Hold(10.7f, 14.8f, new PlayerIntent { Move = Vector2.Right, Aim = true });
                director.At(11.6f, () => ActorView.For(player)!.CameraRig.SetOrientation(0, -75));
                director.At(12.8f, () => ActorView.For(player)!.CameraRig.SetOrientation(0, 45));
                director.At(14, () => ActorView.For(player)!.CameraRig.SetOrientation(0, -10));
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
                    director.At(15.2f, () => player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 30, Reserve = 90 });
                    script.Hold(15.2f, 17.8f, new PlayerIntent { Aim = true });
                    script.Hold(16, 16.4f, new PlayerIntent { FireHeld = true });
                    script.Hold(17, 17.4f, new PlayerIntent { FireHeld = true });
                    script.Hold(20, 23, new PlayerIntent { Move = Vector2.Down });
                    script.Hold(23, 26, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                    director.At(26, () => player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 30, Reserve = 90 });
                    script.Hold(26, 28.9f, new PlayerIntent { FireHeld = true });
                    script.Hold(29, 32, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                    script.Reload(29.5f);
                    foreach (var start in new[] { 15.7f, 18f, 20.5f, 23.5f, 26.5f })
                    {
                        director.At(start, () => ActorView.For(player)!.CameraRig.SetOrientation(0, -75));
                        director.At(start + 1, () => ActorView.For(player)!.CameraRig.SetOrientation(0, 45));
                    }
                    director.At(29, () => ActorView.For(player)!.CameraRig.SetOrientation(0, -10));
                    director.At(33.2f, () =>
                    {
                        ActorView.For(player)!.CameraRig.SetOrientation(0, -75);
                        player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 0, Reserve = 90 };
                    });
                    script.Reload(33.5f);
                    director.At(36.2f, () =>
                    {
                        ActorView.For(player)!.CameraRig.SetOrientation(0, 45);
                        player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 5, Reserve = 90 };
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
                player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 3, Reserve = 60 };
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
        if (motion != null) director.At(director.QuitAfter - 0.08f, motion.Finish);
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
        if (reviewCamera != null)
        {
            reviewCamera.Target = player.GlobalPosition + Vector3.Up * world.Tuning.CameraHeight;
            return;
        }
        if (observer == null)
        {
            return;
        }
        var focus = player.IsRagdolled ? ActorView.For(player)!.CameraRig.Target : player.GlobalPosition + Vector3.Up * 1.0f;
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

    private void AddFootSurface(Vector3 position, Vector3 size, float slope = 0)
    {
        var body = new StaticBody3D { Position = position, CollisionLayer = Layers.World, CollisionMask = 0 };
        body.RotationDegrees = new Vector3(slope, 0, 0);
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.47f, 0.52f) },
        });
        AddChild(body);
    }

    private readonly System.Collections.Generic.List<PropaneTank> tanks = new();

    private void AddTank(Vector3 position)
    {
        var tank = PropaneTank.Create(world);
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
