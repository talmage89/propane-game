using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Dev.NetScenarios;
using Propane.Net;
using Propane.ReleaseSmoke;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite(1)]
public static class NetScenarioTests
{
    private sealed class Host : INetScenarioHost
    {
        public readonly List<string> Events = new();
        public NetClient Net => null!;
        public CaptureDirector Director => null!;
        public string Role { get; init; } = "host";
        public int Players => 2;
        public float MatchClock => 0;
        public SceneTree Tree => throw new InvalidOperationException("fake context must not touch a scene tree");
        public bool InTree => false;
        public void Attach(Node node) => throw new InvalidOperationException("fake context must not attach nodes");
        public SignalAwaiter AwaitSignal(GodotObject source, StringName signal) => throw new InvalidOperationException("fake context must not await native signals");
        public void Log(string text) => Events.Add(text);
        public void Fail(string text) => Events.Add("FAIL " + text);
    }

    private sealed class Scenario(Host host) : NetScenario(host)
    {
        public int Ticks { get; private set; }
        public Action? Queued { get; private set; }
        public bool Passed => scenarioPassed;
        protected override void Preparing(Match match)
        {
            host.Events.Add("prepare");
            Queued = Guard(() => { Ticks++; host.Events.Add("callback"); });
            DetachOnRetirement(() => host.Events.Add("detach"));
        }
        public override void BeforeInbox(Match match) => host.Events.Add("inbox");
        protected override void Assembled(Match match) { Ticks = 0; host.Events.Add("assembled"); }
        protected override void Run(Match match, float dt) { Ticks++; host.Events.Add("tick"); }
        public override void Complete(Match match) => host.Events.Add("complete");
        public void Pass() => scenarioPassed = !scenarioFailed;
        public Action ConnectionCallback() => GuardConnection(() => host.Events.Add("connection callback"));
    }

    private sealed class Idle(Host host) : NetScenario(host);

