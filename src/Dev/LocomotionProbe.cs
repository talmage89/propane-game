using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Reads final poses after all modifiers: finite bones, no snaps, jump-phase coverage, and feet that lift
/// in standing turns. Near-floor drift and ankle separation are reported, not asserted: clips play as authored, so
/// a little foot slide where the gait is faster than its capture is accepted. Classify contacts against each
/// episode's lowest ankle height, then measure consecutive world-space positions.</summary>
public partial class LocomotionProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public string Label { get; set; } = "";
    public bool SampleDrift { get; set; }
    public bool SampleSeparation { get; set; }
    public bool SampleTurns { get; set; }
    public bool RequireJumpPhases { get; init; }
    public bool SkipNextPose { get; set; }
    private readonly List<(string Label, double Time, Vector3 Left, Vector3 Right)> samples = new();
    private readonly StringBuilder csv = new("label,time,left_x,left_y,left_z,right_x,right_y,right_z,max_bone_deg,phase,jump\n");
    private readonly HashSet<JumpPose> jumpPhases = new();
    private const float RestAnkleHeight = 0.104f;
    private const float TurnLift = 0.03f;
    private float maxAngleRate;
    private bool invalid;
    private Quaternion[] previous = Array.Empty<Quaternion>();
    private double time;
    private float maxAngle;
    private string worstBone = "";
    private string worstLabel = "";
    private double worstTime;
    private float minimumSeparation = float.PositiveInfinity;
    private Vector3 previousRoot;
    private readonly int[] turnLifts = new int[2];

    public static LocomotionProbe Attach(PlayerCharacter player, bool requireJumpPhases = false)
    {
        var bones = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("foot_l") >= 0);
        var probe = new LocomotionProbe { Player = player, RequireJumpPhases = requireJumpPhases };
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
        // The network harness teleports from a suburb spawn to its void range after snapshots arrive.
        // Exclude that setup relocation, like scripted local teleports; ordinary 0.8 m corrections stay measured.
        var teleported = time > 0.2 && Player.GlobalPosition.DistanceTo(previousRoot) > 10;
        previousRoot = Player.GlobalPosition;
        if (teleported) GD.Print($"[feet] excluded setup teleport angular sample at {time:F3}s");
        var angle = 0f;
        for (var i = 0; i < previous.Length; i++)
        {
            var q = s.GetBonePoseRotation(i).Normalized();
            invalid |= !float.IsFinite(q.LengthSquared());
            if (time > 0.2 && !SkipNextPose && !teleported)
            {
                var difference = previous[i].Inverse() * q;
                var degrees = Mathf.RadToDeg(2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W)));
                angle = Mathf.Max(angle, degrees);
                if (degrees > maxAngle) { maxAngle = degrees; worstBone = s.GetBoneName(i); worstLabel = Label; worstTime = time; }
            }
            previous[i] = q;
        }
        maxAngleRate = Mathf.Max(maxAngleRate, angle / (float)delta);
        SkipNextPose = false;
        var left = s.GlobalTransform * s.GetBoneGlobalPose(s.FindBone("foot_l")).Origin;
        var right = s.GlobalTransform * s.GetBoneGlobalPose(s.FindBone("foot_r")).Origin;
        invalid |= !left.IsFinite() || !right.IsFinite();
        if (SampleDrift) samples.Add((Label, time, left, right));
        var pose = Player.Locomotion;
        if (time > 0.2) jumpPhases.Add(pose.Jump);
        if (SampleSeparation)
        {
            var rightAxis = new Basis(Vector3.Up, Player.FacingYaw) * Vector3.Right;
            minimumSeparation = Mathf.Min(minimumSeparation, (right - left).Dot(rightAxis));
        }
        for (var side = 0; side < 2; side++)
        {
            var foot = side == 0 ? left : right;
            if (SampleTurns && foot.Y - Player.GlobalPosition.Y > RestAnkleHeight + TurnLift) turnLifts[side]++;
        }
        csv.AppendLine(FormattableString.Invariant($"{Label},{time:F6},{left.X:F6},{left.Y:F6},{left.Z:F6},{right.X:F6},{right.Y:F6},{right.Z:F6},{angle:F3},{pose.Phase:F6},{pose.Jump}"));
    }

    public void AssertTurns()
    {
        GD.Print($"[feet turn] lifted samples left={turnLifts[0]} right={turnLifts[1]}");
        if (DevArgs.Get("assert-feet") != null && (turnLifts[0] < 2 || turnLifts[1] < 2))
        {
            GD.PushError("[feet turn] FAIL standing turns did not lift both feet");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    public void Finish(int expectedEpisodes = 0)
    {
        var assert = DevArgs.Get("assert-feet") != null;
        var failed = invalid;
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
        if (RequireJumpPhases)
        {
            foreach (var phase in Enum.GetValues<JumpPose>())
                if (!jumpPhases.Contains(phase)) { failed = true; GD.PushError($"[feet] missing jump phase {phase}"); }
        }
        // A snap detector (40 degrees in one 60 Hz frame), not a smoothing bound.
        failed |= episodes.Length < expectedEpisodes || !float.IsFinite(maxAngle) || !float.IsFinite(maxAngleRate) || maxAngleRate > 2400.01f;
        if (expectedEpisodes > 0) GD.Print(FormattableString.Invariant($"[feet] ankle-separation-min={minimumSeparation:F6}m"));
        GD.Print(FormattableString.Invariant($"[feet] pose-max={maxAngle:F3}deg bone={worstBone} episode={worstLabel} time={worstTime:F3} episodes={episodes.Length} rate-max={maxAngleRate:F1}deg/s"));
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/feet.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        Active = false;
        if (assert && failed) { GD.PushError("[feet] FAIL coverage, finite pose or pose continuity"); ShutdownGuard.Quit(GetTree(), 1); }
    }
}
