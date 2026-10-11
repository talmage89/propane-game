using System;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>Real collision proof: repeated active samples, a wall, a loose tank and a prop.</summary>
public partial class MeleeRange : Node3D
{
    private WorldContext world = null!;
    public override void _ExitTree() => world.Dispose();

    private readonly ScriptedInput input = new();
    private PlayerCharacter attacker = null!;
    private PlayerCharacter victim = null!;
    private PropaneTank tank = null!;
    private RigidBody3D prop = null!;
    private RigidBody3D largeProp = null!;
    private int largeHits;
    private StaticBody3D wall = null!;
    private float time;
    private int playerHits, tankHits, propHits, throws;
    private Vector3 tankBefore, propBefore;
    private bool proved;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DevArgs.Setup();
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        ProcessPhysicsPriority = -10;
        InputSetup.Register();
        AddChild(new VoidEnvironment());
        var dynamic = new Node3D(); AddChild(dynamic); world.SetContentRoot(dynamic);
        attacker = new PlayerCharacter { World = world, InputOverride = input }; AddChild(attacker);
        victim = new PlayerCharacter { World = world, InputLocked = true, Position = new Vector3(0, 0, -1.45f) }; AddChild(victim);
        var view = LocalView.Attach(attacker, this);
        view.Hud.ShowHud(false);
        view.CameraRig.Camera.Current = true;
        victim.Ragdolled += (_, _, _) => throws++;
        attacker.MeleeHit += hit =>
        {
            if (hit.Collider == victim) playerHits++;
            if (hit.Collider == tank) tankHits++;
            if (hit.Collider == prop) propHits++;
            if (hit.Collider == largeProp) largeHits++;
        };
        wall = new StaticBody3D { CollisionLayer = Layers.World, Position = new Vector3(0, 1, -0.8f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 2, .15f) } });
        AddChild(wall);
        input.Melee(.5f); // Wall must stop contact and lunge.
        var director = new CaptureDirector { QuitAfter = 9 };
        director.ShotAt(.9f, "wall_blocked");
        director.ShotAt(2.3f, "victim_contact");
        director.ShotAt(2.7f, "victim_down");
        director.ShotAt(3.8f, "tank_before");
        director.ShotAt(4.35f, "tank_contact");
        director.ShotAt(5.4f, "tank_shoved_intact");
        director.ShotAt(6.5f, "large_prop_contact");
        AddChild(director);
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        time += dt;
        input.Advance(dt);
        try
        {
            if (Once(1.5f, dt))
            {
                Require(playerHits == 0 && throws == 0, "strike crossed wall");
                wall.QueueFree();
                attacker.Teleport(Vector3.Zero, 0);
                input.Melee(2);
            }
            if (Once(3, dt))
            {
                Require(playerHits == 1 && throws == 1 && victim.IsRagdolled, "active samples must hit/throw victim exactly once");
                attacker.Teleport(new Vector3(6, 0, 0), 0);
                tank = new PropaneTank { World = world, ContentSpawn = world.Spawn, Position = new Vector3(5.6f, .02f, -1.35f) }; AddChild(tank);
                prop = new RigidBody3D { Mass = 10, Position = new Vector3(6.5f, .5f, -1.35f), CollisionLayer = Layers.Props, CollisionMask = Layers.DynamicMask };
                prop.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * .4f } }); AddChild(prop);
            }
            if (Once(3.5f, dt)) { tankBefore = tank.GlobalPosition; propBefore = prop.GlobalPosition; input.Melee(4); }
            if (Once(5.5f, dt))
            {
                Require(tankHits == 1 && propHits == 1, "tank/prop missed or hit repeatedly");
                Require(tank.State == PropaneTank.TankState.Intact && tank.GlobalPosition.DistanceTo(tankBefore) > .05f, "tank did not move intact");
                Require(prop.GlobalPosition.DistanceTo(propBefore) > .1f, "prop did not move");
                attacker.Teleport(new Vector3(12, 0, 0), 0);
                largeProp = new RigidBody3D { Mass = 100, Position = new Vector3(12, .6f, -2.5f), CollisionLayer = Layers.Props, CollisionMask = Layers.DynamicMask };
                for (var shape = 0; shape < 2; shape++)
                    largeProp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2, 1.2f, 3) } });
                AddChild(largeProp);
                input.Melee(6);
                GD.Print($"[melee-range] PASS wall, one hit/throw, tank moved {tank.GlobalPosition.DistanceTo(tankBefore):F3} m intact, prop moved {prop.GlobalPosition.DistanceTo(propBefore):F3} m");
            }
            if (Once(8, dt))
            {
                Require(largeHits == 1, "large prop surface outside center reach missed, or duplicate shapes hit twice");
                proved = true;
                GD.Print("[melee-range] PASS large prop surface and duplicate shapes");
                ShutdownGuard.Quit(GetTree());
            }
            if (time > 8.5f && !proved) throw new InvalidOperationException("proof timeout");
        }
        catch (Exception error) { GD.PushError($"[melee-range] FAIL {error.Message}"); ShutdownGuard.Quit(GetTree(), 1); }
    }

    private bool Once(float at, float dt) => time >= at && time - dt < at;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
