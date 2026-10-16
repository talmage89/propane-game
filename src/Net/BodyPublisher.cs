using System;
using System.Collections.Generic;

namespace Propane.Net;

/// <summary>
/// Session-owned scratch buffers for the owner's body stream. Collect once, group in first-encounter order,
/// then sample poses at serialization time. Sends are synchronous; even retirement during a send retains the
/// collected entries and advances their sequences, while the caller suppresses subsequent delivery.
/// </summary>
internal sealed class BodyPublisher(BodySync bodies, int matchId, Func<double, double> sampleTime, Action<NetWriter, TrafficClass> send)
{
    private sealed class Stream(TrafficClass traffic)
    {
        public readonly TrafficClass Traffic = traffic;
        public readonly List<BodySync.Entry> Entries = new();
    }

    private readonly List<BodySync.Entry> moving = new();
    private readonly List<BodySync.Entry> rested = new();
    private readonly Stream[] streams =
    {
        new(TrafficClass.UnreliableBodyStates),
        new(TrafficClass.ReliableBodyStates),
        new(TrafficClass.ReliableBodyStates2),
        new(TrafficClass.ReliableBodyStates3),
    };
    private readonly List<Stream> encounterOrder = new(4);

    public void Publish(double now, IReadOnlySet<uint> pendingCreation)
    {
        try
        {
            bodies.Collect(now, moving, rested, pendingCreation);
            // Build every group before the first send, as GroupBy did. A synchronous callback may mutate
            // an entry's lane or pose; membership is fixed here, but poses remain live until serialized.
            foreach (var entry in moving)
            {
                var traffic = entry.PublishReliably ? entry.CheckpointTraffic : TrafficClass.UnreliableBodyStates;
                var stream = streams[traffic switch
                {
                    TrafficClass.UnreliableBodyStates => 0,
                    TrafficClass.ReliableBodyStates => 1,
                    TrafficClass.ReliableBodyStates2 => 2,
                    TrafficClass.ReliableBodyStates3 => 3,
                    _ => throw new InvalidOperationException("invalid body publication traffic"),
                }];
                if (stream.Entries.Count == 0) encounterOrder.Add(stream);
                stream.Entries.Add(entry);
            }
            foreach (var stream in encounterOrder)
            {
                var entries = stream.Entries;
                for (var start = 0; start < entries.Count; start += Protocol.MaxBodyStates)
                {
                    var count = Math.Min(Protocol.MaxBodyStates, entries.Count - start);
                    var writer = new NetWriter(Msg.BodyStates, matchId).Double(sampleTime(now)).UShort((ushort)count);
                    for (var i = start; i < start + count; i++)
                    {
                        var entry = entries[i];
                        writer.UInt(entry.Id).Transform(entry.Body.GlobalTransform).Vec3(entry.Body.LinearVelocity).Vec3(entry.Body.AngularVelocity)
                            .UInt(entry.Epoch).UInt(checked(++entry.Sequence));
                    }
                    send(writer, stream.Traffic);
                }
            }
            foreach (var entry in rested)
            {
                send(new NetWriter(Msg.BodyRest, matchId).UInt(entry.Id).Transform(entry.Body.GlobalTransform).Double(now)
                    .UInt(entry.Epoch).UInt(checked(++entry.Sequence)), TrafficClass.ReliableOrdered);
            }
        }
        finally
        {
            // Keep capacity, not native wrappers, after publication (including an interrupted send).
            moving.Clear();
            rested.Clear();
            foreach (var stream in streams) stream.Entries.Clear();
            encounterOrder.Clear();
        }
    }
}
