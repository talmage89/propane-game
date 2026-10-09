using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.Net;

/// <summary>
/// The connection: an ENet host (the server) or an ENet client, sending whole messages as reliable or unreliable
/// packets. Used directly rather than through Godot's RPCs, so the server can route every message itself.
/// For testing, it can hold packets back to simulate lag, jitter and loss.
/// </summary>
public sealed class NetTransport
{
    public const int ServerId = 1;

    private readonly ENetMultiplayerPeer peer = new();
    private readonly List<Pending> incoming = new();
    private readonly List<Pending> outgoing = new();
    private readonly Random random = new();
    private readonly HashSet<int> peers = new();
    private double lastReliableIn;
    private double lastReliableOut;
    private bool open;

    private readonly record struct Pending(double At, int Peer, byte[] Data, bool Reliable);

    /// <summary>A peer finished connecting (on a client, the server, id 1).</summary>
    public event Action<int>? PeerConnected;

    public event Action<int>? PeerDisconnected;

    public event Action<int, byte[]>? Received;

    /// <summary>Simulated one-way delay (seconds) added on this side, in each direction.</summary>
    public double SimulatedLag { get; set; }

    /// <summary>Simulated random extra delay (seconds) on top of the lag.</summary>
    public double SimulatedJitter { get; set; }

    /// <summary>Fraction (0..1) of unreliable packets dropped, for testing.</summary>
    public double SimulatedLoss { get; set; }

    public bool IsServer { get; private set; }

    public int UniqueId => open ? peer.GetUniqueId() : 0;

    public bool IsConnected => open && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    public NetTransport()
    {
        peer.PeerConnected += id =>
        {
            peers.Add((int)id);
            PeerConnected?.Invoke((int)id);
        };
        peer.PeerDisconnected += id =>
        {
            peers.Remove((int)id);
            PeerDisconnected?.Invoke((int)id);
        };
    }

    public Error Host(int port, int maxClients)
    {
        var error = peer.CreateServer(port, maxClients);
        IsServer = true;
        open = error == Error.Ok;
        return error;
    }

    public Error Connect(string address, int port)
    {
        var error = peer.CreateClient(address, port);
        IsServer = false;
        open = error == Error.Ok;
        if (open)
        {
            // Notice a dead server in seconds, not the ENet default of half a minute.
            peer.GetPeer(ServerId)?.SetTimeout(32, 3000, 8000);
        }
        return error;
    }

    public void Close()
    {
        if (open)
        {
            open = false;
            peer.Close();
        }
        incoming.Clear();
        outgoing.Clear();
        peers.Clear();
    }

    /// <summary>Drops one client (server only).</summary>
    public void Kick(int id)
    {
        if (open && IsServer)
        {
            peer.DisconnectPeer(id);
        }
    }

    public void Send(int to, byte[] data, bool reliable)
    {
        if (!open)
        {
            return;
        }
        if (SimulatedLag <= 0 && SimulatedJitter <= 0 && SimulatedLoss <= 0)
        {
            Put(to, data, reliable);
            return;
        }
        if (!reliable && random.NextDouble() < SimulatedLoss)
        {
            return;
        }
        outgoing.Add(new Pending(DelayedTime(reliable, ref lastReliableOut), to, data, reliable));
    }

    public void Send(int to, NetWriter message, bool reliable) => Send(to, message.ToArray(), reliable);

    public void Poll()
    {
        if (!open)
        {
            return;
        }
        Flush(outgoing, p => Put(p.Peer, p.Data, p.Reliable));
        peer.Poll();
        while (open && peer.GetAvailablePacketCount() > 0)
        {
            var from = peer.GetPacketPeer();
            var mode = peer.GetPacketMode();
            var data = peer.GetPacket();
            if (data.Length == 0)
            {
                continue;
            }
            if (SimulatedLag <= 0 && SimulatedJitter <= 0)
            {
                Received?.Invoke(from, data);
            }
            else
            {
                var reliable = mode == MultiplayerPeer.TransferModeEnum.Reliable;
                incoming.Add(new Pending(DelayedTime(reliable, ref lastReliableIn), from, data, reliable));
            }
        }
        Flush(incoming, p => Received?.Invoke(p.Peer, p.Data));
    }

    /// <summary>Round-trip time to the server in milliseconds, as ENet measures it (client only).</summary>
    public double RoundTripMs
    {
        get
        {
            if (!open || IsServer)
            {
                return 0;
            }
            var server = peer.GetPeer(ServerId);
            return server == null ? 0 : server.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) + SimulatedLag * 2000;
        }
    }

    private void Put(int to, byte[] data, bool reliable)
    {
        // A peer that has gone (or is going) cannot take packets.
        if (!open || !peers.Contains(to) || peer.GetPeer(to) is not { } target || target.GetState() != ENetPacketPeer.PeerState.Connected)
        {
            return;
        }
        peer.SetTargetPeer(to);
        // Channel 0 picks ENet's own reliable or unreliable channel for the mode.
        peer.TransferChannel = 0;
        peer.TransferMode = reliable ? MultiplayerPeer.TransferModeEnum.Reliable : MultiplayerPeer.TransferModeEnum.Unreliable;
        peer.PutPacket(data);
    }

    private double DelayedTime(bool reliable, ref double lastReliable)
    {
        var at = Now + SimulatedLag + random.NextDouble() * SimulatedJitter;
        if (reliable)
        {
            // Reliable messages keep their order.
            at = Math.Max(at, lastReliable);
            lastReliable = at;
        }
        return at;
    }

    private static void Flush(List<Pending> queue, Action<Pending> deliver)
    {
        if (queue.Count == 0)
        {
            return;
        }
        var now = Now;
        var ready = queue.FindAll(p => p.At <= now);
        if (ready.Count == 0)
        {
            return;
        }
        queue.RemoveAll(p => p.At <= now);
        // A stable sort, so reliable messages with equal times keep their order.
        foreach (var p in ready.OrderBy(p => p.At))
        {
            deliver(p);
        }
    }

    public static double Now => Time.GetTicksUsec() / 1_000_000.0;
}
