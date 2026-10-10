using System.Linq;
using Godot;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>
/// Runs the real game with a scripted player: watches the intro, turns to the nearest tank, shoots it twice,
/// then the next one, then regenerates the suburb with R.
/// Run: godot --fixed-fps 60 res://scenes/dev/game_test.tscn -- --capture-dir=/tmp/g
/// </summary>
public partial class GameTest : Node
{
    private readonly ScriptedInput input = new();
    private Game game = null!;
    private PropaneTank? target;

    public override void _Ready()
    {
        DevArgs.Setup();
        Core.DevLog.Enabled = DevArgs.Get("verbose-log") != null;
        game = new Game { PauseOnFocusLoss = false };
        AddChild(game);
        game.Player.InputOverride = input;
        var director = new CaptureDirector { QuitAfter = DevArgs.GetFloat("quit", 16f) };
        AddChild(director);
        if (DevArgs.Get("scenario") == "ragdoll")
        {
            ScriptRagdoll(director);
        }
        else if (DevArgs.Get("scenario") == "tour")
        {
            ScriptTour(director);
        }
        else if (DevArgs.Get("scenario") == "shoot")
        {
            // Fire at the front of a house from across the street, hip and aimed, catching the flash and tracer.
            director.At(2.4f, () =>
            {
                var lot = game.Current!.Plan.Lots.First(l => l.HouseModel >= 0);
                var front = lot.House.Center - lot.Bounds.AxisY * (lot.House.HalfExtents.Y + 9f);
                var to = lot.House.Center - front;
                var yaw = Mathf.Atan2(-to.X, -to.Y);
                game.Player.Teleport(World.Suburb.ToWorld(front, 0.05f), yaw);
                game.Player.CameraRig.SetOrientation(Mathf.RadToDeg(yaw), -2f);
            });
            for (var i = 0; i < 4; i++)
            {
                var at = 3f + i * 0.5f;
                director.At(at, () => input.Fire(T(0)));
                director.ShotAt(at + 0.02f, $"hip_{i}_a");
                director.ShotAt(at + 0.06f, $"hip_{i}_b");
            }
            director.At(5f, () => input.Hold(T(0), T(3f), new Player.PlayerIntent { Aim = true }));
            for (var i = 0; i < 4; i++)
            {
                var at = 5.6f + i * 0.5f;
                director.At(at, () => input.Fire(T(0)));
                director.ShotAt(at + 0.02f, $"aim_{i}_a");
                director.ShotAt(at + 0.06f, $"aim_{i}_b");
            }
            director.ShotAt(8f, "zz_impacts");
        }
        else if (DevArgs.Get("scenario") == "spots")
        {
            // A free camera visits every tank as placed, to check each sits right (on the ground, in the bed...).
            var camera = new Camera3D { Fov = 60 };
            foreach (var time in new[] { 0.05f, 0.3f, 0.6f, 1f, 2f })
            {
                director.At(time, () =>
                {
                    foreach (var tank in game.Current!.LiveTanks.Where(t => t.GlobalPosition.Y > 0.5f))
                    {
                        GD.Print($"[spots] t {time:0.00} raised tank at {tank.GlobalPosition} up {tank.GlobalBasis.Y.Y:0.00} sleeping {tank.Sleeping} v {tank.LinearVelocity.Length():0.00}");
                        foreach (var body in game.Current!.DynamicRoot.GetChildren().OfType<RigidBody3D>().Where(b => b is not PropaneTank && b.GlobalPosition.DistanceTo(tank.GlobalPosition) < 4f))
                        {
                            GD.Print($"[spots]    near {body.GetChild(0).Name} at {body.GlobalPosition} v {body.LinearVelocity.Length():0.00} w {body.AngularVelocity.Length():0.00} sleeping {body.Sleeping} rot {body.RotationDegrees}");
                        }
                    }
                });
            }
            director.At(2.5f, () =>
            {
                AddChild(camera);
                camera.Current = true;
            });
            for (var i = 0; i < 10; i++)
            {
                var index = i;
                director.At(2.6f + i * 0.5f, () =>
                {
                    // The first tank of each group.
                    var tanks = game.Current!.Plan.Tanks.Where(t => !t.Reason.EndsWith(" group")).ToList();
                    if (index >= tanks.Count)
                    {
                        return;
                    }
                    var tank = tanks[index];
                    var center = World.Suburb.ToWorld(tank.Position, tank.Elevation + 0.25f);
                    camera.LookAtFromPosition(center + new Vector3(2.6f, 2f, 3.2f), center);
                    GD.Print($"[spots] {index}: {tank.Reason} elevation {tank.Elevation:0.00}");
                });
                director.ShotAt(2.9f + i * 0.5f, $"spot_{i}");
            }
        }
        else if (DevArgs.Get("scenario") == "clear")
        {
            // Clears the whole suburb tank by tank, then waits for the automatic swap to the next one.
            for (var i = 0; i < 14; i++)
            {
                var start = 2.5f + i * 1.6f;
                director.At(start, () =>
                {
                    target = ApproachNearest(6f);
                    GD.Print($"[clear] {game.Current?.TanksRemaining} left, phase transition={game.InTransition}");
                    input.Hold(T(0), T(1.4f), new Player.PlayerIntent { Aim = true });
                });
                director.At(start + 0.5f, () => input.Fire(T(0)));
                director.At(start + 1.1f, () => input.Fire(T(0)));
            }
            director.At(25.5f, () =>
            {
                foreach (var tank in game.Current?.LiveTanks ?? Enumerable.Empty<PropaneTank>())
                {
                    GD.Print($"[clear] left: {tank.State} at {tank.GlobalPosition} up {tank.GlobalBasis.Y.Y:0.00} sleeping {tank.Sleeping}");
                }
            });
            director.ShotAt(26f, "cleared");
            director.ShotAt(31f, "next_suburb");
            director.At(31.5f, () => GD.Print($"[clear] next suburb has {game.Current?.TanksRemaining} tanks"));
        }
        else if (DevArgs.Get("scenario") == "dissolve")
        {
            // A venting tank in view, then R, then a detonation while the old suburb dissolves: nothing may linger.
            PropaneTank? venting = null;
            PropaneTank? late = null;
            director.At(2.5f, () =>
            {
                venting = ApproachNearest(8f);
                venting?.ChainHit(venting.GlobalPosition + Vector3.Up, 1f);
                late = game.Current!.LiveTanks.Where(t => t != venting).OrderBy(t => t.GlobalPosition.DistanceTo(game.Player.GlobalPosition)).FirstOrDefault();
                target = venting;
            });
            director.At(3.5f, () => target = null);
            director.ShotAt(4.4f, "00_venting");
            director.At(4.5f, () => game.RequestNewSuburb());
            // Blows up a second tank once the dissolve is under way, so its debris and scorch spawn mid-transition.
            director.At(4.75f, () => late?.Detonate());
            foreach (var time in new[] { 4.7f, 4.9f, 5.1f, 5.3f, 5.5f })
            {
                director.ShotAt(time, $"dissolve_{time:0.0}");
            }
            director.ShotAt(5.8f, "void");
            director.ShotAt(7f, "materialized");
        }
        else if (DevArgs.Get("scenario") == "auto")
        {
            // Holds the trigger for 1.5 s, aimed down the street: full auto should fire at the fire rate.
            director.At(2.5f, () => input.Hold(T(0), T(1.5f), new Player.PlayerIntent { FireHeld = true, Aim = true }));
            director.ShotAt(3.2f, "auto_firing");
        }
        else if (DevArgs.Get("scenario") == "jump")
        {
            // A running jump (the run must carry straight on after landing), then a standing one (crouch on landing).
            director.At(2.5f, () =>
            {
                input.Hold(T(0), T(2.6f), new Player.PlayerIntent { Move = new Vector2(0, 1) });
                input.Jump(T(0.3f));
                input.Jump(T(3.6f));
            });
            foreach (var time in new[] { 3.5f, 3.75f, 4.0f, 4.25f, 6.6f, 6.85f, 7.1f, 7.35f })
            {
                director.ShotAt(time, $"jump_{time:0.00}");
            }
        }
        else if (DevArgs.Get("scenario") == "settle")
        {
            // Every loose prop should stay exactly where the plan put it until something hits it.
            director.At(3f, ReportUnsettled);
        }
        else if (DevArgs.Get("scenario") == "readme")
        {
            ScriptReadme(director);
        }
        else if (DevArgs.Get("scenario") == "menus")
        {
            director.ShotAt(3f, "00_hud");
            director.At(3.2f, () => Press(Core.InputSetup.Pause));
            director.ShotAt(3.6f, "01_pause");
            director.At(3.8f, () => Press(Core.InputSetup.Pause));
            director.At(4.2f, () => Press(Core.InputSetup.ToggleTuning));
            director.ShotAt(4.6f, "02_tuning");
            director.At(4.8f, () => Press(Core.InputSetup.ToggleTuning));
            director.ShotAt(5.2f, "03_back");
        }
        else
        {
            ScriptLoop(director);
        }
    }

