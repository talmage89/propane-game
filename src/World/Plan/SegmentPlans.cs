using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.World.Plan;

/// <summary>
/// How a run's segments are cut and stocked: each is a strip of street <see cref="Length"/> meters long, generated
/// from the ordinary generator's parts; its tanks fall from <see cref="TanksStart"/> by <see cref="TanksDrop"/> per
/// segment to <see cref="TanksMin"/>, and every <see cref="JackpotEvery"/>th segment adds a pyramid of
/// <see cref="JackpotTanks"/> in the middle of the street.
/// </summary>
public sealed record SegmentSettings(SuburbSettings Suburb, float Length, int TanksStart, int TanksMin, float TanksDrop,
    int JackpotEvery, int JackpotTanks, int AmmoCans);

/// <summary>
/// One segment of a run: its index, the seed it was generated from, where it starts along the run (world X), how many
/// tanks the schedule gave it, whether it holds the jackpot, and its plan in the segment's own frame (centered on the
/// origin, the street along X). The plan is ordinary <see cref="SuburbPlan"/> data, so a server could send it per
/// segment with <c>PlanCodec</c> exactly as it sends a match's suburb.
/// </summary>
public sealed record SegmentPlan(int RunSeed, int Index, int Seed, float Start, float Length, int Tanks, bool Jackpot, SuburbPlan Plan)
{
    public float End => Start + Length;
    public float Center => Start + Length / 2f;
}

/// <summary>
/// Pure, deterministic segment generation: the same run seed and index always give the same segment, in any process
/// and in any order, so segments can be generated ahead on a worker or (later) received from a server.
/// </summary>
public static class SegmentPlans
{
    /// <summary>Generation retries with seeds derived from the segment's; the first attempt that fits wins.</summary>
    public const int Attempts = 12;
    public const string JackpotReason = "jackpot";

    /// <summary>A segment's seed: the run seed and index, mixed (SplitMix64) so neighbors share nothing.</summary>
    public static int SeedOf(int runSeed, int index) => Mix(unchecked(((ulong)(uint)runSeed << 32) | (uint)index));

    /// <summary>The day's seed, the same for everyone on that UTC date: 20261013 for 13 October 2026.</summary>
    public static int DailySeed(DateTime utc) => utc.Year * 10000 + utc.Month * 100 + utc.Day;

    public static int TanksAt(SegmentSettings settings, int index) =>
        Math.Max(settings.TanksMin, settings.TanksStart - (int)MathF.Floor(settings.TanksDrop * Math.Max(0, index)));

    public static bool JackpotAt(SegmentSettings settings, int index) =>
        settings.JackpotEvery > 0 && index >= 0 && (index + 1) % settings.JackpotEvery == 0;

    /// <summary>Where segment <paramref name="index"/> starts along the run: segment 0 is centered on the origin.</summary>
    public static float StartOf(SegmentSettings settings, int index) => (index - 0.5f) * settings.Length;

    public static SegmentPlan Generate(SegmentSettings settings, IPlanCatalog catalog, int runSeed, int index)
    {
        var seed = SeedOf(runSeed, index);
        var tanks = TanksAt(settings, index);
        var jackpot = JackpotAt(settings, index);
        var suburb = settings.Suburb with { StripLength = settings.Length, TankCount = tanks, AmmoSpots = settings.AmmoCans };
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var attemptSeed = attempt == 0 ? seed : Mix(unchecked(((ulong)(uint)seed << 32) | (uint)attempt));
            try
            {
                var plan = new SuburbGenerator(suburb, catalog, attemptSeed).Generate();
                if (jackpot && !AddJackpot(plan, settings)) continue;
                return new SegmentPlan(runSeed, index, attemptSeed, StartOf(settings, index), settings.Length, tanks, jackpot, plan);
            }
            catch (InvalidOperationException error)
            {
                last = error; // A cramped strip that cannot hold its tanks or cans: the next seed.
            }
        }
        throw new InvalidOperationException($"Run {runSeed} segment {index}: no layout fits in {Attempts} attempts.", last);
    }

    /// <summary>
    /// The jackpot: a square pyramid on the crown of the street in the segment's middle. Parked cars in its way are
    /// towed; a layout with a tank on the street there (a pickup's bed) is refused so the schedule's count holds.
    /// </summary>
    private static bool AddJackpot(SuburbPlan plan, SegmentSettings settings)
    {
        var s = settings.Suburb;
        var clear = new Rect2(-7.5f, -s.CarriagewayHalfWidth, 15f, s.CarriagewayHalfWidth * 2f);
        if (plan.Tanks.Any(t => clear.HasPoint(t.Position)) || plan.AmmoSpots.Any(clear.HasPoint)) return false;
        plan.Props.RemoveAll(p => p.Model.StartsWith("car:") && clear.HasPoint(p.Position));
        var pile = plan.Tanks.Count == 0 ? 0 : plan.Tanks.Max(t => t.Pile) + 1;
        var slots = PileBuilder.Build(PileFormation.Square, settings.JackpotTanks,
            new Vector3(s.PileSpacing, s.PileLayerHeight, s.PileDepthSpacing), s.PileUprightLayerHeight);
        for (var i = 0; i < slots.Count; i++)
        {
            var t = slots[i];
            plan.Tanks.Add(new TankPlacement(new Vector2(t.Origin.X, t.Origin.Z), 0, t.Origin.Y, i == 0 ? JackpotReason : $"{JackpotReason} group",
                t.Basis.GetEuler(), pile, PileFormation.Square));
        }
        return true;
    }

    private static int Mix(ulong value)
    {
        var z = value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(z & 0x7FFFFFFF);
    }
}
