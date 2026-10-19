using System;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

/// <summary>FU2b prediction assertions; the acceleration oracle was proved failing on the parent before production changed.</summary>
[UnitSuite]
public static class BodyPredictionTests
{
    public static void Register(TestRunner runner)
    {
        foreach (var kind in new[] { "moving-vent", "intact", "vent-rest" })
        {
            runner.Run("prediction/vent blend scope " + kind, () =>
            {
                using var f = new FlightFixture(runner, venting: kind != "intact");
                f.Tuning.CorrectionBlendTime = .25f;
                f.Tuning.CorrectionVentBlendTime = .1f;
                var pose = f.Pose(0);
                f.Tank.GlobalTransform = pose.Translated(Vector3.Left);
                var rest = kind == "vent-rest";
                f.Sync.OnState(2, new BodyState(1, pose, Vector3.Zero, Vector3.Zero, 1, 1), rest, 10);
                f.Sync.Correct(rest ? 10.4 : 10);
                var expected = Vector3.Right * (kind == "moving-vent" ? 10 : 4);
                TestRunner.Assert(!f.Tank.Sleeping && f.Tank.LinearVelocity.DistanceTo(expected) < .001f,
                    $"{kind} commanded {f.Tank.LinearVelocity}, expected {expected}; sleeping={f.Tank.Sleeping}");
                if (rest)
                {
                    f.Sync.Correct(11);
                    TestRunner.Assert(f.Tank.Sleeping && f.Tank.GlobalTransform.IsEqualApprox(pose),
                        "vent rest did not retain the ordinary final anchor");
                }
            });
        }
        runner.Run("prediction/new timing tuner endpoints survive shared validation", () =>
        {
            foreach (var value in new[] { .025f, .25f })
            {
                using var snapshot = new Godot.Collections.Dictionary
                {
                    [nameof(Tuning.BodyCheckpointInterval)] = value,
                    [nameof(Tuning.CorrectionVentBlendTime)] = value,
                };
                Tuning.ValidateSnapshot(snapshot);
            }
        });
        runner.Run("prediction/accelerated free flight advances pose and velocity together", () =>
        {
            var tank = PropaneTank.Create(runner.World);
            runner.World.Root.AddChild(tank);
            try
            {
                tank.CollisionLayer = tank.CollisionMask = 0;
                tank.PunctureRemote(PropaneTank.LocalCenter + Vector3.Right * .15f, Vector3.Right, 1);
                var sync = new BodySync(runner.World.Tuning, 4, 2);
                sync.Register(1, tank, true, 2);
                var acceleration = Vector3.Down * 10;
                var initial = Vector3.Up * 10;
                var velocity = Vector3.Right * 4 + Vector3.Up * 2;
                Transform3D Pose(float time) => new(Basis.Identity,
                    initial + velocity * time + acceleration * (time * time / 2) - tank.CenterOfMass);
                sync.OnState(2, new BodyState(1, Pose(0), velocity, Vector3.Zero, 1, 1), false, 1);
                sync.Correct(1); // Establish observer correction before the second publication.
                sync.OnState(2, new BodyState(1, Pose(.1f), velocity + acceleration * .1f, Vector3.Zero, 1, 2), false, 1.1);
                // Align the COM with the analytic prediction, leaving feed-forward as the only commanded velocity.
                tank.GlobalTransform = Pose(.2f);
                sync.Correct(1.2);
                var expected = velocity + acceleration * .2f;
                TestRunner.Assert(tank.LinearVelocity.DistanceTo(expected) < .001f,
                    $"accelerated observer velocity {tank.LinearVelocity} differs from analytic current {expected}");
                // Rejected input must not replace the valid estimate or its sample interval.
                sync.OnState(2, new BodyState(1, Pose(.1f), Vector3.One * 1000, Vector3.Zero, 1, 1), false, 1.15);
                tank.GlobalTransform = Pose(.2f);
                sync.Correct(1.2);
                TestRunner.Assert(tank.LinearVelocity.DistanceTo(expected) < .001f, "old sequence contaminated prediction");
                // A reliable rest is an anchor, even after the moving acceleration estimate.
                sync.OnState(2, new BodyState(1, Pose(.2f), Vector3.Zero, Vector3.Zero, 1, 3), true, 1.2);
                sync.Correct(2);
                TestRunner.Assert(tank.Sleeping && tank.LinearVelocity == Vector3.Zero && tank.GlobalTransform.IsEqualApprox(Pose(.2f)),
                    "rest retained moving acceleration");
                TestRunner.Assert(tank.MaxContactsReported == 0 && !tank.ContactMonitor, "correction changed native contact reporting");
                sync.Clear();
            }
            finally { tank.Free(); }
        });
        foreach (var kind in new[] { "zero-interval", "reversed-interval", "long-interval", "large-acceleration", "epoch", "silence" })
        {
            runner.Run("prediction/history does not cross " + kind, () =>
            {
                using var f = new FlightFixture(runner);
                f.Seed();
                var velocity = Vector3.Right * (kind == "large-acceleration" ? 100 : 7);
                var pose = new Transform3D(Basis.Identity, Vector3.Up * 10 - f.Tank.CenterOfMass);
                var now = kind == "long-interval" ? 10.6 : kind == "silence" ? 11.1 : 10.2;
                if (kind == "epoch")
                {
                    f.Sync.OnAuthority(new BodyAuthority(new BodyState(1, pose, velocity, Vector3.Zero, 2, 0),
                        2, BodyState.State.Venting, false, null, Vector3.Zero), now);
                }
                else
                {
                    if (kind == "silence") { f.Sync.Correct(11); }
                    var transit = kind == "zero-interval" ? .1 : kind == "reversed-interval" ? .15 : 0;
                    f.Sync.OnState(2, new BodyState(1, pose, velocity, Vector3.Zero, 1, 3), false, now, transit);
                }
                var entry = f.Sync.Get(1)!;
                var correctionTime = now + .05;
                var lead = (float)(correctionTime - entry.TargetReceived);
                var center = pose * f.Tank.CenterOfMass + velocity * lead;
                f.Tank.GlobalTransform = new Transform3D(Basis.Identity, center - f.Tank.CenterOfMass);
                f.Sync.Correct(correctionTime);
                f.Sync.Correct(correctionTime); // A cold native reporting step must not hide retained history.
                TestRunner.Assert(f.Tank.LinearVelocity.DistanceTo(velocity) < .001f,
                    kind + " retained acceleration from an invalid interval or authority");
            });
        }
        runner.Run("prediction/high spin and a disabled tuner retain constant velocity", () =>
        {
            foreach (var disabled in new[] { false, true })
            {
                using var f = new FlightFixture(runner);
                f.Seed();
                if (disabled) { f.Tuning.CorrectionMaxAcceleration = 0; }
                else
                {
                    f.Sync.OnState(2, new BodyState(1, f.Pose(.2f), new Vector3(4, 0, 0), Vector3.Up * 10, 1, 3), false, 10.2);
                }
                var entry = f.Sync.Get(1)!;
                var time = disabled ? 10.2 : 10.3;
                var lead = (float)(time - entry.TargetReceived);
                var center = entry.Target * f.Tank.CenterOfMass + entry.TargetLinear * lead;
                f.Tank.GlobalPosition = center - f.Tank.GlobalBasis * f.Tank.CenterOfMass;
                f.Sync.Correct(time);
                TestRunner.Assert(f.Tank.LinearVelocity.DistanceTo(entry.TargetLinear) < .001f && f.Tank.MaxContactsReported == 0,
                    "ineligible motion retained acceleration or native contact reporting");
            }
        });
        runner.Run("prediction/replayed future motion starts a new estimator history", () =>
        {
            using var f = new FlightFixture(runner);
            f.Seed();
            var pose = new Transform3D(Basis.Identity, Vector3.Up * 10 - f.Tank.CenterOfMass);
            f.Sync.OnState(3, new BodyState(1, pose, Vector3.Right * 7, Vector3.Zero, 2, 7), false, 10.25, .15);
            f.Sync.OnAuthority(new BodyAuthority(new BodyState(1, pose, Vector3.Zero, Vector3.Zero, 2, 0),
                3, BodyState.State.Venting, false, null, Vector3.Zero), 10.25);
            f.Tank.GlobalPosition = pose.Origin + Vector3.Right * 7 * .15f;
            f.Sync.Correct(10.25);
            f.Sync.Correct(10.25);
            TestRunner.Assert(f.Tank.LinearVelocity.DistanceTo(Vector3.Right * 7) < .001f, "future motion inherited old-epoch acceleration");
            f.Sync.OnState(3, new BodyState(1, pose.Translated(Vector3.Right * .65f), Vector3.Right * 6, Vector3.Zero, 2, 8), false, 10.35, .15);
            f.Tank.GlobalPosition = pose.Origin + Vector3.Right * 1.4375f;
            f.Sync.Correct(10.35);
            TestRunner.Assert(f.Tank.LinearVelocity.DistanceTo(Vector3.Right * 4.5f) < .001f,
                "second future-epoch sample lost its sampling interval or feed-forward");
        });
    }