    /// <summary>The whole loop: intro, two tanks, then R for a new suburb.</summary>
    private void ScriptLoop(CaptureDirector director)
    {
        director.ShotAt(0.3f, "00_intro_start");
        director.ShotAt(0.8f, "01_intro_mid");
        director.ShotAt(1.8f, "02_intro_done");
        director.At(2.0f, () => target = ApproachNearest());
        director.At(2.1f, () => input.Hold(T(0), T(2.2f), new Player.PlayerIntent { Aim = true }));
        director.ShotAt(2.6f, "03_facing_tank");
        director.At(2.7f, () => input.Fire(T(0)));
        director.ShotAt(3.3f, "04_venting");
        director.At(4.0f, () => input.Fire(T(0)));
        director.ShotAt(4.08f, "05_boom");
        director.ShotAt(4.4f, "06_boom");
        director.ShotAt(5.5f, "07_after");
        director.At(6.0f, () => target = ApproachNearest());
        director.At(6.6f, () => { input.Hold(T(0), T(3f), new Player.PlayerIntent { Aim = true }); });
        director.ShotAt(7.0f, "08_aim_second");
        director.At(7.1f, () => input.Fire(T(0)));
        director.At(7.8f, () => input.Fire(T(0)));
        director.ShotAt(7.9f, "09_second_boom");
        director.ShotAt(9.0f, "10_second_after");
        director.At(10f, () => game.RequestNewSuburb());
        director.ShotAt(10.3f, "11_dissolve");
        director.ShotAt(10.9f, "12_void");
        director.ShotAt(11.5f, "13_materialize");
        director.ShotAt(12.5f, "14_new_suburb");
        director.ShotAt(15f, "15_settled");
    }

