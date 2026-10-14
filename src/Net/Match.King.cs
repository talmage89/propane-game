using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Fx;
using Propane.Modes;
using Propane.Tank;

namespace Propane.Net;

public partial class Match
{
    private KingState? kingState;
    private KingZone? kingZone;

    /// <summary>
    /// The mode's own state as this peer knows it now, for the HUD, bots and harnesses: King's zone, from the server's
    /// latest Zone snapshot read at this peer's round clock. Retained for results; null before the first snapshot.
    /// </summary>
    public IModeView? ModeView => kingState != null ? kingState.At(Math.Max(0, Setup.Length - timeLeft)) : null;

    /// <summary>The King zone's presenter, when this peer has a view and plays King.</summary>
    private void BuildKingZone()
    {
        if (mode.Id != ModeRegistry.KingId || !CreateLocalView) return;
        kingZone = new KingZone
        {
            World = World,
            Name = "KingZone",
            State = () => IsPlaying ? ModeView as KingState : null,
            ActorColor = id => profiles.TryGetValue(id, out var profile) ? profile.Appearance.Color : null,
        };
        suburb.AddChild(kingZone);
    }

    /// <summary>King's own payloads; its bank arrives as Tank Bank's. False when the payload is not King's.</summary>
    private bool OnKing(object payload)
    {
        switch (payload)
        {
            case KingState state:
                kingState = state;
                return true;
            case KingPileWanted wanted:
                PlacePile(wanted);
                return true;
            case KingPileSpawned spawned:
                SpawnPile(spawned);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// This peer is the world authority, which the server asks to place each fresh pile because only peers have
    /// physics: the nearest clear, flat ground beside the zone's new spot, or none. It reports as one of its actors.
    /// </summary>
    private void PlacePile(KingPileWanted wanted)
    {
        var actors = Session.Actors.Select(a => a.Character.GlobalPosition).ToArray();
        var site = PileSite.Find(World, wanted.Spot, wanted.Count, wanted.Reach, wanted.Clearance, actors);
        if (site == null) GD.Print($"[match] no clear ground for zone {wanted.Zone}'s pile within {wanted.Reach:0.#} m");
        var reporter = player;
        Net.Send(MessageCodec.Encode(new NetMessage(Msg.ModeEvent, Setup.Id, 0, new ModeEnvelope(round, worldRevision, mode.Id, mode.SchemaVersion,
            reporter.ActorId, reporter.LifeId, checked(++reporter.Runtime.ActionSequence), ModeSchemas.KingKinds.PilePlaced,
            new KingPilePlaced(wanted.Zone, site ?? Array.Empty<Transform3D>()))), fromServer: false));
    }

    /// <summary>A fresh pile the server registered: standing as placed, owned by the world authority, materializing.</summary>
    private void SpawnPile(KingPileSpawned spawned)
    {
        var built = new List<PropaneTank>();
        foreach (var body in spawned.Tanks)
        {
            if (tanks.ContainsKey(body.Id) || exploded.Contains(body.Id)) continue;
            var tank = PropaneTank.Create(World);
            tank.NetId = body.Id;
            tank.Name = $"Zone{spawned.Zone}_{body.Id:x}";
            suburb.AddTank(tank);
            tank.GlobalTransform = body.Transform;
            tank.Sleeping = true;
            tank.MarkPile();
            Track(tank, spawned.Owner);
            built.Add(tank);
        }
        if (built.Count > 0) kingZone?.Materialize(built, PileSite.Center(spawned.Tanks.Select(t => t.Transform).ToArray()));
    }
}
