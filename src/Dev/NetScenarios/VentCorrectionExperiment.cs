#if FU2B_EXPERIMENT
using System;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.NetScenarios;

/// <summary>
/// Scratch-build correction candidates. The preparer inserts calls only in build/; ordinary runtime never calls this.
/// State belongs to one BodySync entry and retires with it. Sample intervals use monotonic network seconds.
/// </summary>
internal static class VentCorrectionExperiment
{
    internal sealed class Estimate
    {
        public Vector3 Acceleration;
    }

    internal static void Accept(BodySync.Entry entry, BodyState state, bool rest, double sampled, Tuning tuning)
    {
        var estimate = entry.VentEstimate;
        estimate.Acceleration = Vector3.Zero;
        if (!entry.HasTarget || entry.TargetRest || rest) { return; }
        var interval = (float)(sampled - entry.TargetReceived);
        if (interval <= 0 || interval > BodyState.MaxExtrapolation) { return; }
        var acceleration = (state.Linear - entry.TargetLinear) / interval;
        if (acceleration.Length() > tuning.CorrectionMaxAcceleration) { return; }
        estimate.Acceleration = acceleration;
    }

    internal static void Predict(BodySync.Entry entry, Vector3 center, Quaternion rotation, float lead,
        Tuning tuning, ref Vector3 targetCenter, ref Vector3 linear)
    {
        if (entry.TargetRest || entry.Body is not Tank.PropaneTank { State: Tank.PropaneTank.TankState.Venting } body) { return; }
        var native = body.IsInsideTree() ? PhysicsServer3D.BodyGetDirectState(body.GetRid()) : null;
        var contact = native?.GetContactCount() > 0;
        using var query = new PhysicsTestMotionParameters3D { RecoveryAsCollision = true, Margin = tuning.CorrectionContactMargin };
        using var result = new PhysicsTestMotionResult3D();
        var probe = DevArgs.Get("vent-experiment-probe") != null;
        if (probe)
        {
            query.From = body.GlobalTransform;
            contact = PhysicsServer3D.BodyTestMotion(body.GetRid(), query, result);
            query.From = entry.Target;
            contact |= PhysicsServer3D.BodyTestMotion(body.GetRid(), query, result);
        }
        if (DevArgs.Get("vent-experiment-acceleration") != null && !contact &&
            entry.TargetAngular.Length() <= tuning.CorrectionAccelerationMaxSpin)
        {
            var acceleration = entry.VentEstimate.Acceleration;
            targetCenter += acceleration * (lead * lead / 2);
            linear += acceleration * lead;
        }
        if (DevArgs.Get("vent-experiment-floor") == null) { return; }
        if (probe)
        {
            query.From = new Transform3D(new Basis(rotation), targetCenter - new Basis(rotation) * center);
            if (PhysicsServer3D.BodyTestMotion(body.GetRid(), query, result))
            {
                Support(center, rotation, tuning, result.GetCollisionNormal(), result.GetCollisionPoint(), ref targetCenter);
            }
            return;
        }
        if (!contact || native == null) { return; }
        var basis = new Basis(rotation);
        for (var index = 0; index < native.GetContactCount(); index++)
        {
            // Jolt reports this normal and the collider position in world coordinates.
            var normal = native.GetContactLocalNormal(index);
            var point = native.GetContactColliderPosition(index);
            // Only static floor support in this experiment; do not constrain against moving props.
            if (normal.Dot(Vector3.Up) < tuning.CorrectionFloorNormal) { continue; }
            var minimum = float.PositiveInfinity;
            foreach (var hullPoint in ((ConvexPolygonShape3D)GameAssets.TankShape).Points)
            {
                minimum = Mathf.Min(minimum, normal.Dot(targetCenter + basis * (hullPoint - center) - point));
            }
            if (minimum < 0) { targetCenter -= normal * minimum; }
        }
    }

    private static void Support(Vector3 center, Quaternion rotation, Tuning tuning, Vector3 normal, Vector3 point, ref Vector3 targetCenter)
    {
        if (normal.Dot(Vector3.Up) < tuning.CorrectionFloorNormal) { return; }
        var minimum = float.PositiveInfinity;
        foreach (var hullPoint in ((ConvexPolygonShape3D)GameAssets.TankShape).Points)
        {
            minimum = Mathf.Min(minimum, normal.Dot(targetCenter + new Basis(rotation) * (hullPoint - center) - point));
        }
        if (minimum < 0) { targetCenter -= normal * minimum; }
    }
}
#endif
