using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Tank;

namespace Propane.Net;

/// <summary>
/// Mode-adapter-owned pile placement/birth mechanics shared by King and Lobby. Borrows the physical replica until
/// adapter retirement. Placement/reporting is synchronous; retry scheduling remains entirely on the server.
/// </summary>
internal sealed class ClientModePiles : IDisposable
{
    private IClientModeReplica replica;
    private readonly ClientPileView view;
    public ClientModePiles(IClientModeReplica replica, ClientPileView view) { this.replica = replica; this.view = view; }

    public void PlacePile(KingPileWanted wanted)
    {
        if (replica.SelectedActor is not { } reporter) return;
        var actors = replica.Actors.Select(a => a.Character.GlobalPosition).ToArray();
        var site = PileSite.Find(replica.World, wanted.Spot, wanted.Count, wanted.Reach, wanted.Clearance, actors);
        if (site == null) GD.Print($"[match] no clear ground for zone {wanted.Zone}'s pile within {wanted.Reach:0.#} m");
        replica.SendMode(reporter, checked(++replica.Replication(reporter.Runtime).ActionSequence), ModeSchemas.KingKinds.PilePlaced,
            new KingPilePlaced(wanted.Zone, site ?? Array.Empty<Transform3D>()));
    }

    public void SpawnPile(KingPileSpawned spawned)
    {
        var built = new List<PropaneTank>();
        foreach (var body in spawned.Tanks)
        {
            if (replica.HasTank(body.Id) || replica.Exploded(body.Id)) continue;
            var tank = PropaneTank.Create(replica.World);
            tank.NetId = body.Id;
            tank.Name = $"Zone{spawned.Zone}_{body.Id:x}";
            replica.AddTank(tank);
            tank.GlobalTransform = body.Transform;
            tank.Sleeping = true;
            tank.MarkPile();
            replica.TrackTank(tank, spawned.Owner);
            built.Add(tank);
        }
        if (built.Count > 0) replica.MaterializePile(view, built, spawned.Tanks);
    }

    public void Dispose() => replica = null!;
}
