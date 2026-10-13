using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Final local bone rotation and body-relative muzzle travel. Diagnostics never modify poses.</summary>
public partial class MotionProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public bool SkipNextPose { get; set; }
    public bool RequireCreep { get; init; }
    private Quaternion[] previous = Array.Empty<Quaternion>();
    private readonly Dictionary<string, float> physicalMaxima = new();
    private float physicalMaximum;
    private readonly Dictionary<string, float> maxima = new();
    private readonly StringBuilder csv = new("time,bone,degrees,rate,muzzle_side,physical,rig_weight\n");
    private double time;
    private Vector3 previousRoot;
    private float maximum, maximumRate;
    private const double SnapRate = 2400;
    private float minMuzzle = float.PositiveInfinity, maxMuzzle = float.NegativeInfinity;
    private int frames, creepFrames;
    private bool invalid;
    private bool rotationFailed;
    private readonly float[] pitchBefore = new float[2];
    private readonly float[] expectedClimb = new float[2];
    private bool climbFailed;
    private readonly bool[] burstStarted = new bool[2], burstFinished = new bool[2];
    private float maximumClimb;
    private int recoilFrames;
    public int RecoilFrames => recoilFrames;

    public static MotionProbe Attach(PlayerCharacter player, bool requireCreep = false)
    {
        var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("hand_l") >= 0);
        var probe = new MotionProbe { Player = player, RequireCreep = requireCreep };
        skeleton.AddChild(probe);
        return probe;
    }

    public override void _Ready() => Player.Fired += Shot;
    public override void _ExitTree() => Player.Fired -= Shot;

    private void Shot(ShotReport shot)
    {
        if (!RequireCreep) return;
        for (var burst = 0; burst < 2; burst++)
        {
            var before = burst == 0 ? 11.3 : 13.8;
            var after = burst == 0 ? 13.3 : 16.0;
            var tuning = Player.World.Tuning;
            if (time >= before && time < after)
                expectedClimb[burst] += tuning.RecoilKickDegrees * (Player.ResolvedState.EffectiveAim ? tuning.AimRecoilScale : 1) * (1 - tuning.RecoilReturnShare);
        }
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (delta <= 0) return;
        time += delta;
        var s = GetSkeleton();
        if (s == null) return;
        if (previous.Length == 0) previous = new Quaternion[s.GetBoneCount()];
        var teleported = Player.GlobalPosition.DistanceTo(previousRoot) > 10;
        previousRoot = Player.GlobalPosition;
        var rig = s.GetNode<RifleRig>("RifleRig");
        var right = new Basis(Vector3.Up, Player.FacingYaw) * Vector3.Right;
        var lateral = (rig.Rifle!.MuzzlePosition - Player.GlobalPosition).Dot(right);
        var physical = s.GetNode<Ragdoll>("Ragdoll").Simulating;
        var worst = "";
        var angle = 0f;
        for (var i = 0; i < previous.Length; i++)
        {
            var q = s.GetBonePoseRotation(i).Normalized();
            invalid |= !float.IsFinite(q.LengthSquared());
            if (time > 0.2 && !teleported && !SkipNextPose)
            {
                var difference = previous[i].Inverse() * q;
                var degrees = Mathf.RadToDeg(2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W)));
                var name = s.GetBoneName(i).ToString();
                var digit = name.StartsWith("index_", StringComparison.Ordinal) || name.StartsWith("middle_", StringComparison.Ordinal) ||
                    name.StartsWith("ring_", StringComparison.Ordinal) || name.StartsWith("pinky_", StringComparison.Ordinal) || name.StartsWith("thumb_", StringComparison.Ordinal);
                // A snap detector, not a smoothing bound: clips and stance changes may move fast, never jump.
                if (!physical || digit) rotationFailed |= degrees / delta > SnapRate;
                var statistics = physical && !digit ? physicalMaxima : maxima;
                if (physical && digit)
                {
                    maximum = Mathf.Max(maximum, degrees);
                    maximumRate = Mathf.Max(maximumRate, degrees / (float)delta);
                }
                statistics[name] = Mathf.Max(statistics.GetValueOrDefault(name), degrees);
                if (degrees > angle) { angle = degrees; worst = name; }
            }
            previous[i] = q;
        }
        SkipNextPose = false;
        frames++;
        if (physical)
        {
            physicalMaximum = Mathf.Max(physicalMaximum, angle);
        }
        else
        {
            maximum = Mathf.Max(maximum, angle);
            maximumRate = Mathf.Max(maximumRate, angle / (float)delta);
        }
        if (rig.Recoil > 0.5f) recoilFrames++;
        if (RequireCreep)
        {
            for (var burst = 0; burst < 2; burst++)
            {
                var before = burst == 0 ? 11.3 : 13.8;
                var after = burst == 0 ? 13.3 : 16.0;
                var pitch = Player.Aim.Pitch;
                if (time >= before && !burstStarted[burst]) { pitchBefore[burst] = pitch; burstStarted[burst] = true; }
                if (time >= after && !burstFinished[burst])
                {
                    var climb = pitch - pitchBefore[burst];
                    maximumClimb = Mathf.Max(maximumClimb, climb);
                    var expected = expectedClimb[burst];
                    // The retained share must remain; the small outstanding settle budget may still be visible.
                    climbFailed |= expected < 0.5f || climb < expected - 0.05f ||
                        climb > expected / (1 - Player.World.Tuning.RecoilReturnShare) + 0.05f;
                    burstFinished[burst] = true;
                }
            }
        }
        // Controlled steady creep window: do not mistake a stance transition or shot for gait sway.
        if (RequireCreep && time >= 9.5 && time <= 10.5)
        {
            invalid |= !Player.ResolvedState.EffectiveAim || !Player.ResolvedState.Grounded;
            minMuzzle = Mathf.Min(minMuzzle, lateral);
            maxMuzzle = Mathf.Max(maxMuzzle, lateral);
            creepFrames++;
        }
        csv.AppendLine(FormattableString.Invariant($"{time:F6},{worst},{angle:F4},{angle / delta:F2},{lateral:F6},{physical},{rig.Weight:F4}"));
    }

    public void Finish()
    {
        var assert = DevArgs.Get("assert-motion") != null;
        foreach (var (bone, degrees) in maxima.Where(p => p.Value > 25).OrderByDescending(p => p.Value))
            GD.Print(FormattableString.Invariant($"[motion spike] {bone} max={degrees:F3}deg"));
        foreach (var (bone, degrees) in physicalMaxima.Where(p => p.Value > 25).OrderByDescending(p => p.Value))
            GD.Print(FormattableString.Invariant($"[motion physical] {bone} max={degrees:F3}deg"));
        var range = creepFrames > 0 ? maxMuzzle - minMuzzle : 0;
        var failed = invalid || frames < 100 || rotationFailed ||
            (RequireCreep && (creepFrames < 50 || !float.IsFinite(range) || range > 0.05f || recoilFrames < 20 || burstFinished.Any(done => !done) || climbFailed));
        GD.Print(FormattableString.Invariant($"[motion] {(assert && failed ? "FAIL" : assert ? "PASS" : "MEASURED")} frames={frames} max={maximum:F3}deg physical-max={physicalMaximum:F3}deg rate={maximumRate:F2}deg/s creep-frames={creepFrames} muzzle-range={range:F6}m recoil-frames={recoilFrames} aim-climb={maximumClimb:F6}deg"));
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/motion.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        Active = false;
        if (assert && failed) { GD.PushError("[motion] FAIL rotation continuity or creep steadiness or persistent aim climb"); ShutdownGuard.Quit(GetTree(), 1); }
    }
}
