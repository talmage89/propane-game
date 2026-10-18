using Godot;

namespace Propane.Player;

/// <summary>Body pose and animation inputs of a player, sent to the other players so they can show it.</summary>
public struct PlayerNetState
{
    public enum Mode : byte
    {
        Active,
        Ragdoll,
        GettingUp,
    }

    public Vector3 Position;
    public Vector3 Velocity;
    public float Yaw;
    public Vector3 AimTarget;
    public float Aim;
    public float Sprint;
    public float Ready;
    public float Reload;
    public float Melee;
    public bool Grounded;
    public PlayerGait Gait;
    public PlayerSupport Support;
    public WeaponAction Weapon;
    public Mode State;
    public Vector3 PelvisPosition;
    public Vector3 PelvisVelocity;

    /// <summary>Counts staggers, so a missed packet does not lose one.</summary>
    public byte Staggers;

    /// <summary>Linear blend of two states (angles take the short way round).</summary>
    public static PlayerNetState Lerp(PlayerNetState a, PlayerNetState b, float t)
    {
        var s = t < 0.5f ? a : b;
        s.Position = a.Position.Lerp(b.Position, t);
        s.Velocity = a.Velocity.Lerp(b.Velocity, t);
        s.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
        s.AimTarget = a.AimTarget.Lerp(b.AimTarget, t);
        // Discrete weapon/support ownership wins over pose interpolation: no residual aim in the air/reload.
        s.Aim = s.Weapon == WeaponAction.Aim && s.Support is PlayerSupport.Grounded or PlayerSupport.Landing
            ? Mathf.Lerp(a.Aim, b.Aim, t) : 0;
        s.Sprint = Mathf.Lerp(a.Sprint, b.Sprint, t);
        s.Ready = Mathf.Lerp(a.Ready, b.Ready, t);
        s.Reload = Mathf.Lerp(a.Reload, b.Reload, t);
        // Do not interpolate across a new swing or its release; that would rewind/skew the contact pose.
        s.Melee = a.Weapon == WeaponAction.Melee && b.Weapon == WeaponAction.Melee && b.Melee >= a.Melee
            ? Mathf.Lerp(a.Melee, b.Melee, t) : s.Melee;
        s.PelvisPosition = a.PelvisPosition.Lerp(b.PelvisPosition, t);
        s.PelvisVelocity = a.PelvisVelocity.Lerp(b.PelvisVelocity, t);
        return s;
    }
}
