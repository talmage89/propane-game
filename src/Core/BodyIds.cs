using System;

namespace Propane.Core;

/// <summary>Stable body identity ranges and allocation arithmetic, independent of replication.</summary>
public static class BodyIds
{
    public const uint MapTankBase = 0x1000_0000;
    public const uint DropTankBase = 0x2000_0000;
    public const uint CarBase = 0x3000_0000;

    /// <summary>Exclusive allocation bound within each world-body range.</summary>
    public const int MaxWorldEntities = 1 << 28;
    /// <summary>Id of a map tank by its index in the plan.</summary>
    public static uint MapTankId(int index) => WorldEntityId(MapTankBase, index);

    /// <summary>Id of a car by its index in the plan's props.</summary>
    public static uint CarId(int propIndex) => WorldEntityId(CarBase, propIndex);
    private static uint WorldEntityId(uint prefix, int index)
    {
        if (index < 0 || index >= MaxWorldEntities) throw new ArgumentOutOfRangeException(nameof(index), "entity allocation space exhausted");
        return prefix + (uint)index;
    }

    // Allocation slots belong to session actors, independently of their position in any round's spawn list.
    public const int MaxAllocationSlots = (int)((CarBase - DropTankBase) >> 20);
    public const int MaxDropsPerActor = 1 << 20;
    public static uint DropTankId(int slot, int counter)
    {
        if (slot < 0 || slot >= MaxAllocationSlots || counter < 0 || counter >= MaxDropsPerActor)
            throw new ArgumentOutOfRangeException(nameof(counter), "entity allocation space exhausted");
        return DropTankBase + ((uint)slot << 20) + (uint)counter;
    }
}
