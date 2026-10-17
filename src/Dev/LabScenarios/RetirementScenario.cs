using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using Propane.Session;
using static Propane.Dev.UnitTests.TestRunner;

using static Godot.GodotObject;
using static Propane.Dev.LabCases.LabScenarioTools;

namespace Propane.Dev.LabCases;

internal sealed class RetirementScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => ReferenceRetirement();

    /// <summary>
    /// A synchronous retirement trace: initial segments are built by the real Lab; no worker completion, wall-time
    /// slicing or native physics step can change an observation boundary. The ordinary streamed run stays a soak.
    /// </summary>
    private Task ReferenceRetirement()
    {
        Assert(equivalence == null && DevArgs.Get("equivalence-trace") != null, "reference-retirement requires an equivalence trace output");
        var lab = Enter(LabScenarios.Get("dissolve"), LabEnvironment.Suburb);
        var arena = lab.Dissolve!;
        var source = arena.Source;
        var segments = source.Segments.ToArray();
        Assert(segments.Length >= 2 && source.Pending == null, "retirement fixture needs assembled initial segments");
        var dissolve = typeof(DissolveArena).GetMethod("Dissolve", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var retire = typeof(DissolveArena).GetMethod("Retire", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var forgotten = 0;
        foreach (var segment in segments.Take(2))
        {
            SegmentWorldSource.CutFloor(segment, (segment.Plan.Start + segment.Plan.End) / 2);
            equivalence!.Boundary($"segment-{segment.Index}/half-floor");
            SegmentWorldSource.CutFloor(segment, segment.Plan.End);
            foreach (var tank in segment.Tanks) dissolve.Invoke(arena, new object[] { tank });
            equivalence.Boundary($"segment-{segment.Index}/dissolved");
            Assert(segment.Floor.Disabled && segment.Tanks.All(arena.Dissolved), "visual dissolve boundary changed");
            retire.Invoke(arena, new object[] { segment });
            forgotten += segment.Tanks.Count;
            equivalence.Boundary($"segment-{segment.Index}/retiring");
            Assert(segment.Tanks.All(t => !lab.LiveTanks.Contains(t)), "retirement retained live tanks");
            source.AdvanceRetire(double.PositiveInfinity);
            equivalence.Boundary($"segment-{segment.Index}/freed");
            Assert(!IsInstanceValid(segment.Suburb) && segment.Tanks.All(t => !IsInstanceValid(t)), "retirement did not free native content");
        }
        Assert(source.Retired == 2 && source.Retiring == 0, "retirement did not drain exactly two segments");
        GD.Print($"[lab reference-retirement] retired={source.Retired} forgotten_tanks={forgotten} remaining_segments={source.Segments.Count}");
        lab.Session.Dispose(); lab.Free();
        return Task.CompletedTask;
    }
}
