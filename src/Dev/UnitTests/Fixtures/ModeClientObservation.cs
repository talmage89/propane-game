using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Opt-in observation injected into isolated builds only. Never referenced by shipping code.</summary>
internal static class ModeClientObservation
{
    private static float[] Point(Vector3 point) => new[] { point.X, point.Y, point.Z };

    public static void Record(object owner, string step, string? detail = null)
    {
        if (DevArgs.Get("mode-client-transcript") is not { } path) return;
        var match = owner as Match;
        var state = match?.ModeView as KingState;
        File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            step,
            detail,
            mode = match?.Setup.ModeId,
            playing = match?.IsPlaying,
            time = match?.TimeLeft,
            scores = match?.Scores.OrderBy(pair => pair.Key).Select(pair => new[] { pair.Key, pair.Value }).ToArray(),
            actors = match?.Session.Actors.Select(actor => new { id = actor.Character.ActorId, locked = actor.Character.InputLocked }).ToArray(),
            tanks = match?.LiveTanks.Select(tank => tank.NetId).OrderBy(id => id).ToArray(),
            zone = state == null ? null : new
            {
                state.Zone,
                state.Spot,
                state.Next,
                state.Radius,
                state.Holder,
                state.Occupants,
                state.NextMint,
                state.MintSeconds,
                state.MoveAt,
                state.WarningSeconds,
                state.Minted,
                state.Elapsed,
                state.Warning,
                state.UntilMove,
                state.MintProgress
            }
        }) + "\n");
    }

    public static byte[] Encode(NetMessage message, bool fromServer)
    {
        var bytes = MessageCodec.Encode(message, fromServer);
        if (message.Type == Msg.ModeEvent && DevArgs.Get("mode-client-transcript") is { } path)
            File.AppendAllText(path, JsonSerializer.Serialize(new { step = "mode-send", bytes = Convert.ToBase64String(bytes) }) + "\n");
        return bytes;
    }

    public static ModeHud Hud(object owner, ModeHud hud)
    {
        if (DevArgs.Get("mode-client-transcript") is not { } path) return hud;
        var match = owner as Match ?? (Match)owner.GetType().GetField("parent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
        File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            step = "hud",
            mode = match.Setup.ModeId,
            hud.Counters,
            hud.Columns,
            hud.Timer,
            objectives = hud.Objectives.Select(Point).ToArray(),
            hud.ObjectivesVisible,
            hud.Banner,
            hud.OutState,
            hud.Feed,
            hud.Bar
        }) + "\n");
        return hud;
    }
}
