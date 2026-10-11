using Godot;
using Propane.Core;

namespace Propane.Player;

public enum JumpPose { Ground, Takeoff, Rising, Apex, Falling, Landing }

/// <summary>One continuous stride clock for the clips and the procedural contacts. Physics observations latch
/// the one-tick jump/landing events; render updates only interpolate presentation, never movement.</summary>
public sealed class LocomotionPose
{
    private readonly Tuning tuning;
    public LocomotionPose(Tuning tuning) => this.tuning = tuning;
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
    public float Stride => Mathf.Lerp(Mathf.Lerp(tuning.JogStrideLength, tuning.WalkStrideLength, Walk), tuning.SprintStrideLength, Sprint);
    public float Contact => Mathf.Lerp(Mathf.Lerp(tuning.JogContactFraction, tuning.WalkContactFraction, Walk), tuning.SprintContactFraction, Sprint);
    public float Lift => Mathf.Lerp(Mathf.Lerp(tuning.JogFootLift, tuning.WalkFootLift, Walk), tuning.SprintFootLift, Sprint);
    private bool supported = true;
    private bool wasJumping;
    private float vertical;
    private float landingTime;
    private float lowestVelocity;
    private float takeoffLeft;

    public void Reset()
    {
        Phase = Walk = Sprint = Moving = Air = Landing = Impact = TakeoffTime = 0;
        supported = true;
        wasJumping = false;
        Takeoffs = 0;
        lowestVelocity = takeoffLeft = landingTime = 0;
        Jump = JumpPose.Ground;
    }

    public void ObservePhysics(PlayerState state, float verticalVelocity)
    {
        if (!state.Active) { Reset(); return; }
        if (!state.Grounded)
        {
            // Accepted rebounds can skip the observed grounded tick. Jumping is authoritative; remote
            // snapshots may hold it across several ticks or omit it entirely.
            if ((state.Support == PlayerSupport.Jumping && !wasJumping) ||
                (supported && verticalVelocity > tuning.JumpApexSpeed))
            {
                Takeoffs++;
                landingTime = 0;
                lowestVelocity = 0;
                takeoffLeft = tuning.TakeoffPoseTime;
                TakeoffTime = 0;
            }
            lowestVelocity = Mathf.Min(lowestVelocity, verticalVelocity);
        }
        else if (!supported)
        {
            Impact = Mathf.Clamp(-lowestVelocity / tuning.LandingFullImpactSpeed, 0, 1);
            landingTime = tuning.LandingSettleTime;
            lowestVelocity = 0;
        }
        supported = state.Grounded;
        wasJumping = state.Support == PlayerSupport.Jumping;
        vertical = verticalVelocity;
    }

    public void Update(float speed, PlayerGait gait, float dt)
    {
        var t = tuning;
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
