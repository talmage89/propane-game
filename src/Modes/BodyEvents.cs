using System.Collections.Generic;

namespace Propane.Modes;

// Inputs and decisions for modes that read where bodies are (Ring Out's slab, King's zone). A runner samples the
// positions it already has (a server: what its clients report; the Lab: its own actors) and passes them in with the
// mode clock, so the rules stay pure and need no per-frame traffic of their own. See docs/modes.md.

/// <summary>
/// One actor's position as the runner knows it: the feet while standing, the pelvis while ragdolled.
/// <paramref name="Down"/> marks a ragdolled (thrown) body.
/// </summary>
public readonly record struct ActorSample(int ActorId, double X, double Y, double Z, bool Down = false);

/// <summary>Where every present actor is now. Runners send one per simulation step while a round runs.</summary>
public sealed record ActorsSampled(IReadOnlyList<ActorSample> Samples) : AcceptedEvent;

/// <summary>
/// An actor was knocked down (ragdolled or shoved while down) by another: a melee hit, or a blast whose chain that
/// actor started. <paramref name="ByActorId"/> is zero when nobody is to blame.
/// </summary>
public sealed record ActorThrown(int VictimId, int ByActorId) : AcceptedEvent;

/// <summary>A notable point in the world, such as a tank pile a zone may sit on.</summary>
public readonly record struct ModeSpot(double X, double Y, double Z);

/// <summary>Why an actor is out.</summary>
public enum EliminationCause : byte { Fell, Dissolved, Left }

/// <summary>
/// An actor is out of the round, finishing in <paramref name="Place"/> (1 is first; actors out together share a
/// place). <paramref name="CreditedTo"/> is whoever knocked them out, or zero.
/// </summary>
public sealed record ActorEliminated(int ActorId, int CreditedTo, int Place, EliminationCause Cause) : ModeCommand;

/// <summary>The round has its result before any deadline: end it now (count-up and untimed rounds).</summary>
public sealed record RoundOver : ModeCommand;

/// <summary>A deterministic generator for rules that choose (a band order, the next zone): no engine or clock.</summary>
public struct ModeRandom
{
    private ulong state;
    public ModeRandom(ulong seed) => state = seed;

    /// <summary>SplitMix64.</summary>
    public ulong Next()
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A value in [0, 1).</summary>
    public double Unit() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A value in [0, count).</summary>
    public int Below(int count) => count <= 1 ? 0 : (int)(Next() % (ulong)count);
}