    /// <summary>
    /// The README footage: punctures two tanks in the biggest group, lets them vent, then detonates one into a chain,
    /// capturing a numbered frame sequence at 15 fps for the GIF.
    /// </summary>
    private void ScriptReadme(CaptureDirector director)
    {
        var distance = DevArgs.GetFloat("distance", 6f);
        PropaneTank? second = null;
        director.At(2.0f, () =>
        {
            var tanks = game.Current!.LiveTanks.ToList();
            var biggest = tanks.OrderByDescending(t => tanks.Count(o => o.GlobalPosition.DistanceTo(t.GlobalPosition) < 2.5f)).ToList();
            target = biggest.FirstOrDefault(t => TryStandFacing(t, distance) || TryStandFacing(t, distance * 0.7f)) ?? ApproachNearest(distance);
            second = tanks.Where(t => t != target).OrderBy(t => t.GlobalPosition.DistanceTo(target!.GlobalPosition)).FirstOrDefault();
            input.Hold(T(0), T(6f), new Player.PlayerIntent { Aim = true });
        });
        director.At(2.6f, () => input.Fire(T(0)));
        director.At(3.0f, () => target = second);
        director.At(3.3f, () => input.Fire(T(0)));
        director.At(4.6f, () => input.Fire(T(0)));
        // A venting tank can jink out of the way of that shot; the footage needs the blast either way.
        director.At(4.65f, () =>
        {
            if (second != null && IsInstanceValid(second) && second.State != PropaneTank.TankState.Exploded)
            {
                second.Detonate();
            }
        });
        for (var i = 0; i < 64; i++)
        {
            director.ShotAt(3.4f + i / 15f, $"frame_{i:000}");
        }
    }

    /// <summary>Blows up a tank a few meters away and follows the ragdoll and get-up from the player camera.</summary>
    private void ScriptRagdoll(CaptureDirector director)
    {
        var distance = DevArgs.GetFloat("distance", 4f);
        director.At(2.0f, () => target = ApproachNearest(distance));
        director.At(2.1f, () => input.Hold(T(0), T(1.5f), new Player.PlayerIntent { Aim = true }));
        director.ShotAt(2.5f, "00_facing");
        director.At(2.6f, () => input.Fire(T(0)));
        director.At(3.2f, () => input.Fire(T(0)));
        float[] times = { 3.25f, 3.35f, 3.5f, 3.7f, 4.0f, 4.4f, 4.9f, 5.5f, 6.2f, 7f, 7.8f, 8.6f, 9.4f, 10.5f, 12f };
        for (var i = 0; i < times.Length; i++)
        {
            director.ShotAt(times[i], $"{i + 1:00}_t{times[i]:0.00}");
        }
    }

