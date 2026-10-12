using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Bots;

/// <summary>Track a desired view and pull against observed recoil through the same look path as a player.</summary>
public sealed class BotAim
{
    private float yaw, pitch;
    private bool initialized;

    public void Reset() => initialized = false;

    public void Track(PlayerCharacter player, Vector3 point, float dt, float skill, bool smooth = true)
    {
        var aim = player.Aim;
        var view = point - aim.EyeOrigin;
        if (view.LengthSquared() < 0.0001f) return;
        if (!initialized)
        {
            yaw = aim.Yaw;
            pitch = aim.Pitch;
            initialized = true;
        }
        var tuning = player.World.Tuning;
        var wantYaw = Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z));
        var wantPitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1)));
        var response = smooth ? 1f - Mathf.Exp(-tuning.BotAimTurnSharpness * Mathf.Lerp(0.35f, 1f, skill) * dt) : 1;
        var nextYaw = yaw + Mathf.Wrap(wantYaw - yaw, -180f, 180f) * response;
        var nextPitch = Mathf.Lerp(pitch, wantPitch, response);
        var pull = 1f - Mathf.Exp(-tuning.BotRecoilControlSpeed * dt);
        var yawMove = Mathf.Wrap(nextYaw - yaw, -180f, 180f) + Mathf.Wrap(yaw - aim.Yaw, -180f, 180f) * pull;
        var pitchMove = nextPitch - pitch + (pitch - aim.Pitch) * pull;
        aim.Rotate(new Vector2(yawMove, pitchMove));
        yaw = nextYaw;
        pitch = nextPitch;
    }
}
