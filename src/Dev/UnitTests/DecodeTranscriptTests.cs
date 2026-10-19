using System;
using System.Collections.Generic;
using System.IO;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Same source runs against parent and candidate; records every byte prefix and first rejection cause.</summary>
[UnitSuite]
public static class DecodeTranscriptTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("decode/every prefix uses only expected malformed-input exceptions", () =>
        {
            var records = new List<string>();
            foreach (var message in ProtocolTests.Fixtures())
            {
                var directions = Protocol.IsRelay(message.Type) && message.Type is not (Msg.Claim or Msg.Puncture or Msg.Explode)
                    ? new[] { false, true } : new[] { message.Type is >= Msg.Welcome and <= Msg.ModeSnapshot };
                foreach (var server in directions)
                {
                    var bytes = MessageCodec.Encode(message, server);
                    for (var length = 0; length <= bytes.Length; length++) Record($"{message.Type}/{server}/{length}", bytes[..length], server, records);
                }
            }
            var bothInvalid = new[]
            {
                new NetWriter(Msg.SetMode).Byte(255).Float(float.NaN).Int(0).ToArray(),
                new NetWriter(Msg.SetMode).Byte(1).Float(float.NaN).Int(0).ToArray(),
                new NetWriter(Msg.SetProfile).Raw(new byte[] { 255, 255, 255, 255, 255 }).ToArray(),
                new NetWriter(Msg.SetProfile).Raw(new byte[] { 2, 0xC0, 0x80 }).ToArray(),
                new NetWriter(Msg.StartMatch).Int(2).String("unknown").Scalar(1f).String("unknown").Scalar(2f).ToArray(),
                new NetWriter(Msg.TuningSet, 7).String("unknown").Scalar(float.NaN).ToArray(),
                new NetWriter(Msg.TuningSet, 7).String("unknown").Scalar(1f).Byte(1).ToArray(),
                new NetWriter(Msg.ModeEvent, 7).UInt(1).UInt(1).Byte(255).UShort(1).Int(0).UInt(0).UInt(1).UShort(1).UShort(0).ToArray(),
            };
            for (var i = 0; i < bothInvalid.Length; i++) Record($"order/{i}", bothInvalid[i], false, records);
            TestRunner.Assert(records.Count > 3000, "prefix coverage was truncated");
            if (DevArgs.Get("decode-transcript") is { } path) File.WriteAllLines(path, records);
            Godot.GD.Print($"[decode] {records.Count} prefix/rejection-order records");
        });
    }
    private static void Record(string label, byte[] bytes, bool server, List<string> records)
    {
        try { MessageCodec.Decode(bytes, server); records.Add(label + " accepted"); }
        catch (Exception error)
        {
            TestRunner.Assert(error is InvalidDataException or EndOfStreamException or FormatException or System.Text.DecoderFallbackException,
                $"unclassified reader failure at {label}: {error}");
            records.Add($"{label} {error.GetType().Name}: {error.Message}");
        }
    }
}
