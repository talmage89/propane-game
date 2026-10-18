using Propane.Core;

namespace Propane.Net;

/// <summary>The only production backend selection. Nodes also accept injected transports before entering the tree.</summary>
public static class PacketTransports
{
    public static ServerTransport CreateServer()
    {
        var ids = new ProtocolPeerMap();
        return new ServerTransport(WithFaults(new NetTransport(ids)), ids);
    }

    // A1 moves the default client construction into the application root. Explicitly injected clients need neither
    // this backend factory nor catalog lookup; the default remains source compatible during that parallel work.
    internal static PacketCodec ClientCodec() => Modes.ModeRegistry.Codec;

    public static IPacketTransport CreateClient()
    {
        return WithFaults(new NetTransport(new ProtocolPeerMap()));
    }

    private static IPacketTransport WithFaults(IPacketTransport transport)
    {
        var args = LaunchArgs.Current;
        return FaultsFrom(args) is { } faults
            ? new FaultPacketTransport(transport, MonotonicClock.Instance, FaultSeed(args), faults)
            : transport;
    }

    /// <summary>
    /// The faults the <c>--net-lag</c>, <c>--net-jitter</c>, <c>--net-reliable-stall</c> and <c>--net-body-stall</c>
    /// (milliseconds) and <c>--net-loss</c> (percent) launch options ask for, or null when none is positive.
    /// </summary>
    internal static PacketFaults? FaultsFrom(LaunchArgs args)
    {
        var lag = args.GetFloat("net-lag", 0) / 2000.0;
        var jitter = args.GetFloat("net-jitter", 0) / 1000.0;
        var loss = args.GetFloat("net-loss", 0) / 100.0;
        var stall = args.GetFloat("net-reliable-stall", 0) / 1000.0;
        var bodyStall = args.GetFloat("net-body-stall", 0) / 1000.0;
        return lag > 0 || jitter > 0 || loss > 0 || stall > 0 || bodyStall > 0
            ? new PacketFaults(lag, jitter, loss, stall, bodyStall)
            : null;
    }

    /// <summary>The <c>--net-seed</c> launch option: the seed of the fault random stream.</summary>
    internal static int FaultSeed(LaunchArgs args) => (int)args.GetFloat("net-seed", 1);
}
