using System;
using System.Linq;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>A completed departure makes the old lobby unrelated; a later failure belongs to the new owner.</summary>
[UnitSuite]
public static class TransitionBoundaryTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("dispatch/create failure after departure preserves the previous lobby", () =>
        {
            using var f = new ServerRunFixture();
            f.Start();
            var previous = f.Runs.Single();
            f.Server.RosterFactory = () => throw new InvalidOperationException("injected new lobby construction failure");
            using var log = new NetLogCapture();
            f.Driver.CreateLobby();
            previous.AssertLive();
            f.Driver.Packets.Clear();
            f.Driver.Advance(1);
            TestRunner.Assert(f.Driver.Packets.Any(p => p.Peer == 3 && p.Message.Type == Msg.Clock), "previous lobby stopped publishing");
            f.Driver.Receive(new NetWriter(Msg.Ping).Double(1));
            TestRunner.Assert(f.Driver.Packets.Last() is { Peer: 2, Message.Type: Msg.Pong }, "departed peer lost service");
            TestRunner.Assert(log.Lines.Any(l => l.Contains("owner failure in lobby none", StringComparison.Ordinal) &&
                l.Contains("injected new lobby construction failure", StringComparison.Ordinal)), "construction failure retained the old owner");
        });
        runner.Run("dispatch/join failure after departure retires only the destination", () =>
        {
            using var f = new ServerRunFixture();
            f.Start();
            var previous = f.Runs.Single();
            var roster = new ParticipantRoster(actorCeiling: 2);
            f.Server.RosterFactory = () => roster;
            f.Server.Worlds = true;
            f.Driver.Connect(4, 2, true);
            f.Driver.CreateLobby(4);
            var destination = f.Runs.Last();
            TestRunner.Assert(destination.Run.IsWorld, "destination needs a live waiting world");
            var target = f.Driver.Packets.Where(p => p.Peer == 4).Select(p => p.Message.Payload).OfType<LobbyInfo>().Last().Id;
            var capture = f.Server.SendOverride!;
            var injected = false;
            f.Server.SendOverride = (peer, bytes, traffic) =>
            {
                capture(peer, bytes, traffic);
                if (injected || peer != 2 || (Msg)bytes[0] != Msg.LobbyList) return;
                // Join already admitted the destination. A synchronous send callback consumes its last identity
                // while the source completes departure, before AddMember assigns client.Lobby to the destination.
                injected = true;
                f.Driver.Receive(MessageCodec.Encode(new NetMessage(Msg.AddActor, 0, 0, new AddActorMessage(1,
                    ActorKind.Bot, "Last", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Server.RosterRevision(4))), false), 4);
            };
            f.Driver.Receive(new NetWriter(Msg.JoinLobby).Int(target));
            TestRunner.Assert(injected && !roster.CanAdd, "join did not reach the intended failure boundary");
            previous.AssertLive();
            destination.AssertOnce();
            f.Driver.Advance(1);
            previous.AssertLive();
        });
    }
}
