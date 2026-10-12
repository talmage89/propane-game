using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Controlled snapshots cover eight directions for each gait. Walk/creep keep aim-facing hips;
/// sprint faces travel, as the local controller does. Corrections cover interpolation and knockback.</summary>
public partial class LocomotionMatrix : Node
{
    public PlayerCharacter Player { get; init; } = null!;
    public LocomotionProbe Probe { get; init; } = null!;
    public static float EpisodeTime => DevArgs.Get("feet-corrections") != null ? 3.5f : 3;
    public const int Episodes = 24;
    private float time;
    private Vector3 position;
    private Vector3 velocity;

    public override void _Ready() => ProcessPhysicsPriority = -5;

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        time += dt;
        var index = Mathf.Min((int)(time / EpisodeTime), Episodes - 1);
        var gait = (PlayerGait)(index / 8);
        var direction = index % 8;
        var angle = direction * Mathf.Tau / 8;
        var speed = gait == PlayerGait.Creep ? Player.World.Tuning.CreepSpeed : gait == PlayerGait.Sprint ? Player.World.Tuning.SprintSpeed : Player.World.Tuning.RunSpeed;
        var localTime = time % EpisodeTime;
        var target = localTime < 0.25f || localTime > EpisodeTime - 0.25f ? Vector3.Zero : new Vector3(Mathf.Sin(angle), 0, -Mathf.Cos(angle)) * speed;
        velocity = velocity.MoveToward(target, Player.World.Tuning.Acceleration * dt);
        position += velocity * dt;
        // Reproducible interpolation corrections in both directions. A saved contact must release rather
        // than drag the old foot through an unreachable solve, and the new joint pose must blend in.
        var correction = DevArgs.Get("feet-corrections") != null && localTime > 0.7f && localTime < 1.8f
            ? new Vector3(Mathf.Sin(angle), 0, -Mathf.Cos(angle)) * 0.8f : Vector3.Zero;
        if (DevArgs.Get("feet-corrections") != null && localTime > 1.1f && localTime < 1.5f)
            correction += Vector3.Up * 0.4f; // Numeric height has advanced; the discrete grounded flag has not.
        Player.SetRemoteState(new PlayerNetState
        {
            Position = position + correction,
            Velocity = velocity,
            Yaw = gait == PlayerGait.Sprint ? -angle : 0,
            AimTarget = position + Vector3.Up * 1.5f + (gait == PlayerGait.Sprint
                ? new Vector3(Mathf.Sin(angle), 0, -Mathf.Cos(angle)) * 20 : Vector3.Forward * 20),
            Grounded = true,
            Support = PlayerSupport.Grounded,
            Gait = gait,
            Weapon = gait == PlayerGait.Creep ? WeaponAction.Aim : WeaponAction.Carry,
            Aim = gait == PlayerGait.Creep ? 1 : 0,
            Ready = gait == PlayerGait.Creep ? 1 : 0,
            Sprint = gait == PlayerGait.Sprint ? 1 : 0,
        });
        Probe.Label = $"{gait}_{direction * 45:000}";
        Probe.SampleDrift = localTime > (DevArgs.Get("feet-corrections") != null ? 2.1f : 1) && localTime < EpisodeTime - 0.25f;
        Probe.SampleSeparation = localTime > 0.2f;
    }
}
