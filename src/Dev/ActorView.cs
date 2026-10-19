using System.Reflection;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Inspect the designated primary view across the actor/view boundary migration.</summary>
internal static class ActorView
{
    public static LocalView? For(PlayerCharacter actor)
    {
        var find = typeof(LocalView).GetMethod("Find", BindingFlags.Static | BindingFlags.Public);
        return find == null ? (LocalView?)typeof(PlayerCharacter).GetProperty("View")!.GetValue(actor) :
            (LocalView?)find.Invoke(null, new object[] { actor });
    }
}
