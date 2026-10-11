namespace Propane.Net;

/// <summary>The only production backend selection. Nodes also accept injected transports before entering the tree.</summary>
public static class PacketTransports
{
    public static ServerTransport CreateServer()
    {
        var ids = new ProtocolPeerMap();
        return new ServerTransport(WithFaults(new NetTransport(ids)), ids);
    }

    public static IPacketTransport CreateClient()
    {
        return WithFaults(new NetTransport(new ProtocolPeerMap()));
    }

    private static IPacketTransport WithFaults(IPacketTransport transport)
    {
        var lag = Dev.DevArgs.GetFloat("net-lag", 0) / 2000.0;
        var jitter = Dev.DevArgs.GetFloat("net-jitter", 0) / 1000.0;
        var loss = Dev.DevArgs.GetFloat("net-loss", 0) / 100.0;
        var stall = Dev.DevArgs.GetFloat("net-reliable-stall", 0) / 1000.0;
        if (lag > 0 || jitter > 0 || loss > 0 || stall > 0)
        {
            transport = new FaultPacketTransport(transport, MonotonicClock.Instance,
                (int)Dev.DevArgs.GetFloat("net-seed", 1), new PacketFaults(lag, jitter, loss, stall));
        }
        return transport;
    }
}
