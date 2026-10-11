using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Reads final poses after all modifiers. Near-floor drift is an independent proxy: classify contacts
/// against each episode's lowest ankle height, then measure consecutive world-space positions.</summary>
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
    private readonly StringBuilder csv = new("label,time,left_x,left_y,left_z,right_x,right_y,right_z,max_bone_deg,phase,left_plant,right_plant,left_error,right_error,reach_error,pelvis,jump\n");
    private readonly HashSet<JumpPose> jumpPhases = new();
    private readonly Dictionary<string, (int Count, float Error, float Drift)> contacts = new();
    private readonly Vector3[] previousFeet = new Vector3[2];
    private readonly Vector3[] previousAnchors = new Vector3[2];
    private readonly bool[] previousContacts = new bool[2];
    private float maxAngleRate;
    private bool invalid;
    private Quaternion[] previous = Array.Empty<Quaternion>();
    private double time;
    private float maxAngle;
    private string worstBone = "";
    private string worstLabel = "";
    private double worstTime;
    private float ankleHeightDifference;
    private float minimumSeparation = float.PositiveInfinity;
    private float minimumSettledSeparation = float.PositiveInfinity;
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
        ankleHeightDifference = left.Y - right.Y;
        invalid |= !left.IsFinite() || !right.IsFinite();
        var feet = s.GetNodeOrNull<FootPlacement>("Feet");
        if (SampleDrift && feet?.Settled != false) samples.Add((Label, time, left, right));
        if (feet != null)
        {
            invalid |= Math.Abs(feet.PelvisOffset) > Tuning.Current.FootPelvisLimit + 0.00001f;
            if (feet.AirSettled)
            {
                var pelvis = s.FindBone("pelvis");
                invalid |= s.GetBoneGlobalPose(pelvis).Origin.DistanceTo(s.GetBoneGlobalRest(pelvis).Origin) > 0.00001f;
            }
            if (time > 0.2) jumpPhases.Add(feet.Pose.Jump);
            if (SampleSeparation)
            {
                var rightAxis = new Basis(Vector3.Up, Player.FacingYaw) * Vector3.Right;
                var separation = (right - left).Dot(rightAxis);
                minimumSeparation = Mathf.Min(minimumSeparation, separation);
                if (feet.Settled) minimumSettledSeparation = Mathf.Min(minimumSettledSeparation, separation);
            }
            for (var side = 0; side < 2; side++)
            {
                var foot = side == 0 ? left : right;
                var anchor = side == 0 ? feet.LeftAnchor : feet.RightAnchor;
                var planted = side == 0 ? feet.LeftPlanted : feet.RightPlanted;
                if (SampleTurns && !planted && foot.Y - Player.GlobalPosition.Y > Tuning.Current.FootSoleHeight + Tuning.Current.WalkFootLift * 0.5f)
                    turnLifts[side]++;
                if (planted)
                {
                    var stat = contacts.GetValueOrDefault(Label);
                    stat.Count++;
                    stat.Error = Mathf.Max(stat.Error, foot.DistanceTo(anchor));
                    if (previousContacts[side] && anchor.DistanceTo(previousAnchors[side]) < 0.00001f)
                        stat.Drift = Mathf.Max(stat.Drift, foot.DistanceTo(previousFeet[side]) / (float)delta);
                    contacts[Label] = stat;
                }
                previousContacts[side] = planted;
                previousAnchors[side] = anchor;
                previousFeet[side] = foot;
            }
        }
        var leftError = feet?.LeftPlanted == true ? left.DistanceTo(feet.LeftAnchor) : 0;
        var rightError = feet?.RightPlanted == true ? right.DistanceTo(feet.RightAnchor) : 0;
        csv.AppendLine(FormattableString.Invariant($"{Label},{time:F6},{left.X:F6},{left.Y:F6},{left.Z:F6},{right.X:F6},{right.Y:F6},{right.Z:F6},{angle:F3},{feet?.Pose.Phase ?? 0:F6},{feet?.LeftPlanted ?? false},{feet?.RightPlanted ?? false},{leftError:F6},{rightError:F6},{feet?.ReachError ?? 0:F6},{feet?.PelvisOffset ?? 0:F6},{feet?.Pose.Jump.ToString() ?? "baseline"}"));
    }

    public void AssertSplitSupport()
    {
        // SkeletonModifier3D poses are restored outside the modifier pass; use the final sampled ankles.
        var delta = ankleHeightDifference;
        GD.Print(FormattableString.Invariant($"[feet terrain] slab/curb ankle-height difference={delta:F6}m (expected 0.12m)"));
        if (DevArgs.Get("assert-feet") != null && Math.Abs(delta - 0.12f) > 0.003f)
        {
            GD.PushError("[feet terrain] FAIL feet do not adapt independently to slab/curb support");
            ShutdownGuard.Quit(GetTree(), 1);
        }
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
            failed |= assert && median > 0.05f;
            failed |= speeds.Count < 12 || !float.IsFinite(median);
        }
        foreach (var (label, stat) in contacts)
        {
            GD.Print(FormattableString.Invariant($"[feet contact] {label} samples={stat.Count} residual-max={stat.Error:F6}m drift-max={stat.Drift:F6}m/s"));
            failed |= stat.Count < 12 || !float.IsFinite(stat.Error) || !float.IsFinite(stat.Drift) || stat.Error > 0.003f || stat.Drift > 0.02f;
        }
        if (RequireJumpPhases)
        {
            foreach (var phase in Enum.GetValues<JumpPose>())
                if (!jumpPhases.Contains(phase)) { failed = true; GD.PushError($"[feet] missing jump phase {phase}"); }
        }
        failed |= episodes.Length < expectedEpisodes || !float.IsFinite(maxAngle) || !float.IsFinite(maxAngleRate) || maxAngleRate > 1500.01f;
        failed |= assert && contacts.Count < Mathf.Max(1, expectedEpisodes);
        if (expectedEpisodes > 0)
        {
            GD.Print(FormattableString.Invariant($"[feet] ankle-separation-min={minimumSeparation:F6}m settled-min={minimumSettledSeparation:F6}m"));
            failed |= minimumSeparation < 0 || minimumSettledSeparation < Tuning.Current.FootMinimumSeparation - 0.003f;
        }
        GD.Print(FormattableString.Invariant($"[feet] pose-max={maxAngle:F3}deg bone={worstBone} episode={worstLabel} time={worstTime:F3} episodes={episodes.Length} rate-max={maxAngleRate:F1}deg/s"));
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/feet.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        Active = false;
        if (assert && failed) { GD.PushError("[feet] FAIL coverage, contact drift, residual or pose continuity"); ShutdownGuard.Quit(GetTree(), 1); }
    }
}
