using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Player;
using Propane.Session;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ActorLifetimeTests
{
    public static void Register(TestRunner runner)
    {
        foreach (var (roaming, king) in new[] { (true, false), (false, false), (false, true) })
        {
            runner.Run($"actor lifetime/{(roaming ? "roaming" : "match")} repeated removal releases actor state", () =>
            {
                using var f = new ActorLifetimeFixture(runner, roaming, king);
                var parent = DevArgs.Get("actor-lifetime-parent") != null;
                var before = f.Counts();
                for (var i = 0; i < 6; i++)
                {
                    var references = f.Cycle(100 + i, i % 2 == 0 ? ActorKind.Human : ActorKind.Bot, i % 3 == 2 ? 3 : 2);
                    var after = f.Counts();
                    Godot.GD.Print($"[actor-lifetime] roaming={roaming} cycle={i} {after}");
                    Assert(parent ? before != after : before == after, $"actor container expectation failed: {before} -> {after}");
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Assert(parent ? references.Any(reference => reference.IsAlive) : references.All(reference => !reference.IsAlive), "actor collection expectation failed in the live session");
                    Assert(f.Match.Session.IsAlive, "collection test retired its session");
                }
                var remaining = f.RetireRemaining();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(remaining.All(reference => !reference.IsAlive), "session retirement retained remaining actors/runtimes");
                Assert(f.Match.Session.Actors.Count == 0, "session retirement retained actors");
            });
        }

        runner.Run("actor lifetime/removal in a delegate snapshot suppresses the detached Match listener", () =>
        {
            using var f = new ActorLifetimeFixture(runner, true);
            var actor = f.Add(100, ActorKind.Human);
            var fired = typeof(PlayerCharacter).GetField("Fired", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var prior = (Action<ShotReport>)fired.GetValue(actor)!;
            var calls = 0;
            Action<ShotReport> remove = _ => { calls++; f.Remove(actor); };
            fired.SetValue(actor, remove + prior);
            var sequence = actor.Runtime.ActionSequence;
            ((Action<ShotReport>)fired.GetValue(actor)!)(new ShotReport(Vector3.Zero, Vector3.One, Vector3.Up, Vector3.Forward, null, false));
            Assert(calls == 1 && !GodotObject.IsInstanceValid(actor), "snapshot listener did not retire its emitter");
            Assert(actor.Runtime.ActionSequence == sequence, "detached listener sent an action from the retired actor");
            f.Match.Session.RemoveActor(actor.Runtime);
        });
        runner.Run("actor lifetime/removal in the actor's real damage/death callback", () =>
        {
            using var f = new ActorLifetimeFixture(runner, true);
            var actor = f.Add(100, ActorKind.Human);
            actor.Ragdolled += (_, _, _) => f.Remove(actor);
            actor.ReceiveMelee(Vector3.Forward);
            Assert(!GodotObject.IsInstanceValid(actor) && f.Match.Session.IsAlive, "damage callback did not remove its actor in the live world");
        });
        runner.Run("actor lifetime/removal in the actor's real firing and get-up callbacks", () =>
        {
            using var f = new ActorLifetimeFixture(runner, true);
            var actor = f.Add(100, ActorKind.Human);
            actor.InputLocked = false;
            actor.Runtime.IntentSource = new FireInput();
            actor.Fired += _ => f.Remove(actor);
            actor._PhysicsProcess(1d / 120);
            Assert(!GodotObject.IsInstanceValid(actor), "physics input did not fire/remove its actor");
            actor = f.Add(101, ActorKind.Human);
            actor.Collapse();
            actor.GotUp += (_, _) => f.Remove(actor);
            ActorLifetimeFixture.Call(actor, "BeginGetUp");
            Assert(!GodotObject.IsInstanceValid(actor), "get-up callback did not remove its actor");
        });
        runner.Run("actor lifetime/removal while another world notification iterates", () =>
        {
            using var f = new ActorLifetimeFixture(runner, true);
            PlayerCharacter actor = null!;
            var after = false;
            f.Match.World.Events.Explosion += _ => f.Remove(actor);
            actor = f.Add(100, ActorKind.Human);
            f.Match.World.Events.Explosion += _ => after = true;
            f.Match.World.Events.RaiseExplosion(Vector3.Zero);
            Assert(after && !GodotObject.IsInstanceValid(actor) && f.Match.Session.IsAlive, "notification stopped or retained the removed actor");
        });
        runner.Run("actor lifetime/removal during session unsubscription keeps reverse callback order", () =>
        {
            using var f = new ActorLifetimeFixture(runner, true);
            var actor = f.Add(100, ActorKind.Human);
            var log = new List<string>();
            f.Match.Session.OnUnsubscribing(() => log.Add("first"));
            f.Match.Session.OnUnsubscribing(() => { log.Add("remove"); f.Match.Session.RemoveActor(actor.Runtime); });
            f.Match.Session.OnUnsubscribing(() => log.Add("last"));
            f.Match.Session.OnStopping(() => { log.Add("stop"); f.Match.Session.RemoveActor(f.Match.Session.Actors.Last()); });
            f.Match.Session.Dispose();
            f.Match.Session.Dispose();
            Assert(log.SequenceEqual(new[] { "last", "remove", "first", "stop" }), "removing an unsubscription entry skipped/reordered a callback");
            Assert(ActorLifetimeFixture.Count(f.Match.Session, "registrations") == 0, "session retained registrations");
        });
        runner.Run("actor lifetime/failing and reentrant attachment cleanup still frees native actors", () =>
        {
            using var f = new ActorLifetimeFixture(runner, true);
            var session = f.Match.Session;
            var actor = session.AddActor(new PlayerCharacter { World = f.Match.World, ActorId = 100 });
            var detached = 0;
            var removed = 0;
            session.AttachActor(actor, () => { removed++; session.RemoveActor(actor); throw new InvalidOperationException("remove failure"); });
            session.SubscribeActor(actor, () => { detached++; throw new InvalidOperationException("detach failure"); });
            var registration = session.Registration(actor);
            try { session.RemoveActor(actor); Assert(false, "cleanup errors were swallowed"); }
            catch (AggregateException errors) { Assert(errors.Flatten().InnerExceptions.Count == 2, "one attachment failure was lost"); }
            registration.Dispose();
            session.RemoveActor(actor);
            Assert(detached == 1 && removed == 1 && !GodotObject.IsInstanceValid(actor.Character), "cleanup repeated or skipped native free");
            Assert(session.IsAlive, "attachment error stopped the surviving world");
        });
    }

    private sealed class FireInput : IPlayerInputSource
    {
        public PlayerIntent Read() => new() { FirePressed = true };
    }
}
