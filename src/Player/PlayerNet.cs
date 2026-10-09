using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>A magazine and reserve rounds, for matches. Single player has no ammo limit.</summary>
public sealed class AmmoState
{
    public int Magazine { get; set; }

    public int Reserve { get; set; }

    /// <summary>Seconds of reload left, or 0 when not reloading.</summary>
    public float ReloadLeft { get; private set; }

    public bool Reloading => ReloadLeft > 0;

    /// <summary>0 at the start of a reload, 1 at its end; 0 when not reloading.</summary>
    public float ReloadProgress
    {
        get
        {
            var time = Tuning.Current.ReloadTime;
            return Reloading && time > 0 ? 1f - ReloadLeft / time : 0f;
        }
    }

    public static AmmoState Starting()
    {
        var tuning = Tuning.Current;
        return new AmmoState { Magazine = tuning.MagazineSize, Reserve = tuning.StartingReserve };
    }

    public bool CanReload => !Reloading && Reserve > 0 && Magazine < Tuning.Current.MagazineSize;

    public void StartReload()
    {
        if (CanReload)
        {
            ReloadLeft = Mathf.Max(Tuning.Current.ReloadTime, 0.001f);
        }
    }

    public void CancelReload() => ReloadLeft = 0;

    public void Update(float dt)
    {
        if (!Reloading)
        {
            return;
        }
        ReloadLeft -= dt;
        if (ReloadLeft <= 0)
        {
            ReloadLeft = 0;
            var moved = Mathf.Min(Tuning.Current.MagazineSize - Magazine, Reserve);
            Magazine += moved;
            Reserve -= moved;
        }
    }

    /// <summary>Adds picked-up rounds to the reserve, up to the cap. Returns how many fit.</summary>
    public int AddReserve(int rounds)
    {
        var added = Mathf.Clamp(Tuning.Current.MaxReserve - Reserve, 0, rounds);
        Reserve += added;
        return added;
    }
}

/// <summary>What a shot did, for a match to send on.</summary>
public readonly record struct ShotReport(Vector3 Muzzle, Vector3 End, Vector3 Normal, Vector3 Direction, GodotObject? Collider, bool Hit);

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
    public bool Grounded;
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
        s.Aim = Mathf.Lerp(a.Aim, b.Aim, t);
        s.Sprint = Mathf.Lerp(a.Sprint, b.Sprint, t);
        s.Ready = Mathf.Lerp(a.Ready, b.Ready, t);
        s.Reload = Mathf.Lerp(a.Reload, b.Reload, t);
        s.PelvisPosition = a.PelvisPosition.Lerp(b.PelvisPosition, t);
        s.PelvisVelocity = a.PelvisVelocity.Lerp(b.PelvisVelocity, t);
        return s;
    }
}
