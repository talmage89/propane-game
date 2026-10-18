using System.Linq;
using Godot;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Identical opt-in observation injected into isolated builds by actor_equivalence.py.</summary>
internal static class ActorRetirementObservation
{
    public static void Record(Match match, string step, int actor = 0)
    {
        if (DevArgs.Get("actor-order") == null) return;
        var peers = match.Setup.Peers.Select(peer => peer.PeerId).ToArray();
        var roster = string.Join(',', match.Session.Actors.Select(runtime => $"{runtime.Character.ActorId}:{System.Array.IndexOf(peers, runtime.Character.OwnerPeerId)}"));
        var bots = ((System.Collections.IEnumerable)ActorLifetimeFixture.Field(match, "bots")).Cast<object>()
            .Select(bot => (Propane.Player.PlayerCharacter)bot.GetType().GetProperty("Actor")!.GetValue(bot)!)
            .Where(character => match.Session.Actor(character.ActorId) != null).Select(character => character.ActorId);
        GD.Print($"[actor-order] mode={match.Setup.ModeId} {step} actor={actor} roster={roster} host={System.Array.IndexOf(peers, match.Bodies.Host)} bots={string.Join(',', bots)}");
    }
}
