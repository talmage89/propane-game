using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Reads final poses after all modifiers. Near-floor drift is an independent proxy: classify contacts
/// against each episode's lowest ankle height, then measure consecutive world-space positions.</summary>
public partial class LocomotionProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public string Label { get; set; } = "";
    public bool SampleDrift { get; set; }
    public bool SkipNextPose { get; set; }
    private readonly List<(string Label, double Time, Vector3 Left, Vector3 Right)> samples = new();
    private readonly StringBuilder csv = new("label,time,left_x,left_y,left_z,right_x,right_y,right_z,max_bone_deg\n");
    private Quaternion[] previous = Array.Empty<Quaternion>();
    private double time;
    private float maxAngle;
    private string worstBone = "";
    private string worstLabel = "";
    private double worstTime;

    public static LocomotionProbe Attach(PlayerCharacter player)
    {
        var bones = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("foot_l") >= 0);
        var probe = new LocomotionProbe { Player = player };
        bones.AddChild(probe);
        return probe;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (delta <= 0) return;
        time += delta;
        var s = GetSkeleton();
        if (s == null) return;
        if (previous.Length == 0) previous = new Quaternion[s.GetBoneCount()];
        var angle = 0f;
        for (var i = 0; i < previous.Length; i++)
        {
            var q = s.GetBonePoseRotation(i);
            if (time > 0.2 && !SkipNextPose)
            {
                var difference = previous[i].Inverse() * q;
                var degrees = Mathf.RadToDeg(2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W)));
                angle = Mathf.Max(angle, degrees);
                if (degrees > maxAngle) { maxAngle = degrees; worstBone = s.GetBoneName(i); worstLabel = Label; worstTime = time; }
            }
            previous[i] = q;
        }
        SkipNextPose = false;
        var left = s.GlobalTransform * s.GetBoneGlobalPose(s.FindBone("foot_l")).Origin;
        var right = s.GlobalTransform * s.GetBoneGlobalPose(s.FindBone("foot_r")).Origin;
        if (SampleDrift) samples.Add((Label, time, left, right));
        csv.AppendLine(FormattableString.Invariant($"{Label},{time:F6},{left.X:F6},{left.Y:F6},{left.Z:F6},{right.X:F6},{right.Y:F6},{right.Z:F6},{angle:F3}"));
    }

    public void Finish(int expectedEpisodes = 0)
    {
        var failed = false;
        var episodes = samples.GroupBy(s => s.Label).ToArray();
        foreach (var episode in episodes)
        {
            var frames = episode.ToArray();
            var speeds = new List<float>();
            for (var side = 0; side < 2; side++)
            {
                Vector3 Foot(int i) => side == 0 ? frames[i].Left : frames[i].Right;
                var floor = frames.Min(f => side == 0 ? f.Left.Y : f.Right.Y);
                for (var i = 1; i < frames.Length; i++)
                {
                    if (Foot(i).Y > floor + 0.03f || Foot(i - 1).Y > floor + 0.03f) continue;
                    var movement = Foot(i) - Foot(i - 1);
                    speeds.Add(new Vector2(movement.X, movement.Z).Length() / (float)(frames[i].Time - frames[i - 1].Time));
                }
            }
            speeds.Sort();
            var median = speeds.Count > 0 ? speeds[speeds.Count / 2] : float.PositiveInfinity;
            var p95 = speeds.Count > 0 ? speeds[(speeds.Count - 1) * 95 / 100] : float.PositiveInfinity;
            GD.Print(FormattableString.Invariant($"[feet] {episode.Key} frames={frames.Length} contact-pairs={speeds.Count} drift-median={median:F4}m/s p95={p95:F4}m/s"));
            failed |= speeds.Count < 12 || !float.IsFinite(median);
        }
        failed |= episodes.Length < expectedEpisodes || !float.IsFinite(maxAngle);
        GD.Print(FormattableString.Invariant($"[feet] pose-max={maxAngle:F3}deg bone={worstBone} episode={worstLabel} time={worstTime:F3} episodes={episodes.Length}"));
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/feet.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        Active = false;
        if (failed) { GD.PushError("[feet] FAIL missing/nonfinite measurements"); GetTree().Quit(1); }
    }
}
