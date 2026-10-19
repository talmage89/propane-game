using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class DispatchTransportTests
{
    private static readonly PacketEndpoint Endpoint = new("", 1234);
    public static void Register(TestRunner runner)
    {
        foreach (var faulted in new[] { false, true })
        {
            foreach (var after in new[] { false, true })
            {
                runner.Run($"dispatch/client send failure (faults {faulted}, after mutation/acceptance {after})", () =>
                {
                    var network = new LoopbackNetwork();
                    var clock = new ManualClock();
                    var server = network.Create();
                    var raw = network.Create();
                    IPacketTransport adapter = faulted ? new FaultPacketTransport(raw, clock, 3, new PacketFaults(.01, .01)) : raw;
                    var transport = new InjectedPacketTransport(adapter);
                    var client = new NetClient(transport, ModeRegistry.Codec, clock);
                    using var log = new NetLogCapture();
                    try
                    {
                        server.Listen(Endpoint, 2);
                        client._Ready();
                        client.Connect("", Endpoint.Port, "before", 0);
                        client._Process(0);
                        server.Poll();
                        Handshake.Welcome(client, 2);
                        var retired = 0;
                        client.StatusChanged += (status, _) => { if (status == NetClient.Status.Offline) throw new InvalidOperationException("injected offline observer failure"); };
                        client.StatusChanged += (status, _) => { if (status == NetClient.Status.Offline) retired++; };
                        client.NoticeReceived += _ =>
                        {
                            if (after) client.SetProfile("after", AppearanceRecord.FromPalette(1));
                            else client.Send(new NetWriter(Msg.Ping).Double(0));
                            throw new InvalidOperationException("continued after failed send");
                        };
                        transport.AfterAcceptance = after;
                        transport.Failure = new InvalidDataException("injected client send failure");
                        transport.CleanupFailure = true;
                        client.Handle(new NetWriter(Msg.Notice).String("probe").ToArray());
                        TestRunner.Assert(client.PlayerName == (after ? "after" : "before"), "send failure changed the mutation boundary");
                        TestRunner.Assert(client.State == NetClient.Status.Offline && client.ActiveMatchId == 0 && retired == 1,
                            "client failed to retire through its offline cleanup observers");
                        TestRunner.Assert(transport.AcceptedBeforeFailure == (after ? 1 : 0) && transport.Disconnects == 1 && transport.Closes == 1,
                            "client failed to reach send/cleanup boundaries");
                        AssertCause(log, "injected client send failure");
                        TestRunner.Assert(log.Lines.Any(l => l.Contains("disconnect cleanup failure", StringComparison.Ordinal)) &&
                            log.Lines.Any(l => l.Contains("close cleanup failure", StringComparison.Ordinal)) &&
                            log.Lines.Any(l => l.Contains("offline observer failure", StringComparison.Ordinal)), "secondary cleanup cause lost");
                        TestRunner.Assert(!log.Lines.Any(l => l.Contains("continued after failed send", StringComparison.Ordinal)), "failed handler continued");
                    }
                    finally
                    {
                        transport.CleanupFailure = false;
                        client._ExitTree(); client.Free(); server.Close();
                    }
                });
                runner.Run($"dispatch/server send failure (faults {faulted}, after mutation/acceptance {after})", () =>
                {
                    var network = new LoopbackNetwork();
                    var clock = new ManualClock();
                    var raw = network.Create();
                    IPacketTransport adapter = faulted ? new FaultPacketTransport(raw, clock, 3, new PacketFaults(.01, .01)) : raw;
                    var transport = new InjectedPacketTransport(adapter);
                    var peers = Enumerable.Range(0, 3).Select(_ => network.Create()).ToArray();
                    var server = ModeRegistry.CreateServer(new ServerTransport(transport, raw.PeerIds), clock);
                    server.Worlds = false;
                    var runs = new List<ServerRunFixture.Release>();
                    server.RunCreated = run => runs.Add(new ServerRunFixture.Release(server, run));
                    using var tuning = new Tuning { CountdownTime = 0, ResultsTime = 1, LobbyStageTime = 0 };
                    using var log = new NetLogCapture();
                    try
                    {
                        transport.Listen(Endpoint, 8);
                        foreach (var peer in peers) { peer.Connect(Endpoint, out _); peer.Poll(); }
                        transport.Poll();
                        var driver = new ServerDriver(clock, server);
                        for (var peer = 2; peer <= 4; peer++) driver.Connect(peer, peer - 2, true);
                        driver.CreateLobby(); driver.Join(3); driver.StartMatch(tuning);
                        var failed = runs.Single();
                        driver.Receive(new NetWriter(Msg.Loaded, failed.Run.Id));
                        driver.Receive(new NetWriter(Msg.Loaded, failed.Run.Id), 3);
                        driver.Advance(); driver.Advance();
                        driver.CreateLobby(4); driver.StartMatch(tuning, 4);
                        var unrelated = runs.Last();
                        driver.Receive(new NetWriter(Msg.Loaded, unrelated.Run.Id), 4);
                        driver.Advance(); driver.Advance();
                        var otherClocks = 0;
                        peers[2].Packet += (_, _, bytes) =>
                        {
                            var message = MessageCodec.Decode(bytes.ToArray(), true);
                            if (message.Type == Msg.Clock && message.MatchId == unrelated.Run.Id) otherClocks++;
                        };
                        server.SendOverride = null; // The real adapter call, not the handler fixture's packet sink.
                        var before = failed.Tuning.BlastRadius;
                        var atFailure = float.NaN;
                        transport.OnFailure = () => atFailure = failed.Tuning.BlastRadius;
                        transport.FailureConnection = raw.PeerIds.Connection(after ? 3 : 2);
                        transport.Persistent = !after;
                        transport.AfterAcceptance = after;
                        transport.Failure = new InvalidDataException("injected server send failure");
                        if (after) driver.Receive(new NetWriter(Msg.TuningSet, failed.Run.Id).String(nameof(Tuning.BlastRadius)).Scalar(before + 1));
                        else driver.Receive(new NetWriter(Msg.Ping).Double(0));
                        TestRunner.Assert(atFailure == before + (after ? 1 : 0), "server send did not reach the intended mutation boundary");
                        failed.AssertOnce(); unrelated.AssertLive();
                        TestRunner.Assert(transport.AcceptedBeforeFailure == (after ? 1 : 0) && transport.Disconnects == 0,
                            "server failure dropped an innocent peer or missed acceptance");
                        AssertCause(log, "injected server send failure");
                        driver.Advance(1); unrelated.AssertLive();
                        transport.Failure = null;
                        clock.Seconds += 1;
                        transport.Poll();
                        foreach (var peer in peers) peer.Poll();
                        TestRunner.Assert(otherClocks > 0, "unrelated lobby stopped publishing clocks");
                        TestRunner.Assert(raw.PeerIds.Connection(2) != null && raw.PeerIds.Connection(3) != null && raw.PeerIds.Connection(4) != null,
                            "failure retired a healthy transport handle");
                        var received = 0;
                        peers[0].Packet += (_, _, bytes) => { if ((Msg)bytes.Span[0] == Msg.Pong) received++; };
                        driver.Receive(new NetWriter(Msg.Ping).Double(1));
                        clock.Seconds += 1; transport.Poll(); peers[0].Poll();
                        TestRunner.Assert(received == 1, "affected lobby's peer no longer receives healthy traffic");
                    }
                    finally { server._ExitTree(); server.Free(); foreach (var peer in peers) peer.Close(); }
                });
            }
        }
        foreach (var replay in new[] { false, true })
        {
            runner.Run($"dispatch/client inbox failure (replay {replay})", () =>
            {
                using var f = new ServerFixture();
                var client = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec);
                using var log = new NetLogCapture();
                try
                {
                    Handshake.Welcome(client, 2);
                    var lobby = new LobbyInfo(1, 2, 10, LobbyPhase.Waiting, f.Setup.Players.Select(p => p.Player).ToArray()) { Peers = new[] { 2, 3 } };
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby), true));
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchStart, f.Id, 0, f.Setup), true));
                    var packet = new NetWriter(Msg.MatchGo, f.Id).Float(10).ToArray();
                    var calls = 0;
                    void Throw(NetMessage _) { calls++; throw new EndOfStreamException("injected inbox handler failure"); }
                    if (replay)
                    {
                        client.Handle(packet); client.Handle(packet);
                        TestRunner.Assert(!client.AttachMatch(f.Id, Throw), "failed replay claimed to attach");
                    }
                    else { TestRunner.Assert(client.AttachMatch(f.Id, Throw), "live receiver did not attach"); client.Handle(packet); }
                    TestRunner.Assert(calls == 1 && client.State == NetClient.Status.Offline && client.ActiveMatchId == 0,
                        "failed receiver/queue survived retirement or replay continued");
                    AssertCause(log, "injected inbox handler failure");
                    log.Lines.Clear();
                    Handshake.Welcome(client, 2);
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby), true));
                    var next = f.Setup with { Id = f.Id + 1 };
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchStart, next.Id, 0, next), true));
                    TestRunner.Assert(client.AttachMatch(next.Id, _ => throw new MatchInbox.OverflowException()), "overflow receiver did not attach");
                    client.Handle(new NetWriter(Msg.MatchGo, next.Id).Float(10).ToArray());
                    TestRunner.Assert(log.Lines.Any(l => l.Contains("owner failure", StringComparison.Ordinal) && l.Contains("OverflowException", StringComparison.Ordinal)),
                        "receiver exception was mistaken for queue admission overflow");
                }
                finally { client.Free(); }
            });
        }
    }
    private static void AssertCause(NetLogCapture log, string cause)
    {
        TestRunner.Assert(log.Lines.Any(l => l.Contains("owner failure", StringComparison.Ordinal) && l.Contains(cause, StringComparison.Ordinal)), "real failure cause lost");
        TestRunner.Assert(!log.Lines.Any(l => l.Contains("bad message", StringComparison.Ordinal) || l.Contains("bad Ping", StringComparison.Ordinal) ||
            l.Contains("bad TuningSet", StringComparison.Ordinal)), "handler/send failure was blamed on the peer");
    }
}
