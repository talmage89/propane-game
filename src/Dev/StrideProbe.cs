using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Samples the final rendered feet in world space. Near-floor samples are a proxy for planted contacts,
/// not collision-classified contacts or foot IK. Diagnostic tolerances do not alter gameplay.</summary>
public partial class StrideProbe : SkeletonModifier3D
{
    private static readonly string[] Bones = { "foot_l", "ball_l", "foot_r", "ball_r" };
    private readonly List<(double Time, Vector3 Root, Vector3[] Feet)> samples = new();
    private int[] indices = Array.Empty<int>();
    private double elapsed;
    public PlayerCharacter Player { get; init; } = null!;
    public float SampleFrom { get; init; }
    public float SampleUntil { get; init; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        elapsed += delta;
        if (elapsed < SampleFrom || elapsed > SampleUntil) return;
        var skeleton = GetSkeleton();
        if (skeleton == null) return;
        if (indices.Length == 0) indices = Bones.Select(skeleton.FindBone).ToArray();
        if (indices.Any(i => i < 0) || !Player.ResolvedState.EffectiveAim || !Player.ResolvedState.Grounded ||
            Math.Abs(new Vector2(Player.Velocity.X, Player.Velocity.Z).Length() - Player.World.Tuning.CreepSpeed) > 0.001f)
        {
            GD.PushError("[stride] FAIL missing bones or unsettled aimed movement");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        samples.Add((elapsed, Player.GlobalPosition,
            indices.Select(i => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(i).Origin).ToArray()));
    }

    public void Finish()
    {
        if (samples.Count < 20)
        {
            GD.PushError($"[stride] FAIL only {samples.Count} rendered samples");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        var csv = new StringBuilder("time,root_x,root_y,root_z,bone,x,y,z\n");
        var slides = new List<float>();
        var distances = new List<float>();
        for (var b = 0; b < Bones.Length; b++)
        {
            var minY = samples.Min(s => s.Feet[b].Y);
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                var foot = s.Feet[b];
                csv.AppendLine(FormattableString.Invariant($"{s.Time:F6},{s.Root.X:F6},{s.Root.Y:F6},{s.Root.Z:F6},{Bones[b]},{foot.X:F6},{foot.Y:F6},{foot.Z:F6}"));
                if (i == 0 || foot.Y > minY + 0.03f || samples[i - 1].Feet[b].Y > minY + 0.03f) continue;
                var movement = foot - samples[i - 1].Feet[b];
                var distance = new Vector2(movement.X, movement.Z).Length();
                distances.Add(distance);
                slides.Add(distance / (float)(s.Time - samples[i - 1].Time));
            }
        }
        if (slides.Count < 20)
        {
            GD.PushError($"[stride] FAIL only {slides.Count} near-floor sample pairs");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        slides.Sort();
        distances.Sort();
        GD.Print(FormattableString.Invariant($"[stride] PASS speed={Player.World.Tuning.CreepSpeed:F2} frames={samples.Count} near-floor-pairs={slides.Count} slide-median={slides[slides.Count / 2]:F3}m/s p75={slides[slides.Count * 3 / 4]:F3}m/s median-frame-drift={distances[distances.Count / 2] * 1000:F2}mm"));
        var directory = DevArgs.Get("capture-dir");
        if (directory == null) return;
        DirAccess.MakeDirRecursiveAbsolute(directory);
        using var file = FileAccess.Open($"{directory}/stride.csv", FileAccess.ModeFlags.Write);
        file.StoreString(csv.ToString());
    }
}
