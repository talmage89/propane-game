using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Core;

/// <summary>
/// Main-thread process physics controls. These are separate policies, not a shared eligibility rule or clock.
/// Worlds register hitstop at construction. Tank simulations register catch-up only on their first tank's entry.
/// Both registrations end at their existing invalidation/exit boundaries; no processing node is introduced.
/// </summary>
internal static class ProcessPhysicsPolicies
{
    /// <summary>Exclusive offline hitstop, expiring in real seconds and restoring TimeScale to one.</summary>
    internal static class Hitstop
    {
        private static int worlds;
        private static WorldContext? owner;
        private static double releaseAt;

        internal static void Register()
        {
            worlds++;
            if (worlds > 1 && owner != null) { Release(owner); }
        }

        internal static void Unregister(WorldContext world)
        {
            Release(world);
            worlds--;
        }

        internal static bool TryAcquire(WorldContext world, float duration, float scale)
        {
            if (!world.IsAlive || world.Networked || world.Paused || worlds != 1) { return false; }
            owner = world;
            Engine.TimeScale = scale;
            // Hitstop is a presentation lease measured in real seconds; gameplay still advances with physics delta.
            releaseAt = Time.GetTicksUsec() / 1_000_000.0 + duration;
            return true;
        }

        internal static void Poll(WorldContext world)
        {
            if (owner == world && Time.GetTicksUsec() / 1_000_000.0 >= releaseAt) { Release(world); }
        }

        internal static void Release(WorldContext world)
        {
            if (owner != world) { return; }
            owner = null;
            Engine.TimeScale = 1;
        }
    }

    private static readonly HashSet<CatchupLease> catchup = new();
    private static int? ordinaryCatchup;

    internal static CatchupLease RegisterCatchup(WorldContext world)
    {
        var lease = new CatchupLease(world);
        catchup.Add(lease);
        return lease;
    }

    /// <summary>A tank simulation owns this registration until world stopping or its node's exit, whichever is first.</summary>
    internal sealed class CatchupLease(WorldContext world) : IDisposable
    {
        internal readonly WorldContext World = world;
        internal int Steps;

        public void Dispose()
        {
            // Stopping and tree exit both refresh arbitration, as before, even after this entry was removed.
            catchup.Remove(this);
            RequestCatchup(this, 0);
        }
    }

    // Any registered live network simulation vetoes offline requests, even while paused. The minimum overlapping
    // request never raises the prior engine budget; ending the last request restores exactly that prior value.
    internal static void RequestCatchup(CatchupLease request, int requestedSteps)
    {
        request.Steps = requestedSteps;
        var steps = int.MaxValue;
        foreach (var lease in catchup)
        {
            if (!lease.World.IsAlive) continue;
            if (lease.World.Networked) { steps = int.MaxValue; break; }
            if (lease.Steps > 0) steps = Math.Min(steps, lease.Steps);
        }
        if (steps == int.MaxValue)
        {
            if (ordinaryCatchup is { } previous) Engine.MaxPhysicsStepsPerFrame = previous;
            ordinaryCatchup = null;
        }
        else
        {
            ordinaryCatchup ??= Engine.MaxPhysicsStepsPerFrame;
            Engine.MaxPhysicsStepsPerFrame = Math.Min(ordinaryCatchup.Value, steps);
        }
    }
}
