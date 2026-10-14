using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Tank;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// Server-side existence, authority and lifecycle, never physics. Cached poses are reported by owners; exploded
/// entries remain tombstones. Claims compare expected epochs and mutate the entire group only after validation.
/// </summary>
public sealed class TankLedger
{
    public enum State { Intact, Venting, Exploded }
    private readonly Dictionary<uint, BodyAuthority> bodies = new();
    private readonly Dictionary<(int Peer, uint Request), int> explosions = new();
    private readonly Dictionary<uint, (uint Epoch, uint Sequence)> gathers = new();
    private int worldTanks; // Map-range tanks so far, the plan's first; a mode's fresh piles follow them.

    public TankLedger(SuburbPlan plan, int host)
    {
        worldTanks = plan.Tanks.Count;
        for (var i = 0; i < plan.Tanks.Count; i++)
        {
            var tank = plan.Tanks[i];
            Register(new BodyState(BodySync.MapTankId(i), new Transform3D(new Basis(Vector3.Up, tank.Yaw),
                Suburb.ToWorld(tank.Position, tank.Elevation + 0.01f)), Vector3.Zero, Vector3.Zero), host, rest: true);
        }
        for (var i = 0; i < plan.Props.Count; i++)
        {
            var prop = plan.Props[i];
            if (!prop.Model.StartsWith("car:")) { continue; }
            Register(new BodyState(BodySync.CarId(i), new Transform3D(new Basis(Vector3.Up, prop.Yaw),
                Suburb.ToWorld(prop.Position, prop.Elevation)), Vector3.Zero, Vector3.Zero), host, rest: true);
        }
    }

    public int MapDetonations { get; private set; }
    public int DropDetonations { get; private set; }

    public BodyAuthority? Get(uint id) => bodies.GetValueOrDefault(id);

    /// <summary>This peer's explosion request was accepted: a causal dependency mode events may name.</summary>
    public bool Exploded(int peer, uint request) => explosions.ContainsKey((peer, request));

    public void ValidateBodies(IEnumerable<uint> ids)
    {
        if (ids.Any(id => !bodies.ContainsKey(id))) { throw new InvalidDataException("unknown body id"); }
    }

    public AuthorityMessage Claim(int peer, ClaimMessage claim, bool allowed)
    {
        var reason = Check(peer, claim.Cause, claim.Bodies.Select(c => (c.Id, c.Epoch)), allowed);
        if (reason == ClaimRefusal.None)
        {
            var grants = claim.Bodies.Select(item => Grant(bodies[item.Id], peer, item, rest: false)).ToArray();
            foreach (var grant in grants) { bodies[grant.Body.Id] = grant; }
        }
        return Result(peer, claim.Request, reason, claim.Bodies.Select(c => c.Id));
    }

    public AuthorityMessage Puncture(int peer, PunctureMessage puncture, int actorId, bool allowed)
    {
        var reason = Check(peer, puncture.Cause, new[] { (puncture.Id, puncture.Epoch) }, allowed);
        if (reason == ClaimRefusal.None && puncture.Cause != 0 && explosions[(peer, puncture.Cause)] != actorId) reason = ClaimRefusal.Cause;
        if (reason == ClaimRefusal.None && (puncture.Id >= BodySync.CarBase || bodies[puncture.Id].State != State.Intact))
        {
            reason = ClaimRefusal.Lifecycle;
        }
        if (reason == ClaimRefusal.None)
        {
            var current = bodies[puncture.Id];
            bodies[puncture.Id] = Grant(current, peer, new BodyState(puncture.Id, puncture.Transform, puncture.Linear, puncture.Angular), rest: false) with
            {
                State = State.Venting,
                Vent = new VentParameters(puncture.Hole, puncture.Normal, puncture.Swirl),
                CauseActorId = actorId,
            };
        }
        return Result(peer, puncture.Request, reason, new[] { puncture.Id });
    }

    public AuthorityMessage Detonate(int peer, ExplodeMessage explosion, int actorId, bool allowed)
    {
        var reason = Check(peer, explosion.Cause, new[] { (explosion.Id, explosion.Epoch) }, allowed);
        if (reason == ClaimRefusal.None && explosions.ContainsKey((peer, explosion.Request))) reason = ClaimRefusal.DuplicateRequest;
        if (reason == ClaimRefusal.None && explosion.Cause != 0 && explosions[(peer, explosion.Cause)] != actorId) reason = ClaimRefusal.Cause;
        if (reason == ClaimRefusal.None && explosion.Id >= BodySync.CarBase) { reason = ClaimRefusal.Lifecycle; }
        if (reason == ClaimRefusal.None)
        {
            var current = bodies[explosion.Id];
            bodies[explosion.Id] = Grant(current, peer, current.Body, rest: true) with { State = State.Exploded, Center = explosion.Center, CauseActorId = actorId };
            explosions.Add((peer, explosion.Request), actorId);
            if (explosion.Id < BodySync.DropTankBase) { MapDetonations++; }
            else { DropDetonations++; }
        }
        return Result(peer, explosion.Request, reason, new[] { explosion.Id });
    }

