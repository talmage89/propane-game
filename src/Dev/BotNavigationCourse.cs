using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev;

/// <summary>Seeded courses using the real suburb fence builder and player controller, not a simulated capsule.</summary>
public partial class BotNavigationCourse : Node3D, IPlayerInputSource
{
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
    private int course;
    private int completed;
    private int passed;
    private int stuckEvents;
    private int jumps;
    private bool captured;
    private readonly List<float> times = new();

    public PlayerIntent Read()
    {
        var read = intent;
        intent.JumpPressed = false;
        return read;
    }

    public override void _Ready()
    {
        ProcessPhysicsPriority = -10;
        DevArgs.Setup();
        InputSetup.Register();
        AddChild(new VoidEnvironment());
        player = new PlayerCharacter { Name = "CourseBot", InputOverride = this };
        AddChild(player);
        observer = new Camera3D { Current = true, Fov = 55 };
        AddChild(observer);
        director = new CaptureDirector();
        AddChild(director);
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
        suburb = Suburb.Build(plan, new SuburbSettings { TankCount = 0 });
        AddChild(suburb);
        player.GlobalPosition = new Vector3(course == 1 ? 3 : 0, Suburb.GroundHeight, 7);
        player.Velocity = Vector3.Zero;
        player.CameraRig.SetOrientation(0, 0);
        goal = new Vector3(0, Suburb.GroundHeight, -7);
        lastPosition = player.GlobalPosition;
        elapsed = stuck = 0;
        captured = false;
        observer.GlobalPosition = new Vector3(11, 7, 9);
        observer.LookAt(new Vector3(0, 1, 0));
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        elapsed += dt;
        intent = new PlayerIntent();
        if (elapsed < 0.3f) { return; }
        var here = player.GlobalPosition;
        var to = goal - here;
        to.Y = 0;
        var yaw = Mathf.RadToDeg(Mathf.Atan2(-to.X, -to.Z));
        player.CameraRig.SetOrientation(yaw, 0);
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
        if (!captured && here.Y > Suburb.GroundHeight + 1.5f && Mathf.Abs(here.Z) < 1f)
        {
            captured = true;
            director.Screenshot($"seed{seed + 1}_course{course}_air");
        }
        if (to.Length() < 0.7f || elapsed > 15f)
        {
            var success = to.Length() < 0.7f;
            completed++;
            if (success) { passed++; times.Add(elapsed); }
            GD.Print($"[bot-course] seed={seed + 1} course={course} pass={success} time={elapsed:0.00} distance={to.Length():0.00}");
            course++;
            if (course == 3) { course = 0; seed++; }
            if (seed >= (int)DevArgs.GetFloat("seeds", 20))
            {
                var rate = (float)passed / completed;
                GD.Print($"[bot-course] success={passed}/{completed} rate={rate:0.000} mean={(times.Count > 0 ? times.Average() : 0):0.00}s stuck={stuckEvents} jumps={jumps}");
                GetTree().Quit(DevArgs.Get("baseline") != null || rate >= 0.95f ? 0 : 1);
                SetPhysicsProcess(false);
            }
            else { Begin(); }
        }
    }
}
