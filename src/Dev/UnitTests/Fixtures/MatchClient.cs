using System;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// A client-side <see cref="Match"/> in a fixture owner's tree over a welcomed, unconnected <see cref="NetClient"/>, with
/// the shared test clock. Disposal frees the match's world, the match and then the client, in that order.
/// </summary>
internal sealed class MatchClient : IDisposable
{
    public readonly NetClient Client;
    public readonly Match Match;

    /// <param name="peerId">The peer id the server's Welcome gave this client.</param>
    /// <param name="template">The client's tuning template; the saved template by default.</param>
    /// <param name="authoritySend">Where the match sends authority requests instead of its client, when a test relays them itself.</param>
    public MatchClient(Node runner, MatchSetup setup, IMonotonicClock clock, int peerId, Tuning? template = null, Action<byte[]>? authoritySend = null,
        bool createLocalView = true)
    {
        Client = new NetClient(PacketTransports.CreateClient(), clock) { Template = template ?? Tuning.SavedTemplate };
        Handshake.Welcome(Client, peerId);
        Match = new Match { Net = Client, Setup = setup, Clock = clock, AuthoritySendOverride = authoritySend, CreateLocalView = createLocalView };
        runner.AddChild(Match);
    }

    public void Dispose() { Match.World.Dispose(); Match.Free(); Client.Free(); }
}
