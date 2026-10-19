using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class SendRetirementTests
{
    public static void Register(TestRunner runner)
    {
        using var source = new ServerFixture();
        // Each entry reaches the real origin, codec and connection Send; the transport faults only that send.
        (string Name, Action<SendRetirementFixture> Run)[] origins =
        {
            ("pickup iteration", f =>
            {
                var pickup = f.Match.Pickups.First();
                pickup.GlobalPosition = f.Match.Player.GlobalPosition;
                pickup.Reveal(0);
                f.Match.Player.Ammo!.Reserve = 0;
                ((ClientActorSet)ActorLifetimeFixture.Field(f.Match, "actorReplication")).CheckPickups(f.Match.Player);
            }),
            ("shot", f => ActorLifetimeFixture.Call(f.Match, "OnFired", f.Match.Player, new ShotReport())),
            ("melee", f => ActorLifetimeFixture.Call(f.Match, "SendActor", f.Match.Player, Msg.MeleeStart,
                new MeleeStartMessage(1, Vector3.Forward), true)),
            ("ragdoll and mode spill", f => ActorLifetimeFixture.Call(f.Match, "OnRagdolled", f.Match.Player,
                Vector3.Zero, Vector3.Zero, Vector3.Zero)),
            ("mode adapter", f => ((IModeClient)ActorLifetimeFixture.Field(f.Match, "clientMode")).Thrown(f.Match.Player, Vector3.Zero)),
            ("melee contact", f => ActorLifetimeFixture.Call(f.Match, "OnMeleeHit", f.Match.Player,
                new MeleeHit(1, f.Match.RemoteBodies.First()))),
            ("get up", f => ((Action<Vector3, float>)typeof(PlayerCharacter).GetField("GotUp",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Match.Player)!)(Vector3.Zero, 0)),
            ("loaded acknowledgement", f => f.Client.Send(new NetWriter(Msg.Loaded, f.Match.Setup.Id))),
            ("body publication", f => ActorLifetimeFixture.Call(f.Match, "SendBodies", 0.0)),
            ("actor publication", f => ActorLifetimeFixture.Call(f.Match, "SendPlayerState", f.Match.Player, 0.0)),
            ("authority", f => f.Match.Claim(new GodotObject[] { f.Match.LiveTanks.First() }, atomic: true)),
            ("gather publication", f => ActorLifetimeFixture.Call(f.Match, "PublishGather", f.Match.LiveTanks.First(), GatherPhase.Glide)),
            ("tuning panel", f =>
            {
                var panel = f.Match.GetNode<Propane.UI.TuningPanel>("Tuning");
                ((Action<string, Variant>)typeof(Propane.UI.TuningPanel).GetField("ValueChanged",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!)(
                    nameof(Tuning.BlastRadius), f.Match.World.Tuning.BlastRadius);
            }),
            ("create lobby", f => f.Client.CreateLobby()),
            ("join lobby", f => f.Client.JoinLobby(1)),
            ("leave lobby menu", f => f.Client.LeaveLobby()),
            ("profile menu", f => f.Client.SetProfile("changed", AppearanceRecord.FromPalette(1))),
            ("capacity menu", f => f.Client.SetCapacity(6)),
            ("length menu", f => f.Client.SetMatchLength(20)),
            ("mode menu", f => f.Client.SetMode(ModeSettings.Default)),
            ("start menu", f => f.Client.StartMatch()),
            ("cancel menu", f => f.Client.CancelStart()),
            ("add actor", f => f.Client.AddActor(ActorKind.Bot, "bot", AppearanceRecord.FromPalette(1), ControllerConfig.Bot)),
            ("update actor", f => f.Client.UpdateActor(f.Match.Player.ActorId, "changed", AppearanceRecord.FromPalette(1), ControllerConfig.Human)),
            ("remove actor", f => f.Client.RemoveActor(f.Match.Player.ActorId)),
        };
        foreach (var (name, origin) in origins)
        {
            if (DevArgs.Get("send-origin") is { } only && !name.Contains(only, StringComparison.Ordinal)) continue;
            foreach (var throws in new[] { false, true })
            {
                runner.Run($"send retirement/{name} {(throws ? "throw" : "failure")}", () =>
                {
                    using var f = new SendRetirementFixture(runner, source.Setup, source.Clock, createLocalView: name == "tuning panel");
                    using var log = new NetLogCapture();
                    // Seed mode state through its normal decoded command before arming a transport fault.
                    var adapter = (IModeClient)ActorLifetimeFixture.Field(f.Match, "clientMode");
                    adapter.Apply(new ModeEnvelope(1, 1, ModeRegistry.TankBankId, 1, f.Match.Player.ActorId, 1,
                        1, ModeSchemas.TankBankKinds.Score, new TankBankScore(8, 0)));
                    if (throws) f.Transport.Failure = new InvalidOperationException("injected origin " + name);
                    else f.Transport.Result = SendResult.Backpressure;
                    f.OriginRunning = true;
                    origin(f);
                    f.OriginRunning = false;
                    TestRunner.Assert(f.Transport.Faults == 1, "origin did not reach the injected transport send");
                    f.AssertLive(); // Continuations may finish only while their borrowed native state is live.
                    f.Client._Process(0);
                    // Disposable returned failures retain the connection; thrown sends always retire it.
                    if (name == "actor publication" && !throws)
                    {
                        f.AssertLive();
                    }
                    else
                    {
                        f.AssertRetired();
                        TestRunner.Assert(log.Lines.Any(line => line.Contains(throws ? "injected origin " + name : "Backpressure", StringComparison.Ordinal)),
                            "real transport failure cause was not logged");
                    }
                });
            }
        }
        if (DevArgs.Get("send-origin") == null)
        {
            Boundaries(runner);
            runner.Run("send retirement/run inbox stays owner-controlled through waiting", () => Connected((client, _) =>
            {
                var setup = source.Setup;
                var lobby = new LobbyInfo(1, 2, 10, LobbyPhase.Playing, setup.Players.Select(p => p.Player).ToArray()) { Peers = new[] { 2, 3 } };
                client.Handle(client.Codec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby), true));
                client.Handle(client.Codec.Encode(new NetMessage(Msg.MatchStart, setup.Id, 0, setup), true));
                var received = 0;
                TestRunner.Assert(client.AttachMatch(setup.Id, _ => received++), "run receiver did not attach");
                client.Handle(client.Codec.Encode(new NetMessage(Msg.MatchEnd, setup.Id, 0, setup.Players.Select(p =>
                    new Standing(p.Player.Id, p.Player.Name, p.Player.Appearance, 0, true)).ToArray()), true));
                client.Handle(client.Codec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with { Phase = LobbyPhase.Waiting }), true));
                client.Handle(new NetWriter(Msg.Clock, setup.Id).Float(0).ToArray());
                TestRunner.Assert(client.ActiveMatchId == setup.Id && received == 2, "waiting retired ordinary results traffic before its scene owner");
                ActorLifetimeFixture.Call(client, "RetireMatchInbox", setup.Id);
                TestRunner.Assert(client.ActiveMatchId == 0, "failed run's inbox survived explicit retirement");
                var next = setup with { Id = setup.Id + 1 };
                client.Handle(client.Codec.Encode(new NetMessage(Msg.MatchStart, next.Id, 0, next), true));
                ActorLifetimeFixture.Call(client, "RetireMatchInbox", setup.Id);
                TestRunner.Assert(client.ActiveMatchId == next.Id, "old retirement cleared a newer run's inbox");
            }));
        }
    }

    private static void Boundaries(TestRunner runner)
    {
        foreach (var traffic in Enum.GetValues<TrafficClass>())
        {
            foreach (var result in new[] { SendResult.Closed, SendResult.Oversized, SendResult.Backpressure })
            {
                runner.Run($"send boundary/{traffic}/{result}", () => Connected((client, transport) =>
                {
                    var retired = 0;
                    client.StatusChanged += (status, _) => { if (status == NetClient.Status.Offline) retired++; };
                    transport.Result = result;
                    TestRunner.Assert(client.Send(new byte[] { (byte)Msg.Ping }, traffic) == result, "send result changed");
                    TestRunner.Assert(retired == 0 && client.State == (traffic.IsReliable() ? NetClient.Status.Offline : NetClient.Status.Connected),
                        "send changed its immediate failure observation or notified retirement inside its caller");
                    if (traffic.IsReliable())
                    {
                        TestRunner.Assert(client.Send(new byte[] { (byte)Msg.Ping }) == SendResult.Closed && transport.Faults == 1,
                            "pending failure accepted another send");
                    }
                    client._PhysicsProcess(0);
                    TestRunner.Assert(client.State == (traffic.IsReliable() ? NetClient.Status.Offline : NetClient.Status.Connected),
                        "wrong reliable/disposable retirement boundary");
                }));
            }
        }
        runner.Run("send boundary/loaded bookkeeping throw", () => Connected((client, transport) =>
        {
            using var log = new NetLogCapture();
            transport.LoadingFailure = true;
            TestRunner.Assert(client.Send(new NetWriter(Msg.Loaded, 1)) == SendResult.Closed && client.State == NetClient.Status.Offline,
                "loading throw escaped or retired the sender");
            client._Process(0);
            TestRunner.Assert(client.State == NetClient.Status.Offline && log.Lines.Any(l => l.Contains("injected loading send failure", StringComparison.Ordinal)),
                "loading throw did not log its cause and retire");
        }));
        runner.Run("send boundary/disconnect cancels pending failure", () => Connected((client, transport) =>
        {
            transport.Result = SendResult.Backpressure;
            client.CreateLobby();
            client.Disconnect();
            client.Connect("", 1234, "replacement", 0);
            client._Process(0);
            Handshake.Welcome(client, 2);
            client._Process(0);
            TestRunner.Assert(client.State == NetClient.Status.Connected && client.LastDisconnect == null,
                "old failure retired the replacement connection");
        }));
    }

    private static void Connected(Action<NetClient, InjectedPacketTransport> test)
    {
        var network = new LoopbackNetwork();
        var listener = network.Create();
        var transport = new InjectedPacketTransport(network.Create());
        var client = new NetClient(transport, ModeRegistry.Codec);
        try
        {
            listener.Listen(new PacketEndpoint("", 1234), 2);
            client._Ready();
            client.Connect("", 1234, "probe", 0);
            client._Process(0);
            listener.Poll();
            Handshake.Welcome(client, 2);
            test(client, transport);
        }
        finally { client._ExitTree(); client.Free(); listener.Close(); }
    }
}

