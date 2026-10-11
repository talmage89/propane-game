using Godot;
using Propane.Core;

namespace Propane.Player;

public enum JumpPose { Ground, Takeoff, Rising, Apex, Falling, Landing }

/// <summary>One continuous stride clock for the clips and the procedural contacts. Physics observations latch
/// the one-tick jump/landing events; render updates only interpolate presentation, never movement.</summary>
public sealed class LocomotionPose
{
    public int Takeoffs { get; private set; }
    public float Phase { get; private set; }
    public float Walk { get; private set; }
    public float Sprint { get; private set; }
    public float Moving { get; private set; }
    public float Air { get; private set; }
    public float Landing { get; private set; }
    public float Impact { get; private set; }
    public float TakeoffTime { get; private set; }
    public JumpPose Jump { get; private set; }
    public float Stride => Mathf.Lerp(Mathf.Lerp(Tuning.Current.JogStrideLength, Tuning.Current.WalkStrideLength, Walk), Tuning.Current.SprintStrideLength, Sprint);
    public float Contact => Mathf.Lerp(Mathf.Lerp(Tuning.Current.JogContactFraction, Tuning.Current.WalkContactFraction, Walk), Tuning.Current.SprintContactFraction, Sprint);
    public float Lift => Mathf.Lerp(Mathf.Lerp(Tuning.Current.JogFootLift, Tuning.Current.WalkFootLift, Walk), Tuning.Current.SprintFootLift, Sprint);
    private bool supported = true;
    private float vertical;
    private float landingTime;
    private float lowestVelocity;
    private float takeoffLeft;

    public void Reset()
    {
        Phase = Walk = Sprint = Moving = Air = Landing = Impact = TakeoffTime = 0;
        supported = true;
        Takeoffs = 0;
        lowestVelocity = takeoffLeft = landingTime = 0;
        Jump = JumpPose.Ground;
    }

    public void ObservePhysics(PlayerState state, float verticalVelocity)
    {
        if (!state.Active) { Reset(); return; }
        if (!state.Grounded)
        {
            if (supported && (state.Support == PlayerSupport.Jumping || verticalVelocity > Tuning.Current.JumpApexSpeed))
            {
                Takeoffs++;
                landingTime = 0;
                takeoffLeft = Tuning.Current.TakeoffPoseTime;
                TakeoffTime = 0;
            }
            lowestVelocity = Mathf.Min(lowestVelocity, verticalVelocity);
        }
        else if (!supported)
        {
            Impact = Mathf.Clamp(-lowestVelocity / Tuning.Current.LandingFullImpactSpeed, 0, 1);
            landingTime = Tuning.Current.LandingSettleTime;
            lowestVelocity = 0;
        }
        supported = state.Grounded;
        vertical = verticalVelocity;
    }

    public void Update(float speed, PlayerGait gait, float dt)
    {
        var t = Tuning.Current;
        var blend = dt / Mathf.Max(t.GaitBlendTime, 0.001f);
        var walk = gait == PlayerGait.Creep ? 1 : 1 - Mathf.SmoothStep(0, t.RunSpeed, speed);
        var sprint = gait == PlayerGait.Sprint ? Mathf.SmoothStep(0, t.SprintSpeed, speed) : 0;
        Walk = Mathf.MoveToward(Walk, walk, blend);
        Sprint = Mathf.MoveToward(Sprint, sprint, blend);
        var moving = Mathf.Clamp(speed / Mathf.Max(t.AnimationIdleSpeed, 0.001f), 0, 1);
        Moving = Mathf.MoveToward(Moving, moving, dt / t.LocomotionSettleTime);
        if (supported)
        {
            Phase = Mathf.PosMod(Phase + speed * dt / Stride, 1);
        }
        if (takeoffLeft > 0)
        {
            TakeoffTime += dt;
            takeoffLeft = Mathf.Max(0, takeoffLeft - dt);
        }
        landingTime = Mathf.Max(0, landingTime - dt);
        Landing = Impact * Mathf.Sin(Mathf.Pi * Mathf.Clamp(landingTime / t.LandingSettleTime, 0, 1));
        Air = Mathf.MoveToward(Air, supported ? 0 : 1, dt * (supported ? t.AirPoseLowerSpeed : t.AirPoseRaiseSpeed));
        Jump = supported ? landingTime > 0 ? JumpPose.Landing : JumpPose.Ground
            : takeoffLeft > 0 ? JumpPose.Takeoff : Mathf.Abs(vertical) < t.JumpApexSpeed ? JumpPose.Apex
            : vertical > 0 ? JumpPose.Rising : JumpPose.Falling;
    }
}
