using System;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

/// <summary>Opt-in FU2b behavioral assertions: run on the parent first to establish a failing oracle.</summary>
[UnitSuite]
public static class BodyPredictionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("prediction/accelerated free flight advances pose and velocity together", () =>
        {
            if (DevArgs.Get("correction-proof") == null) { return; }
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
    }
}
