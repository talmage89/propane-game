using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>The two ends of the connection handshake, as tests send and receive them without a transport.</summary>
internal static class Handshake
{
    /// <summary>A current-version Hello from a client named after its peer id; <paramref name="debug"/> says it is a debug build, whose creator may tune.</summary>
    public static NetWriter Hello(int peer, int colorIndex, bool debug) => new NetWriter(Msg.Hello).Int(Protocol.Version)
        .String(BuildInfo.GameVersion).String("test").String($"Player{peer}").Int(colorIndex).Bool(debug);

    /// <summary>Deliver the server's Welcome to a client, which then knows its peer id.</summary>
    public static void Welcome(NetClient client, int peerId) =>
        client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(peerId, null)), true));
}
