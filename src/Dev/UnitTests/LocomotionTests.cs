using System;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev.UnitTests;

public static class LocomotionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("locomotion keeps phase across gaits and stops", Phase);
        runner.Run("locomotion blends synchronized gaits over actual speed", SpeedBlend);
        runner.Run("locomotion latches jump across physics/render rates", Jump);
        runner.Run("locomotion handles remote takeoff without a jumping snapshot", RemoteJump);
        runner.Run("locomotion scales landing and allows immediate movement", Landing);
        runner.Run("locomotion resets phase and support blends on lifecycle exit", Reset);
    }

    private static PlayerState State(PlayerSupport support, PlayerLifecycle lifecycle = PlayerLifecycle.Active) =>
        new(lifecycle, support, PlayerGait.Default, WeaponAction.Carry, 0, 0, 0, false, support == PlayerSupport.Jumping, false);

    private static void Phase()
    {
        var pose = new LocomotionPose();
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        foreach (var gait in new[] { PlayerGait.Creep, PlayerGait.Sprint, PlayerGait.Default, PlayerGait.Creep })
        {
            var phase = pose.Phase;
            pose.Update(0, gait, 1f / 60);
            TestRunner.Assert(pose.Phase == phase, "gait or stop restarted the stride clock");
            for (var i = 0; i < 60; i++) pose.Update(1.6f, gait, 1f / 60);
        }
        foreach (var speed in new[] { 0.98f, 1.6f, 2.2f, 2.8f, 4.9f })
        {
            var phase = pose.Phase;
            pose.Update(speed, PlayerGait.Creep, 1f / 60);
            var distance = Mathf.PosMod(pose.Phase - phase, 1) * Tuning.Current.WalkStrideLength;
            TestRunner.Assert(Math.Abs(distance - speed / 60) < 0.00001f, $"creep stride caps speed {speed}");
        }
    }

    private static void Jump()
    {
        var pose = new LocomotionPose();
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        // The jump lasts one 120 Hz physics tick; render sees Airborne instead of Jumping.
        pose.ObservePhysics(State(PlayerSupport.Jumping), 6.4f);
        pose.ObservePhysics(State(PlayerSupport.Airborne), 6.2f);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Takeoff && pose.Takeoffs == 1, "lost the one-tick takeoff");
        var phase = pose.Phase;
        for (var i = 0; i < 12; i++) pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Rising && pose.Phase == phase, "air advanced contacts or skipped rising");
        pose.ObservePhysics(State(PlayerSupport.Airborne), 0);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Apex, "apex does not follow velocity");
        pose.ObservePhysics(State(PlayerSupport.Airborne), -4);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Falling, "fall does not follow velocity");
    }

    private static void SpeedBlend()
    {
        var pose = new LocomotionPose();
        void Settle(float speed, PlayerGait gait)
        {
            for (var i = 0; i < 30; i++) pose.Update(speed, gait, 1f / 60);
        }
        Settle(Tuning.Current.RunSpeed / 2, PlayerGait.Default);
        TestRunner.Assert(pose.Walk > 0 && pose.Walk < 1 && pose.Sprint == 0, "slow default motion does not blend walk and jog");
        Settle(Tuning.Current.RunSpeed, PlayerGait.Default);
        TestRunner.Assert(pose.Walk == 0, "full default speed does not reach jog");
        Settle(Tuning.Current.SprintSpeed / 2, PlayerGait.Sprint);
        TestRunner.Assert(pose.Sprint > 0 && pose.Sprint < 1, "slow sprint motion does not blend its gait");
        Settle(Tuning.Current.SprintSpeed, PlayerGait.Sprint);
        TestRunner.Assert(pose.Sprint == 1, "full sprint speed does not reach sprint");
        Settle(Tuning.Current.CreepSpeed, PlayerGait.Creep);
        TestRunner.Assert(pose.Walk == 1 && pose.Sprint == 0, "resolved creep lost its walk presentation");
    }

    private static void RemoteJump()
    {
        var pose = new LocomotionPose();
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        pose.ObservePhysics(State(PlayerSupport.Airborne), 5);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Takeoff, "missed remote upward support change");
        pose.Reset();
        pose.ObservePhysics(State(PlayerSupport.Airborne), -1);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Apex && pose.Takeoffs == 0, "a walk off an edge invented a jump");
    }

    private static void Landing()
    {
        float Impact(float velocity)
        {
            var pose = new LocomotionPose();
            pose.ObservePhysics(State(PlayerSupport.Airborne), velocity);
            pose.ObservePhysics(State(PlayerSupport.Landing), 0);
            pose.Update(6, PlayerGait.Default, 1f / 60);
            TestRunner.Assert(pose.Jump == JumpPose.Landing && pose.Landing > 0 && pose.Phase > 0, "moving off blocked or erased landing");
            return pose.Impact;
        }
        TestRunner.Assert(Impact(-6) > Impact(-2), "landing does not scale with impact");
        var rebound = new LocomotionPose();
        rebound.ObservePhysics(State(PlayerSupport.Airborne), -6);
        rebound.ObservePhysics(State(PlayerSupport.Landing), 0);
        rebound.ObservePhysics(State(PlayerSupport.Jumping), 6);
        rebound.Update(0, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(rebound.Landing == 0 && rebound.Jump == JumpPose.Takeoff, "rebound retains the old landing");
    }

    private static void Reset()
    {
        var pose = new LocomotionPose();
        pose.ObservePhysics(State(PlayerSupport.Jumping), 6);
        pose.Update(6, PlayerGait.Sprint, 1f / 60);
        pose.ObservePhysics(State(PlayerSupport.Airborne, PlayerLifecycle.Ragdoll), 6);
        TestRunner.Assert(pose.Air == 0 && pose.Sprint == 0 && pose.Phase == 0 && pose.Takeoffs == 0, "lifecycle retained presentation");
    }
}
