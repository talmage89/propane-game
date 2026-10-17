using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Core;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// A real LobbyServer with two connected peers (2 creates the lobby, 3 joins it) and a started match, by default already
/// playing, driven through the production receive handler and a manual clock. It counts a lobby's own packets, so the
/// lobby world is off, and the lobby world has its own fixture.
/// </summary>
internal sealed class ServerFixture : IDisposable
{
    public readonly ManualClock Clock = new();
    public readonly LobbyServer Server;
    public readonly Tuning Tuning = new() { CountdownTime = 0, ResultsTime = 1 };
    public readonly List<(int Peer, NetMessage Message)> Packets;
    public readonly int Id;
    public readonly MatchSetup Setup;
    private readonly ServerDriver driver;
    private readonly ActionSequence actions = new();

    public ServerFixture(bool debug = true, bool playing = true, int bots = 0, Func<ParticipantRoster>? rosterFactory = null, ModeDefinition? mode = null, ModeSettings? modeSettings = null, float length = 10, int creatorTankSupply = 1)
    {
        // These fixtures count a lobby's own packets; the lobby world has its own suite.
        Server = new LobbyServer(PacketTransports.CreateServer(), Clock) { StartScore = 4, LobbyMode = mode ?? ModeRegistry.TankBank, Worlds = false };
        if (rosterFactory != null) Server.RosterFactory = rosterFactory;
        driver = new ServerDriver(Clock, Server);
        Packets = driver.Packets;
        for (var peer = 2; peer <= 3; peer++) driver.Connect(peer, peer - 2, peer == 2 ? debug : true);
        driver.CreateLobby();
        driver.Join(3);
        for (var i = 0; i < bots; i++)
        {
            Receive(MessageCodec.Encode(new NetMessage(Msg.AddActor, 0, 0,
            new AddActorMessage((uint)i + 1, ActorKind.Bot, $"Bot{i}", AppearanceRecord.FromPalette(i), ControllerConfig.Bot, Revision())), false));
        }

        if (modeSettings != null) Receive(MessageCodec.Encode(new NetMessage(Msg.SetMode, 0, 0, modeSettings), false));
        Receive(new NetWriter(Msg.SetMatchLength).Float(length));
        Tuning.TankCountMultiplier = creatorTankSupply;
        driver.StartMatch(Tuning);
        Setup = Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
        Id = Setup.Id;
        if (playing)
        {
            Receive(new NetWriter(Msg.Loaded, Id));
            Receive(new NetWriter(Msg.Loaded, Id), peer: 3);
            Advance();
            Advance();
            TestRunner.Assert(Packets.Any(p => p.Message.Type == Msg.MatchGo), "fixture never reached play");
        }
        Packets.Clear();
    }

    /// <summary>The current roster revision of peer 2's lobby, for roster requests.</summary>
    public uint Revision(int peer = 2) => Server.RosterRevision(peer);

    public void Advance(double seconds = 0) => driver.Advance(seconds);

    public void Receive(NetWriter packet, int peer = 2) => Receive(packet.ToArray(), peer);
    public int ActorId(int peer) => Setup.Players.First(p => p.Player.OwnerPeerId == peer).Player.Id;
    public void Receive(byte[] packet, int peer = 2) => driver.Receive(Prepare(packet, peer), peer);
    private byte[] Prepare(byte[] packet, int peer)
    {
        // Legacy payload probes use this fixture's default owner. Production and spoof tests send explicit ids.
        if (Setup != null && Setup.Players.Any(p => p.Player.OwnerPeerId == peer) && packet.Length >= 17 && Protocol.IsActorMessage((Msg)packet[0]) && BitConverter.ToInt32(packet, 5) == 0)
        {
            packet = packet.ToArray();
            BitConverter.GetBytes(ActorId(peer)).CopyTo(packet, 5);
            BitConverter.GetBytes(1u).CopyTo(packet, 9);
            BitConverter.GetBytes(actions.Next(peer)).CopyTo(packet, 13);
        }
        return packet;
    }
    public void Reject(byte[] bytes, int peer = 2)
    {
        Packets.Clear();
        bytes = Prepare(bytes, peer);
        Receive(bytes, peer);
        if (Packets.Count == 1 && Packets[0] is { Message.Payload: AuthorityMessage { Reason: not ClaimRefusal.None } authority } && Packets[0].Peer == peer)
        {
            var request = MessageCodec.Decode(bytes, false).Payload switch
            {
                ClaimMessage claim => claim.Request,
                PunctureMessage puncture => puncture.Request,
                ExplodeMessage explosion => explosion.Request,
                _ => throw new InvalidOperationException("non-claim received an authority refusal"),
            };
            TestRunner.Assert(authority.Request == request && authority.Peer == peer, "refusal changed its request identity");
            return;
        }
        if (Packets.Count == 1 && ModeWireFixtures.Refused(Packets[0].Message) is { } reply && Packets[0].Peer == peer)
        {
            // Only a fully decoded spill may receive a refusal. Malformed packets must emit nothing.
            var drop = (MessageCodec.Decode(bytes, false).Payload as ModeEnvelope)?.Payload as TankBankSpill;
            TestRunner.Assert(drop != null && reply.SequenceEqual(drop.Tanks.Select(t => t.Id)), "refusal changed the predicted ids");
        }
        else
        {
            TestRunner.Assert(Packets.Count == 0, $"invalid {(bytes.Length > 0 ? ((Msg)bytes[0]).ToString() : "empty")} mutated or relayed {Packets.Count} packets");
        }
    }
    private uint NextAction(int peer) => actions.Next(peer);
    /// <summary>A spill ticket from this peer's actor, sharing its action sequence with its other actor messages.</summary>
    public byte[] Drop(IReadOnlyList<BodyState> tanks, byte reason = 1, int peer = 2) => MessageCodec.Encode(
        ModeWireFixtures.Spill(Id, ActorId(peer), 1, NextAction(peer), (SpillReason)reason, tanks), false);
    /// <summary>A spill envelope whose declared count is invalid, with no bodies following.</summary>
    public byte[] RawSpill(byte reason, int count, int peer = 2) => new NetWriter(Msg.ModeEvent, Id).UInt(1).UInt(1).Byte(ModeRegistry.TankBankId)
        .UShort(1).Int(ActorId(peer)).UInt(1).UInt(NextAction(peer)).UShort(ModeSchemas.TankBankKinds.Spill).UShort(2).Byte(reason).Byte((byte)count).ToArray();
    public void AssertScore(int player, int score, int delta) => TestRunner.Assert(Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: TankBankScore s } e &&
        e.ActorId == ActorId(player) && s.Score == score && s.Delta == delta), $"expected score {score}, delta {delta}");
    public void Dispose() { Server.Free(); Tuning.Dispose(); }
}
