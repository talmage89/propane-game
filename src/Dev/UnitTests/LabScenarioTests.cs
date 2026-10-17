using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.LabCases;
using Propane.Lab;
using static Propane.Dev.UnitTests.TestRunner;
using LabCase = Propane.Dev.LabCases.LabScenario;

namespace Propane.Dev.UnitTests;

[UnitSuite(1)]
public static class LabScenarioTests
{
    private sealed class Host : ILabScenarioHost
    {
        public readonly List<string> Events = new();
        public Tuning Template => throw new InvalidOperationException("fake context must not create fixtures");
        public CaptureDirector Capture => throw new InvalidOperationException("fake context must not capture");
        public Equivalence.LabTrace? Trace => null;
        public LabSession Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment) => throw new InvalidOperationException();
        public void AddLab(LabSession lab) => throw new InvalidOperationException();
        public void Attach(Node node) => throw new InvalidOperationException();
        public SceneTree Tree => throw new InvalidOperationException();
        public Viewport Viewport => throw new InvalidOperationException();
        public readonly TaskCompletionSource Gate = new();
        public Task Frames(int count) { Events.Add($"frames {count}"); return Gate.Task; }
        public Task Collect() => throw new InvalidOperationException();
        public (int, int, int) Counts() => throw new InvalidOperationException();
    }

    private sealed class Scenario(Host host, bool fail = false) : LabCase(host)
    {
        public Action? Queued { get; private set; }
        public IDisposable? First { get; private set; }
        protected override async Task RunCore()
        {
            host.Events.Add("begin");
            Queued = Guard(() => host.Events.Add("callback"));
            First = DetachOnDisposal(() => host.Events.Add("detach first"));
            DetachOnDisposal(() => host.Events.Add("detach last"));
            await Frames(3).ConfigureAwait(true);
            host.Events.Add("resume");
            if (fail) throw new InvalidOperationException("forced case failure");
        }
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("lab scenarios/incomplete completion fails and continuation stays on its boundary", () =>
        {
            var host = new Host();
            using var scenario = new Scenario(host);
            Throws<InvalidOperationException>(scenario.CheckCompletion);
            var run = scenario.Execute();
            Assert(host.Events.SequenceEqual(new[] { "begin", "frames 3" }) && !run.IsCompleted, "async case crossed a pending frame boundary");
            Throws<InvalidOperationException>(scenario.CheckCompletion);
            host.Gate.SetResult();
            run.GetAwaiter().GetResult();
            scenario.CheckCompletion();
            Assert(host.Events.SequenceEqual(new[] { "begin", "frames 3", "resume" }), "frame continuation changed");
            Throws<InvalidOperationException>(() => scenario.Execute().GetAwaiter().GetResult());
        });
        runner.Run("lab scenarios/disposal detaches in reverse once and suppresses late callbacks", () =>
        {
            var host = new Host();
            var scenario = new Scenario(host);
            var run = scenario.Execute();
            scenario.Queued!();
            scenario.Dispose(); scenario.Dispose();
            scenario.Queued!();
            Assert(host.Events.SequenceEqual(new[] { "begin", "frames 3", "callback", "detach last", "detach first" }), "disposal order or late callback changed");
            host.Gate.SetResult();
            Throws<ObjectDisposedException>(() => run.GetAwaiter().GetResult());
            Throws<InvalidOperationException>(scenario.CheckCompletion);
            Throws<ObjectDisposedException>(() => scenario.Execute().GetAwaiter().GetResult());
        });
        runner.Run("lab scenarios/fixture-scoped subscriptions detach before the next collection", () =>
        {
            var host = new Host();
            var scenario = new Scenario(host);
            var run = scenario.Execute();
            scenario.First!.Dispose(); scenario.First.Dispose();
            host.Gate.SetResult();
            run.GetAwaiter().GetResult();
            scenario.Dispose();
            Assert(host.Events.SequenceEqual(new[] { "begin", "frames 3", "detach first", "resume", "detach last" }),
                "fixture cleanup was deferred, repeated or changed remaining reverse disposal");
        });
        runner.Run("lab scenarios/failed case never satisfies completion", () =>
        {
            var host = new Host();
            using var scenario = new Scenario(host, fail: true);
            var run = scenario.Execute();
            host.Gate.SetResult();
            Throws<InvalidOperationException>(() => run.GetAwaiter().GetResult());
            Throws<InvalidOperationException>(scenario.CheckCompletion);
        });
    }
}
