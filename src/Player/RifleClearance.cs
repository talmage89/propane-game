using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>Skin-derived head volume and shoulder pocket constraints for the displayed rifle.
/// These objectives precede wrist IK; moving a solved arm afterward would detach its grip.</summary>
public sealed class RifleClearance
{
    public readonly record struct Arm(Vector3 Shoulder, Vector3 Elbow, Vector3 Wrist);
    private readonly int head, upper, lower;
    private readonly Vector3 center, radius;
    private readonly List<(int Bone, Vector3 Center, Vector3 Radius)> body = new();
    private Transform3D[] bodyFrames = Array.Empty<Transform3D>();
    private Transform3D headFrame;
    private Transform3D toSkeleton, fromSkeleton;
    /// <summary>Nearest volume from the most recent stock query, for frame-level clearance diagnostics.</summary>
    public int LastStockBodyBone { get; private set; } = -1;

    public RifleClearance(Skeleton3D skeleton)
    {
        head = skeleton.FindBone("Head");
        upper = skeleton.FindBone("upperarm_r");
        lower = skeleton.FindBone("lowerarm_r");
        var points = new List<Vector3>();
        var volumes = new[] { "pelvis", "spine_01", "spine_02", "spine_03", "Head", "thigh_l", "thigh_r" }
            .ToDictionary(skeleton.FindBone, _ => new List<Vector3>());
        var restFrames = volumes.Keys.ToDictionary(bone => bone, bone => skeleton.GetBoneGlobalRest(bone).AffineInverse());
        var root = skeleton.GetParent();
        while (root is Node3D && root.GetParent() is Node3D) root = root.GetParent();
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", owned: false).OfType<MeshInstance3D>())
        {
            if (mesh.Skin == null || mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton) != skeleton) continue;
            var skin = mesh.Skin;
            var map = Enumerable.Range(0, skin.GetBindCount()).Select(i => skin.GetBindName(i).ToString().Length > 0
                ? skeleton.FindBone(skin.GetBindName(i)) : skin.GetBindBone(i)).ToArray();
            var toHead = skeleton.GetBoneGlobalRest(head).AffineInverse() * skeleton.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            var toSkeleton = skeleton.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                using var arrays = mesh.Mesh.SurfaceGetArrays(surface);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var ids = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                var influences = ids.Length / vertices.Length;
                for (var v = 0; v < vertices.Length; v++)
                {
                    var weight = 0f;
                    for (var j = 0; j < influences; j++) if (map[ids[v * influences + j]] == head) weight += weights[v * influences + j];
                    if (weight > 0.5f) points.Add(toHead * vertices[v]);
                    foreach (var (bone, samples) in volumes)
                    {
                        var influence = 0f;
                        for (var j = 0; j < influences; j++) if (map[ids[v * influences + j]] == bone) influence += weights[v * influences + j];
                        if (influence > 0.5f)
                            samples.Add(restFrames[bone] * toSkeleton * vertices[v]);
                    }
                }
            }
        }
        if (points.Count < 20) throw new InvalidOperationException("Mannequin head skin missing");
        var min = points.Aggregate((a, b) => a.Min(b));
        var max = points.Aggregate((a, b) => a.Max(b));
        center = (min + max) / 2;
        radius = (max - min) / 2;
        foreach (var (bone, samples) in volumes)
        {
            if (samples.Count < 20) continue;
            min = samples.Aggregate((a, b) => a.Min(b));
            max = samples.Aggregate((a, b) => a.Max(b));
            body.Add((bone, (min + max) / 2, (max - min) / 2));
        }
        bodyFrames = new Transform3D[body.Count];
        UpdatePose(skeleton);
    }

    /// <summary>Refresh once after the torso/head pose changes. Arm IK doesn't change these volumes.</summary>
    public void UpdatePose(Skeleton3D skeleton)
    {
        UpdateFrame(skeleton);
        headFrame = skeleton.GetBoneGlobalPose(head).AffineInverse();
        for (var i = 0; i < body.Count; i++) bodyFrames[i] = skeleton.GetBoneGlobalPose(body[i].Bone).AffineInverse();
    }

    /// <summary>Physics can move the actor after the displayed body pose was cached.</summary>
    public void UpdateFrame(Skeleton3D skeleton)
    {
        fromSkeleton = skeleton.GlobalTransform;
        toSkeleton = fromSkeleton.AffineInverse();
    }

    public float HeadGap(Skeleton3D skeleton, Transform3D rifle, Tuning tuning)
    {
        var toHead = headFrame * toSkeleton * rifle;
        var gap = float.PositiveInfinity;
        for (var i = 0; i <= 20; i++)
        {
            var stock = new Vector3(0, 0.064f, Mathf.Lerp(0.08f, 0.255f, i / 20f));
            var relative = (toHead * stock - center) / radius;
            gap = Mathf.Min(gap, (relative.Length() - 1) * Mathf.Min(radius.X, Mathf.Min(radius.Y, radius.Z)) - tuning.RifleStockRadius);
        }
        return gap;
    }

    public float ShoulderGap(Skeleton3D skeleton, Transform3D rifle, Tuning tuning)
        => ShoulderContact(skeleton, rifle, tuning).Gap;

    public float JointGap(Vector3 point, float clearance) => BodyContact(point, 0, clearance).Gap;

    public float StockBodyGap(Skeleton3D skeleton, Transform3D rifle, Tuning tuning)
        => StockContact(skeleton, rifle, tuning).Gap;

    private (float Gap, Vector3 Normal) StockContact(Skeleton3D skeleton, Transform3D rifle, Tuning tuning, float? margin = null)
    {
        var local = toSkeleton * rifle;
        var gap = float.PositiveInfinity;
        var normal = Vector3.Zero;
        LastStockBodyBone = -1;
        for (var i = 0; i <= 20; i++)
        {
            var point = local * new Vector3(0, 0.064f, Mathf.Lerp(0.08f, 0.255f, i / 20f));
            for (var j = 0; j < body.Count; j++)
            {
                var (_, volumeCenter, volumeRadius) = body[j];
                var normalized = (bodyFrames[j] * point - volumeCenter) / volumeRadius;
                var distance = EllipsoidDistance(bodyFrames[j] * point - volumeCenter, volumeRadius,
                    tuning.RifleStockRadius + (margin ?? tuning.RifleHeadClearance)) - tuning.RifleStockRadius;
                if (distance >= gap) continue;
                gap = distance;
                LastStockBodyBone = body[j].Bone;
                normal = fromSkeleton.Basis * bodyFrames[j].Basis.Transposed() * (normalized / volumeRadius).Normalized();
            }
        }
        return (gap, normal.Normalized());
    }

    private (float Gap, Vector3 Normal) ShoulderContact(Skeleton3D skeleton, Transform3D rifle, Tuning tuning)
    {
        var start = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(upper).Origin;
        var end = start.MoveToward(skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(lower).Origin, tuning.RifleShoulderContactLength);
        var gap = float.PositiveInfinity;
        var normal = Vector3.Zero;
        for (var i = 0; i <= 20; i++)
        {
            var p = rifle * new Vector3(0, 0.064f, Mathf.Lerp(0.08f, 0.255f, i / 20f));
            var d = end - start;
            var closest = start + d * Mathf.Clamp((p - start).Dot(d) / Mathf.Max(d.LengthSquared(), 0.00001f), 0, 1);
            var distance = p.DistanceTo(closest) - tuning.RifleShoulderRadius - tuning.RifleStockRadius;
            if (distance < gap) { gap = distance; normal = (p - closest).Normalized(); }
        }
        return (gap, normal);
    }

    /// <summary>Bounded skin and wrist clearance corrections around the shoulder pocket. The caller
    /// alternates these surface objectives with anatomical reach and elbow clearance.</summary>
    public Transform3D Project(Skeleton3D skeleton, Transform3D rifle, Tuning tuning, float shouldered = 0, Vector3? rightGrip = null, Vector3? leftGrip = null, float? minimumGap = null)
    {
        var minimum = minimumGap ?? tuning.RifleHeadClearance;
        var wristMinimum = minimumGap ?? tuning.RifleLimbClearance;
        if (shouldered > 0)
        {
            var contact = ShoulderContact(skeleton, rifle, tuning);
            rifle.Origin += contact.Normal * Mathf.Clamp(tuning.RifleShoulderContactGap - contact.Gap,
                -tuning.RifleClearanceLimit, tuning.RifleClearanceLimit) * shouldered;
        }
        (float Gap, Vector3 Normal) Contact(Transform3D pose)
        {
            var stockContact = StockContact(skeleton, pose, tuning, minimum);
            var result = (Gap: stockContact.Gap - minimum, stockContact.Normal);
            void Grip(Vector3? grip)
            {
                if (grip == null) return;
                var local = toSkeleton * (pose * grip.Value);
                var contact = BodyContact(local, tuning.RifleArmRadius, wristMinimum);
                contact.Gap -= wristMinimum;
                if (contact.Gap < result.Gap)
                    result = (contact.Gap, fromSkeleton.Basis * contact.Normal);
            }
            Grip(rightGrip);
            Grip(leftGrip);
            if (shouldered >= 0.999f)
            {
                var shoulder = ShoulderContact(skeleton, pose, tuning);
                var minimumShoulder = shoulder.Gap - tuning.RifleShoulderMinimumGap;
                var maximumShoulder = tuning.RifleShoulderMaximumGap - shoulder.Gap;
                if (minimumShoulder < result.Gap) result = (minimumShoulder, shoulder.Normal);
                if (maximumShoulder < result.Gap) result = (maximumShoulder, -shoulder.Normal);
            }
            return result;
        }
        var start = rifle;
        var best = rifle;
        var bestGap = float.NegativeInfinity;
        for (var i = 0; i < 12; i++)
        {
            var contact = Contact(rifle);
            if (contact.Gap > bestGap) { bestGap = contact.Gap; best = rifle; }
            if (contact.Gap >= -0.000001f) return rifle;
            // Correct the violated surface, then measure the other objectives again. Searching for
            // clearance of every surface along one normal can have no solution and jump the whole
            // correction limit even for a submillimeter obstruction.
            rifle.Origin += contact.Normal * Mathf.Min(tuning.RifleClearanceLimit,
                tuning.RifleSolveReserve - contact.Gap);
            rifle.Origin = start.Origin.MoveToward(rifle.Origin, tuning.RifleClearanceLimit);
        }
        return best;
    }

    /// <summary>Choose an elbow on the anatomical two-bone circle while keeping both wrist contacts.
    /// Only a penetrating canonical bend searches alternatives; preference stays with the original pole.</summary>
    public Vector3 ElbowPole(Skeleton3D skeleton, int upper, int lower, int hand, Vector3 wrist, Vector3 pole, Tuning tuning, Arm? other = null)
    {
        var shoulder = skeleton.GetBoneGlobalPose(upper).Origin;
        var elbow = skeleton.GetBoneGlobalPose(lower).Origin;
        var end = skeleton.GetBoneGlobalPose(hand).Origin;
        return ElbowPole(shoulder, elbow, end, wrist, pole, tuning, other);
    }

    public Vector3 ElbowPole(Vector3 shoulder, Vector3 elbow, Vector3 end, Vector3 wrist, Vector3 pole, Tuning tuning, Arm? other = null)
    {
        var l1 = shoulder.DistanceTo(elbow);
        var l2 = elbow.DistanceTo(end);
        var direction = (wrist - shoulder).Normalized();
        var distance = shoulder.DistanceTo(wrist);
        var along = (l1 * l1 - l2 * l2 + distance * distance) / (2 * Mathf.Max(distance, 0.00001f));
        var height = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - along * along));
        var bend = pole - shoulder;
        bend = (bend - direction * bend.Dot(direction)).Normalized();
        var center = shoulder + direction * along;
        var best = center + bend * height;
        var required = tuning.RifleLimbClearance + tuning.RifleSolveReserve;
        float Gap(Vector3 candidate)
        {
            var value = ArmContact(shoulder, candidate, wrist, tuning).Gap;
            return other is Arm arm ? Mathf.Min(value, ArmPairGap(new Arm(shoulder, candidate, wrist), arm, tuning)) : value;
        }
        var gap = Gap(best);
        if (gap >= tuning.RifleLimbClearance - 0.000001f) return best;
        for (var i = 1; i <= 12; i++)
        {
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var candidate = center + bend.Rotated(direction, sign * i * Mathf.Pi / 12) * height;
                var candidateGap = Gap(candidate);
                if (candidateGap > gap) { best = candidate; gap = candidateGap; }
                if (gap >= required)
                {
                    var low = (i - 1) * Mathf.Pi / 12;
                    var high = i * Mathf.Pi / 12;
                    for (var step = 0; step < 8; step++)
                    {
                        var middle = (low + high) / 2;
                        var trial = center + bend.Rotated(direction, sign * middle) * height;
                        if (Gap(trial) < required) low = middle;
                        else high = middle;
                    }
                    return center + bend.Rotated(direction, sign * high) * height;
                }
            }
        }
        return best;
    }

    /// <summary>Sample tapered forearms: a wrist is narrower than an elbow. Keep the other arm's
    /// actual contact target in this objective, so a free reload/melee hand cannot enter a solved arm.</summary>
    public static float ArmPairGap(Arm a, Arm b, Tuning tuning)
    {
        static float PointGap(Vector3 point, float radius, Vector3 start, Vector3 end, float r0, float r1)
        {
            var axis = end - start;
            var fraction = Mathf.Clamp((point - start).Dot(axis) / Mathf.Max(axis.LengthSquared(), 0.00001f), 0, 1);
            return point.DistanceTo(start + axis * fraction) - radius - Mathf.Lerp(r0, r1, fraction);
        }
        var gap = float.PositiveInfinity;
        for (var i = 0; i <= 8; i++)
        {
            var phase = i / 8f;
            var radius = Mathf.Lerp(tuning.RifleArmRadius, tuning.RifleWristRadius, phase);
            var upper = a.Shoulder.Lerp(a.Elbow, phase);
            var lower = a.Elbow.Lerp(a.Wrist, phase);
            gap = Mathf.Min(gap, PointGap(upper, tuning.RifleArmRadius, b.Shoulder, b.Elbow, tuning.RifleArmRadius, tuning.RifleArmRadius));
            gap = Mathf.Min(gap, PointGap(upper, tuning.RifleArmRadius, b.Elbow, b.Wrist, tuning.RifleArmRadius, tuning.RifleWristRadius));
            gap = Mathf.Min(gap, PointGap(lower, radius, b.Shoulder, b.Elbow, tuning.RifleArmRadius, tuning.RifleArmRadius));
            gap = Mathf.Min(gap, PointGap(lower, radius, b.Elbow, b.Wrist, tuning.RifleArmRadius, tuning.RifleWristRadius));
        }
        return gap;
    }

    public float ArmGap(Skeleton3D skeleton, Vector3 shoulder, Vector3 elbow, Vector3 wrist, Tuning tuning)
        => ArmContact(shoulder, elbow, wrist, tuning).Gap;

    public (float Gap, Vector3 Normal) ArmContact(Vector3 shoulder, Vector3 elbow, Vector3 wrist, Tuning tuning)
    {
        var worst = (Gap: float.PositiveInfinity, Normal: Vector3.Zero);
        for (var i = 0; i <= 8; i++)
        {
            // The upper arm joins the shoulder; omit that socket's proximal skin contact.
            var upper = BodyContact(shoulder.Lerp(elbow, Mathf.Lerp(Mathf.Min(1, tuning.RifleShoulderContactLength / Mathf.Max(shoulder.DistanceTo(elbow), 0.00001f)), 1, i / 8f)), tuning.RifleArmRadius, tuning.RifleLimbClearance);
            var lower = BodyContact(elbow.Lerp(wrist, i / 8f), tuning.RifleArmRadius, tuning.RifleLimbClearance);
            if (upper.Gap < worst.Gap) worst = upper;
            if (lower.Gap < worst.Gap) worst = lower;
        }
        return worst;
    }

    private static float EllipsoidDistance(Vector3 point, Vector3 radii, float exactWithin = 0.04f)
    {
        var outside = (point.Abs() - radii).Max(Vector3.Zero).Length();
        if (outside > exactWithin) return outside;
        var scaled = point / radii;
        var length = scaled.Length();
        var conservative = (length - 1) * Mathf.Min(radii.X, Mathf.Min(radii.Y, radii.Z));
        if (conservative > exactWithin || length < 0.00001f) return conservative;
        if (length <= 1) return (length - 1) / (scaled.Normalized() / radii).Length();
        var low = 0f;
        var high = radii.Length() * point.Length();
        var squared = radii * radii;
        for (var step = 0; step < 12; step++)
        {
            var mid = (low + high) / 2;
            var constraint = radii * point / (squared + Vector3.One * mid);
            if (constraint.LengthSquared() > 1) low = mid;
            else high = mid;
        }
        var surface = squared * point / (squared + Vector3.One * high);
        return point.DistanceTo(surface);
    }

    private (float Gap, Vector3 Normal) BodyContact(Vector3 point, float limbRadius, float margin)
    {
        var gap = float.PositiveInfinity;
        var normal = Vector3.Zero;
        for (var i = 0; i < body.Count; i++)
        {
            var (_, volumeCenter, volumeRadius) = body[i];
            var normalized = (bodyFrames[i] * point - volumeCenter) / volumeRadius;
            var distance = EllipsoidDistance(bodyFrames[i] * point - volumeCenter, volumeRadius, limbRadius + margin) - limbRadius;
            if (distance >= gap) continue;
            gap = distance;
            normal = bodyFrames[i].Basis.Transposed() * (normalized / volumeRadius).Normalized();
        }
        return (gap, normal.Normalized());
    }
}
