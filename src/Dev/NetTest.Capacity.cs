using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

public partial class NetTest
{
    private float capacityTime;
    private bool capacityClaimed;
    private bool capacityPunctured;
    private bool capacityExploded;
    private int capacityThrows;
    private int capacityTransfers;
    private Vector3 capacityCenter;
    private uint capacityTank;
    private int capacitySlot;
    private bool capacityQuiesced;
    private double capacityReadyAt;

    private void PrepareCapacity(MatchSetup setup)
    {
        var current = match!;
        capacityTime = 0;
        capacityClaimed = capacityPunctured = capacityExploded = false;
        capacityThrows = capacityTransfers = 0;
        capacityQuiesced = false;
        capacityReadyAt = -1;
        ScenarioAssert(setup.Players.Count == players && setup.Peers.Count == players &&
            setup.Players.Select(p => p.Player.Id).Distinct().Count() == players &&
            setup.Players.Select(p => p.Player.OwnerPeerId).Distinct().Count() == players,
            "capacity: incomplete actor/peer roster");
        ScenarioAssert(setup.HostId == setup.CreatorId && setup.Peers.All(p => p.DebugTuning == (p.PeerId == setup.CreatorId && setup.CreatorDebug)),
            "capacity: frozen authority or capabilities disagree");
        var plan = current.Suburb.Plan;
        var settings = SuburbPlans.Match(current.World.Tuning, players);
        ScenarioAssert(plan.Spawns.Count == players && plan.Tanks.Count == settings.TankCount && plan.AmmoSpots.Count == settings.AmmoSpots,
            "capacity: starts or supplies do not scale with actors");
        for (var i = 0; i < plan.Spawns.Count; i++)
        {
            for (var j = i + 1; j < plan.Spawns.Count; j++)
            {
                ScenarioAssert(plan.Spawns[i].Position.DistanceTo(plan.Spawns[j].Position) + .01f >= settings.StartSpacing,
                    "capacity: unsafe start spacing");
            }
        }

        capacitySlot = setup.Players.Single(p => p.Player.Id == current.Player.ActorId).Spawn;
        capacityCenter = new Vector3(plan.Bounds.End.X + 18, 0.05f, 0);
        capacityTank = BodySync.MapTankId(0);
        current.Player.InputOverride = new ScriptedInput();
        current.Player.Teleport(capacityCenter + Vector3.Right.Rotated(Vector3.Up, capacitySlot * Mathf.Tau / players) * 4, 0);
        current.Player.Ragdolled += (_, _, _) => { capacityThrows++; Log("capacity throw"); };
        current.AuthorityResolved += reply =>
        {
            if (reply.Request == 0 && reply.Bodies.Count > 0)
            {
                capacityTransfers++;
                ScenarioAssert(reply.Bodies.All(b => b.Owner != setup.CreatorId && b.Body.Epoch > 1), "capacity: invalid departure epoch");
                Log($"capacity transfer {reply.Bodies.Count} bodies");
            }
        };
        Log($"capacity roster-map {string.Join(",", setup.Players.Select(p => $"{p.Player.Id}/{p.Player.OwnerPeerId}/{p.Player.LifeId}/{p.Spawn}/{p.Player.AllocationSlot}/{p.Player.Name}/{p.Player.Appearance.Color.ToHtml(false)}"))}");
        Log($"capacity roster actors={players} peers={setup.Peers.Count} revision={setup.RosterRevision} starts={plan.Spawns.Count} tanks={plan.Tanks.Count} ammo={plan.AmmoSpots.Count} inbox={net.InboxPressure.Packets}/{net.InboxPressure.Bytes}B");
    }

