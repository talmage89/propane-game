using System;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class RecoilTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("logical recoil retains magazine climb independently of render rate", () =>
        {
            float Sample(int hz)
            {
                using var tuning = new Tuning { RecoilKickDegrees = 0.8f, RecoilYawDegrees = 0 };
                using var world = new WorldContext(runner, tuning);
                using var aim = new LogicalAim(world) { Target = Vector3.Up * 2 };
                aim.SetOrientation(0, 0);
                var shot = 0;
                for (var frame = 0; frame < hz * 11; frame++)
                {
                    if (shot < 30 && (float)frame / hz >= (float)shot / 13)
                    {
                        aim.Kick(0.48f);
                        shot++;
                    }
                    aim.Advance(1.0 / hz);
                }
                TestRunner.Assert(shot == 30, "fixture did not fire a full magazine");
                return aim.Pitch;
            }
            var retained = Sample(60);
            TestRunner.Assert(Math.Abs(retained - 12.6f) < 0.001f, "logical magazine climb returned automatically");
            TestRunner.Assert(Math.Abs(Sample(30) - retained) < 0.0001f && Math.Abs(Sample(120) - retained) < 0.0001f,
                "logical settle depends on frame rate");
        });
        runner.Run("logical recoil accepts immediate mouse and stick correction without clamp windup", () =>
        {
            using var tuning = new Tuning { RecoilReturnShare = 0, RecoilYawDegrees = 0 };
            using var world = new WorldContext(runner, tuning);
            using var aim = new LogicalAim(world);
            aim.SetOrientation(0, 0);
            aim.Kick(3);
            TestRunner.Assert(Math.Abs(Mathf.RadToDeg(Mathf.Asin(aim.Forward.Y)) - 3) < 0.00001f, "kick missed the immediate shot ray");
            aim.Look(new Vector2(0, 3 / Mathf.RadToDeg(tuning.MouseSensitivity)));
            TestRunner.Assert(Math.Abs(aim.Pitch) < 0.00001f, "mouse cannot pull down persistent climb");
            aim.Kick(3);
            aim.Look(Vector2.Zero, Vector2.Down, 3 / tuning.StickLookDegreesPerSecond);
            TestRunner.Assert(Math.Abs(aim.Pitch) < 0.00001f, "stick cannot pull down persistent climb");
            world.Tuning.RecoilReturnShare = 0.125f;
            aim.SetOrientation(0, 45);
            for (var shot = 0; shot < 30; shot++) aim.Kick(1);
            aim.Advance(10);
            TestRunner.Assert(aim.Pitch == 45, "blocked kicks accumulated settle at the pitch limit");
            aim.SetOrientation(0, 0);
            aim.Advance(10);
            TestRunner.Assert(aim.Pitch == 0, "spawn orientation retained a settle budget");
        });
        runner.Run("camera kick never changes logical shot aim", () =>
        {
            using var world = new WorldContext(runner, Tuning.SavedTemplate);
            using var aim = new LogicalAim(world);
            aim.SetOrientation(0, 0);
            aim.Kick(0.4f);
            var pitch = aim.Pitch;
            var forward = aim.Forward;
            var camera = new CameraRig { World = world, Aim = aim };
            world.Root.AddChild(camera);
            try
            {
                camera.KickPresentation(1);
                camera._Process(1.0 / 60);
                TestRunner.Assert(aim.Pitch == pitch && aim.Forward == forward, "camera spring moved the shot ray");
                TestRunner.Assert((-camera.Camera.GlobalBasis.Z).AngleTo(forward) > 0, "fixture did not apply visual kick");
                for (var frame = 0; frame < 180; frame++) camera._Process(1.0 / 60);
                TestRunner.Assert(aim.Pitch == pitch && aim.Forward == forward, "camera recovery erased persistent climb");
            }
            finally { camera.Free(); }
        });
        runner.Run("barrel alignment preserves the muzzle and handles close and vertical endpoints", () =>
        {
            var muzzleLocal = new Vector3(0.03f, 0.15f, -0.6f);
            var pose = new Transform3D(Basis.FromEuler(new Vector3(.2f, .8f, .1f)), new Vector3(3, 2, 1));
            var muzzle = pose * muzzleLocal;
            foreach (var direction in new[] { Vector3.Up, Vector3.Down, Vector3.Back, new Vector3(-1, .2f, -1).Normalized() })
            {
                foreach (var distance in new[] { .03f, 2f, 400f })
                {
                    var aligned = Rifle.AlignBarrel(pose, muzzleLocal, muzzle + direction * distance);
                    TestRunner.Assert((aligned * muzzleLocal).DistanceTo(muzzle) < 0.000001f, "alignment moved the obstruction origin");
                    TestRunner.Assert(Mathf.RadToDeg((-aligned.Basis.Z).AngleTo(direction)) < 0.001f, "barrel missed the sampled endpoint");
                }
            }
        });
        runner.Run("recoil recovery is independent of render rate", () =>
        {
            float Sample(int hz)
            {
                var spring = new RecoilSpring();
                spring.Kick(5, 2);
                for (var i = 0; i < hz / 5; i++) spring.Update(1f / hz, 5, 0.65f);
                return spring.Position;
            }
            TestRunner.Assert(Math.Abs(Sample(30) - Sample(60)) < 0.00001f && Math.Abs(Sample(60) - Sample(120)) < 0.00001f,
                "spring recovery depends on frame rate");
        });
        runner.Run("recoil bursts accumulate and settle without drift", () =>
        {
            var spring = new RecoilSpring();
            spring.Kick(5, 2);
            spring.Update(1f / 9, 5, 0.65f);
            var remaining = spring.Position;
            spring.Kick(5, 2);
            TestRunner.Assert(remaining > 0 && spring.Position == remaining, "new shot discards displacement");
            for (var frame = 0; frame < 240; frame++)
            {
                if (frame < 120 && frame % 7 == 0) spring.Kick(5, 2);
                spring.Update(1f / 60, 5, 0.65f);
                TestRunner.Assert(float.IsFinite(spring.Position) && Math.Abs(spring.Position) < 2, "burst is unbounded");
            }
            TestRunner.Assert(Math.Abs(spring.Position) < 0.00001f && Math.Abs(spring.Velocity) < 0.00001f, "burst retains drift");
            spring.Kick(5, 2);
            spring.Reset();
            TestRunner.Assert(spring.Position == 0 && spring.Velocity == 0, "lifecycle retains recoil momentum");
        });
    }
}
