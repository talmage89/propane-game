using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Builders for the actor messages a client sends, with every header field explicit.</summary>
internal static class ActorMessages
{
    /// <summary>The bytes of a client-to-server actor message in match <paramref name="matchId"/>, from one actor, life and action.</summary>
    public static byte[] Encode(Msg type, int matchId, int actorId, uint lifeId, uint actionId, object payload) =>
        MessageCodec.Encode(new NetMessage(type, matchId, 0, payload, actorId, lifeId, actionId), false);
}