    private void CapacityScenario(float dt)
    {
        var current = match!;
        if (!current.IsPlaying) { if (capacityQuiesced) HoldCapacityTanks(current); return; }
        capacityTime = current.Setup.Length - current.TimeLeft; // Stage against the replicated match clock, including delayed loading.
        if (!capacityClaimed)
        {
            capacityClaimed = true;
            if (current.LiveTanks.SingleOrDefault(t => t.NetId == capacityTank) is { } tank)
            {
                // Every owner contests the same body's expected epoch. The authority ledger orders the proposals.
                tank.Freeze = true; // Hold the staged blast near the victims while authority is contested.
                BodySync.Snap(tank, new Transform3D(Basis.Identity, capacityCenter), Vector3.Zero, Vector3.Zero);
                current.Claim(new[] { tank }, atomic: true);
                Log("capacity contested claim");
            }
        }
        // Owners publish their staged poses only after construction. Wait for every actor before firing;
        // the last owner still exercises buffering past the server's loading deadline.
        if (capacityReadyAt < 0 && current.Session.Actors.Count == players &&
            current.Session.Actors.All(a => a.Character.GlobalPosition.DistanceTo(capacityCenter) < 7))
        {
            capacityReadyAt = current.Clock.Seconds;
            Log("capacity all actors loaded");
        }
        if (capacityReadyAt < 0) return;
        var stage = current.Clock.Seconds - capacityReadyAt;
        if (!capacityPunctured && stage >= 2 && capacitySlot < 2)
        {
            capacityPunctured = true;
            if (current.LiveTanks.SingleOrDefault(t => t.NetId == capacityTank) is { State: PropaneTank.TankState.Intact } tank)
            {
                var normal = capacitySlot == 0 ? Vector3.Right : Vector3.Left;
                tank.TakeShot(tank.ToGlobal(PropaneTank.LocalCenter + normal * .15f), normal, -normal, current.Player.ActorId);
                Log("capacity contested puncture");
            }
        }
        if (!capacityExploded && stage >= 5 && capacitySlot == 1)
        {
            capacityExploded = true;
            if (current.LiveTanks.SingleOrDefault(t => t.NetId == capacityTank) is { } tank)
            {
                // The second actor detonates the contested tank; every victim owner resolves the nearby throw.
                tank.TakeShot(tank.ToGlobal(PropaneTank.LocalCenter), Vector3.Right, Vector3.Left, current.Player.ActorId);
                Log("capacity root explosion");
            }
        }
        if (capacityTime >= current.Setup.Length - 10)
        {
            if (!capacityQuiesced)
            {
                capacityQuiesced = true;
                current.Player.InputOverride = new ScriptedInput();
                Log("capacity quiescence: owners hold live tanks and publish their final rest poses");
            }
            // Vents keep moving through results. A shared stationary endpoint makes final position agreement
            // meaningful instead of comparing different wall times during each client's results countdown.
            HoldCapacityTanks(current);
        }
        if (role == "host" && stage >= 14)
        {
            ScenarioAssert(capacityThrows > 0 && net.InboxPressure.Overflows == 0, "capacity: creator proof incomplete before departure");
            Log("capacity creator departure");
            net.LeaveLobby();
            ShutdownGuard.Quit(GetTree(), scenarioFailed ? 1 : 0);
        }
    }

    private void HoldCapacityTanks(Match current)
    {
        foreach (var tank in current.LiveTanks)
        {
            if (current.Bodies.Get(tank.NetId) is not { } entry || entry.ClaimPending) continue;
            if (entry.Owner != net.MyId)
            {
                // Use the production frozen-observer interpolation path. Simply freezing an ordinary observer
                // prevents its velocity correction from closing small errors before the next rest packet.
                if (!tank.Gathering) tank.BeginGather();
                continue;
            }
            tank.EndGather();
            if (tank.Freeze) continue;
            tank.LinearVelocity = tank.AngularVelocity = Vector3.Zero;
            tank.Freeze = true;
            tank.Sleeping = true;
            entry.PublishRequired = true;
            // Godot does not promise a sleeping state for a frozen body. Publish the stationary endpoint reliably
            // rather than depending on that engine flag to trigger BodySync's natural rest detection.
            net.Send(new NetWriter(Msg.BodyRest, current.Setup.Id).UInt(entry.Id).Transform(tank.GlobalTransform)
                .Double(current.Clock.Seconds).UInt(entry.Epoch).UInt(checked(++entry.Sequence)));
        }
    }

    private void FinishCapacity()
    {
        var current = match!;
        ScenarioAssert(current.FinalStandings.Count == players && current.Scores.Count == players &&
            current.FinalStandings.All(s => current.Scores[s.Id] == s.Score), "capacity: final per-actor score agreement failed");
        ScenarioAssert(current.FinalStandings.Count(s => !s.Present) == 1 && current.Session.Actors.Count == players - 1 &&
            current.Bodies.Host != current.Setup.CreatorId && capacityTransfers > 0,
            "capacity: creator departure or authority transfer missing");
        ScenarioAssert(net.InboxPressure.Overflows == 0, "capacity: inbox lost required events");
        // Every victim owner resolves the throw; the delayed owner also proves its loading inbox was used.
        ScenarioAssert(capacityThrows > 0, "capacity: victim-owned throw missing");
        if (DevArgs.Get("name") == $"Bot{players}") ScenarioAssert(net.InboxPressure.Packets > 0, "capacity: delayed-loading inbox was not exercised");
        scenarioPassed = !scenarioFailed;
        Log($"capacity PASS actors={players} throws={capacityThrows} transfers={capacityTransfers} inbox={net.InboxPressure.Packets}/{net.InboxPressure.Bytes}B overflows={net.InboxPressure.Overflows}");
    }
}
