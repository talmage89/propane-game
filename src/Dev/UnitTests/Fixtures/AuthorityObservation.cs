using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Injected into isolated builds only; observes transaction boundaries without production hooks.</summary>
internal static class AuthorityObservation
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Fields)?.GetValue(owner);
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, Fields)?.GetValue(owner);
    private static IEnumerable<object> Items(object? value) => value is IEnumerable items ? items.Cast<object>() : Array.Empty<object>();
    private static object[] Pairs(object? value) => Items(value).Select(pair => (object)new
    {
        key = Property(pair, "Key"),
        value = Property(pair, "Value")
    }).ToArray();
    private static object Request(object request) => new
    {
        type = Property(request, "Type"),
        request = Property(request, "Request"),
        cause = Property(request, "Cause"),
        ids = Property(request, "Ids"),
        actor = Property(request, "ActorId"),
        life = Property(request, "LifeId"),
        expected = Pairs(Field(request, "Expected"))
    };

    public static void Record(object owner, string step, string? detail = null)
    {
        if (DevArgs.Get("authority-transcript") is not { } path) return;
        var match = owner as Match ?? Field(owner, "replica") as Match;
        var transactions = match == null ? owner : Field(match, "authority") ?? match;
        var queued = Items(Field(transactions, "outgoingAuthority")).Select(Request).ToArray();
        var pending = Items(Field(transactions, "pendingAuthority")).Select(pair => Request(Property(pair, "Value")!)).ToArray();
        var gathers = Items(Field(transactions, "gatherRequests")).Select(item => Property(item, "Key") ?? item).ToArray();
        var bodies = match != null ? match.Bodies.Entries.Select(entry => new
        {
            id = entry.Id,
            owner = entry.Owner,
            epoch = entry.Epoch,
            pending = entry.ClaimPending,
            observe = entry.ObserveWhilePending,
            sequence = entry.LastSequence
        }).ToArray() : null;
        File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            step,
            detail,
            queued,
            pending,
            reservations = Pairs(Field(transactions, "pendingBodies")),
            roots = Items(Field(transactions, "sentExplosions")).ToArray(),
            gathers,
            bodies
        }) + "\n");
    }
}
