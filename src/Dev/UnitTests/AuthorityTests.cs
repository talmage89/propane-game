using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests;

[UnitSuite]
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
        foreach (var (sleeping, botVictim) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            runner.Run($"authority/unconfirmed {(sleeping ? "resting" : "moving")} drops from {(botVictim ? "bot" : "human")} defer publication until creation", () =>
            {
                using var f = new MatchFixture(runner, bots: 1);
                var victim = botVictim ? f.Match.Session.OwnedActors.Single(a => a.Character != f.Match.Player).Character : f.Match.Player;
                f.Match.OnMessage(new NetMessage(Msg.MatchGo, f.Server.Id, 0, new FloatMessage(60)));
                f.Match.OnMessage(ModeWireFixtures.Score(f.Server.Id, victim.ActorId, 5, 0));
                victim.Runtime.GraceUntil = 0;
                f.Match.OnMessage(new NetMessage(Msg.Shot, f.Server.Id, 3,
                    new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 0, victim.ActorId, -1), f.Server.ActorId(3), 1, 1));
                var drops = f.Match.LiveTanks.Where(t => t.NetId >= BodySync.DropTankBase && t.NetId < BodySync.CarBase).ToArray();
                TestRunner.Assert(drops.Length > 0, "production shot did not create predicted drops");
                foreach (var drop in drops) { drop.Sleeping = sleeping; }
                var known = f.Match.Bodies.Get(Id)!;
                known.Body.Sleeping = false;
                f.Server.Clock.Seconds += .1;
                f.Match._PhysicsProcess(1.0 / 60);
                TestRunner.Assert(known.Sequence > 0, "pending drops blocked a registered body's stream");
                TestRunner.Assert(drops.All(t => f.Match.Bodies.Get(t.NetId) is { Sequence: 0, PublishRequired: true }),
                    "unregistered drop published or consumed its required first state");
                f.Match.OnMessage(ModeWireFixtures.Score(f.Server.Id, victim.ActorId, 5 - drops.Length, -drops.Length));
                f.Server.Clock.Seconds += .1;
                f.Match._PhysicsProcess(1.0 / 60);
                TestRunner.Assert(drops.All(t => f.Match.Bodies.Get(t.NetId)!.Sequence == 0),
                    "bank debit released publication before creation confirmation");
                var states = drops.Select(t => new BodyState(t.NetId, t.GlobalTransform, t.LinearVelocity, t.AngularVelocity)).ToArray();
                f.Match.OnMessage(ModeWireFixtures.Accepted(f.Server.Id, victim.ActorId, 1, Modes.SpillReason.Hit, states));
                f.Match._PhysicsProcess(1.0 / 60);
                TestRunner.Assert(drops.All(t => f.Match.Bodies.Get(t.NetId) is { Sequence: 1, PublishRequired: false }),
                    "confirmation lost the first moving checkpoint or final rest");
                f.Match._PhysicsProcess(1.0 / 60);
                TestRunner.Assert(drops.All(t => f.Match.Bodies.Get(t.NetId)!.Sequence == 1),
                    "confirmation repeatedly published without elapsed network time");
            });
        }
        runner.Run("authority/rotating an offset body does not translate its mass center", () =>
        {
            using var body = new BodyFixture(runner);
            body.Body.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
            body.Body.CenterOfMass = Vector3.Up * .19f;
            var center = Vector3.Up * 10;
            var rotated = new Basis(Vector3.Forward, Mathf.Pi / 2);
            body.Body.GlobalTransform = new Transform3D(rotated, center - rotated * body.Body.CenterOfMass);
            var pose = new Transform3D(Basis.Identity, center - body.Body.CenterOfMass);
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
            sync.Register(Id, body.Body, true, 2);
            sync.OnState(2, new BodyState(Id, pose, Vector3.Zero, Vector3.Zero, 1, 1), false, 1);
            sync.Correct(1);
            TestRunner.Assert(body.Body.LinearVelocity.Length() < .0001f,
                "orientation error accelerated an already aligned mass center");
            TestRunner.Assert(body.Body.AngularVelocity.Length() > 1, "orientation error was not corrected");
        });
        runner.Run("authority/spinning motion extrapolates around the mass center", () =>
        {
            using var body = new BodyFixture(runner);
            body.Body.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
            body.Body.CenterOfMass = Vector3.Up * .19f;
            var center = Vector3.Up * 10;
            body.Body.GlobalPosition = center - body.Body.CenterOfMass - Vector3.Right * 100;
            var pose = new Transform3D(Basis.Identity, center - body.Body.CenterOfMass);
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
            sync.Register(Id, body.Body, true, 2);
            sync.OnState(2, new BodyState(Id, pose, Vector3.Right * 10, Vector3.Forward * 10, 1, 1), false, 1);
            sync.Correct(1.1);
            var actual = body.Body.GlobalTransform * body.Body.CenterOfMass;
            TestRunner.Assert(actual.DistanceTo(center + Vector3.Right) < .0001f,
                "extrapolated rotation displaced the translating mass center");
        });
        runner.Run("authority/pending gather lease still applies the resting owner", () =>
        {
            using var body = new BodyFixture(runner);
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
            sync.Register(Id, body.Body, true, 2);
            var rest = Pose(Id, 1, 1) with { Linear = Vector3.Zero, Angular = Vector3.Zero };
            sync.OnState(2, rest, true, 1);
            sync.Correct(2);
            sync.ClaimGroup(new[] { body.Body }, predictMotion: false);
            body.Body.GlobalPosition += Vector3.Right * .3f;
            sync.Correct(2.1);
            TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(rest.Transform), "lease abandoned the resting owner");
            sync.Claim(new[] { body.Body }); // A real push during the lease must still predict locally.
            body.Body.GlobalPosition += Vector3.Right;
            var pushed = body.Body.GlobalTransform;
            sync.Correct(2.2);
            TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(pushed), "gather lease erased a genuine predicted impulse");
        });
        runner.Run("authority/match queue preserves a nonpredictive gather target", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            f.Match.Bodies.OnAuthority(Grant(3, 2) with { Rest = true }, 0);
            f.Match.Bodies.Correct(1);
            f.Match.Claim(new[] { tank }, atomic: true, predictMotion: false);
            TestRunner.Assert(f.Match.Bodies.Get(Id) is { ClaimPending: true, HasTarget: true, TargetRest: true },
                "production authority queue discarded the gather rest target");
        });
        runner.Run("authority/frozen gather motion never publishes a premature rest", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var entry = f.Match.Bodies.Get(Id)!;
            tank.BeginGather();
            tank.Sleeping = true; // Native frozen bodies can sleep while the owner moves their transform.
            var moving = new List<BodySync.Entry>();
            var rested = new List<BodySync.Entry>();
            f.Match.Bodies.Collect(.1, moving, rested);
            TestRunner.Assert(moving.Contains(entry) && !rested.Contains(entry),
                "frozen glide published a rest that ends observer gathering");
            tank.EndGather();
            tank.Sleeping = true;
            f.Match.Bodies.Collect(.1, moving, rested);
            TestRunner.Assert(rested.Contains(entry), "actual gather completion lost its final rest");
        });
        runner.Run("authority/each body's checkpoints advance independent retransmission lanes", () =>
        {
            using var body = new BodyFixture(runner);
            var sync = new BodySync(Tuning.SavedTemplate, 2, 2);
            sync.Register(Id, body.Body, true, 2);
            body.Body.Sleeping = false;
            var lanes = new HashSet<int>();
            var moving = new List<BodySync.Entry>();
            var rested = new List<BodySync.Entry>();
            for (var i = 0; i < 3; i++)
            {
                sync.Collect((i + 1) * .3, moving, rested);
                var sample = moving.Single();
                TestRunner.Assert(sample.PublishReliably, "probe did not collect a checkpoint");
                lanes.Add(NetTransport.Channel(sample.CheckpointTraffic));
            }
            TestRunner.Assert(lanes.Count == 3 && !lanes.Contains(NetTransport.Channel(TrafficClass.ReliableOrdered)),
                "new owner checkpoints remain behind an older checkpoint's retransmission");
        });
        runner.Run("authority/solid geometry cannot trap a fresh observer across a wall", () =>
        {
            using var body = new BodyFixture(runner.World.Root);
            var wall = new StaticBody3D { CollisionLayer = Layers.World };
            using var shape = new BoxShape3D { Size = new Vector3(.1f, 10, 10) };
            wall.AddChild(new CollisionShape3D { Shape = shape });
            runner.World.Root.AddChild(wall);
            try
            {
                var from = new Vector3(-1, 2, 0);
                var pose = new Transform3D(Basis.Identity, new Vector3(1, 2, 0));
                using var ray = PhysicsRayQueryParameters3D.Create(from, pose.Origin, Layers.World);
                TestRunner.Assert(runner.World.Physics.DirectSpaceState.IntersectRay(ray).Count > 0, "wall fixture was not visible to native physics");
                var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
                sync.Register(Id, body.Body, true, 2);
                body.Body.GlobalPosition = from;
                sync.OnState(2, new BodyState(Id, pose, Vector3.Right, Vector3.Zero, 1, 1), false, 1);
                sync.Correct(1);
                TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(pose), "fresh observer steered into a wall instead of recovering the owner pose");
                // Free-space copies still ease: obstruction does not lower the normal snap threshold.
                var clearFrom = from + Vector3.Forward * 20;
                var clearPose = new Transform3D(Basis.Identity, pose.Origin + Vector3.Forward * 20);
                using var clearRay = PhysicsRayQueryParameters3D.Create(clearFrom, clearPose.Origin, Layers.World);
                TestRunner.Assert(runner.World.Physics.DirectSpaceState.IntersectRay(clearRay).Count == 0, "free-space fixture was obstructed");
                body.Body.GlobalPosition = clearFrom;
                sync.OnState(2, new BodyState(Id, clearPose, Vector3.Right, Vector3.Zero, 1, 2), false, 2);
                sync.Correct(2);
                TestRunner.Assert(body.Body.GlobalPosition.IsEqualApprox(clearFrom) && body.Body.LinearVelocity.X > 1,
                    "unobstructed observer lost ordinary smoothing");
            }
            finally { wall.Free(); }
        });
        runner.Run("authority/moving owners periodically checkpoint through disposable loss", () =>
        {
            using var body = new BodyFixture(runner);
            body.Body.Sleeping = false;
            var sync = new BodySync(Tuning.SavedTemplate, 2, 2);
            sync.Register(Id, body.Body, true, 2);
            var moving = new List<BodySync.Entry>();
            var rested = new List<BodySync.Entry>();
            sync.Collect(.01, moving, rested);
            var first = moving.Single();
            TestRunner.Assert(first.PublishReliably, "new owner started with only disposable movement");
            var reliable = 0;
            var disposable = 0;
            var silence = 0.0;
            for (var step = 0; step < 120; step++)
            {
                silence += 1.0 / 60;
                sync.Collect(.01 + (step + 1) / 60.0, moving, rested);
                foreach (var entry in moving)
                {
                    if (entry.PublishReliably) { reliable++; silence = 0; }
                    else { disposable++; }
                }
                TestRunner.Assert(silence <= .35, "moving reliable checkpoint exceeded correction freshness");
            }
            TestRunner.Assert(reliable >= 6 && disposable > reliable, "checkpoint cadence replaced the disposable motion stream");
        });
        runner.Run("authority/match publications keep real-time cadence when physics falls behind", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var entry = f.Match.Bodies.Get(Id)!;
            tank.Sleeping = false;
            var lastCheckpoint = f.Server.Clock.Seconds;
            for (var step = 0; step < 60; step++)
            {
                f.Server.Clock.Seconds += .1;
                var sequence = entry.Sequence;
                f.Match._PhysicsProcess(1.0 / 120);
                TestRunner.Assert(entry.Sequence > sequence, "simulation delta throttled a due wall-clock publication");
                if (entry.PublishReliably) { lastCheckpoint = f.Server.Clock.Seconds; }
                TestRunner.Assert(f.Server.Clock.Seconds - lastCheckpoint <= .35,
                    "slow physics left reliable motion beyond the correction freshness budget");
            }
            var beforeCatchup = entry.Sequence;
            for (var step = 0; step < 8; step++) { f.Match._PhysicsProcess(1.0 / 120); }
            TestRunner.Assert(entry.Sequence == beforeCatchup,
                "catch-up physics emitted old poses without any elapsed network time");
        });
        runner.Run("authority/reliable grant steers observers before owner publications", () =>
        {
            using var body = new BodyFixture(runner);
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
            sync.Register(Id, body.Body, true, 2);
            body.Body.GlobalPosition = Vector3.One * 20;
            sync.OnAuthority(Grant(3, 2), 0);
            sync.Correct(0);
            TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(Pose(Id).Transform), "grant did not supply an observer correction before owner motion");
            var rest = Grant(2, 3) with { Rest = true };
            sync.OnAuthority(rest, 0);
            sync.Correct(1);
            TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(rest.Body.Transform) && sync.Get(Id) is { HasTarget: true, TargetRest: true },
                "resting handoff lost the new owner's last word");
        });
        runner.Run("authority/vent spin limit does not erase observer steering", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.PunctureRemote(Vector3.Zero, Vector3.Right, 1);
            tank.GlobalTransform = new Transform3D(Basis.Identity, tank.GlobalPosition);
            var spin = Vector3.Up * f.Match.World.Tuning.VentMaxSpin;
            var target = new Transform3D(new Basis(Vector3.Up, .5f), tank.GlobalPosition);
            // This fixture's host owns the initial tank; grant the observer stream explicitly.
            f.Match.Bodies.OnAuthority(new BodyAuthority(new BodyState(Id, target, Vector3.Zero, spin, 2, 0),
                3, TankLedger.State.Venting, false, new VentParameters(Vector3.Zero, Vector3.Right, 1), Vector3.Zero));
            f.Match.Bodies.OnState(3, new BodyState(Id, target, Vector3.Zero, spin, 2, 1), false, 1);
            f.Match.Bodies.Correct(1);
            var steering = tank.AngularVelocity;
            TestRunner.Assert(steering.Length() > spin.Length(), "fixture did not require steering beyond owner spin");
            tank._PhysicsProcess(1.0 / 60);
            TestRunner.Assert(tank.AngularVelocity.IsEqualApprox(steering), "vent spin limiter erased observer correction");
            f.Match.Bodies.Correct(2);
            tank._PhysicsProcess(1.0 / 60);
            TestRunner.Assert(tank.AngularVelocity.Length() <= spin.Length() + .001f, "stale observer escaped the ordinary spin limit");
            f.Match.Bodies.OnAuthority(new BodyAuthority(new BodyState(Id, target, Vector3.Zero, spin, 3, 0),
                2, TankLedger.State.Venting, false, new VentParameters(Vector3.Zero, Vector3.Right, 1), Vector3.Zero));
            f.Match.Bodies.Correct(3);
            tank.AngularVelocity = steering;
            tank._PhysicsProcess(1.0 / 60);
            TestRunner.Assert(tank.AngularVelocity.Length() <= spin.Length() + .001f, "new owner escaped its spin limit");
        });
        runner.Run("authority/settled intact contact recovers despite continuous awake publications", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var pose = tank.GlobalTransform;
            var stalled = new Transform3D(new Basis(Vector3.Forward, .45f) * pose.Basis, pose.Origin + Vector3.Up * .15f);
            f.Match.Bodies.OnAuthority(new BodyAuthority(new BodyState(Id, pose, Vector3.Zero, Vector3.Zero, 2, 0),
                3, TankLedger.State.Intact, false, null, Vector3.Zero), 0);
            tank.GlobalTransform = stalled;
            for (var tick = 0; tick < 120; tick++)
            {
                var now = tick / 120.0;
                // Native floor contact consumes the observer's steering while the owner remains awake.
                tank.LinearVelocity = tank.AngularVelocity = Vector3.Zero;
                f.Match.Bodies.OnState(3, new BodyState(Id, pose, Vector3.Zero, Vector3.Zero, 2, (uint)tick + 1), false, now);
                f.Match.Bodies.Correct(now);
            }
            TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(pose), "fresh low-motion stream left an intact copy wedged in contact");
            TestRunner.Assert(f.Match.Bodies.Get(Id) is { TargetRest: false } && !tank.Sleeping,
                "contact recovery converted an awake owner publication into a rest");
        });
        runner.Run("authority/owner motion restarts settled contact recovery", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var pose = tank.GlobalTransform;
            var stalled = pose.Translated(Vector3.Up * .15f);
            f.Match.Bodies.OnAuthority(new BodyAuthority(new BodyState(Id, pose, Vector3.Zero, Vector3.Zero, 2, 0),
                3, TankLedger.State.Intact, false, null, Vector3.Zero), 0);
            tank.GlobalTransform = stalled;
            uint sequence = 0;
            void Tick(double now, Vector3 ownerVelocity)
            {
                tank.LinearVelocity = tank.AngularVelocity = Vector3.Zero;
                f.Match.Bodies.OnState(3, new BodyState(Id, pose, ownerVelocity, Vector3.Zero, 2, ++sequence), false, now);
                f.Match.Bodies.Correct(now);
            }
            Tick(0, Vector3.Zero);
            Tick(.6, Vector3.Right);
            Tick(.61, Vector3.Zero);
            Tick(1.2, Vector3.Zero);
            TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(stalled), "old stationary interval survived resumed owner motion");
            Tick(1.5, Vector3.Zero);
            TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(pose), "new stationary interval could not recover contact");
        });
        runner.Run("authority/rest remains authoritative after observer settles and drifts locally", () =>
        {
            using var body = new BodyFixture(runner);
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
            sync.Register(Id, body.Body, true, 2);
            var rest = Pose(Id, 1, 1) with { Linear = Vector3.Zero, Angular = Vector3.Zero };
            body.Body.GlobalTransform = rest.Transform;
            body.Body.Sleeping = true;
            sync.OnState(2, rest, true, 1);
            sync.Correct(2);
            TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(rest.Transform), $"rest was not applied to observer: {body.Body.GlobalTransform} vs {rest.Transform}");
            body.Body.GlobalPosition += Vector3.Right * .3f;
            body.Body.Sleeping = true;
            sync.Correct(3);
            TestRunner.Assert(body.Body.GlobalTransform.IsEqualApprox(rest.Transform), "settled observer forgot the owner's last word");
            sync.OnState(2, rest with { Sequence = 2, Linear = Vector3.Right }, false, 4);
            TestRunner.Assert(sync.Get(Id) is { TargetRest: false, LastSequence: 2 }, "newer owner wake could not release rest");
        });
        runner.Run("authority/initial sleeping owner publishes its resting pose", () =>
        {
            using var body = new BodyFixture(runner);
            body.Body.Sleeping = true;
            var sync = new BodySync(Tuning.SavedTemplate, 2, 2);
            sync.Register(Id, body.Body, true, 2);
            var moving = new List<BodySync.Entry>();
            var rested = new List<BodySync.Entry>();
            sync.Collect(.01, moving, rested);
            TestRunner.Assert(rested.SingleOrDefault()?.Id == Id, "initially asleep owner never sent an authoritative rest");
            sync.Collect(.01, moving, rested);
            TestRunner.Assert(rested.Count == 0, "unchanged sleep repeated its reliable publication");
        });
        runner.Run("authority/match copies local bot dials beside creator tuning", () =>
        {
            using var local = new Tuning { TankMass = 7, BotRouteSpacing = 9 };
            using var fixture = new MatchFixture(runner, local);
            var tuning = fixture.Match.World.Tuning;
            TestRunner.Assert(tuning.TankMass == fixture.Server.Setup.Tuning["TankMass"].AsSingle() && tuning.BotRouteSpacing == 9,
                "local brain dials were lost or local gameplay tuning overrode the creator");
            local.BotRouteSpacing = 15;
            TestRunner.Assert(tuning.BotRouteSpacing == 9 && local.TankMass == 7, "match tuning retained its application template");
        });
        runner.Run("authority/predicted explosions can retire already freed body entries", () =>
        {
            var body = new RigidBody3D();
            var sync = new BodySync(Tuning.SavedTemplate, 2, 2);
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
            var sync = new BodySync(Tuning.SavedTemplate, 2, 2);
            sync.Register(Id, mine.Body, true, 2);
            sync.Register(Other, other.Body, true, 3);
            sync.Get(Other)!.ClaimPending = true;
            TestRunner.Assert(sync.ClaimGroup(new[] { mine.Body, other.Body }).Count == 0 && !sync.Get(Id)!.ClaimPending, "pending suffix partially predicted a group");
            sync.Get(Other)!.ClaimPending = false;
            var group = sync.ClaimGroup(new[] { mine.Body, other.Body, mine.Body });
            TestRunner.Assert(group.Count == 2 && group.All(b => b.Epoch == 1) && sync.Entries.All(e => e.ClaimPending), "owned member was omitted or duplicate member repeated");
            var moving = new List<BodySync.Entry>();
            var rested = new List<BodySync.Entry>();
            sync.Collect(1, moving, rested);
            TestRunner.Assert(moving.Count == 0, "unconfirmed group published motion");
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
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
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
        runner.Run("authority/correction includes bounded relay transit through an overtaken grant", () =>
        {
            using var body = new BodyFixture(runner);
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
            sync.Register(Id, body.Body, true, 2);
            var motion = Pose(Id, 2, 1) with { Linear = Vector3.Right * 10, Angular = Vector3.Zero };
            sync.OnState(3, motion, false, 2, 0.1);
            sync.OnAuthority(Grant(3, 2));
            TestRunner.Assert(Math.Abs(sync.Get(Id)!.TargetReceived - 1.9) < 0.00001, "future grant forgot relay transit");
            sync.Correct(2);
            TestRunner.Assert(body.Body.GlobalPosition.IsEqualApprox(motion.Transform.Origin + Vector3.Right),
                "correction left the body behind by its relay transit");
            sync.OnState(3, motion with { Sequence = 2 }, false, 3, 100);
            TestRunner.Assert(Math.Abs(sync.Get(Id)!.TargetReceived - 2.7) < 0.00001, "extrapolation accepted unbounded clock error");
            sync.OnState(3, motion with { Sequence = 3 }, false, 4, -1);
            TestRunner.Assert(sync.Get(Id)!.TargetReceived == 4, "negative clock error predicted backward");
            sync.OnState(3, motion with { Sequence = 4 }, true, 5, 0.1);
            TestRunner.Assert(sync.Get(Id) is { TargetReceived: 5, TargetRest: true }, "relay transit changed resting settle time");
            sync.OnState(3, motion with { Sequence = 3 }, false, 6, 0);
            TestRunner.Assert(sync.Get(Id) is { LastSequence: 4, TargetRest: true }, "timestamp overrode sequence ordering");
        });
        runner.Run("authority/motion freshness starts at receipt rather than sampling time", () =>
        {
            foreach (var overtakesGrant in new[] { false, true })
            {
                using var body = new BodyFixture(runner);
                var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
                sync.Register(Id, body.Body, true, 2);
                if (!overtakesGrant) { sync.OnAuthority(Grant(3, 2)); }
                sync.OnState(3, Pose(Id, 2, 1), false, 10, .3);
                if (overtakesGrant) { sync.OnAuthority(Grant(3, 2)); }
                sync.Correct(10.4);
                TestRunner.Assert(sync.Get(Id)!.HasTarget, "fresh delayed motion expired before the owner's silence timeout");
                sync.Correct(10.51);
                TestRunner.Assert(!sync.Get(Id)!.HasTarget, "quiet owner kept steering beyond the receipt timeout");
            }
        });
        runner.Run("authority/future motion survives overtaking its reliable grant", () =>
        {
            using var body = new BodyFixture();
            var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
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

                runner.World.SetContentRoot(effects);
                var ledger = Ledger();
                var replies = order.Select(peer => ledger.Puncture(peer, Puncture(peer), 100 + peer, true)).ToArray();
                TestRunner.Assert(replies[0].Reason == ClaimRefusal.None && replies.Skip(1).All(r => r.Reason == ClaimRefusal.StaleEpoch), "first contender did not win alone");
                var canonical = replies[0].Bodies.Single();
                foreach (var peer in order)
                {
                    var tank = PropaneTank.Create(runner.World);
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
                        var lean = vent.Normal.Rotated(Vector3.Up, Mathf.DegToRad(Tuning.SavedTemplate.VentSwirlDegrees) * vent.Swirl).Normalized();
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
            var puncture = ledger.Puncture(3, Puncture(3), 103, true).Bodies.Single();
            var latest = Pose(Id, 2, 99);
            ledger.Update(3, latest, false);
            var transferred = ledger.Transfer(3, 2).Single();
            TestRunner.Assert(transferred is { Owner: 2, State: TankLedger.State.Venting } && transferred.Body.Epoch == 3 && transferred.Body.Sequence == 0 &&
                transferred.Vent == puncture.Vent && transferred.Body.Transform == latest.Transform, "departure lost the canonical vent or pose");
            TestRunner.Assert(!ledger.Update(3, Pose(Id, 2, 100), true) && ledger.Update(2, Pose(Id, 3, 1), false), "handoff kept the departed stream");
        });
        foreach (var moving in new[] { false, true })
        {
            runner.Run($"authority/new canonical vent reconciles an intact observer (moving {moving})", () =>
            {
                using var f = new MatchFixture(runner);
                var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
                // A relayed blast can already have pushed this intact observer before its child puncture arrives.
                var current = new Transform3D(Basis.Identity, new Vector3(9, 4, 8));
                BodySync.Snap(tank, current, Vector3.One * 20, Vector3.One * 2);
                tank.Sleeping = !moving;
                var puncture = Puncture(3);
                Send(f.Server, 3, Msg.Puncture, puncture);
                f.Deliver();
                TestRunner.Assert(tank.State == PropaneTank.TankState.Venting && tank.HoleLocal == puncture.Hole &&
                    f.Match.Bodies.Get(Id) is { Owner: 3, Epoch: 2 }, "observer did not adopt canonical vent authority");
                TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(moving ? current : puncture.Transform) &&
                    tank.LinearVelocity == (moving ? Vector3.One * 20 : puncture.Linear) &&
                    tank.AngularVelocity == (moving ? Vector3.One * 2 : puncture.Angular), "canonical vent rewound existing motion or failed to initialize a resting observer");
                // Simulate an arbitrary pause before delivery; receipt and correction share manual time.
                f.Server.Clock.Seconds += 30;
                var now = f.Server.Clock.Seconds;
                f.Match.OnMessage(new NetMessage(Msg.BodyStates, f.Server.Id, 3,
                    new BodyStatesMessage(now, new[] { Pose(Id, 2, 1) })));
                TestRunner.Assert(f.Match.Bodies.Get(Id)!.TargetReceived == now, "receipt escaped the manual clock");
                f.Match.Bodies.Correct(now);
                TestRunner.Assert(tank.GlobalTransform.IsEqualApprox(Pose(Id).Transform), "confirmed owner's stream did not reconcile the observer");
            });
        }
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
            var accepted = ledger.Detonate(2, new ExplodeMessage(Id, Vector3.One, Request: 10), 102, true);
            var duplicate = ledger.Detonate(3, new ExplodeMessage(Id, Vector3.Right, Request: 11), 103, true);
            TestRunner.Assert(accepted.Reason == ClaimRefusal.None && duplicate.Reason == ClaimRefusal.Exploded && duplicate.Bodies.Single().Center == Vector3.One && ledger.MapDetonations == 1, "duplicate blast changed the canonical transition or score");
            var child = Puncture(3) with { Id = Other, Cause = 10 };
            TestRunner.Assert(ledger.Puncture(3, child, 103, true).Reason == ClaimRefusal.Cause, "another peer borrowed the chain");
            TestRunner.Assert(ledger.Puncture(2, child, 102, true).Reason == ClaimRefusal.None, "winning root could not start its chain");
            TestRunner.Assert(ledger.Claim(3, Claim(12, Pose(Other, 2)) with { Cause = 11 }, true).Reason == ClaimRefusal.Cause, "refused root granted pushed-body ownership");
        });
        runner.Run("authority/a canceled chain fuse cannot detonate a restored or re-punctured tank", () =>
        {
            var effects = new Node3D();
            runner.AddChild(effects);

            runner.World.SetContentRoot(effects);
            var callbacks = new List<Action>();
            var tank = PropaneTank.Create(runner.World);
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
        runner.Run("authority/queued puncture proposes its current motion after the preceding grant", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            f.Match.Claim(new[] { tank }, atomic: true);
            tank.TakeShot(tank.ToGlobal(new Vector3(.15f, .23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            var current = new Transform3D(Basis.Identity, new Vector3(9, 4, 8));
            BodySync.Snap(tank, current, Vector3.One * 6, Vector3.One * 2);
            BodyAuthority? accepted = null;
            f.Match.AuthorityResolved += reply =>
            {
                if (reply.Reason == ClaimRefusal.None && reply.Bodies.Single().State == TankLedger.State.Venting) { accepted = reply.Bodies.Single(); }
            };
            f.Flush();
            TestRunner.Assert(accepted is { Body.Epoch: 3 } && accepted.Body.Transform.IsEqualApprox(current) &&
                accepted.Body.Linear == Vector3.One * 6 && accepted.Body.Angular == Vector3.One * 2,
                "queued puncture granted a stale prediction pose instead of the motion at send time");
        });
        runner.Run("authority/chain puncture proposes motion after the complete blast push", () =>
        {
            using var f = new MatchFixture(runner);
            var root = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var child = f.Match.LiveTanks.Single(t => t.NetId == Other);
            root.GlobalPosition = new Vector3(0, 60, 0);
            root.Detonate(f.Match.Player.ActorId); // Assign/send the root request, away from other bodies.
            var cause = root.BlastRequest;
            child.ChainHit(child.GlobalPosition - Vector3.Right, 0, cause, f.Match.Player.ActorId);
            child.LinearVelocity = Vector3.One * 6;
            child.AngularVelocity = Vector3.One * 2;
            // This production callback follows all pushes; pending children are excluded from Claim itself.
            f.Match.OnLocalBlast(root.GlobalPosition, new[] { child }, cause);
            BodyAuthority? accepted = null;
            f.Match.AuthorityResolved += reply =>
            {
                accepted = reply.Bodies.FirstOrDefault(b => b.Body.Id == Other && b.State == TankLedger.State.Venting) ?? accepted;
            };
            f.Flush();
            TestRunner.Assert(accepted != null && accepted.Body.Linear == Vector3.One * 6 && accepted.Body.Angular == Vector3.One * 2,
                "chain puncture proposed pre-push motion or was never flushed when all bodies were already pending");
        });
        runner.Run("authority/queued group proposes current motion after its root is sent", () =>
        {
            using var f = new MatchFixture(runner);
            Send(f.Server, 3, Msg.Claim, Claim(20, Pose(Other)));
            f.Deliver();
            var root = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var other = f.Match.LiveTanks.Single(t => t.NetId == Other);
            root.GlobalPosition = new Vector3(0, 60, 0);
            root.TakeShot(root.ToGlobal(new Vector3(.15f, .23f, 0)), root.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            root.Detonate(f.Match.Player.ActorId); // Its explosion waits for the first-shot grant.
            f.Match.Claim(new[] { other }, cause: root.BlastRequest, atomic: true);
            var current = new Transform3D(Basis.Identity, new Vector3(9, 4, 8));
            BodySync.Snap(other, current, Vector3.One * 6, Vector3.One * 2);
            BodyAuthority? accepted = null;
            f.Match.AuthorityResolved += reply =>
            {
                accepted = reply.Bodies.FirstOrDefault(b => b.Body.Id == Other && b.Owner == 2) ?? accepted;
            };
            f.Flush();
            TestRunner.Assert(accepted is { Body.Epoch: 3 } && accepted.Body.Transform.IsEqualApprox(current) &&
                accepted.Body.Linear == Vector3.One * 6 && accepted.Body.Angular == Vector3.One * 2,
                "queued group reused a pose from before its causal root was sent");
        });
        foreach (var broadcastFirst in new[] { false, true })
        {
            runner.Run($"authority/losing puncture preserves motion (broadcast first {broadcastFirst})", () =>
            {
                using var f = new MatchFixture(runner);
                var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
                tank.TakeShot(tank.ToGlobal(new Vector3(.15f, .23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
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
        runner.Run("authority/refused root cancels a chain fuse on an already owned vent", () =>
        {
            using var f = new MatchFixture(runner);
            var root = f.Match.LiveTanks.Single(t => t.NetId == Id);
            var child = f.Match.LiveTanks.Single(t => t.NetId == Other);
            child.TakeShot(child.ToGlobal(new Vector3(.15f, .23f, 0)), child.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            f.Flush();
            TestRunner.Assert(f.Match.Bodies.Get(Other) is { Owner: 2, Epoch: 2 }, "child was not already owned");
            root.GlobalPosition = new Vector3(0, 60, 0); // Isolate manual chain contact from the blast query.
            root.Detonate(f.Match.Player.ActorId);
            Action? callback = null;
            var detonations = 0;
            child.Detonated += _ => detonations++;
            child.FuseScheduleOverride = (_, finish) => callback = finish;
            child.ChainHit(Vector3.Zero, 0, root.BlastRequest, f.Match.Player.ActorId);
            f.Match.OnLocalBlast(root.GlobalPosition, new[] { child }, root.BlastRequest);
            f.Server.Advance(20);
            f.Server.Packets.Clear();
            f.Flush();
            TestRunner.Assert(callback != null && f.Match.LastAuthorityRefusal == ClaimRefusal.WrongPhase,
                "probe did not refuse the root after scheduling the owned child's fuse");
            callback!();
            TestRunner.Assert(child.State == PropaneTank.TankState.Venting && child.ChainCause == 0 && detonations == 0,
                "refused root left an owned child's fuse live");
        });
        runner.Run("authority/refused vent ownership cancels its predicted chain fuse", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.TakeShot(tank.ToGlobal(new Vector3(.15f, .23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            f.Flush();
            Action? callback = null;
            tank.FuseScheduleOverride = (_, finish) => callback = finish;
            tank.ChainHit(Vector3.Zero, 0, 99, f.Match.Player.ActorId);
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
            tank.TakeShot(tank.ToGlobal(new Vector3(0.15f, 0.23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            f.Server.Advance(20);
            f.Server.Packets.Clear();
            f.Flush();
            TestRunner.Assert(f.Match.LastAuthorityRefusal == ClaimRefusal.WrongPhase && tank.State == PropaneTank.TankState.Intact &&
                f.Match.Bodies.Get(Id) is { Epoch: 1, ClaimPending: false }, "refused puncture remained venting");
        });
        runner.Run("gather/refused explosion preserves gathered pile membership", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0, 60, 0));
            tank.MarkPile();
            var entry = f.Match.Bodies.Get(Id)!;
            entry.Sequence = 1;
            Send(f.Server, 2, Msg.BodyRest, new BodyRestMessage(Id, tank.GlobalTransform, 1, entry.Epoch, 1));
            f.Server.Advance(20);
            f.Server.Packets.Clear();
            tank.Detonate(f.Match.Player.ActorId);
            tank.Free();
            f.Flush();
            var restored = f.Match.LiveTanks.Single(t => t.NetId == Id);
            TestRunner.Assert(restored != tank && restored.State == PropaneTank.TankState.Intact && !restored.Loose,
                "refused explosion made a recreated settled tank loose");
        });
        runner.Run("authority/refused predicted detonation recreates the canonical venting body", () =>
        {
            using var f = new MatchFixture(runner);
            var tank = f.Match.LiveTanks.Single(t => t.NetId == Id);
            tank.TakeShot(tank.ToGlobal(new Vector3(0.15f, 0.23f, 0)), tank.GlobalBasis * Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            f.Flush();
            var vent = new VentParameters(tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign);
            // Move away from piles so this probe tests the refused root alone.
            tank.GlobalPosition = new Vector3(0, 60, 0);
            tank.Detonate(f.Match.Player.ActorId);
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
            tank.Detonate(f.Match.Player.ActorId);
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
            tank.ChainHit(Vector3.Zero, 0, 99, f.Match.Player.ActorId);
            f.Match.Claim(new[] { tank }, atomic: true);
            f.Server.Advance(20);
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

    internal sealed class MatchFixture : IDisposable
    {
        public readonly ServerTests.Fixture Server;
        private readonly NetClient client;
        public readonly Match Match;
        private readonly Queue<byte[]> sent = new();
        public MatchFixture(TestRunner runner, Tuning? template = null, int bots = 0)
        {
            Server = new ServerTests.Fixture(bots: bots);
            client = new NetClient(PacketTransports.CreateClient(), Server.Clock) { Template = template ?? Tuning.SavedTemplate };
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            Match = new Match { Net = client, Setup = Server.Setup, Clock = Server.Clock, AuthoritySendOverride = sent.Enqueue };
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
        public void Dispose() { Match.World.Dispose(); Match.Free(); client.Free(); Server.Dispose(); }
    }
}
