using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.World;

namespace Propane.Dev;

/// <summary>The first physical-bone transforms must not depend on a distant actor's render sample rate.</summary>
public partial class RagdollPoseTests : Node3D
{
    private WorldContext world = null!;
    private PlayerCharacter full = null!, distant = null!;
    private Camera3D observer = null!;
    private int ticks;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        InputSetup.Register();
        using var tuning = new Tuning { ActorPoseFarRate = 1, ActorPoseHiddenRate = 1 };
        world = new WorldContext(this, tuning, present: true);
        world.SetContentRoot(world.Root);
        world.Spawn.InWorld(new VoidEnvironment());
        PlayerCharacter Actor(int id, Vector3 position)
        {
            var input = new ScriptedInput();
            input.Hold(0, 3, new PlayerIntent { Move = Vector2.Up });
            return world.Spawn.InWorld(new PlayerCharacter { World = world, ActorId = id, Position = position, InputOverride = input });
        }
        full = Actor(1, Vector3.Right * 20);
        distant = Actor(8, Vector3.Zero);
        LocalView.Attach(full, this).Hud.ShowHud(false);
        observer = world.AddView(this, new Camera3D { Current = true });
        observer.LookAtFromPosition(new Vector3(0, 3, 140), Vector3.Up);
        ProcessPhysicsPriority = int.MinValue;
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            observer.MakeCurrent();
            ((ScriptedInput)full.InputOverride!).Advance((float)delta);
            ((ScriptedInput)distant.InputOverride!).Advance((float)delta);
            if (++ticks != 150) return;
            full.Collapse();
            distant.Collapse();
            var a = full.FindChildren("*", "PhysicalBoneSimulator3D", owned: false).OfType<Ragdoll>().Single();
            var b = distant.FindChildren("*", "PhysicalBoneSimulator3D", owned: false).OfType<Ragdoll>().Single();
            if (a.Bones.Count == 0 || a.Bones.Count != b.Bones.Count)
                throw new InvalidOperationException("actors must expose the same nonempty physical-bone rig");
            var offset = full.GlobalPosition - distant.GlobalPosition;
            var maximum = 0f;
            var rotation = 0f;
            for (var i = 0; i < a.Bones.Count; i++)
            {
                maximum = Math.Max(maximum, (a.Bones[i].GlobalPosition - offset).DistanceTo(b.Bones[i].GlobalPosition));
                rotation = Math.Max(rotation, Mathf.RadToDeg(a.Bones[i].GlobalBasis.GetRotationQuaternion()
                    .AngleTo(b.Bones[i].GlobalBasis.GetRotationQuaternion())));
            }
            if (!float.IsFinite(maximum) || !float.IsFinite(rotation) || maximum > .001f || rotation > .1f)
                throw new InvalidOperationException($"render cadence changed initial physical-bone transforms by {maximum:F6}m/{rotation:F4}deg");
            GD.Print($"[ragdoll pose] PASS full vs 1-Hz presentation initial bone error={maximum:F6}m/{rotation:F4}deg");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[ragdoll pose] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    public override void _ExitTree() => world.Dispose();
}
