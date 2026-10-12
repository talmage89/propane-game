using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Warm the real animation tree, then bound recurring managed driver allocations.</summary>
public partial class AnimationDriverTests : Node3D
{
    public override void _Ready()
    {
        Node3D? model = null;
        try
        {
            using var tuning = new Tuning();
            model = GameAssets.Instantiate("res://assets/character/mannequin.glb");
            AddChild(model);
            var skeleton = model.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single();
            var player = model.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>().Single();
            var animator = new CharacterAnimator(tuning, player, model, model.GetPathTo(skeleton));
            animator.Initialize();
            _ = Sample(animator, 256);
            const int frames = 2000;
            // Leave a small interop allowance; recurring native-name wrappers exceed this per update.
            const int bytesPerFrame = 64;
            var allocated = Sample(animator, frames);
            if (allocated > frames * bytesPerFrame)
                throw new InvalidOperationException($"animation driver allocated {allocated} bytes in {frames} updates");
            GD.Print($"[animation driver] PASS {frames} updates, {allocated} managed bytes after warmup");
        }
        catch (Exception error)
        {
            GD.PushError($"[animation driver] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        finally
        {
            model?.Free();
        }
        ShutdownGuard.Quit(GetTree());
    }

    private static long Sample(CharacterAnimator animator, int frames)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
            animator.UpdateLocomotion(new Vector3(1.2f, 0, -.9f), frame * .01f, 1f / 60, PlayerGait.Creep);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
