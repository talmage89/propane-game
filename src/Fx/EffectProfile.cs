using System;
using System.Diagnostics;

namespace Propane.Fx;

/// <summary>Opt-in dev timers; inclusive scopes accumulate until the harness drains each frame.</summary>
public static class EffectProfile
{
    public enum System { VentCreate, VentProcess, TankPhysics, ExplosionCreate, ExplosionProcess, Blast, BlastReceivers, Debris, Scorch, Count }
    public static bool Enabled { get; set; }
    private static readonly double[] milliseconds = new double[(int)System.Count];
    public static Scope Measure(System system) => new(system);
    public readonly struct Scope : IDisposable
    {
        private readonly System system;
        private readonly long start;
        internal Scope(System system) { this.system = system; start = Enabled ? Stopwatch.GetTimestamp() : 0; }
        public void Dispose()
        {
            if (start != 0) milliseconds[(int)system] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }
    public static double Drain(System system)
    {
        var value = milliseconds[(int)system];
        milliseconds[(int)system] = 0;
        return value;
    }
}
