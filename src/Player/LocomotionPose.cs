using Godot;
using Propane.Core;

namespace Propane.Player;

public enum JumpPose { Ground, Takeoff, Rising, Apex, Falling, Landing }

/// <summary>One continuous stride clock for every locomotion clip, and the blend weights that choose them.
/// Each clip is one cycle starting at the left foot's touchdown, so sampling all of them at the same phase keeps
/// the feet in step through gait and direction blends. Physics observations latch the one-tick jump/landing
/// events; render updates only interpolate presentation, never movement.</summary>
public sealed class LocomotionPose
{
    private readonly Tuning tuning;
    public LocomotionPose(Tuning tuning) => this.tuning = tuning;
    public int Takeoffs { get; private set; }
    public float Phase { get; private set; }
    /// <summary>Walk family weight against the jog (1 = walk clips only).</summary>
    public float Walk { get; private set; }
    public float Sprint { get; private set; }
    public float Moving { get; private set; }
    /// <summary>Walk family: share of the sidestep clip, from travel relative to the body.</summary>
    public float Side { get; private set; }
    /// <summary>Walk family: right sidestep against the left, and backpedal against the forward walk. Each pair
    /// changes over only where its share of the blend is small.</summary>
    public float Right { get; private set; }
    public float Back { get; private set; }
    /// <summary>Jog family: share of the backward run (the jog played in reverse).</summary>
    public float JogBack { get; private set; }
    public float Air { get; private set; }
    public float Landing { get; private set; }
    public float Impact { get; private set; }
    public float TakeoffTime { get; private set; }
    public JumpPose Jump { get; private set; }
    /// <summary>Ground covered by one cycle of the current blend.</summary>
    public float Stride
    {
        get
        {
            var t = tuning;
            var walk = Mathf.Lerp(Mathf.Lerp(t.WalkStrideLength, t.BackStrideLength, Back), t.SideStrideLength, Side);
            return Mathf.Lerp(Mathf.Lerp(t.JogStrideLength, walk, Walk), t.SprintStrideLength, Sprint);
        }
    }
    private float heading;
    private bool supported = true;
    private bool wasJumping;
    private float vertical;
    private float landingTime;
    private float lowestVelocity;
    private float takeoffLeft;

    public void Reset()
    {
        Phase = Walk = Sprint = Moving = Air = Landing = Impact = TakeoffTime = Side = JogBack = Right = Back = heading = 0;
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

    /// <summary><paramref name="travel"/> is planar velocity in the body frame: x right, y forward (m/s).</summary>
    public void Update(Vector2 travel, PlayerGait gait, float dt)
    {
        var t = tuning;
        var speed = travel.Length();
        var blend = dt / Mathf.Max(t.GaitBlendTime, 0.001f);
        var walk = gait == PlayerGait.Creep ? 1 : 1 - Mathf.SmoothStep(0, t.RunSpeed, speed);
        var sprint = gait == PlayerGait.Sprint ? Mathf.SmoothStep(0, t.SprintSpeed, speed) : 0;
        Walk = Mathf.MoveToward(Walk, walk, blend);
        Sprint = Mathf.MoveToward(Sprint, sprint, blend);
        var moving = Mathf.Clamp(speed / Mathf.Max(t.AnimationIdleSpeed, 0.001f), 0, 1);
        Moving = Mathf.MoveToward(Moving, moving, dt / t.LocomotionSettleTime);
        if (speed > 0.05f)
        {
            // Turn the heading of travel rather than blending weights: even a reversal sweeps through a side
            // direction, where the front/back pair has almost no weight, so no clip pops in. At most a quarter
            // turn per gait blend time, so a sudden reversal still crosses the side over several frames.
            var turn = Mathf.Wrap(Mathf.Atan2(travel.X, travel.Y) - heading, -Mathf.Pi, Mathf.Pi);
            var limit = Mathf.Pi / 2 * blend;
            heading = Mathf.Wrap(heading + Mathf.Clamp(turn * (1 - Mathf.Exp(-3 * blend)), -limit, limit), -Mathf.Pi, Mathf.Pi);
        }
        var direction = new Vector2(Mathf.Sin(heading), Mathf.Cos(heading));
        // Diamond (L1) weights interpolate linearly between neighboring clips; 45 degrees is half of each.
        Side = Mathf.Abs(direction.X) / (Mathf.Abs(direction.X) + Mathf.Abs(direction.Y));
        Right = Mathf.SmoothStep(-0.15f, 0.15f, direction.X);
        Back = Mathf.SmoothStep(0.15f, -0.15f, direction.Y);
        JogBack = Mathf.SmoothStep(0.3f, -0.3f, direction.Y);
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
