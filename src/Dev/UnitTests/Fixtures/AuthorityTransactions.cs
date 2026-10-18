using System;
using System.Reflection;
using Propane.Net;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Observes private transaction ownership without giving production a test state API.</summary>
internal static class AuthorityTransactions
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static object Owner(Match match) => typeof(Match).GetField("authority", Private)?.GetValue(match) ?? match;
    public static int Count(object owner, string field)
    {
        var value = owner.GetType().GetField(field, Private)!.GetValue(owner)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }
    public static void Empty(object owner)
    {
        foreach (var name in new[] { "outgoingAuthority", "pendingAuthority", "pendingBodies", "sentExplosions", "gatherRequests" })
        {
            var value = owner.GetType().GetField(name, Private)!.GetValue(owner)!;
            Assert((int)value.GetType().GetProperty("Count")!.GetValue(value)! == 0, $"retired authority retained {name}");
        }
    }
    public static void Gather(Match match, PropaneTank tank, Action<Func<PropaneTank, bool>?> done) =>
        typeof(Match).GetMethod("GrantGather", Private)!.Invoke(match, new object[] { new[] { tank }, done });
    public static int Listeners(Match match) => ((Delegate?)typeof(Match).GetField("AuthorityResolved", Private)!.GetValue(match))?.GetInvocationList().Length ?? 0;
}
