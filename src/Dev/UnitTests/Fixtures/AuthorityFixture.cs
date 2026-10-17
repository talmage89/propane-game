using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// A <see cref="ServerFixture"/> match and the client of its peer 2 as a real <see cref="Match"/>, with the authority
/// requests it sends held in a queue that <see cref="Flush"/> relays to the server, and the server's replies handed back
/// by <see cref="Deliver"/>. Tests decide when each leg runs.
/// </summary>
internal sealed class AuthorityFixture : IDisposable
{
    public readonly ServerFixture Server;
    private readonly MatchClient client;
    public readonly Match Match;
    private readonly Queue<byte[]> sent = new();

    public AuthorityFixture(TestRunner runner, Tuning? template = null, int bots = 0)
    {
        Server = new ServerFixture(bots: bots);
        client = new MatchClient(runner, Server.Setup, Server.Clock, 2, template, sent.Enqueue);
        Match = client.Match;
    }

    public void Deliver()
    {
        var messages = Server.Packets.Where(p => p.Peer == 2 && p.Message.Type == Msg.Authority).Select(p => p.Message).ToArray();
        Server.Packets.Clear();
        foreach (var message in messages) { Match.OnMessage(message); }
    }

    public void Flush()
    {
        while (sent.TryDequeue(out var packet)) { Server.Receive(packet); Deliver(); }
    }

    public void Dispose() { client.Dispose(); Server.Dispose(); }
}
