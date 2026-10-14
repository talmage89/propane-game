using System;
using Propane.Core;

namespace Propane.Player;

/// <summary>Presentation-only distance bands and a staggered clock; simulation never consumes this decision.</summary>
public sealed class ActorPoseCadence
{
    private readonly float phase;
    private bool near = true, far;
    private float elapsed, remaining, previousInterval;
    public ActorPoseCadence(ulong slot) => phase = (slot % 17) / 17f;

    public float Interval(Tuning tuning, float distance, bool visible, bool forceFull)
    {
        if (forceFull || !tuning.ActorPoseLod)
        {
            near = true;
            far = false;
            return 0;
        }
        var nearDistance = MathF.Max(0, tuning.ActorPoseNearDistance);
        var farDistance = MathF.Max(nearDistance, tuning.ActorPoseFarDistance);
        var hysteresis = MathF.Max(0, tuning.ActorPoseHysteresis);
        near = distance <= nearDistance || near && distance <= nearDistance + hysteresis;
        if (near) { far = false; return 0; }
        far = distance > farDistance + hysteresis || far && distance > farDistance;
        var rate = !visible ? tuning.ActorPoseHiddenRate : far ? tuning.ActorPoseFarRate : tuning.ActorPoseRate;
        return 1 / MathF.Max(rate, 1);
    }

    /// <summary>Sample immediately on a band/rate change; subsequent actors spread across the interval.
    /// Returns actual accumulated time, never a manufactured interval, so springs and clips keep real time.</summary>
    public bool Advance(float dt, float interval, out float sampleDelta)
    {
        elapsed += dt;
        remaining -= dt;
        if (interval > 0 && interval == previousInterval && remaining > 0.000001f)
        {
            sampleDelta = 0;
            return false;
        }
        sampleDelta = elapsed;
        elapsed = 0;
        remaining = interval != previousInterval ? interval * phase : remaining + interval;
        if (remaining < 0) remaining = interval; // A hitch needs one current pose, not catch-up pose work.
        previousInterval = interval;
        return true;
    }

    public float PendingTime => elapsed;

    public void Reset()
    {
        elapsed = remaining = previousInterval = 0;
        near = true;
        far = false;
    }
}
