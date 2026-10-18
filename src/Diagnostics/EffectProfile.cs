using System;
using System.Diagnostics;

namespace Propane.Diagnostics;

/// <summary>Opt-in dev timers; inclusive scopes accumulate until the harness drains each frame.</summary>
public static class EffectProfile
{
    public enum System { VentCreate, VentProcess, VentLights, TankPhysics, ExplosionCreate, ExplosionProcess, Blast, BlastQuery, BlastExclusions, BlastPush, BlastReceivers, Debris, Scorch, DebrisProcess, BotBrain, BotChoose, BotNavigation, NavigationSweep, NavigationSupport, Count }
    public static bool Enabled { get; set; }
    private static readonly double[] milliseconds = new double[(int)System.Count];
    private static readonly long[] calls = new long[(int)System.Count];
    private static readonly long[] allocations = new long[(int)System.Count];
    public static Scope Measure(System system) => new(system);
    public readonly struct Scope : IDisposable
    {
        private readonly System system;
        private readonly long start;
        private readonly long allocated;
        internal Scope(System system)
        {
            this.system = system;
            start = Enabled ? Stopwatch.GetTimestamp() : 0;
            allocated = Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        }
        public void Dispose()
        {
            if (start != 0)
            {
                milliseconds[(int)system] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocations[(int)system] += GC.GetAllocatedBytesForCurrentThread() - allocated;
                calls[(int)system]++;
            }
        }
    }
    public static double Peek(System system) => milliseconds[(int)system];

    public static long DrainCalls(System system)
    {
        var value = calls[(int)system];
        calls[(int)system] = 0;
        return value;
    }
    public static long DrainBytes(System system)
    {
        var value = allocations[(int)system];
        allocations[(int)system] = 0;
        return value;
    }
    public static double Drain(System system)
    {
        var value = milliseconds[(int)system];
        milliseconds[(int)system] = 0;
        return value;
    }
}