    /// <summary>One test owns tuning, synchronization state and native tank; the runner's world is borrowed.</summary>
    private sealed class FlightFixture : IDisposable
    {
        public readonly Tuning Tuning = new() { CorrectionMaxAcceleration = 30, CorrectionAccelerationMaxSpin = 6 };
        public readonly PropaneTank Tank;
        public readonly BodySync Sync;
        public FlightFixture(TestRunner runner, bool venting = true)
        {
            Tank = PropaneTank.Create(runner.World);
            runner.World.Root.AddChild(Tank);
            Tank.CollisionLayer = Tank.CollisionMask = 0;
            if (venting) { Tank.PunctureRemote(PropaneTank.LocalCenter + Vector3.Right * .15f, Vector3.Right, 1); }
            Sync = new BodySync(Tuning, 4, 2);
            Sync.Register(1, Tank, true, 2);
        }
        public Transform3D Pose(float time) => new(Basis.Identity,
            Vector3.Up * 10 + new Vector3(4, 2, 0) * time + Vector3.Down * (5 * time * time) - Tank.CenterOfMass);
        public void Seed()
        {
            Sync.OnState(2, new BodyState(1, Pose(0), new Vector3(4, 2, 0), Vector3.Zero, 1, 1), false, 10);
            Sync.Correct(10);
            Sync.OnState(2, new BodyState(1, Pose(.1f), new Vector3(4, 1, 0), Vector3.Zero, 1, 2), false, 10.1);
        }
        public void Dispose() { Sync.Clear(); Tank.Free(); Tuning.Dispose(); }
    }
}
