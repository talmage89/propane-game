using System;
using System.Collections.Generic;

namespace Propane.Net;

/// <summary>
/// Protocol 2 compatibility bridge: ENet's remote peer id is still the player's wire id. Only this map joins
/// transport handles to game ids. A6 can issue ids here without teaching gameplay about ENet or Steam handles.
/// Each backend binds before Connected and retains bindings through Disconnected; Clear begins a new listen.
/// </summary>
public sealed class ProtocolPeerMap
{
    private readonly Dictionary<ConnectionHandle, int> ids = new();
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
