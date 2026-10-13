using System.Collections.Generic;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Tank Bank envelope messages for assertion suites, in the first round and world revision.</summary>
public static class ModeWireFixtures
{
    // Client command order only accepts newer ids; one increasing sequence suits every fixture match.
    private static uint nextCommand;

    public static ModeEnvelope Envelope(int actorId, uint lifeId, uint eventId, ushort kind, object payload) =>
        new(1, 1, ModeRegistry.TankBankId, ModeRegistry.TankBank.SchemaVersion, actorId, lifeId, eventId, kind, payload);

    public static NetMessage Score(int matchId, int actorId, int score, int delta, uint lifeId = 1) => new(Msg.ModeCommand, matchId, 0,
        Envelope(actorId, lifeId, ++nextCommand, ModeSchemas.TankBankKinds.Score, new TankBankScore(score, delta)));

    public static NetMessage Accepted(int matchId, int actorId, uint lifeId, SpillReason reason, IReadOnlyList<BodyState> tanks) =>
        new(Msg.ModeCommand, matchId, 0, Envelope(actorId, lifeId, ++nextCommand, ModeSchemas.TankBankKinds.SpillAccepted, new TankBankSpill(reason, tanks)));

    public static NetMessage Spill(int matchId, int actorId, uint lifeId, uint eventId, SpillReason reason, IReadOnlyList<BodyState> tanks) =>
        new(Msg.ModeEvent, matchId, 0, Envelope(actorId, lifeId, eventId, ModeSchemas.TankBankKinds.Spill, new TankBankSpill(reason, tanks)),
            actorId, lifeId, eventId);

    public static bool IsScore(NetMessage message, int actorId, int score) =>
        message.Payload is ModeEnvelope { Payload: TankBankScore s } e && e.ActorId == actorId && s.Score == score;

    public static bool IsScore(NetMessage message, int actorId) => message.Payload is ModeEnvelope { Payload: TankBankScore } e && e.ActorId == actorId;

    public static IReadOnlyList<uint>? Refused(NetMessage message) =>
        message.Payload is ModeEnvelope { Payload: TankBankRefused refused } ? refused.Ids : null;

    public static TankBankSpill? AcceptedSpill(NetMessage message) =>
        message is { Type: Msg.ModeCommand, Payload: ModeEnvelope { Payload: TankBankSpill spill } } ? spill : null;
}
