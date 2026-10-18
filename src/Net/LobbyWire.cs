using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// The lobby world on the wire. A new world is empty, so its first stock is placed here, as the plan's piles would be,
/// and registered before anyone arrives (arrivals receive every live body in their world start). A refill asks the
/// world authority for clear ground beside its spot, exactly as King asks for a fresh pile; the answer is checked,
/// registered as world tanks it owns and sent to everyone to materialize. A new world authority is asked again. The
/// runner reports each spot's standing stock (<see cref="Stock"/>) for the rules to decide refills.
/// </summary>
public sealed class LobbyWire : IModeWire
{
    /// <summary>How far past its reach and how far above or below its spot a placed tank may stand.</summary>
    private const float PileSlack = 2.5f;

    private readonly Dictionary<int, IReadOnlyList<uint>> stock = new();
    private readonly Dictionary<int, (int Spot, KingPileWanted Pile, int Peer)> wanted = new();
    private int nextRequest;

    /// <summary>The tanks registered at each spot, newest pile only.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<uint>> Piles => stock;

    public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now)
    {
        if (envelope.Payload is KingPilePlaced placed) Place(runner, host, peer, envelope, placed, now);
    }

    public void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case StockPile { Spot: var spot }:
                    var placed = host.SpawnWorldTanks(Pile(host, Spot(runner, spot)));
                    stock[spot] = placed.Select(t => t.Id).ToArray();
                    host.Broadcast(Msg.ModeCommand, runner.Envelope(ModeSchemas.LobbyKinds.PileSpawned, 0, 0,
                        new KingPileSpawned(0, host.WorldAuthority, placed)));
                    Execute(runner, host, runner.Handle(new PileFilled(spot), now), now);
                    break;
                case RefillPile { Spot: var refill }:
                    var tuning = host.WireTuners;
                    nextRequest = nextRequest % ModeSchemas.MaxZone + 1;
                    Ask(runner, host, nextRequest, refill, new KingPileWanted(nextRequest, Spot(runner, refill), tuning.LobbyPileCount,
                        tuning.LobbyPileReach, tuning.LobbyPileClearance));
                    break;
            }
        }
        foreach (var (request, pending) in wanted.ToArray())
        {
            if (pending.Peer != host.WorldAuthority) Ask(runner, host, request, pending.Spot, pending.Pile); // The authority moved on.
        }
    }

    public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }

    /// <summary>Each spot's own tanks still intact within reach of it, and every live tank, from the ledger.</summary>
    public PilesStocked Stock(ModeRunner runner, IModeHost host)
    {
        var spots = (runner.Rules as LobbyRules)?.Spots ?? Array.Empty<ModeSpot>();
        var reach = host.WireTuners.LobbyPileReach;
        var intact = new int[spots.Count];
        for (var i = 0; i < spots.Count; i++)
        {
            if (!stock.TryGetValue(i, out var ids)) continue;
            var spot = Spot(runner, i);
            intact[i] = ids.Count(id => host.Body(id) is { State: TankLedger.State.Intact } body &&
                new Vector2(body.Body.Transform.Origin.X - spot.X, body.Body.Transform.Origin.Z - spot.Z).Length() <= reach);
        }
        return new PilesStocked(intact, host.LiveTanks);
    }

    private static Vector3 Spot(ModeRunner runner, int index)
    {
        var spot = ((LobbyRules)runner.Rules).Spots[index];
        return new Vector3((float)spot.X, (float)spot.Y, (float)spot.Z);
    }

    /// <summary>A square pile standing on the void's floor at a spot, as the Lab and a fresh King pile build one.</summary>
    private static Transform3D[] Pile(IModeHost host, Vector3 at)
    {
        var tuning = host.WireTuners;
        return PileBuilder.Build(PileFormation.Square, tuning.LobbyPileCount,
                tuning.PileSpacing, tuning.PileUprightLayerHeight)
            .Select(t => new Transform3D(t.Basis, t.Origin + at)).ToArray();
    }

    private void Ask(ModeRunner runner, IModeHost host, int request, int spot, KingPileWanted pile)
    {
        wanted[request] = (spot, pile, host.WorldAuthority);
        host.Send(host.WorldAuthority, Msg.ModeCommand, runner.Envelope(ModeSchemas.LobbyKinds.PileWanted, 0, 0, pile));
    }

    private void Place(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, KingPilePlaced placed, double now)
    {
        if (!wanted.TryGetValue(placed.Zone, out var pending) || peer != host.WorldAuthority || !runner.Current(envelope) ||
            !runner.Permits(ModeGate.World, now))
        {
            host.Log($"lobby pile {placed.Zone} from {peer} refused: not the one asked for");
            return;
        }
        var pile = pending.Pile;
        if (placed.Tanks.Count == 0)
        {
            wanted.Remove(placed.Zone);
            host.Log($"lobby pile {placed.Zone} at spot {pending.Spot}: no clear ground within {pile.Reach:0.#} m");
            Execute(runner, host, runner.Handle(new PileUnplaced(pending.Spot), now), now);
            return;
        }
        var spot = pile.Spot;
        if (placed.Tanks.Count != pile.Count || placed.Tanks.Any(t => new Vector2(t.Origin.X - spot.X, t.Origin.Z - spot.Z).Length() > pile.Reach + PileSlack ||
                Math.Abs(t.Origin.Y - spot.Y) > PileSlack))
        {
            wanted.Remove(placed.Zone);
            host.Log($"lobby pile {placed.Zone} from {peer} refused: {placed.Tanks.Count} tanks, not {pile.Count} within reach");
            Execute(runner, host, runner.Handle(new PileUnplaced(pending.Spot), now), now);
            return;
        }
        wanted.Remove(placed.Zone);
        var tanks = host.SpawnWorldTanks(placed.Tanks);
        stock[pending.Spot] = tanks.Select(t => t.Id).ToArray();
        host.Broadcast(Msg.ModeCommand, runner.Envelope(ModeSchemas.LobbyKinds.PileSpawned, 0, 0, new KingPileSpawned(placed.Zone, host.WorldAuthority, tanks)));
        host.Log($"lobby pile {placed.Zone} at spot {pending.Spot}: {tanks.Count} tanks {tanks[0].Id:x}..{tanks[^1].Id:x}");
        Execute(runner, host, runner.Handle(new PileFilled(pending.Spot), now), now);
    }
}
