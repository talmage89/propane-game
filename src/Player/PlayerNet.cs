using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>A magazine and reserve rounds, for matches. Single player has no ammo limit.</summary>
public sealed class AmmoState
{
    public required Tuning Tuning { get; init; }
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
            var time = Tuning.ReloadTime;
            return Reloading && time > 0 ? 1f - ReloadLeft / time : 0f;
        }
    }

    public static AmmoState Starting(Tuning tuning)
    {
        return new AmmoState { Tuning = tuning, Magazine = tuning.MagazineSize, Reserve = tuning.StartingReserve };
    }

    public bool CanReload => !Reloading && Reserve > 0 && Magazine < Tuning.MagazineSize;

    public void StartReload()
    {
        if (CanReload)
        {
            ReloadLeft = Mathf.Max(Tuning.ReloadTime, 0.001f);
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
            var moved = Mathf.Min(Tuning.MagazineSize - Magazine, Reserve);
            Magazine += moved;
            Reserve -= moved;
        }
    }

    /// <summary>Adds picked-up rounds to the reserve, up to the cap. Returns how many fit.</summary>
    public int AddReserve(int rounds)
    {
        var added = Mathf.Clamp(Tuning.MaxReserve - Reserve, 0, rounds);
        Reserve += added;
        return added;
    }
}

/// <summary>What a shot did, for a match to send on.</summary>
public readonly record struct ShotReport(Vector3 Muzzle, Vector3 End, Vector3 Normal, Vector3 Direction, GodotObject? Collider, bool Hit);