    /// <summary>Stands the player in awkward spots (against a house, by a fence, at and beyond the slab edge) to check the camera.</summary>
    private void ScriptTour(CaptureDirector director)
    {
        void Stand(float time, string name, System.Func<(Vector3 Position, float Yaw, float Pitch)> where)
        {
            director.At(time, () =>
            {
                var (position, yaw, pitch) = where();
                game.Player.Teleport(position, yaw);
                game.Player.CameraRig.SetOrientation(Mathf.RadToDeg(yaw), pitch);
            });
            director.ShotAt(time + 0.6f, name);
        }

        World.Plan.Lot Lot() => game.Current!.Plan.Lots.First(l => l.HouseModel >= 0);
        // Backyard, 0.6 m behind the house, facing away from it: the camera has to pull in off the wall.
        Stand(2.5f, "00_against_house", () =>
        {
            var lot = Lot();
            var behind = lot.House.Center + lot.Bounds.AxisY * (lot.House.HalfExtents.Y + 0.9f);
            var facing = lot.Bounds.AxisY;
            return (World.Suburb.ToWorld(behind, 0.05f), Mathf.Atan2(-facing.X, -facing.Y), -10f);
        });
        // Same spot, looking steeply up and then down.
        Stand(3.5f, "01_look_up", () =>
        {
            var lot = Lot();
            var behind = lot.House.Center + lot.Bounds.AxisY * (lot.House.HalfExtents.Y + 0.9f);
            var facing = lot.Bounds.AxisY;
            return (World.Suburb.ToWorld(behind, 0.05f), Mathf.Atan2(-facing.X, -facing.Y), 60f);
        });
        Stand(4.5f, "02_look_down", () =>
        {
            var lot = Lot();
            var behind = lot.House.Center + lot.Bounds.AxisY * (lot.House.HalfExtents.Y + 0.9f);
            var facing = lot.Bounds.AxisY;
            return (World.Suburb.ToWorld(behind, 0.05f), Mathf.Atan2(-facing.X, -facing.Y), -70f);
        });
        // Hugging a fence, looking along it.
        Stand(5.5f, "03_fence", () =>
        {
            var fence = game.Current!.Plan.Fences.OrderByDescending(f => f.A.DistanceTo(f.B)).First();
            var along = (fence.B - fence.A).Normalized();
            var side = new Vector2(-along.Y, along.X);
            var spot = (fence.A + fence.B) / 2f + side * 0.45f;
            return (World.Suburb.ToWorld(spot, 0.05f), Mathf.Atan2(-along.X, -along.Y), -5f);
        });
        // Out in the void beyond the slab edge, looking back at the suburb, then looking up from the void.
        Stand(6.5f, "04_void_look_back", () =>
        {
            var bounds = game.Current!.Plan.Bounds;
            var spot = new Vector2(bounds.GetCenter().X, bounds.End.Y + 12f);
            return (new Vector3(spot.X, 0.05f, spot.Y), 0f, -6f);
        });
        Stand(7.5f, "05_void_look_up", () =>
        {
            var bounds = game.Current!.Plan.Bounds;
            var spot = new Vector2(bounds.GetCenter().X, bounds.End.Y + 12f);
            return (new Vector3(spot.X, 0.05f, spot.Y), 0f, 60f);
        });
        // On the slab edge, walking out into the void.
        director.At(8.5f, () =>
        {
            var bounds = game.Current!.Plan.Bounds;
            game.Player.Teleport(new Vector3(bounds.GetCenter().X, 0.2f, bounds.End.Y - 2f), Mathf.Pi);
            game.Player.CameraRig.SetOrientation(180f, -12f);
            input.Hold(T(0), T(1.5f), new Player.PlayerIntent { Move = new Vector2(0, 1) });
        });
        director.ShotAt(8.9f, "06_edge_walk");
        director.ShotAt(9.6f, "07_edge_off");
    }

