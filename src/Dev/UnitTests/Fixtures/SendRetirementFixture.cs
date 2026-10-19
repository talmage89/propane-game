using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Connected client with a native session. Offline observers retire it just as Main does; the witness
/// remains readable after disposal. Tests invoke a whole send origin before servicing the next network boundary.</summary>
internal sealed class SendRetirementFixture : IDisposable
{
    private readonly ManualClock clock;
    private readonly IPacketTransport listener;
    public readonly InjectedPacketTransport Transport;
    public readonly NetClient Client;
    public readonly Match Match;
    public bool OriginRunning;
    public bool RetiredInsideOrigin { get; private set; }
    public int Retirements { get; private set; }

    public SendRetirementFixture(Node owner, MatchSetup setup, ManualClock clock, bool createLocalView = false)
    {
        this.clock = clock;
        var network = new LoopbackNetwork();
        listener = network.Create();
        Transport = new InjectedPacketTransport(network.Create());
        Client = new NetClient(Transport, ModeRegistry.Codec, clock);
        listener.Listen(new PacketEndpoint("", 1234), 2);
        Client._Ready();
        Client.Connect("", 1234, "probe", 0);
        Client._Process(0);
        listener.Poll();
        Handshake.Welcome(Client, 2);
        Client.Handle(Client.Codec.Encode(new NetMessage(Msg.LobbyState, 0, 0,
            new LobbyInfo(1, 2, 10, LobbyPhase.Playing, setup.Players.Select(p => p.Player).ToArray()) { Peers = new[] { 2, 3 } }), true));
        Client.Handle(Client.Codec.Encode(new NetMessage(Msg.MatchStart, setup.Id, 0, setup), true));
        Match = new Match { Net = Client, Setup = setup, Clock = clock, CreateLocalView = createLocalView };
        owner.AddChild(Match);
        Match.OnMessage(new NetMessage(Msg.MatchGo, setup.Id, 0, new FloatMessage(10)));
        Client.StatusChanged += (status, _) =>
        {
            if (status != NetClient.Status.Offline) return;
            Retirements++;
            RetiredInsideOrigin |= OriginRunning;
            Match.Session.Dispose();
        };
    }

    public void AssertLive() => TestRunner.Assert(!RetiredInsideOrigin && Retirements == 0 && Match.World.IsAlive && Match.Session.Actors.Count > 0,
        "send origin continued after synchronous session retirement");
    public void AssertRetired() => TestRunner.Assert(Retirements == 1 && !Match.World.IsAlive && Match.Session.Actors.Count == 0 &&
        Client.State == NetClient.Status.Offline && Client.ActiveMatchId == 0 && Client.Lobby == null && Client.LastDisconnect?.Kind == DisconnectKind.TransportFailure,
        "failed send did not retire through the normal connection/session cleanup exactly once");
    public void Drain() => listener.Poll();
    public void Advance(double dt) => clock.Seconds += dt;
    public void Dispose()
    {
        Match.Session.Dispose();
        Match.Free();
        Client._ExitTree();
        Client.Free();
        listener.Close();
    }
}
