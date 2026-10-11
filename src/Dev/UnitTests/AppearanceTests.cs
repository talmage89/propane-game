using System;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class AppearanceTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("appearance/opaque bytes and cosmetic ids round trip", () =>
        {
            foreach (var color in new[] { Colors.Black, Colors.White, Colors.Red, new Color(0.01f, 0.02f, 0.03f), new Color(0.23f, 0.67f, 0.91f) })
            {
                var appearance = AppearanceRecord.FromColor(color) with
                { ModelOrSkin = "base_01", Outfit = "outfit-2", Hat = "hat3", GunSkin = "gun4", BulletSkin = "bullet5" };
                TestRunner.Assert(AppearanceCodec.Decode(AppearanceCodec.Encode(appearance)) == appearance, "record fields changed");
                TestRunner.Assert(appearance.WithColor(Colors.Black).Hat == appearance.Hat, "changing color discarded cosmetics");
                TestRunner.Assert(appearance.Color.A == 1 && appearance.LegacyIndex is >= 0 and < 8, "opacity or fallback invalid");
            }
            var bounded = new AppearanceRecord(0, 255, 1, new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64), new string('e', 64));
            TestRunner.Assert(AppearanceCodec.Decode(AppearanceCodec.Encode(bounded)) == bounded, "maximum catalog ids rejected");
            var random = new Random(51);
            for (var i = 0; i < 1000; i++)
            {
                var appearance = new AppearanceRecord((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                TestRunner.Assert(AppearanceCodec.Decode(AppearanceCodec.Encode(appearance)) == appearance, "sRGB bytes changed");
            }
        });
        runner.Run("appearance/contrast supports extremes without changing presets", () =>
        {
            foreach (var preset in Protocol.PlayerColors)
            {
                TestRunner.Assert(AppearanceContrast.Strength(preset, Tuning.SavedTemplate) == 0 && AppearanceContrast.ScoreOutline(preset, Tuning.SavedTemplate) != Colors.White,
                    "preset presentation changed");
            }
            TestRunner.Assert(Mathf.IsEqualApprox(AppearanceContrast.Strength(Colors.Black, Tuning.SavedTemplate), 1) && Mathf.IsEqualApprox(AppearanceContrast.Strength(Colors.White, Tuning.SavedTemplate), 1),
                "extremes have no contrast edge");
            TestRunner.Assert(AppearanceContrast.ScoreOutline(Colors.Black, Tuning.SavedTemplate) == Colors.White && AppearanceContrast.ScoreOutline(Colors.Blue, Tuning.SavedTemplate) == Colors.White &&
                AppearanceContrast.ScoreOutline(Colors.White, Tuning.SavedTemplate) != Colors.White, "score outline cannot contrast with its text");
        });
        runner.Run("appearance/unknown slots skip binary payload and keep safe defaults", () =>
        {
            var record = new byte[] { 1, 12, 34, 56, 3, 240, 3, 0, 255, 0, 128, 3, 3, 0, (byte)'h', (byte)'a', (byte)'t', 241, 0, 0 };
            TestRunner.Assert(AppearanceCodec.Decode(record) == new AppearanceRecord(12, 34, 56, Hat: "hat"), "unknown slots affected known color/hat");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(new byte[] { 1, 12, 34, 56, 16 });
            for (var i = 0; i < 16; i++) { writer.Write((byte)(200 + i)); writer.Write((ushort)60); writer.Write(new byte[60]); }
            TestRunner.Assert(AppearanceCodec.Decode(stream.ToArray()) == new AppearanceRecord(12, 34, 56), "maximum unknown slot count rejected");
        });
        runner.Run("appearance/reject lengths counts tags versions paths and truncation", () =>
        {
            var record = AppearanceCodec.Encode(new AppearanceRecord(1, 2, 3, Hat: "hat"));
            for (var i = 0; i < record.Length; i++) { TestRunner.Throws<InvalidDataException>(() => AppearanceCodec.Decode(record[..i])); }
            foreach (var bad in new[]
                     {
                         new byte[AppearanceCodec.MaxRecordBytes + 1],
                         new byte[] { 2, 1, 2, 3, 0 }, new byte[] { 1, 1, 2, 3, 17 },
                         new byte[] { 1, 1, 2, 3, 1, 6, 65, 0 }, new byte[] { 1, 1, 2, 3, 1, 6, 1, 0 },
                         new byte[] { 1, 1, 2, 3, 2, 6, 0, 0, 6, 0, 0 },
                         new byte[] { 1, 1, 2, 3, 1, 0, 0, 0 }, new byte[] { 1, 1, 2, 3, 1, 3, 0, 0 },
                         new byte[] { 1, 1, 2, 3, 1, 3, 1, 0, 128 }, record.Append((byte)0).ToArray(),
                     })
            {
                TestRunner.Throws<InvalidDataException>(() => AppearanceCodec.Decode(bad));
            }
            foreach (var id in new[] { "", new string('a', 65), "res://hat", "../hat", "a.b", "hat skin", "帽子" })
            {
                TestRunner.Throws<InvalidDataException>(() => AppearanceCodec.Encode(new AppearanceRecord(1, 2, 3, Hat: id)));
            }
            var oversized = new NetWriter(Msg.SetProfile).String("Test").Bytes(new byte[1025]).ToArray();
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(oversized, false));
        });
        runner.Run("appearance/Hello preserves old prefix and falls back without extension", () =>
        {
            for (var index = 0; index < Protocol.PlayerColors.Length; index++)
            {
                var prefix = new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test").String("Test").Int(index).Bool(true);
                var prefixBytes = prefix.ToArray();
                var legacy = (HelloMessage)MessageCodec.Decode(prefixBytes, false).Payload;
                TestRunner.Assert(legacy.Appearance == AppearanceRecord.FromPalette(index), "legacy palette fallback lost");
                var appearance = new AppearanceRecord(12, 34, 56);
                var extended = prefix.Appearance(appearance).ToArray();
                TestRunner.Assert(extended.AsSpan(0, prefixBytes.Length).SequenceEqual(prefixBytes), "Hello prefix changed");
                TestRunner.Assert(((HelloMessage)MessageCodec.Decode(extended, false).Payload).Appearance == appearance, "extension lost");
            }
            // Even a malformed extension on an incompatible build must reach version rejection untouched.
            var incompatible = new NetWriter(Msg.Hello).Int(999).String(BuildInfo.GameVersion).String("test").String("Test").Int(1).Byte(255).Int(-1);
            TestRunner.Assert(((HelloMessage)MessageCodec.Decode(incompatible.ToArray(), false).Payload).Protocol == 999, "incompatible extension interpreted");
        });
        runner.Run("appearance/settings migrate once and persist the same record", () =>
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://build"));
            var path = $"res://build/appearance-settings-{Guid.NewGuid():N}.cfg";
            try
            {
                using var config = new ConfigFile();
                config.SetValue("player", "color", 4);
                TestRunner.Assert(config.Save(path) == Error.Ok, "fixture save failed");
                PlayerSettings.UseFile(path);
                TestRunner.Assert(PlayerSettings.Appearance == AppearanceRecord.FromPalette(4), "palette did not migrate");
                using var reread = new ConfigFile();
                TestRunner.Assert(reread.Load(path) == Error.Ok && reread.HasSectionKey("player", "appearance"), "migration was not saved");
                var custom = new AppearanceRecord(12, 34, 56);
                PlayerSettings.Appearance = custom;
                PlayerSettings.UseFile(path);
                TestRunner.Assert(PlayerSettings.Appearance == custom, "old index overwrote custom record");
                reread.Load(path);
                reread.SetValue("player", "appearance", new byte[] { 255 });
                reread.Save(path);
                PlayerSettings.UseFile(path);
                TestRunner.Assert(PlayerSettings.Appearance == null, "corrupt record was accepted or re-migrated");
            }
            finally
            {
                PlayerSettings.UseFile("user://settings.cfg");
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            }
        });
        runner.Run("appearance/server accepts duplicate colors through roster match and standings", () =>
        {
            var appearance = new AppearanceRecord(12, 34, 56);
            var clock = new ManualClock();
            var server = new LobbyServer(PacketTransports.CreateServer(), clock);
            try
            {
                var packets = new System.Collections.Generic.List<NetMessage>();
                server.SendOverride = (_, bytes, _) => packets.Add(MessageCodec.Decode(bytes, true));
                for (var peer = 2; peer <= 3; peer++)
                {
                    server.OnConnected(peer);
                    server.OnReceived(peer, new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test")
                        .String($"Player{peer}").Int(0).Bool(true).Appearance(appearance).ToArray());
                }
                server.OnReceived(2, new NetWriter(Msg.CreateLobby).ToArray());
                server.OnReceived(3, new NetWriter(Msg.JoinLobby).Int(1).ToArray());
                TestRunner.Assert(packets.Select(p => p.Payload).OfType<LobbyInfo>().Last().Members.All(p => p.Appearance == appearance), "join substituted duplicate color");
                var updated = appearance with { Red = 13 };
                for (var peer = 2; peer <= 3; peer++)
                {
                    server.OnReceived(peer, new NetWriter(Msg.SetProfile).String($"Player{peer}").Appearance(updated).ToArray());
                }
                TestRunner.Assert(packets.Select(p => p.Payload).OfType<LobbyInfo>().Last().Members.All(p => p.Appearance == updated), "update substituted duplicate color");
                using var tuning = new Tuning { CountdownTime = 0, ResultsTime = 1 };
                server.OnReceived(2, new NetWriter(Msg.SetMatchLength).Float(10).ToArray());
                server.OnReceived(2, new NetWriter(Msg.StartMatch).Snapshot(tuning.Snapshot()).ToArray());
                var setup = packets.Select(p => p.Payload).OfType<MatchSetup>().First();
                TestRunner.Assert(setup.Players.All(p => p.Player.Appearance == updated), "MatchStart lost appearance");
                foreach (var peer in new[] { 2, 3 }) { server.OnReceived(peer, new NetWriter(Msg.Loaded, setup.Id).ToArray()); }
                server.AdvanceMatches(clock.Seconds); server.AdvanceMatches(clock.Seconds);
                clock.Seconds += 20;
                server.AdvanceMatches(clock.Seconds);
                TestRunner.Assert(packets.Select(p => p.Payload).OfType<System.Collections.Generic.IReadOnlyList<Standing>>().First().All(p => p.Appearance == updated), "standings lost appearance");
            }
            finally { server.Free(); }
        });
    }
}
