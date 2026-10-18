using System;
using System.Linq;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class MatchPlayerTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("match player/ordered human preference, fallback, removal and profile replacement", () =>
        {
            using var f = new PlayerRosterFixture(runner);
            f.Check("assembly");
            var first = f.Selected!.ActorId;
            f.Add(f.Profile(40, ActorKind.Bot)); f.Check("bot arrival");
            f.Add(f.Profile(41, ActorKind.Human)); f.Check("second human arrival");
            f.Add(f.Profile(42, ActorKind.Bot)); f.Check("second bot arrival");
            f.Remove(first); f.Check("first human removed");
            Assert(f.Selected!.ActorId == 41, "first bot displaced the remaining human");
            f.Remove(41); f.Check("last human removed");
            Assert(f.Selected!.ActorId == 40, "fallback lost insertion order");
            f.Update(f.Profile(42, ActorKind.Human)); f.Check("bot profile replaced by human");
            f.Update(f.Profile(40, ActorKind.Human)); f.Check("earlier profile replaced by human");
            f.Update(f.Profile(40, ActorKind.Bot)); f.Check("earlier human becomes bot");
            f.Remove(42); f.Check("preferred actor freed");
            f.Remove(40); f.Check("last owned actor freed");
            Assert(f.Selected == null, "empty roster retained a freed actor");
            Throws<InvalidOperationException>(() => _ = f.Match.Player);
            Assert(f.Match.MyScore == 0 && f.Match.LastHitBy == 0 && double.IsNegativeInfinity(f.Match.LastHitTime), "empty roster has local feedback");
            f.Match._Process(0);
            f.Match._PhysicsProcess(0);
            f.Match.OnMessage(new NetMessage(Msg.MatchEnd, f.Match.Setup.Id, 0, Array.Empty<Standing>()));
            f.Add(f.Profile(43, ActorKind.Bot)); f.Check("bot after empty roster");
            f.Add(f.Profile(44, ActorKind.Human)); f.Check("human after bot");
            f.Remove(43); f.Check("fallback removed before human");
            f.Match.OnMessage(new NetMessage(Msg.PlayerLeft, f.Match.Setup.Id, 0, new PlayerLeftMessage(2, 3)));
            f.Check("owned peer departure");
            f.Match._Process(0);
            f.Match.Session.Dispose();
            Assert(f.Selected == null, "retired session retained its player");
        });
        runner.Run("match player/bot-only assembly gives the first owned bot its view", () =>
        {
            using var f = new PlayerRosterFixture(runner, botsFirst: true, botsOnly: true);
            f.Check("bot-only assembly");
            Assert(f.Selected!.ActorId == 40 && f.Selected.View != null, "bot-only fallback or initial view changed");
            var view = f.Selected.View!;
            f.Add(f.Profile(99, ActorKind.Human)); f.Check("human arrives after bot-only assembly");
            Assert(f.Selected!.ActorId == 99 && f.Selected.View == null && view.Actor.ActorId == 40, "roster refresh retargeted the existing camera/input view");
        });
        runner.Run("match player/authority handover preserves selection; rebuild applies actor ownership", () =>
        {
            using (var f = new PlayerRosterFixture(runner, botsFirst: true))
            {
                f.Check("bots before human");
                var before = f.Selected;
                var remote = f.Match.RemoteBodies.Single();
                f.Match.Bodies.Host = remote.OwnerPeerId;
                f.Check("remote world authority");
                f.Match.OnMessage(new NetMessage(Msg.PlayerLeft, f.Match.Setup.Id, 0, new PlayerLeftMessage(remote.OwnerPeerId, 2)));
                f.Check("peer departure and authority handover");
                Assert(f.Match.Bodies.Host == 2 && ReferenceEquals(before, f.Selected), "world authority handover changed the selected actor");
            }
            using var rebuilt = new PlayerRosterFixture(runner, handover: true);
            rebuilt.Check("rebuilt roster with ownership handed over");
            Assert(rebuilt.Selected!.OwnerPeerId == 2 && rebuilt.Match.Session.OwnedActors.Count() == 1, "rebuild did not apply ownership");
        });
    }

}