/// <summary>Opt-in paired whole-callback cost with successful connected sends and bounded delivery queues.</summary>
[UnitSuite]
public static class SendRetirementBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("send benchmark/connected callback", () =>
        {
            if (DevArgs.Get("send-bench") == null) return;
            using var source = new ServerFixture();
            using var f = new SendRetirementFixture(runner, source.Setup, source.Clock, createLocalView: true);
            var packets = 0L;
            var payload = 0L;
            // Observe accepted packets at the actual adapter, then drain its peer once per complete callback.
            void Tick()
            {
                f.Advance(1d / 120);
                f.Client._PhysicsProcess(1d / 120);
                f.Match._PhysicsProcess(1d / 120);
                f.Match._Process(1d / 120);
                f.Drain();
            }
            for (var i = 0; i < 3000; i++) Tick();
            var before = f.Client.Diagnostics;
            const int ticks = 20000;
            var samples = new double[ticks];
            var clock = MonotonicClock.Instance;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < ticks; i++)
            {
                var start = clock.Seconds;
                Tick();
                samples[i] = (clock.Seconds - start) * 1_000_000;
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var after = f.Client.Diagnostics;
            packets = after.PacketsOut - before.PacketsOut;
            payload = after.BytesOut - before.BytesOut;
            f.AssertLive();
            TestRunner.Assert(packets > ticks / 10 && f.Transport.Faults == 0, "successful send benchmark workload missing");
            var mean = samples.Average();
            Array.Sort(samples);
            GD.Print(FormattableString.Invariant($"[send-bench] ticks={ticks} actors={f.Match.Session.Actors.Count} bodies={f.Match.Bodies.Entries.Count()} packets={packets} payload={payload} bytes_per_tick={bytes / (double)ticks:F2} mean_us={mean:F3} p95_us={samples[(int)(ticks * .95) - 1]:F3} p99_us={samples[(int)(ticks * .99) - 1]:F3}"));
        });
    }
}
