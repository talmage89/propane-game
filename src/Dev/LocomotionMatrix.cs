using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Controlled snapshot motion covers every body-relative direction even where local sprint intent is
/// forward-only. The animation must also tolerate those directions during interpolated turns and knockback.</summary>
public partial class LocomotionMatrix : Node
{
    public PlayerCharacter Player { get; init; } = null!;
    public LocomotionProbe Probe { get; init; } = null!;
    public const float EpisodeTime = 3;
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
        var speed = gait == PlayerGait.Creep ? Tuning.Current.CreepSpeed : gait == PlayerGait.Sprint ? Tuning.Current.SprintSpeed : Tuning.Current.RunSpeed;
        var localTime = time % EpisodeTime;
        var target = localTime < 0.25f || localTime > 2.75f ? Vector3.Zero : new Vector3(Mathf.Sin(angle), 0, -Mathf.Cos(angle)) * speed;
        velocity = velocity.MoveToward(target, Tuning.Current.Acceleration * dt);
        position += velocity * dt;
        Player.SetRemoteState(new PlayerNetState
        {
            Position = position, Velocity = velocity, Yaw = 0,
            AimTarget = position + new Vector3(0, 1.5f, -20), Grounded = true,
            Support = PlayerSupport.Grounded, Gait = gait,
            Weapon = gait == PlayerGait.Creep ? WeaponAction.Aim : WeaponAction.Carry,
            Aim = gait == PlayerGait.Creep ? 1 : 0, Ready = gait == PlayerGait.Creep ? 1 : 0,
            Sprint = gait == PlayerGait.Sprint ? 1 : 0,
        });
        Probe.Label = $"{gait}_{direction * 45:000}";
        Probe.SampleDrift = localTime > 1 && localTime < 2.7f;
    }
}
