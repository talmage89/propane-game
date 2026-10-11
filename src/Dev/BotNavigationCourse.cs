using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Navigation;
using Propane.Player;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev;

/// <summary>Seeded courses using the real suburb fence builder and player controller, not a simulated capsule.</summary>
public partial class BotNavigationCourse : Node3D, IPlayerInputSource
{
    private WorldContext world = null!;
    public override void _ExitTree() => world.Dispose();

    private readonly BotNavigator navigator = new();
    private PlayerCharacter player = null!;
    private Suburb? suburb;
    private Camera3D observer = null!;
    private CaptureDirector director = null!;
    private PlayerIntent intent;
    private Vector3 goal;
    private Vector3 lastPosition;
    private float stuck;
    private float elapsed;
    private int seed;
    private int firstSeed;
    private int course;
    private int completed;
    private int passed;
    private int stuckEvents;
    private int jumps;
    private bool captured;
    private bool capturedLanding;
    private float fenceHeight;
    private int picketCourses;
    private int privacyCourses;
    private int courseJumps;
    private readonly List<float> times = new();

    public PlayerIntent Read()
    {
        var read = intent;
        intent.JumpPressed = false;
        return read;
    }

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        ProcessPhysicsPriority = -10;
        DevArgs.Setup();
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        InputSetup.Register();
        AddChild(new VoidEnvironment());
        player = new PlayerCharacter { World = world, Name = "CourseBot", InputOverride = this };
        AddChild(player);
        observer = new Camera3D { Current = true, Fov = 55 };
        AddChild(observer);
        director = new CaptureDirector();
        AddChild(director);
        firstSeed = seed = (int)DevArgs.GetFloat("first-seed", 1) - 1;
        Begin();
    }

    private void Begin()
    {
        if (suburb != null)
        {
            RemoveChild(suburb);
            suburb.QueueFree();
        }
        // Different seeds select actual picket/privacy styles. A central gap in every third course competes
        // with jumping; the other courses require a crossing or a substantial detour around the ends.
        var plan = new SuburbPlan { Seed = seed + 1, Bounds = new Rect2(-16, -16, 32, 32) };
        if (course == 2)
        {
            plan.Fences.Add(new FenceRun(new Vector2(-12, 0), new Vector2(-1, 0)));
            plan.Fences.Add(new FenceRun(new Vector2(1, 0), new Vector2(12, 0)));
        }
        else
        {
            plan.Fences.Add(new FenceRun(new Vector2(-12, 0), new Vector2(12, 0)));
        }
        if (course == 1)
        {
            plan.Props.Add(new PropPlacement("prop:wheelie_bin", new Vector2(0, -4), 0, 1, PropBody.Loose, 20, Colors.Gray));
        }
        if (course == 3)
        {
            plan.Props.Add(new PropPlacement("car:sedan", new Vector2(0, 2), 0, 1, PropBody.Breakaway, 1300, Colors.SlateGray));
        }
        if (course == 4)
        {
            for (var i = 0; i < 6; i++)
            {
                plan.Tanks.Add(new TankPlacement(new Vector2((i % 3 - 1) * 0.4f, -2.5f - i / 3 * 0.4f), 0, 0, "course pile"));
            }
        }
        if (course == 5)
        {
            var house = new OrientedRect(new Vector2(0, 3), Catalog.Houses[0].Footprint / 2, 0);
            plan.Lots.Add(new Lot { Bounds = house.Grown(2), House = house, Seed = seed + 1, HouseModel = 0 });
        }
        suburb = Suburb.Build(world, plan, new SuburbSettings { TankCount = 0 });
        AddChild(suburb);
        suburb.Rotation = new Vector3(0, (seed % 7 - 3) * 0.13f, 0);
        player.GlobalPosition = suburb.GlobalTransform * new Vector3(course == 1 ? 3 : (seed % 5 - 2) * 0.25f,
            Suburb.GroundHeight, course == 6 ? 0.385f : 7 + seed % 3 * 0.4f);
        player.Velocity = Vector3.Zero;
        player.Aim.SetOrientation(0, 0);
        goal = suburb.GlobalTransform * new Vector3((seed * 7 % 5 - 2) * 0.25f, Suburb.GroundHeight, -7 - seed % 2 * 0.3f);
        navigator.Reset();
        courseJumps = navigator.Jumps;
        var fence = suburb.DynamicRoot.GetChildren().OfType<PhysicsBody3D>().First(p => p.Name == "FencePanel");
        var height = ((BoxShape3D)fence.GetChildren().OfType<CollisionShape3D>().Single().Shape).Size.Y;
        fenceHeight = height;
        if (height < 1.5f) { picketCourses++; } else { privacyCourses++; }
        lastPosition = player.GlobalPosition;
        elapsed = stuck = 0;
        captured = capturedLanding = false;
        observer.GlobalPosition = new Vector3(11, 7, 9);
        observer.LookAt(new Vector3(0, 1, 0));
    }

    public override void _PhysicsProcess(double delta)
    {
        try { Step((float)delta); }
        catch (Exception e)
        {
            GD.PushError($"[bot-course] FAIL {e}");
            SetPhysicsProcess(false);
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void Step(float dt)
    {
        elapsed += dt;
        intent = new PlayerIntent();
        if (elapsed < 0.3f) { return; }
        var here = player.GlobalPosition;
        var to = goal - here;
        to.Y = 0;
        if (DevArgs.Get("baseline") != null)
        {
            var yaw = Mathf.RadToDeg(Mathf.Atan2(-to.X, -to.Z));
            player.Aim.SetOrientation(yaw, 0);
            intent.Move = Vector2.Down;
            var moved = here.DistanceTo(lastPosition);
            lastPosition = here;
            stuck = moved < 0.02f ? stuck + dt : 0;
            if (stuck > 0.4f && stuck - dt <= 0.4f) { stuckEvents++; }
            if (stuck > 0.4f)
            {
                intent.JumpPressed = true;
                intent.Move = new Vector2(1, 0.4f);
                if (stuck > 2.5f) { stuck = 0; }
            }
            if (intent.JumpPressed && player.IsOnFloor()) { jumps++; }
        }
        else
        {
            var t = world.Tuning;
            var shape = player.GetChildren().OfType<CollisionShape3D>().Single();
            var capabilities = new NavigationCapabilities(shape.Shape, shape.Position, ((CapsuleShape3D)shape.Shape).Radius,
                t.RunSpeed, t.SprintSpeed, t.JumpVelocity, t.PlayerGravity, t.Acceleration, t.AirControl);
            var hazards = suburb!.LiveTanks.Select(tank => new NavigationHazard(tank.GlobalPosition, t.BotTankClearance)).ToArray();
            var navigation = new NavigationWorld(GetWorld3D().DirectSpaceState, capabilities, hazards, t.BotJumpMargin, minimumJumpRange: t.BotJumpMinimumRange);
            var move = navigator.Navigate(navigation, new NavigationState(here, player.Velocity, player.IsOnFloor()), capabilities, goal, t, dt);
            if (move.Direction.LengthSquared() > 0)
            {
                var yaw = Mathf.RadToDeg(Mathf.Atan2(-move.Direction.X, -move.Direction.Z));
                player.Aim.SetOrientation(yaw, 0);
                intent.Move = Vector2.Down;
            }
            if (!player.IsOnFloor())
            {
                foreach (var hazard in hazards)
                {
                    var flat = new Vector2(here.X - hazard.Center.X, here.Z - hazard.Center.Z);
                    if (flat.Length() < hazard.Radius) { throw new InvalidOperationException("jump entered the tank pile clearance zone"); }
                }
            }
            intent.Sprint = move.Sprint;
            intent.JumpPressed = move.Jump;
            if (move.Jump)
            {
                var landing = here + move.Direction * capabilities.JumpRange;
                var pile = new NavigationWorld(GetWorld3D().DirectSpaceState, capabilities,
                    new[] { new NavigationHazard(here.Lerp(landing, 0.5f), t.BotTankClearance) }, t.BotJumpMargin, t.BotJumpMinimumRange);
                var vent = new NavigationWorld(GetWorld3D().DirectSpaceState, capabilities,
                    new[] { new NavigationHazard(landing, t.RagdollRadius + 1) }, t.BotJumpMargin, t.BotJumpMinimumRange);
                if (pile.Jump(here, landing) || vent.Jump(here, landing))
                {
                    throw new InvalidOperationException("jump must reject pile and vent clearance zones");
                }
                if (course == 0)
                {
                    var escape = new NavigationWorld(GetWorld3D().DirectSpaceState, capabilities,
                        new[] { new NavigationHazard(here, t.RagdollRadius + 1) }, t.BotJumpMargin, t.BotJumpMinimumRange, here);
                    if (!escape.Walk(here, here - move.Direction * 2))
                    {
                        throw new InvalidOperationException("a bot already inside danger must be able to walk out");
                    }
                }
            }
            if (DevArgs.Get("nav-log") != null && Mathf.PosMod(elapsed, 0.5f) < dt)
            {
                GD.Print($"[bot-course trace] seed={seed + 1} course={course} t={elapsed:0.00} pos={here} vel={player.Velocity} dir={move.Direction} jump={move.Jump} preparing={navigator.Jumping} failed={move.Failed} route={navigator.Route.Count} first={navigator.Route.FirstOrDefault()} stuck={navigator.StuckEvents} replan={navigator.Replans}");
            }
            stuckEvents = navigator.StuckEvents;
            jumps = navigator.Jumps;
        }
        if (!captured && here.Y > Suburb.GroundHeight + fenceHeight + 0.2f && Mathf.Abs(suburb!.ToLocal(here).Z) < 0.4f)
        {
            captured = true;
            director.Screenshot($"seed{seed + 1}_course{course}_{(fenceHeight < 1.5f ? "picket" : "privacy")}_air");
        }
        if (captured && !capturedLanding && player.IsOnFloor() && suburb!.ToLocal(here).Z < -1)
        {
            capturedLanding = true;
            director.Screenshot($"seed{seed + 1}_course{course}_landed");
        }
        if (to.Length() < 0.7f || elapsed > 15f)
        {
            var success = to.Length() < 0.7f;
            if (DevArgs.Get("baseline") == null && course == 2 && navigator.Jumps != courseJumps)
            {
                throw new InvalidOperationException("a gate as fast as jumping must be walked through");
            }
            if (DevArgs.Get("baseline") == null && course == 0 && success && navigator.Jumps == courseJumps)
            {
                throw new InvalidOperationException("closed fence course must use a jump, not the long detour");
            }
            completed++;
            if (success) { passed++; times.Add(elapsed); }
            GD.Print($"[bot-course] seed={seed + 1} course={course} pass={success} time={elapsed:0.00} distance={to.Length():0.00}");
            course++;
            if (course == 7) { course = 0; seed++; }
            if (seed - firstSeed >= (int)DevArgs.GetFloat("seeds", 20))
            {
                var rate = (float)passed / completed;
                GD.Print($"[bot-course] success={passed}/{completed} rate={rate:0.000} mean={(times.Count > 0 ? times.Average() : 0):0.00}s stuck={stuckEvents} jumps={jumps} picket={picketCourses} privacy={privacyCourses}");
                ShutdownGuard.Quit(GetTree(), DevArgs.Get("baseline") != null || rate >= 0.95f ? 0 : 1);
                SetPhysicsProcess(false);
            }
            else { Begin(); }
        }
    }
}
