using System;
using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using Propane.Bots;
using Propane.Core;
using Propane.Lab;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Owns one native Lab fixture and retires its session before freeing the facade.</summary>
internal sealed class PrototypeObservationFixture : IDisposable
{
    private readonly LabSession lab;
    internal PrototypeRunner Runner => lab.Prototype!;
    internal IBotWorld Observations { get; }

    internal PrototypeObservationFixture(TestRunner host, string id)
    {
        using var tuning = Tuning.LoadBaked();
        tuning.FixedSeed = 23;
        tuning.LabPrototypeBots = 2;
        tuning.HitstopDuration = 0;
        lab = new LabSession
        {
            Scenario = LabScenarios.Get(id),
            Template = tuning,
            EnvironmentKind = id is "shortfuse" or "dissolve" ? LabEnvironment.Suburb : LabEnvironment.Void,
            CreateLocalView = false,
            TestRandomSeed = 23,
            TestSeedPhysicsRandom = true,
        };
        host.AddChild(lab);
        if (id == "dissolve") Runner.AttachBot(lab.Player);
        if (id == "king") Runner._PhysicsProcess(1d / 120); // The first actor sample places the initial zone.
        TestRunner.Assert(Runner.View != null, "observation fixture produced no presentation snapshot");
        var brain = ((IEnumerable)LabRunnerInspection.Field(Runner, "brains")).Cast<ITuple>().First();
        Observations = (IBotWorld)brain[2]!;
    }

    public void Dispose() { lab.Session.Dispose(); lab.Free(); }
}
