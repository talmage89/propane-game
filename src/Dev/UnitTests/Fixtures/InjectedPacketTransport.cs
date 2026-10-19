using System;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Fails at the real adapter call; throws may occur before or after the inner transport accepts bytes.</summary>
internal sealed class InjectedPacketTransport(IPacketTransport inner) : IPacketTransport
{
    public Exception? Failure;
    public SendResult? Result;
    public int Faults;
    public bool AfterAcceptance;
    public bool Persistent;
    public ConnectionHandle? FailureConnection;
    public Msg? FailureType = null;
    public Action? OnFailure;
    public bool CleanupFailure;
    public bool LoadingFailure;
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
        if (Failure == null && Result == null || FailureConnection != null && FailureConnection != connection ||
            FailureType != null && (bytes.IsEmpty || (Msg)bytes.Span[0] != FailureType))
        {
            return inner.Send(connection, traffic, bytes);
        }

        var failure = Failure;
        var result = Result;
        if (!Persistent) Failure = null;
        if (!Persistent) Result = null;
        Faults++;
        if (AfterAcceptance)
        {
            TestRunner.Assert(inner.Send(connection, traffic, bytes) == SendResult.Success, "failure probe did not reach acceptance");
            AcceptedBeforeFailure++;
        }
        OnFailure?.Invoke();
        if (failure != null) throw failure;
        return result!.Value;
    }
    public void SetLoading(ConnectionHandle connection, bool loading)
    {
        inner.SetLoading(connection, loading);
        if (LoadingFailure) throw new InvalidOperationException("injected loading send failure");
    }
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
