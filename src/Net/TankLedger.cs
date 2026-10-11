using System.Collections.Generic;
using System.IO;
using System.Linq;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// Server-side existence and lifecycle only, never physics. Exploded entries stay as tombstones for the whole
/// match, so neither late claims nor recycled drop ids can earn a second point.
/// </summary>
public sealed class TankLedger
{
    public enum State { Intact, Venting, Exploded }
    private readonly Dictionary<uint, State> tanks = new();
    private readonly HashSet<uint> cars = new();

    public TankLedger(SuburbPlan plan)
    {
        for (var i = 0; i < plan.Tanks.Count; i++) { tanks.Add(BodySync.MapTankId(i), State.Intact); }
        for (var i = 0; i < plan.Props.Count; i++)
        {
            if (plan.Props[i].Model.StartsWith("car:")) { cars.Add(BodySync.CarId(i)); }
        }
    }

    public int MapDetonations { get; private set; }
    public int DropDetonations { get; private set; }

    public void ValidateBodies(IEnumerable<uint> ids)
    {
        // An unreliable state can arrive after a reliable explosion. Known tombstones are harmless to clients.
        if (ids.Any(id => !tanks.ContainsKey(id) && !cars.Contains(id))) { throw new InvalidDataException("unknown body id"); }
    }

    public bool Puncture(uint id)
    {
        var state = Tank(id);
        if (state != State.Intact) { return false; }
        tanks[id] = State.Venting;
        return true;
    }

    public bool Detonate(uint id, bool scoring)
    {
        if (Tank(id) == State.Exploded) { return false; }
        tanks[id] = State.Exploded;
        if (scoring)
        {
            if (id < BodySync.DropTankBase) { MapDetonations++; }
            else { DropDetonations++; }
        }
        return true;
    }

    public void RegisterDrops(int slot, IReadOnlyList<BodyState> drops)
    {
        var prefix = BodySync.DropTankId(slot, 0);
        // Check the entire batch, including collisions with tombstones, before creating any entry.
        if (drops.Select(d => d.Id).Distinct().Count() != drops.Count ||
            drops.Any(d => (d.Id & 0xFFF0_0000u) != prefix || tanks.ContainsKey(d.Id)))
        {
            throw new InvalidDataException("invalid or reused drop id");
        }
        foreach (var drop in drops) { tanks.Add(drop.Id, State.Intact); }
    }

    private State Tank(uint id) => tanks.TryGetValue(id, out var state) ? state : throw new InvalidDataException("unknown tank id");
}