    private void ReportUnsettled()
    {
        var suburb = game.Current!;
        var bodies = suburb.DynamicRoot.GetChildren().OfType<RigidBody3D>().Where(b => b.HasMeta(World.Suburb.PlanIndexMeta)).ToList();
        var moved = 0;
        foreach (var body in bodies)
        {
            var start = suburb.Plan.Props[body.GetMeta(World.Suburb.PlanIndexMeta).AsInt32()];
            var drift = new Vector2(body.GlobalPosition.X, body.GlobalPosition.Z).DistanceTo(start.Position);
            var tilt = Mathf.RadToDeg(body.GlobalBasis.Y.AngleTo(Vector3.Up));
            if (drift > 0.15f || tilt > 4f)
            {
                moved++;
                GD.Print($"[settle] {start.Model} drifted {drift:0.00} m, tilted {tilt:0.0} deg at {body.GlobalPosition}");
            }
        }
        var tanks = suburb.LiveTanks.Count(t => Mathf.RadToDeg(t.GlobalBasis.Y.AngleTo(Vector3.Up)) > 4f);
        GD.Print($"[settle] seed {suburb.Plan.Seed}: {moved} of {bodies.Count} props unsettled, {tanks} tanks tipped");
    }

    private float clock;

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private float T(float offset) => clock + offset;

    public override void _PhysicsProcess(double delta)
    {
        clock += (float)delta;
        input.Advance((float)delta);
        if (target != null && IsInstanceValid(target))
        {
            // Aim exactly, like a perfect player would, from wherever the camera actually is.
            var rig = game.Player.CameraRig;
            var to = target.GlobalTransform * PropaneTank.LocalCenter - rig.Camera.GlobalPosition;
            var yaw = Mathf.RadToDeg(Mathf.Atan2(-to.X, -to.Z));
            var pitch = Mathf.RadToDeg(Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length()));
            rig.SetOrientation(yaw, pitch);
        }
    }

    /// <summary>Puts the player a set distance from the nearest tank, somewhere with a clear line of sight to it.</summary>
    private PropaneTank? ApproachNearest(float distance = 7f)
    {
        // The nearest tank with a clear firing spot; if none has one, the nearest anyway.
        var tanks = game.Current?.LiveTanks.OrderBy(t => t.GlobalPosition.DistanceTo(game.Player.GlobalPosition)).ToList() ?? new();
        foreach (var tank in tanks)
        {
            foreach (var range in new[] { distance, distance * 0.6f, distance * 1.5f })
            {
                if (TryStandFacing(tank, range))
                {
                    return tank;
                }
            }
        }
        return tanks.FirstOrDefault();
    }

    /// <summary>Teleports the player a set distance from a tank, somewhere with a clear line of sight to it.</summary>
    private bool TryStandFacing(PropaneTank tank, float distance)
    {
        var space = game.Player.GetWorld3D().DirectSpaceState;
        var center = tank.GlobalTransform * PropaneTank.LocalCenter;
        for (var i = 0; i < 24; i++)
        {
            var angle = i * Mathf.Tau / 24f;
            var stand = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
            var eye = new Vector3(stand.X, 1.7f, stand.Z);
            var down = PhysicsRayQueryParameters3D.Create(eye + Vector3.Up * 3f, new Vector3(stand.X, -1, stand.Z), Core.Layers.World | Core.Layers.Props);
            var ground = space.IntersectRay(down);
            var side = new Vector3(Mathf.Cos(angle + Mathf.Pi / 2), 0, Mathf.Sin(angle + Mathf.Pi / 2));
            var clear = true;
            // Clear only if the first thing hit, from both the eye and roughly where the camera will be, is the tank.
            foreach (var from in new[] { eye, eye + side * 0.7f + (eye - center).Normalized() * 3.4f, eye - side * 0.7f + (eye - center).Normalized() * 3.4f })
            {
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, center + (center - from).Normalized(), Core.Layers.Shootable));
                clear &= hit.Count > 0 && hit["collider"].As<GodotObject>() == tank;
            }
            if (clear && ground.Count > 0 && ground["position"].AsVector3().Y < 0.2f && !BushesBetween(eye, center))
            {
                var yaw = Mathf.Atan2(-(center.X - stand.X), -(center.Z - stand.Z));
                game.Player.Teleport(new Vector3(stand.X, 0.2f, stand.Z), yaw);
                GD.Print($"[test] target tank at {center} player at {stand}");
                return true;
            }
        }
        return false;
    }

    /// <summary>Bushes have no collision but still hide a tank, so the test avoids looking through them.</summary>
    private bool BushesBetween(Vector3 from, Vector3 to)
    {
        foreach (var prop in game.Current!.Plan.Props.Where(p => p.Model.Contains("bush")))
        {
            var p = World.Suburb.ToWorld(prop.Position, 0.5f);
            var t = Mathf.Clamp((p - from).Dot((to - from).Normalized()), 0, from.DistanceTo(to));
            if ((from + (to - from).Normalized() * t).DistanceTo(p) < 1.2f)
            {
                return true;
            }
        }
        return false;
    }
}
