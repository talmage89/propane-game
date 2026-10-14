using System;
using System.Diagnostics;

namespace Propane.Player;

/// <summary>Opt-in actor attribution. Inclusive scopes are drained by the dev frame sampler.</summary>
public static class ActorProfile
{
    public enum System { Physics, Movement, MoveAndSlide, Ragdoll, GetUp, Presentation, Locomotion, RiflePose, PoseBlend, HandFollow, Aim, AimQuery, Count }
    public static bool Enabled { get; set; }
    private static readonly double[] milliseconds = new double[(int)System.Count];
    private static readonly long[] bytes = new long[(int)System.Count];
    private static readonly long[] calls = new long[(int)System.Count];
    public static Scope Measure(System system) => new(system);
    public readonly struct Scope : IDisposable
    {
        private readonly System system;
        private readonly long start, allocated;
        internal Scope(System system)
        {
            this.system = system;
            start = Enabled ? Stopwatch.GetTimestamp() : 0;
            allocated = Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        }
        public void Dispose()
        {
            if (start == 0) return;
            milliseconds[(int)system] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bytes[(int)system] += GC.GetAllocatedBytesForCurrentThread() - allocated;
            calls[(int)system]++;
        }
    }
    public static (double Milliseconds, long Bytes, long Calls) Drain(System system)
    {
        var index = (int)system;
        var sample = (milliseconds[index], bytes[index], calls[index]);
        milliseconds[index] = 0;
        bytes[index] = calls[index] = 0;
        return sample;
    }
}
