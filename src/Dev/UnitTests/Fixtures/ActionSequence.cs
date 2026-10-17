using System.Collections.Generic;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Action ids for the actor messages a fixture sends: each key counts up from 1, and a key's ids are never reused.</summary>
internal sealed class ActionSequence
{
    private readonly Dictionary<int, uint> last = new();

    public uint Next(int key)
    {
        var next = last.GetValueOrDefault(key) + 1;
        last[key] = next;
        return next;
    }
}