    public static void Register(TestRunner runner)
    {
        runner.Run("net scenarios/dispatch preserves phase order and incomplete completion fails", () =>
        {
            var host = new Host();
            using var scenario = new Scenario(host);
            scenario.BeginAssembly(null!);
            scenario.BeforeInbox(null!);
            scenario.AfterAssembly(null!);
            scenario.Tick(null!, 1f / 120);
            Assert(scenario.CheckCompletion(NetScenarioCompletion.Assertions), "ordinary failed assertion must still allow completion telemetry");
            scenario.Complete(null!);
            scenario.RetireMatch();
            Assert(host.Events.SequenceEqual(new[] { "prepare", "inbox", "assembled", "tick",
                "FAIL scenario ended before all assertions passed", "complete", "detach" }), "phase order or completion obligation changed");
        });
        runner.Run("net scenarios/repeated matches reset completion and retire old callbacks", () =>
        {
            var host = new Host();
            using var scenario = new Scenario(host);
            scenario.BeginAssembly(null!);
            scenario.AfterAssembly(null!);
            scenario.Tick(null!, .1f);
            scenario.Pass();
            scenario.CheckCompletion(NetScenarioCompletion.Assertions);
            var old = scenario.Queued!;
            scenario.RetireMatch();
            old();
            Assert(scenario.Ticks == 1, "retired callback mutated the completed run");
            scenario.BeginAssembly(null!);
            scenario.BeforeInbox(null!);
            scenario.AfterAssembly(null!);
            Assert(!scenario.Passed && scenario.Ticks == 0, "second match reused completion or mutable case state");
            old();
            Assert(scenario.Ticks == 0, "old callback mutated the next match");
            scenario.Queued!();
            Assert(scenario.Ticks == 1, "current callback was suppressed");
            scenario.CheckCompletion(NetScenarioCompletion.Assertions);
            Assert(host.Events.Count(e => e.StartsWith("FAIL", StringComparison.Ordinal)) == 1, "first match completion leaked into second match");
        });
        runner.Run("net scenarios/replacement and disposal detach exactly once and ignore late delivery", () =>
        {
            var host = new Host();
            var scenario = new Scenario(host);
            scenario.BeginAssembly(null!);
            var replaced = scenario.Queued!;
            scenario.BeginAssembly(null!);
            scenario.AfterAssembly(null!);
            replaced();
            var shutdown = scenario.Queued!;
            scenario.Dispose(); scenario.Dispose();
            shutdown();
            scenario.Tick(null!, .1f);
            Assert(scenario.Ticks == 0 && host.Events.Count(e => e == "detach") == 2, "replacement/shutdown retained subscriptions or dispatched a retired callback");
            Throws<ObjectDisposedException>(() => scenario.BeginAssembly(null!));
        });
        runner.Run("net scenarios/movement requires completion only from its observing host", () =>
        {
            var host = new Host();
            using var observer = new MovementScenario(host);
            Assert(!observer.CheckCompletion(NetScenarioCompletion.Movement) && host.Events.SequenceEqual(new[]
                { "FAIL [net movement] FAIL match ended before movement proof completed" }), "incomplete movement observer succeeded");
            var join = new Host { Role = "join" };
            using var actor = new MovementScenario(join);
            Assert(actor.CheckCompletion(NetScenarioCompletion.Movement) && join.Events.Count == 0,
                "movement imposed observer completion on the acting joiner");
        });
        runner.Run("net scenarios/watch joiners retain ordinary bot dispatch", () =>
        {
            var host = new Host { Role = "join" };
            using var scenario = new WatchScenario(host);
            scenario.BeginAssembly(null!);
            scenario.AfterAssembly(null!);
            Assert(!scenario.Tick(null!, .01f), "watch consumed a joiner's bot tick");
        });
        runner.Run("net scenarios/lobby callbacks survive match retirement and stop on connection disposal", () =>
        {
            var host = new Host();
            var scenario = new Scenario(host);
            var lobby = scenario.ConnectionCallback();
            scenario.BeginAssembly(null!);
            scenario.AfterAssembly(null!);
            scenario.RetireMatch();
            lobby();
            Assert(host.Events.Count(e => e == "connection callback") == 1, "returning from a match lost a lobby callback");
            scenario.Dispose();
            lobby();
            Assert(host.Events.Count(e => e == "connection callback") == 1, "connection disposal allowed a late lobby callback");
        });
        runner.Run("net scenarios/warmed dispatch allocates nothing", () =>
        {
            var host = new Host();
            using var scenario = new Idle(host);
            scenario.BeginAssembly(null!);
            scenario.AfterAssembly(null!);
            for (var tick = 0; tick < 1000; tick++) scenario.Tick(null!, .01f);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var tick = 0; tick < 10000; tick++) scenario.Tick(null!, .01f);
            Assert(GC.GetAllocatedBytesForCurrentThread() == before, "steady dispatch allocated");
        });
        runner.Run("net scenarios/registration inventory and completion obligations remain independent", () =>
        {
            var required = new[] { "duel", "pickup", "drop-refusal", "puncture-race", "root-refusal", "future-epoch", "appearance", "gather", "melee", "body-stream", "ballistic", "vent-contact", "king", "king-late" };
            var names = new[] { "", "watch", "capacity", "movement" }.Concat(required).Order(StringComparer.Ordinal).ToArray();
            Assert(NetScenarioRegistry.All.Select(r => r.Name).Order(StringComparer.Ordinal).SequenceEqual(names), "CLI scenario inventory changed");
            Assert(NetScenarioRegistry.All.Select(r => r.Name).Distinct().Count() == names.Length, "duplicate scenario registration");
            Assert(NetScenarioRegistry.All.Where(r => r.Completion == NetScenarioCompletion.Assertions).Select(r => r.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal)), "required completion coverage changed");
            Assert(NetScenarioRegistry.Find("movement").Completion == NetScenarioCompletion.Movement &&
                NetScenarioRegistry.Find("capacity").Completion == NetScenarioCompletion.FinalChecks, "movement/capacity completion phase changed");
            foreach (var registration in NetScenarioRegistry.All)
            {
                Assert((registration.Factory == null) == (registration.Name == ""), "a named scenario still uses legacy host dispatch");
                using var scenario = registration.Factory?.Invoke(new Host());
                Assert((scenario == null) == (registration.Name == ""), "a registered factory did not create its owner");
            }
            Assert(NetScenarioRegistry.Find(null) == NetScenarioRegistry.Find(""), "default bot selection changed");
            Throws<ArgumentException>(() => NetScenarioRegistry.Find("not-a-scenario"));
        });
    }
}
