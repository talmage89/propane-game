using System;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// A <see cref="ServerFixture"/> match and the client of its peer 3 as a real <see cref="Match"/>, to which tests deliver
/// relayed messages from peer 2 directly. Its authority requests go to its unconnected client, not back to the server.
/// </summary>
internal sealed class GatherFixture : IDisposable
{
    private readonly ServerFixture server = new();
    private readonly MatchClient client;
    public Match Match { get; }
    public int Id => server.Id;

    public GatherFixture(TestRunner runner)
    {
        client = new MatchClient(runner, server.Setup, server.Clock, 3);
        Match = client.Match;
    }

    public void Receive(Msg type, object payload) => Match.OnMessage(new NetMessage(type, Id, 2, payload));

    public void Dispose() { client.Dispose(); server.Dispose(); }
}
