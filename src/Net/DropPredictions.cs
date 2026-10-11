using System;
using System.Collections.Generic;

namespace Propane.Net;

/// <summary>Tracks local drops until the server confirms their bank debit and creation, or refuses their ids.</summary>
public sealed class DropPredictions
{
    private readonly HashSet<uint> pending = new();
    public int UnconfirmedCount { get; private set; }

    public void Add(uint id)
    {
        if (!pending.Add(id)) { throw new InvalidOperationException("duplicate drop prediction"); }
        UnconfirmedCount++;
    }

    // Scores precede accepted Drop echoes on the reliable channel. Keep their ids until the echo arrives, but
    // release the bank reservation at once so a delayed echo cannot prevent another legitimate spill.
    public void ScoreConfirmed(int delta) => UnconfirmedCount = Math.Max(0, UnconfirmedCount + Math.Min(0, delta));
    public void Accepted(IEnumerable<uint> ids) => pending.ExceptWith(ids);

    public IReadOnlyList<uint> Refused(IEnumerable<uint> ids)
    {
        var removed = new List<uint>();
        foreach (var id in ids) { if (pending.Remove(id)) { removed.Add(id); } }
        UnconfirmedCount = Math.Max(0, UnconfirmedCount - removed.Count);
        return removed;
    }
}
