using Godot;
using Propane.Core;

namespace Propane.Player;

public enum JumpPose { Ground, Takeoff, Rising, Apex, Falling, Landing }

/// <summary>One continuous distance clock for the authored directional clips. Physics observations latch
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
    public float Turn { get; private set; }
    public float TurnPhase { get; private set; }
    public float TurnRight { get; private set; }
    public float Impact { get; private set; }
    public float TakeoffTime { get; private set; }
    public JumpPose Jump { get; private set; }
    public Vector2 Direction { get; private set; } = Vector2.Down;
    public float BackBlend => Direction.Y < 0 ? 1 : 0;
    public float RightBlend => Direction.X > 0 ? 1 : 0;
    public float WalkSideBlend => SideBlend(tuning.WalkStrideLength, tuning.BackWalkStrideLength, tuning.SideWalkStrideLength);
    public float JogSideBlend => SideBlend(tuning.JogStrideLength, tuning.BackJogStrideLength, tuning.SideJogStrideLength);
    public float WalkDiagonalWeight => DiagonalWeight(tuning.WalkStrideLength, tuning.BackWalkStrideLength, tuning.SideWalkStrideLength);
    public float JogDiagonalWeight => DiagonalWeight(tuning.JogStrideLength, tuning.BackJogStrideLength, tuning.SideJogStrideLength);
    public float Stride => Mathf.Lerp(Mathf.Lerp(
        DirectionalStride(tuning.JogStrideLength, tuning.BackJogStrideLength, tuning.SideJogStrideLength),
        DirectionalStride(tuning.WalkStrideLength, tuning.BackWalkStrideLength, tuning.SideWalkStrideLength), Walk), tuning.SprintStrideLength, Sprint);

    private float DirectionalStride(float forward, float back, float side)
    {
        var direction = Direction.Normalized();
        return 1 / Mathf.Max(Mathf.Abs(direction.X) / side + Mathf.Abs(direction.Y) / (direction.Y < 0 ? back : forward), 0.001f);
    }

    private float SideBlend(float forward, float back, float side)
    {
        var lateral = Mathf.Abs(Direction.X) / side;
        var longitudinal = Mathf.Abs(Direction.Y) / (Direction.Y < 0 ? back : forward);
        return lateral / Mathf.Max(lateral + longitudinal, 0.001f);
    }

    private float DiagonalWeight(float forward, float back, float side)
    {
        var longitudinal = Direction.Y < 0 ? back : forward;
        return longitudinal / (longitudinal + side);
    }
    private bool supported = true;
    private bool wasJumping;
    private float vertical;
    private float landingTime;
    private float lowestVelocity;
    private float takeoffLeft;

    public void Reset()
    {
        Phase = Walk = Sprint = Moving = Air = Landing = Impact = TakeoffTime = 0;
        Turn = TurnPhase = TurnRight = 0;
        Direction = Vector2.Down;
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

    public void Update(float speed, PlayerGait gait, float dt, Vector2 direction = default, float yawSpeed = 0)
    {
        var t = tuning;
        var blend = dt / Mathf.Max(t.GaitBlendTime, 0.001f);
        if (speed > t.AnimationIdleSpeed && direction.LengthSquared() > 0)
        {
            var target = direction.Normalized();
            // Angle interpolation avoids a zero vector when travel reverses while keeping the stride clock.
            var angle = Mathf.LerpAngle(Mathf.Atan2(Direction.X, Direction.Y), Mathf.Atan2(target.X, target.Y), Mathf.Min(1, blend));
            Direction = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
        }
        var walk = gait == PlayerGait.Creep ? 1 : 1 - Mathf.SmoothStep(t.CreepSpeed, t.RunSpeed, speed);
        var sprint = gait == PlayerGait.Sprint ? Mathf.SmoothStep(0, t.SprintSpeed, speed) : 0;
        Walk = Mathf.MoveToward(Walk, walk, blend);
        Sprint = Mathf.MoveToward(Sprint, sprint, blend);
        var moving = Mathf.Clamp(speed / Mathf.Max(t.AnimationIdleSpeed, 0.001f), 0, 1);
        Moving = Mathf.MoveToward(Moving, moving, dt / t.LocomotionSettleTime);
        var turning = supported && speed < t.AnimationIdleSpeed && Mathf.Abs(yawSpeed) >= t.TurnPoseThresholdDegrees;
        if (turning)
        {
            if (Turn == 0) TurnPhase = tuning.TurnInitialPhase + (yawSpeed < 0 ? 0.5f : 0);
            TurnRight = yawSpeed < 0 ? 1 : 0;
            TurnPhase = Mathf.PosMod(TurnPhase + Mathf.Abs(yawSpeed) * dt / t.TurnStrideDegrees, 1);
        }
        Turn = Mathf.MoveToward(Turn, turning ? 1 : 0, dt / t.LocomotionSettleTime);
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