    /// <summary>Disposable motion may arrive on either side of a reliable grant/rest. Only this owner/epoch wins.</summary>
    public bool Update(int peer, BodyState state, bool rest)
    {
        if (!bodies.TryGetValue(state.Id, out var current) || current.State == State.Exploded || current.Owner != peer ||
            current.Body.Epoch != state.Epoch || state.Sequence <= current.Body.Sequence) { return false; }
        bodies[state.Id] = current with { Body = state, Rest = rest };
        return true;
    }

    /// <summary>Gather presentation obeys the same ordered body stream; only the world authority starts piles.</summary>
    public bool Gather(int peer, int host, GatherMessage gather, bool allowed)
    {
        if (!allowed || peer != host || gather.Bodies.Any(b => !bodies.TryGetValue(b.Id, out var current) ||
            current.Owner != peer || current.Body.Epoch != b.Epoch || current.State != State.Intact ||
            gathers.TryGetValue(b.Id, out var prior) && prior.Epoch == b.Epoch && b.Sequence <= prior.Sequence)) { return false; }
        foreach (var body in gather.Bodies)
        {
            // Disposable motion may overtake reliable presentation. Relay membership without rewinding its pose.
            gathers[body.Id] = (body.Epoch, body.Sequence);
            Update(peer, body, rest: gather.Phase == GatherPhase.Settled);
        }
        return true;
    }

    public IReadOnlyList<BodyAuthority> Transfer(int left, int host)
    {
        var transferred = bodies.Values.Where(b => b.Owner == left && b.State != State.Exploded)
            .Select(b => Grant(b, host, b.Body, b.Rest)).ToArray();
        foreach (var body in transferred) { bodies[body.Body.Id] = body; }
        return transferred;
    }

    public void RegisterDrops(int slot, int owner, IReadOnlyList<BodyState> drops)
    {
        var prefix = BodySync.DropTankId(slot, 0);
        if (drops.Select(d => d.Id).Distinct().Count() != drops.Count ||
            drops.Any(d => (d.Id & 0xFFF0_0000u) != prefix || bodies.ContainsKey(d.Id) || d.Epoch != 1 || d.Sequence != 0))
        {
            throw new InvalidDataException("invalid or reused drop id");
        }
        foreach (var drop in drops) { Register(drop, owner, rest: false); }
    }

    /// <summary>
    /// New world tanks (a mode's fresh pile) at these poses, resting and owned by <paramref name="owner"/>. Their ids
    /// follow the plan's map tanks and are never reused, so they detonate and are audited as map tanks.
    /// </summary>
    public IReadOnlyList<BodyState> SpawnWorldTanks(int owner, IReadOnlyList<Transform3D> poses)
    {
        if (owner <= 0 || poses.Count > BodySync.MaxWorldEntities - worldTanks)
            throw new InvalidDataException("world tank allocation space exhausted or invalid owner");
        var spawned = poses.Select((pose, index) => new BodyState(BodySync.MapTankId(worldTanks + index), pose, Vector3.Zero, Vector3.Zero)).ToArray();
        foreach (var body in spawned) { Register(body, owner, rest: true); }
        worldTanks += spawned.Length;
        return spawned;
    }

    private void Register(BodyState body, int owner, bool rest) =>
        bodies.Add(body.Id, new BodyAuthority(body, owner, State.Intact, rest, null, Vector3.Zero));

    private static BodyAuthority Grant(BodyAuthority current, int peer, BodyState pose, bool rest) => current with
    {
        Owner = peer,
        Body = pose with { Epoch = checked(current.Body.Epoch + 1), Sequence = 0 },
        Rest = rest,
    };

    private ClaimRefusal Check(int peer, uint cause, IEnumerable<(uint Id, uint Epoch)> expected, bool allowed)
    {
        var group = expected.ToArray();
        if (group.Any(item => !bodies.ContainsKey(item.Id))) { return ClaimRefusal.UnknownBody; }
        if (!allowed) { return ClaimRefusal.WrongPhase; }
        if (cause != 0 && !explosions.ContainsKey((peer, cause))) { return ClaimRefusal.Cause; }
        if (group.Any(item => bodies[item.Id].State == State.Exploded)) { return ClaimRefusal.Exploded; }
        if (group.Any(item => bodies[item.Id].Body.Epoch != item.Epoch)) { return ClaimRefusal.StaleEpoch; }
        return ClaimRefusal.None;
    }

    private AuthorityMessage Result(int peer, uint request, ClaimRefusal reason, IEnumerable<uint> ids) =>
        new(peer, request, reason, ids.Where(bodies.ContainsKey).Select(id => bodies[id]).ToArray());
}
