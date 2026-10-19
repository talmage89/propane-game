using System;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Throws at the real adapter call, either before acceptance or after the inner transport accepted bytes.</summary>
internal sealed class InjectedPacketTransport(IPacketTransport inner) : IPacketTransport
{
    public Exception? Failure;
    public bool AfterAcceptance;
    public bool Persistent;
    public ConnectionHandle? FailureConnection;
    public Action? OnFailure;
    public bool CleanupFailure;
    public int AcceptedBeforeFailure, Disconnects, Closes;
    public event Action<ConnectionHandle>? Connected { add => inner.Connected += value; remove => inner.Connected -= value; }
    public event Action<ConnectionHandle, DisconnectReason>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
    public event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet { add => inner.Packet += value; remove => inner.Packet -= value; }
    public int MaxPayloadSize => inner.MaxPayloadSize;
    public OpenResult Listen(PacketEndpoint endpoint, int maxConnections) => inner.Listen(endpoint, maxConnections);
    public OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection) => inner.Connect(endpoint, out connection);
    public void Poll() => inner.Poll();
    public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
    {
        if (Failure is not { } failure || FailureConnection != null && FailureConnection != connection) return inner.Send(connection, traffic, bytes);
        if (!Persistent) Failure = null;
        if (AfterAcceptance)
        {
            TestRunner.Assert(inner.Send(connection, traffic, bytes) == SendResult.Success, "failure probe did not reach acceptance");
            AcceptedBeforeFailure++;
        }
        OnFailure?.Invoke();
        throw failure;
    }
    public void SetLoading(ConnectionHandle connection, bool loading) => inner.SetLoading(connection, loading);
    public void Disconnect(ConnectionHandle connection, DisconnectReason reason)
    {
        Disconnects++;
        inner.Disconnect(connection, reason);
        if (CleanupFailure) throw new InvalidOperationException("injected disconnect cleanup failure");
    }
    public void Close()
    {
        Closes++;
        inner.Close();
        if (CleanupFailure) throw new InvalidOperationException("injected close cleanup failure");
    }
    public TransportDiagnostics Diagnostics(ConnectionHandle connection) => inner.Diagnostics(connection);
}
