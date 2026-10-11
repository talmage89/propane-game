using System;
using System.Linq;
using Godot;
using Propane.Net;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

public static class GatherTests
{
    private static GatherGroups.Sample S(ulong id, float x, bool eligible = true, ulong revision = 0) => new(id, new Vector3(x, 0, 0), eligible, revision);

    public static void Register(TestRunner runner)
    {
        runner.Run("gather/radius boundary is inclusive and transitive", () =>
        {
            var groups = new GatherGroups().Advance(new[] { S(1, 0), S(2, 3), S(3, 6), S(4, 9.001f) }, 3, 2, 0, 0);
            Assert(groups.Count == 1 && groups[0].Select(s => s.Id).SequenceEqual(new ulong[] { 1, 2, 3 }), "connected group or radius boundary wrong");
        });
        runner.Run("gather/minimum and isolated singles", () =>
        {
            var detector = new GatherGroups();
            Assert(detector.Advance(new[] { S(1, 0), S(2, 2), S(3, 20) }, 3, 3, 0, 0).Count == 0, "minimum ignored");
            Assert(detector.Advance(new[] { S(1, 0), S(2, 2), S(3, 20) }, 3, 2, 0, 0).Single().Length == 2, "single included");
        });
        runner.Run("gather/vents and shielded drops excluded by production eligibility", () =>
        {
            var tank = PropaneTank.Create();
            runner.AddChild(tank);
            tank.Sleeping = true;
            Assert(TankGather.Eligible(tank), "loose resting intact tank excluded");
            tank.MakeInvulnerable(10, Colors.White);
            Assert(!TankGather.Eligible(tank), "shielded drop eligible");
            tank.PunctureRemote(Vector3.Right, Vector3.Right, 1);
            Assert(!TankGather.Eligible(tank), "vent eligible");
            var groups = new GatherGroups().Advance(new[] { S(1, 0), S(2, 1, TankGather.Eligible(tank)) }, 3, 2, 0, 0);
            Assert(groups.Count == 0, "ineligible neighbor counted");
            tank.Free();
        });
        runner.Run("gather/rest revision catches slow drift and motion between detector scans", () =>
        {
            var tank = PropaneTank.Create();
            runner.AddChild(tank);
            tank.Sleeping = true;
            tank._PhysicsProcess(1.0 / 60);
            var revision = tank.Disturbance;
            tank.GlobalPosition += Vector3.Right * Core.Tuning.Current.GatherRestDistance * 2;
            tank._PhysicsProcess(1.0 / 60);
            Assert(tank.Disturbance > revision, "slow drift retained the group clock");
            revision = tank.Disturbance;
            tank.Sleeping = false;
            tank.LinearVelocity = Vector3.Right;
            tank._PhysicsProcess(1.0 / 60);
            tank.LinearVelocity = Vector3.Zero;
            tank.Sleeping = true;
            tank._PhysicsProcess(1.0 / 60);
            Assert(tank.Disturbance > revision && TankGather.Eligible(tank), "brief motion between scans retained the clock");
            tank.MarkPile();
            Assert(!tank.Loose, "pile tank classified loose");
            tank.GlobalPosition += Vector3.Right * Core.Tuning.Current.GatherPileTolerance * 2;
            Assert(tank.Loose, "knocked-out tank retained pile membership");
            tank.GlobalTransform = tank.PilePose!.Value;
            Assert(!tank.Loose, "tank back in its original pile remained loose");
            tank.Free();
        });
        runner.Run("gather/clock resets on disturbance, movement, joining and leaving", () =>
        {
            var detector = new GatherGroups();
            var samples = new[] { S(1, 0), S(2, 1) };
            detector.Advance(samples, 3, 2, 20, 0);
            Assert(detector.Advance(samples, 3, 2, 20, 19).Count == 0, "early gather");
            samples[0] = S(1, 0, revision: 1);
            Assert(detector.Advance(samples, 3, 2, 20, 2).Count == 0, "disturbance did not reset");
            Assert(detector.Advance(samples, 3, 2, 20, 20).Count == 1, "delay never elapsed");
            Assert(detector.Advance(samples.Append(S(3, 2)), 3, 2, 20, 1).Count == 0, "new member did not reset");
            Assert(detector.Advance(samples, 3, 2, 20, 1).Count == 0, "member leaving did not reset");
            detector.Advance(new[] { S(1, 0, false), S(2, 1) }, 3, 2, 20, 1);
            Assert(detector.Advance(samples, 3, 2, 20, 20).Count == 0, "motion failed to discard clock");
        });
        runner.Run("gather/clear center selection and blocked path skip", () =>
        {
            var positions = new[] { Vector3.Left, Vector3.Right };
            var tested = 0;
            var center = GatherGroups.SelectCenter(positions, 1, 2, 8, p => { tested++; return p.X > 0.4f ? p : null; });
            Assert(center is { X: > 0.4f } && tested > 1, "did not avoid occupied centroid");
            Assert(GatherGroups.SelectCenter(positions, 1, 2, 8, _ => null) == null, "blocked paths accepted");
            Assert(GatherGroups.SelectCenter(positions, 1, 2, 8, p => p) == Vector3.Zero, "clear centroid not preferred");
            var from = new Vector3(-2, 0.235f, 0); var to = new Vector3(2, 0.235f, 0);
            Assert(GatherGroups.Path(from, to, 1.5f, 0) == from && GatherGroups.Path(from, to, 1.5f, 1) == to, "wrong endpoints");
            Assert(GatherGroups.Path(from, to, 1.5f, 0.5f).Y > 1.7f, "low obstacle clearance missing");
        });
        runner.Run("gather/reliable presentation survives overtaking motion and ends exactly", () =>
        {
            using var fixture = new MatchFixture(runner);
            var match = fixture.Match;
            var tank = match.LiveTanks.First();
            var entry = match.Bodies.Get(tank.NetId)!;
            var pose = new Transform3D(Basis.Identity, new Vector3(0, 4, 0));
            var state = new BodyState(tank.NetId, pose, Vector3.Right, Vector3.Zero, entry.Epoch, 3);
            fixture.Receive(Msg.BodyStates, new BodyStatesMessage(1, new[] { state }));
            fixture.Receive(Msg.Gather, new GatherMessage(GatherPhase.Glide, new[] { state with { Sequence = 2 } }));
            Assert(tank.Gathering && tank.Freeze && entry.LastSequence == 3, "overtaking motion hid the start");
            var final = state with { Transform = new Transform3D(Basis.Identity, Vector3.One), Sequence = 5, Linear = Vector3.Zero };
            fixture.Receive(Msg.Gather, new GatherMessage(GatherPhase.Settled, new[] { final }));
            Assert(!tank.Gathering && !tank.Freeze && !tank.Loose && tank.Sleeping && tank.GlobalTransform.IsEqualApprox(final.Transform), "final pose/membership lost");
            fixture.Receive(Msg.Gather, new GatherMessage(GatherPhase.Glide, new[] { state with { Sequence = 2 } }));
            fixture.Receive(Msg.BodyStates, new BodyStatesMessage(1, new[] { state with { Sequence = 4 } }));
            Assert(!tank.Gathering && entry.TargetRest, "old start/motion woke settled pile");
        });
        runner.Run("gather/final body rest releases an observer without inventing a pile", () =>
        {
            using var fixture = new MatchFixture(runner);
            var match = fixture.Match;
            var tank = match.LiveTanks.First();
            var builtPose = tank.PilePose;
            var entry = match.Bodies.Get(tank.NetId)!;
            var state = new BodyState(tank.NetId, tank.GlobalTransform, Vector3.Right, Vector3.Zero, entry.Epoch, 2);
            fixture.Receive(Msg.Gather, new GatherMessage(GatherPhase.Glide, new[] { state }));
            fixture.Receive(Msg.BodyRest, new BodyRestMessage(tank.NetId, state.Transform, 1, entry.Epoch, 1));
            Assert(tank.Gathering, "stale rest released active glide");
            fixture.Receive(Msg.BodyRest, new BodyRestMessage(tank.NetId, state.Transform, 1, entry.Epoch, 3));
            Assert(!tank.Gathering && !tank.Freeze && tank.PilePose == builtPose, "final rest stranded gravity or invented a gathered pile");
        });
        runner.Run("gather/epoch handoff cancels frozen observer and refuses old completion", () =>
        {
            using var fixture = new MatchFixture(runner);
            var match = fixture.Match;
            var tank = match.LiveTanks.First();
            var entry = match.Bodies.Get(tank.NetId)!;
            var pose = new Transform3D(Basis.Identity, new Vector3(0, 4, 0));
            var state = new BodyState(tank.NetId, pose, Vector3.Right, Vector3.Zero, entry.Epoch, 1);
            fixture.Receive(Msg.Gather, new GatherMessage(GatherPhase.Glide, new[] { state }));
            Assert(tank.Gathering, "no flight");
            match.OnMessage(new NetMessage(Msg.Authority, fixture.Id, 0, new AuthorityMessage(3, 8, ClaimRefusal.None,
                new[] { new BodyAuthority(state with { Epoch = 2, Sequence = 0 }, 3, TankLedger.State.Intact, false, null, Vector3.Zero) })));
            Assert(!tank.Gathering && !tank.Freeze && entry.Epoch == 2, "handoff stranded frozen body");
            tank.GlobalTransform = pose;
            fixture.Receive(Msg.Gather, new GatherMessage(GatherPhase.Settled, new[] { state with { Sequence = 9 } }));
            Assert(tank.GlobalTransform.IsEqualApprox(pose) && tank.Loose, "old epoch completed a pile");
        });
        runner.Run("gather/ledger relays presentation overtaken by disposable motion", () =>
        {
            var ledger = new TankLedger(PlanTests.Fixture(), 2);
            var state = ledger.Get(BodySync.MapTankId(0))!.Body with { Sequence = 3 };
            Assert(ledger.Update(2, state, false), "motion refused");
            var message = new GatherMessage(GatherPhase.Glide, new[] { state with { Sequence = 2 } });
            Assert(ledger.Gather(2, 2, message, true), "motion hid reliable start");
            Assert(ledger.Get(state.Id)!.Body.Sequence == 3, "presentation rewound motion");
            Assert(!ledger.Gather(2, 2, message, true), "duplicate start relayed");
        });
        runner.Run("gather/ledger rejects stale, nonhost, venting and partial presentation", () =>
        {
            var ledger = new TankLedger(PlanTests.Fixture(), 2);
            var state = ledger.Get(BodySync.MapTankId(0))!.Body with { Sequence = 1 };
            var message = new GatherMessage(GatherPhase.Glide, new[] { state });
            Assert(!ledger.Gather(3, 2, message, true), "nonhost accepted");
            Assert(!ledger.Gather(2, 2, message, false), "wrong phase accepted");
            var unknown = state with { Id = BodySync.MapTankId(999) };
            Assert(!ledger.Gather(2, 2, message with { Bodies = new[] { state, unknown } }, true), "partial unknown group accepted");
            Assert(ledger.Get(state.Id)!.Body.Sequence == 0, "partial group mutated");
            Assert(ledger.Gather(2, 2, message, true), "valid gather refused");
            Assert(!ledger.Gather(2, 2, message, true), "duplicate accepted");
            ledger.Puncture(2, new PunctureMessage(state.Id, state.Transform, Vector3.Zero, Vector3.Zero, Vector3.Right, Vector3.Right, 1), true);
            Assert(!ledger.Gather(2, 2, message with { Bodies = new[] { state with { Epoch = 2, Sequence = 2 } } }, true), "venting gather accepted");
        });
    }
    private sealed class MatchFixture : IDisposable
    {
        private readonly ServerTests.Fixture server = new();
        private readonly NetClient client = new();
        public Match Match { get; }
        public int Id => server.Id;
        public MatchFixture(TestRunner runner)
        {
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(3, null)), true));
            Match = new Match { Net = client, Setup = server.Setup };
            runner.AddChild(Match);
        }
        public void Receive(Msg type, object payload) => Match.OnMessage(new NetMessage(type, Id, 2, payload));
        public void Dispose() { Match.Free(); client.Free(); server.Dispose(); }
    }
}
