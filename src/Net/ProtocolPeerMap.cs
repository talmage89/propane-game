using System;
using System.Collections.Generic;

namespace Propane.Net;

/// <summary>Server-issued peer ids, independent of backend connection ids. Bind supports injected transports.</summary>
public sealed class ProtocolPeerMap
{
    private readonly Dictionary<ConnectionHandle, int> ids = new();
    private readonly IdentitySequence sequence = new(2);
    public int Issue(ConnectionHandle connection)
    {
        var id = sequence.Allocate();
        Bind(connection, id);
        return id;
    }
    private readonly Dictionary<int, ConnectionHandle> connections = new();
    public void Bind(ConnectionHandle connection, int peerId)
    {
        if (peerId <= 0 || ids.ContainsKey(connection) || connections.ContainsKey(peerId))
        {
            throw new ArgumentException("duplicate or invalid protocol peer binding");
        }
        ids.Add(connection, peerId);
        connections.Add(peerId, connection);
    }
    public int PeerId(ConnectionHandle connection) => ids[connection];
    public ConnectionHandle? Connection(int peerId) => connections.GetValueOrDefault(peerId);
    public void Retire(ConnectionHandle connection)
    {
        if (ids.TryGetValue(connection, out var id) && connections.GetValueOrDefault(id) == connection)
        {
            connections.Remove(id);
        }
    }
    public void Forget(ConnectionHandle connection) => ids.Remove(connection);
    public void Clear() { connections.Clear(); }
}

public sealed record ServerTransport(IPacketTransport Transport, ProtocolPeerMap PeerIds);
