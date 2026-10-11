using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests;

public static class AuthorityTests
{
    private static readonly uint Id = BodySync.MapTankId(0);
    private static readonly uint Other = BodySync.MapTankId(1);
    private static BodyState Pose(uint id, uint epoch = 1, uint sequence = 0) =>
        new(id, new Transform3D(Basis.Identity, new Vector3(4, 2, 3)), Vector3.Right, Vector3.Up, epoch, sequence);
    private static BodyAuthority Grant(int owner, uint epoch, uint sequence = 0) =>
        new(Pose(Id, epoch, sequence), owner, TankLedger.State.Intact, false, null, Vector3.Zero);
    private static ClaimMessage Claim(uint request, params BodyState[] bodies) =>
        new(request, bodies);
    private static PunctureMessage Puncture(int contender, uint epoch = 1) =>
        new(Id, Pose(Id).Transform, Vector3.Right, Vector3.Up, new Vector3(0.15f, 0.23f, contender * 0.01f),
            contender % 2 == 0 ? Vector3.Right : Vector3.Left, contender % 2 == 0 ? 1 : -1, epoch, (uint)contender);
    private static TankLedger Ledger()
    {
        var plan = new SuburbPlan { Seed = 1, Bounds = new Rect2(-10, -10, 20, 20) };
        plan.Tanks.Add(new TankPlacement(Vector2.Zero, 0, 0, "test"));
        plan.Tanks.Add(new TankPlacement(Vector2.One, 0, 0, "test"));
        return new TankLedger(plan, 2);
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("authority/predicted explosions can retire already freed body entries", () =>
        {
            var body = new RigidBody3D();
            var sync = new BodySync(2, 2);
            sync.Register(Id, body, true, 2);
            body.Free();
            sync.Remove(Id);
            TestRunner.Assert(sync.Get(Id) == null, "freed prediction stayed registered");
        });
        runner.Run("authority/group grants are atomic and every acceptance increments each epoch", () =>
        {
            var ledger = Ledger();
            var first = ledger.Claim(3, Claim(1, Pose(Id), Pose(Other)), true);
            TestRunner.Assert(first.Reason == ClaimRefusal.None && first.Bodies.All(b => b.Owner == 3 && b.Body.Epoch == 2 && b.Body.Sequence == 0), "group was not granted together");
            var stale = ledger.Claim(4, Claim(2, Pose(Id, 2), Pose(Other)), true);
            TestRunner.Assert(stale.Reason == ClaimRefusal.StaleEpoch && ledger.Get(Id)!.Owner == 3 && ledger.Get(Id)!.Body.Epoch == 2, "stale suffix partially granted its prefix");
            var sameOwner = ledger.Claim(3, Claim(3, Pose(Id, 2), Pose(Other, 2)), true);
            TestRunner.Assert(sameOwner.Bodies.All(b => b.Body.Epoch == 3), "same-peer grant reused an epoch");
            var unknown = ledger.Claim(4, Claim(4, Pose(Id, 3), Pose(999)), true);
            TestRunner.Assert(unknown.Reason == ClaimRefusal.UnknownBody && ledger.Get(Id)!.Body.Epoch == 3, "unknown member partially granted a group");
        });
        runner.Run("authority/group prediction includes owned members and never starts partially", () =>
        {
            using var mine = new BodyFixture(runner);
            using var other = new BodyFixture(runner);
            var sync = new BodySync(2, 2);
            sync.Register(Id, mine.Body, true, 2);
            sync.Register(Other, other.Body, true, 3);
            sync.Get(Other)!.ClaimPending = true;
            TestRunner.Assert(sync.ClaimGroup(new[] { mine.Body, other.Body }).Count == 0 && !sync.Get(Id)!.ClaimPending, "pending suffix partially predicted a group");
            sync.Get(Other)!.ClaimPending = false;
            var group = sync.ClaimGroup(new[] { mine.Body, other.Body, mine.Body });
            TestRunner.Assert(group.Count == 2 && group.All(b => b.Epoch == 1) && sync.Entries.All(e => e.ClaimPending), "owned member was omitted or duplicate member repeated");
            TestRunner.Assert(sync.Collect(1).Moving.Count == 0, "unconfirmed group published motion");
        });
        runner.Run("authority/group grants include partial ownership overlap", () =>
        {
            var ledger = Ledger();
            ledger.Claim(3, Claim(1, Pose(Id)), true);
            var group = ledger.Claim(3, Claim(2, Pose(Id, 2), Pose(Other)), true);
            TestRunner.Assert(group.Reason == ClaimRefusal.None && group.Bodies.All(b => b.Owner == 3) &&
                ledger.Get(Id)!.Body.Epoch == 3 && ledger.Get(Other)!.Body.Epoch == 2, "partly owned group did not get one complete grant");
            ledger.Claim(4, Claim(3, Pose(Id, 3)), true);
            var stale = ledger.Claim(3, Claim(4, Pose(Other, 2), Pose(Id, 3)), true);
            TestRunner.Assert(stale.Reason == ClaimRefusal.StaleEpoch && ledger.Get(Other)!.Body.Epoch == 2 &&
                ledger.Get(Id)!.Owner == 4, "overlapping claim partially regranted the valid prefix");
        });
        runner.Run("authority/departure resolves an overlapping in-flight group claim", () =>
        {
            using var f = new MatchFixture(runner);
            var group = f.Match.LiveTanks.Where(t => t.NetId == Id || t.NetId == Other).ToArray();
            f.Match.Claim(group, atomic: true);
            Send(f.Server, 3, Msg.Claim, Claim(10, Pose(Id), Pose(Other)));
            f.Server.Server.OnDisconnected(3);
            // The accepted competing grant and its successor grant both cross our pending request.
            f.Deliver();
            f.Flush();
            TestRunner.Assert(f.Match.LastAuthorityRefusal == ClaimRefusal.StaleEpoch &&
                group.All(t => f.Match.Bodies.Get(t.NetId) is { Owner: 2, Epoch: 3, ClaimPending: false }),
                "departure stranded a pending group or restored the departed owner");
        });
        runner.Run("authority/stale and non-owner publications never enter the server cache", () =>
        {
            var ledger = Ledger();
            ledger.Claim(3, Claim(1, Pose(Id)), true);
            TestRunner.Assert(!ledger.Update(2, Pose(Id, 1, 999), false), "old epoch motion accepted");
            TestRunner.Assert(!ledger.Update(2, Pose(Id, 2, 1), false), "non-owner motion accepted");
            TestRunner.Assert(ledger.Update(3, Pose(Id, 2, 2), true), "owner's final rest refused");
            TestRunner.Assert(!ledger.Update(3, Pose(Id, 2, 1), false), "delayed movement replaced rest");
            TestRunner.Assert(ledger.Update(3, Pose(Id, 2, 3), false), "genuine wake refused");
        });
        runner.Run("authority/epoch ordering survives A to B to A with delayed rests and wakes", () =>
        {
            using var body = new BodyFixture();
            var sync = new BodySync(4, 2);
            sync.Register(Id, body.Body, true, 2);
            sync.OnState(2, Pose(Id, 1, 100), true, 1);
            sync.OnAuthority(Grant(3, 2));
            sync.OnState(3, Pose(Id, 2, 1), false, 2);
            sync.OnState(2, Pose(Id, 1, 101), true, 3);
            TestRunner.Assert(sync.Get(Id) is { Epoch: 2, LastSequence: 1, TargetRest: false }, "old rest stopped the new owner");
            sync.OnAuthority(Grant(2, 3));
            sync.OnState(2, Pose(Id, 3, 1), false, 4);
            sync.OnState(2, Pose(Id, 1, 999), true, 5);
            TestRunner.Assert(sync.Get(Id) is { Epoch: 3, LastSequence: 1, TargetRest: false }, "returning peer accepted its earlier epoch");
            TestRunner.Assert(!sync.OnAuthority(Grant(3, 2)), "old grant replaced newer authority");
            sync.OnState(2, Pose(Id, 3, 2), true, 6);
            sync.Get(Id)!.HasTarget = false;
            sync.OnState(2, Pose(Id, 3, 1), false, 7);
            TestRunner.Assert(!sync.Get(Id)!.HasTarget, "completed rest forgot its sequence boundary");
        });
        runner.Run("authority/future motion survives overtaking its reliable grant", () =>
        {
            using var body = new BodyFixture();
            var sync = new BodySync(4, 2);
            sync.Register(Id, body.Body, true, 2);
            sync.OnState(3, Pose(Id, 2, 2), true, 1);
            sync.OnState(3, Pose(Id, 2, 1), false, 2);
            TestRunner.Assert(!sync.Get(Id)!.HasTarget, "future owner corrected before its grant");
            sync.OnAuthority(Grant(3, 2));
            TestRunner.Assert(sync.Get(Id) is { TargetRest: true, LastSequence: 2 }, "overtaking rest was lost");
            sync.OnState(3, Pose(Id, 2, 3), false, 3);
            TestRunner.Assert(sync.Get(Id) is { TargetRest: false, LastSequence: 3 }, "later wake was refused");
        });
        foreach (var order in new[] { new[] { 2, 3 }, new[] { 3, 2 }, new[] { 2, 3, 4 }, new[] { 2, 4, 3 }, new[] { 3, 2, 4 }, new[] { 3, 4, 2 }, new[] { 4, 2, 3 }, new[] { 4, 3, 2 } })
        {
            runner.Run($"authority/canonical vent adoption race {string.Join('-', order)}", () =>
            {
                var effects = new Node3D();
                runner.AddChild(effects);
                Spawn.SetEffectsRoot(effects);
                Spawn.SetWorldRoot(effects);
                var ledger = Ledger();
                var replies = order.Select(peer => ledger.Puncture(peer, Puncture(peer), true)).ToArray();
                TestRunner.Assert(replies[0].Reason == ClaimRefusal.None && replies.Skip(1).All(r => r.Reason == ClaimRefusal.StaleEpoch), "first contender did not win alone");
                var canonical = replies[0].Bodies.Single();
                foreach (var peer in order)
                {
                    var tank = PropaneTank.Create();
                    tank.NetId = Id;
                    runner.AddChild(tank);
                    try
                    {
                        var local = Puncture(peer);
                        tank.PunctureRemote(local.Hole, local.Normal, local.Swirl);
                        var transform = new Transform3D(new Basis(Vector3.Up, 0.7f), new Vector3(8, 2, 9));
                        BodySync.Snap(tank, transform, Vector3.One * 3, Vector3.One * 2);
                        var vent = canonical.Vent!;
                        tank.PunctureRemote(vent.Hole, vent.Normal, vent.Swirl);
                        TestRunner.Assert(tank.HoleLocal == vent.Hole && tank.HoleNormalLocal == vent.Normal && tank.SwirlSign == vent.Swirl, "loser kept its local vent");
                        var lean = vent.Normal.Rotated(Vector3.Up, Mathf.DegToRad(Tuning.Current.VentSwirlDegrees) * vent.Swirl).Normalized();
                        TestRunner.Assert(tank.JetDirectionLocal.IsEqualApprox(lean), "canonical lean differs");
                        TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(transform) && tank.LinearVelocity == Vector3.One * 3 && tank.AngularVelocity == Vector3.One * 2, "adoption changed current motion");
                    }
                    finally { tank.Free(); }
                }
                effects.Free();
            });
        }
        runner.Run("authority/departure mid-vent preserves canonical parameters and resets the stream", () =>
        {
            var ledger = Ledger();
            var puncture = ledger.Puncture(3, Puncture(3), true).Bodies.Single();
            var latest = Pose(Id, 2, 99);
            ledger.Update(3, latest, false);
            var transferred = ledger.Transfer(3, 2).Single();
            TestRunner.Assert(transferred is { Owner: 2, State: TankLedger.State.Venting } && transferred.Body.Epoch == 3 && transferred.Body.Sequence == 0 &&
                transferred.Vent == puncture.Vent && transferred.Body.Transform == latest.Transform, "departure lost the canonical vent or pose");
            TestRunner.Assert(!ledger.Update(3, Pose(Id, 2, 100), true) && ledger.Update(2, Pose(Id, 3, 1), false), "handoff kept the departed stream");
        });
        runner.Run("authority/server echoes canonical punctures and refuses contenders explicitly", () =>
        {
            using var f = new ServerTests.Fixture();
            Send(f, 2, Msg.Puncture, Puncture(2));
            TestRunner.Assert(f.Packets.Count(p => p.Message.Payload is AuthorityMessage { Reason: ClaimRefusal.None }) == 2, "canonical echo excluded its contender");
            f.Packets.Clear();
            Send(f, 3, Msg.Puncture, Puncture(3));
            TestRunner.Assert(f.Packets.Single() is { Peer: 3, Message.Payload: AuthorityMessage { Reason: ClaimRefusal.StaleEpoch } refusal } &&
                refusal.Bodies.Single().Vent == new VentParameters(Puncture(2).Hole, Puncture(2).Normal, Puncture(2).Swirl), "loser did not receive the canonical vent");
        });
        runner.Run("authority/loading-phase requests receive canonical refusals", () =>
        {
            using var f = new ServerTests.Fixture(playing: false);
            Send(f, 2, Msg.Puncture, Puncture(2));
            TestRunner.Assert(f.Packets.Single().Message.Payload is AuthorityMessage { Reason: ClaimRefusal.WrongPhase } result &&
                result.Bodies.Single().State == TankLedger.State.Intact, "loading silently discarded a predicted puncture");
        });
        runner.Run("authority/detonation and chain claims share ordering and initiating-peer attribution", () =>
        {
            var ledger = Ledger();
            var accepted = ledger.Detonate(2, new ExplodeMessage(Id, Vector3.One, Request: 10), true);
            var duplicate = ledger.Detonate(3, new ExplodeMessage(Id, Vector3.Right, Request: 11), true);
            TestRunner.Assert(accepted.Reason == ClaimRefusal.None && duplicate.Reason == ClaimRefusal.Exploded && duplicate.Bodies.Single().Center == Vector3.One && ledger.MapDetonations == 1, "duplicate blast changed the canonical transition or score");
            var child = Puncture(3) with { Id = Other, Cause = 10 };
            TestRunner.Assert(ledger.Puncture(3, child, true).Reason == ClaimRefusal.Cause, "another peer borrowed the chain");
            TestRunner.Assert(ledger.Puncture(2, child, true).Reason == ClaimRefusal.None, "winning root could not start its chain");
            TestRunner.Assert(ledger.Claim(3, Claim(12, Pose(Other, 2)) with { Cause = 11 }, true).Reason == ClaimRefusal.Cause, "refused root granted pushed-body ownership");
        });
        runner.Run("authority/a canceled chain fuse cannot detonate a restored or re-punctured tank", () =>
        {
            var effects = new Node3D();
            runner.AddChild(effects);
            Spawn.SetEffectsRoot(effects);
            Spawn.SetWorldRoot(effects);
            var callbacks = new List<Action>();
            var tank = PropaneTank.Create();
            tank.FuseScheduleOverride = (_, callback) => callbacks.Add(callback);
            effects.AddChild(tank);
            var detonations = 0;
            tank.Detonated += _ => detonations++;
            try
            {
                tank.PunctureRemote(Vector3.One * .1f, Vector3.Right, 1);
                tank.ChainHit(Vector3.Zero, 0, 11);
                tank.RestoreIntact();
                tank.PunctureRemote(Vector3.One * .1f, Vector3.Left, -1);
                tank.ChainHit(Vector3.Zero, 0, 12);
                TestRunner.Assert(callbacks.Count == 2, "probe did not schedule both production callbacks");
                callbacks[0]();
                TestRunner.Assert(tank.State == PropaneTank.TankState.Venting && tank.ChainCause == 12 && detonations == 0, "old fuse detonated a new lifecycle");
                callbacks[1]();
                TestRunner.Assert(tank.State == PropaneTank.TankState.Exploded && detonations == 1, "current fuse did not detonate exactly once");
            }
            finally { effects.Free(); }
        });
        runner.Run("authority/refused ownership restores the latest canonical pose and clears prediction", () =>
        {
            using var f = new MatchFixture(runner);
            Send(f.Server, 3, Msg.Claim, Claim(5, Pose(Id)));
            f.Deliver();
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.GlobalPosition = Vector3.One * 90;
            f.Match.Claim(new[] { tank });
            Send(f.Server, 3, Msg.Claim, Claim(6, Pose(Id, 2)));
            f.Server.Packets.Clear(); // Its grant is still in flight when the stale prediction arrives.
            f.Flush();
            TestRunner.Assert(f.Match.LastAuthorityRefusal == ClaimRefusal.StaleEpoch && tank.GlobalTransform.IsEqualApprox(Pose(Id).Transform) &&
                f.Match.Bodies.Get(Id) is { Owner: 3, Epoch: 3, ClaimPending: false }, "refused claim left divergent motion or ownership");
        });
        foreach (var broadcastFirst in new[] { false, true })
        {
            runner.Run($"authority/losing puncture preserves motion (broadcast first {broadcastFirst})", () =>
            {
                using var f = new MatchFixture(runner);
                var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
                tank.TakeShot(tank.ToGlobal(new Vector3(.15f, .23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left);
                Send(f.Server, 3, Msg.Puncture, Puncture(3));
                if (!broadcastFirst) { f.Server.Packets.Clear(); }
                var transform = new Transform3D(new Basis(Vector3.Up, .7f), new Vector3(8, 2, 9));
                BodySync.Snap(tank, transform, Vector3.One * 3, Vector3.One * 2);
                if (broadcastFirst) { f.Deliver(); }
                f.Flush();
                TestRunner.Assert(f.Match.LastAuthorityRefusal == ClaimRefusal.StaleEpoch && tank.HoleLocal == Puncture(3).Hole &&
                    tank.HoleNormalLocal == Puncture(3).Normal && tank.SwirlSign == Puncture(3).Swirl &&
                    f.Match.Bodies.Get(Id) is { Owner: 3, Epoch: 2, ClaimPending: false }, "loser did not adopt canonical authority and vent");
                TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(transform) && tank.LinearVelocity == Vector3.One * 3 &&
                    tank.AngularVelocity == Vector3.One * 2, "puncture refusal rewound the contender's current motion");
            });
        }
        runner.Run("authority/refused vent ownership cancels its predicted chain fuse", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.TakeShot(tank.ToGlobal(new Vector3(.15f, .23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left);
            f.Flush();
            Action? callback = null;
            tank.FuseScheduleOverride = (_, finish) => callback = finish;
            tank.ChainHit(Vector3.Zero, 0, 99);
            f.Match.Claim(new[] { tank }, atomic: true);
            Send(f.Server, 3, Msg.Claim, Claim(30, Pose(Id, 2)));
            f.Server.Packets.Clear();
            f.Flush();
            TestRunner.Assert(callback != null, "probe never scheduled the chain fuse");
            callback!();
            TestRunner.Assert(tank.State == PropaneTank.TankState.Venting && f.Match.Bodies.Get(Id) is { Owner: 3, Epoch: 3, ClaimPending: false },
                "refused prediction's fuse detonated the canonical vent");
        });
        runner.Run("authority/wrong-phase puncture restores intact state without a phantom vent", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.TakeShot(tank.ToGlobal(new Vector3(0.15f, 0.23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left);
            f.Server.Server.AdvanceMatches(NetTransport.Now + 20);
            f.Server.Packets.Clear();
            f.Flush();
            TestRunner.Assert(f.Match.LastAuthorityRefusal == ClaimRefusal.WrongPhase && tank.State == PropaneTank.TankState.Intact &&
                f.Match.Bodies.Get(Id) is { Epoch: 1, ClaimPending: false }, "refused puncture remained venting");
        });
        runner.Run("authority/refused predicted detonation recreates the canonical venting body", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.TakeShot(tank.ToGlobal(new Vector3(0.15f, 0.23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left);
            f.Flush();
            var vent = new VentParameters(tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign);
            // Move away from piles so this probe tests the refused root alone.
            tank.GlobalPosition = new Vector3(0, 60, 0);
            tank.Detonate();
            tank.Free(); // The reliable refusal usually arrives after QueueFree has completed.
            Send(f.Server, 3, Msg.Claim, Claim(20, Pose(Id, 2)));
            f.Server.Packets.Clear();
            f.Flush();
            var restored = f.Match.LiveTanks.Single(t => t.NetId == Id);
            TestRunner.Assert(restored != tank && restored.State == PropaneTank.TankState.Venting && restored.HoleLocal == vent.Hole &&
                restored.HoleNormalLocal == vent.Normal && restored.SwirlSign == vent.Swirl && restored.GlobalTransform.IsEqualApprox(Pose(Id).Transform), "refusal did not recreate the authoritative tank");
        });
        runner.Run("authority/newer motion cannot suppress refused lifecycle restoration", () =>
        {
            using var f = new MatchFixture(runner);
            Send(f.Server, 3, Msg.Puncture, Puncture(3));
            f.Deliver();
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.GlobalPosition = new Vector3(0, 60, 0);
            tank.Detonate();
            tank.Free();
            // Refusal was generated before this disposable publication, but arrives after it.
            var vent = new VentParameters(Puncture(3).Hole, Puncture(3).Normal, Puncture(3).Swirl);
            var latest = Pose(Id, 2, 8) with { Transform = new Transform3D(Basis.Identity, new Vector3(9, 2, 3)) };
            f.Match.OnMessage(new NetMessage(Msg.BodyStates, f.Server.Id, 3, new BodyStatesMessage(1, new[] { latest })));
            f.Match.OnMessage(new NetMessage(Msg.Authority, f.Server.Id, 0,
                new AuthorityMessage(2, 1, ClaimRefusal.WrongPhase, new[]
                {
                    new BodyAuthority(Pose(Id, 2, 7), 3, TankLedger.State.Venting, false, vent, Vector3.Zero),
                })));
            var restored = f.Match.LiveTanks.Single(t => t.NetId == Id);
            TestRunner.Assert(restored != tank && restored.State == PropaneTank.TankState.Venting && restored.HoleLocal == vent.Hole,
                "newer publication suppressed the refused explosion's restoration");
            TestRunner.Assert(restored.GlobalTransform.IsEqualApprox(latest.Transform), "restoration rewound newer motion");
            TestRunner.Assert(f.Match.Bodies.Get(Id) is { LastSequence: 8, HasTarget: true, Target.Origin: var position } && position == latest.Transform.Origin,
                "restoration lost the newer correction or its sequence boundary");
        });
        runner.Run("authority/newer motion cannot suppress refused fuse cancellation", () =>
        {
            using var f = new MatchFixture(runner);
            Send(f.Server, 3, Msg.Puncture, Puncture(3));
            f.Deliver();
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            Action? callback = null;
            tank.FuseScheduleOverride = (_, finish) => callback = finish;
            tank.ChainHit(Vector3.Zero, 0, 99);
            f.Match.Claim(new[] { tank }, atomic: true);
            f.Server.Server.AdvanceMatches(NetTransport.Now + 20);
            Send(f.Server, 3, Msg.BodyStates, new BodyStatesMessage(1, new[] { Pose(Id, 2, 7) }));
            f.Server.Packets.Clear();
            f.Match.OnMessage(new NetMessage(Msg.BodyStates, f.Server.Id, 3, new BodyStatesMessage(2, new[] { Pose(Id, 2, 8) })));
            f.Flush();
            TestRunner.Assert(callback != null && f.Match.LastAuthorityRefusal == ClaimRefusal.WrongPhase, "probe did not refuse the predicted chain claim");
            callback!();
            TestRunner.Assert(tank.State == PropaneTank.TankState.Venting && tank.ChainCause == 0 &&
                f.Match.Bodies.Get(Id) is { LastSequence: 8, ClaimPending: false }, "newer motion suppressed fuse cancellation or lost its boundary");
        });
    }

    private static void Send(ServerTests.Fixture f, int peer, Msg type, object payload) =>
        f.Receive(MessageCodec.Encode(new NetMessage(type, f.Id, 0, payload), false), peer);

    private sealed class BodyFixture : IDisposable
    {
        public readonly RigidBody3D Body = new();
        public BodyFixture(Node? parent = null) => parent?.AddChild(Body);
        public void Dispose() => Body.Free();
    }

    private sealed class MatchFixture : IDisposable
    {
        public readonly ServerTests.Fixture Server = new();
        private readonly NetClient client = new();
        public readonly Match Match;
        private readonly Queue<byte[]> sent = new();
        public MatchFixture(TestRunner runner)
        {
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            Match = new Match { Net = client, Setup = Server.Setup, AuthoritySendOverride = sent.Enqueue };
            runner.AddChild(Match);
        }
        public void Deliver()
        {
            var messages = Server.Packets.Where(p => p.Peer == 2 && p.Message.Type == Msg.Authority).Select(p => p.Message).ToArray();
            Server.Packets.Clear();
            foreach (var message in messages) { Match.OnMessage(message); }
        }
        public void Flush()
        {
            while (sent.TryDequeue(out var packet)) { Server.Receive(packet); Deliver(); }
        }
        public void Dispose() { Match.Free(); client.Free(); Server.Dispose(); }
    }
}
