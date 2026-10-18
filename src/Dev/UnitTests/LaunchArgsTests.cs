using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>
/// The launch arguments the shipped game reads: the parser's exact rules (checked against the parser as it stood in
/// DevArgs before the runtime service was extracted) and every option a runtime call site consumes, through its real
/// consumer where one can run headlessly. Server startup (<c>--server</c>, <c>--port</c>, <c>--start-score</c>,
/// <c>--match-seed</c>, <c>--tune.*</c>) is covered by the native server scenes; <c>--connect</c>, <c>--name</c>,
/// <c>--windowed</c>, <c>--release-test</c>, the update options and the trace and metrics switches by the exported
/// launches in docs/code-quality.md.
/// </summary>
[UnitSuite]
public static class LaunchArgsTests
{
    private static readonly PacketEndpoint Endpoint = new("", 1234);

    private static void Assert(bool condition, string message) => TestRunner.Assert(condition, message);

    /// <summary>Runs <paramref name="body"/> with this process's launch arguments replaced, and restores them.</summary>
    private static void With(string[] args, Action<LaunchArgs> body)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var parsed = new LaunchArgs(args);
            using var scope = LaunchArgs.OverrideForTests(parsed);
            body(parsed);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>DevArgs's parser exactly as it was before the extraction: the oracle for the rules below.</summary>
    private static Dictionary<string, string> Legacy(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>();
        foreach (var arg in args)
        {
            var trimmed = arg.TrimStart('-');
            var split = trimmed.IndexOf('=');
            if (split > 0)
            {
                values[trimmed[..split]] = trimmed[(split + 1)..];
            }
            else
            {
                values[trimmed] = "true";
            }
        }
        return values;
    }

    private static string? Hello(string[] args, Func<HelloMessage, string?> check)
    {
        string? failure = null;
        With(args, parsedArgs =>
        {
            var clock = new ManualClock();
            var network = new LoopbackNetwork();
            var server = network.Create();
            var transport = network.Create();
            var client = new NetClient(transport, clock);
            try
            {
                Assert(server.Listen(Endpoint, 5).Success, "listen failed");
                var hellos = new List<HelloMessage>();
                server.Packet += (_, _, bytes) =>
                {
                    if (MessageCodec.Decode(bytes.ToArray(), fromServer: false).Payload is HelloMessage hello) { hellos.Add(hello); }
                };
                client._Ready();
                client.Connect("", Endpoint.Port, "Tester", 0);
                for (var i = 0; i < 3; i++) { server.Poll(); client._Process(0); }
                server.Poll();
                Assert(hellos.Count == 1, "the client did not send exactly one Hello");
                failure = check(hellos[0]);
            }
            finally
            {
                client._ExitTree();
                client.Free();
                transport.Close();
                server.Close();
            }
        });
        return failure;
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("launch-args/parser keeps dash trimming, the first equals sign and last-wins", () =>
        {
            var args = new LaunchArgs(new[]
            {
                "--a=b", "-c=d", "---e=f", "g=h", "--flag", "-x", "--empty=", "--k=a=b", "--n=-5", "--=z", "--", "--dup=1", "--dup=2",
                "--Case=1", "--sp ace=a b",
            });
            Assert(args.Get("a") == "b" && args.Get("c") == "d" && args.Get("e") == "f" && args.Get("g") == "h", "plain key=value");
            Assert(args.Get("flag") == "true" && args.Get("x") == "true", "a bare argument is \"true\"");
            Assert(args.Get("empty") == "" && args.Has("empty"), "key= is present and empty");
            Assert(args.Get("k") == "a=b", "only the first equals sign splits");
            Assert(args.Get("n") == "-5", "only leading dashes of the argument are dropped");
            Assert(args.Get("=z") == "true" && args.Get("") == "true", "an equals sign in first place is part of a bare key; \"--\" is the empty key");
            Assert(args.Get("dup") == "2", "a later argument replaces an earlier one");
            Assert(args.Get("Case") == "1" && args.Get("case") == null, "keys are case-sensitive");
            Assert(args.Get("sp ace") == "a b", "spaces survive");
            Assert(args.Get("missing") == null && !args.Has("missing"), "an absent key is null");
            Assert(new LaunchArgs(Array.Empty<string>()).Get("a") == null, "no arguments, no values");
        });
        runner.Run("launch-args/numbers fall back unless the value parses", () =>
        {
            With(new[] { "--i=12", "--f=-2.5", "--e=1e3", "--word=abc", "--empty=", "--bare" }, args =>
            {
                Assert(args.GetFloat("i", 7) == 12f && args.GetFloat("f", 7) == -2.5f && args.GetFloat("e", 7) == 1000f, "numbers parse");
                Assert(args.GetFloat("word", 7) == 7f && args.GetFloat("empty", 7) == 7f && args.GetFloat("bare", 7) == 7f, "non-numbers fall back");
                Assert(args.GetFloat("missing", 7) == 7f && args.GetFloat("missing", -1.5f) == -1.5f, "an absent key falls back");
            });
        });
        runner.Run("launch-args/parser matches the pre-extraction parser on shuffled argument lists", () =>
        {
            var vocabulary = new[]
            {
                "--a=1", "--a=2", "-a", "---a=x=y", "--b", "--b=", "--c=-5", "--c=3.5", "--=z", "--", "-", "", "d=4", "--tune.MaxPlayers=6",
                "--net-lag=120", "--server", "--port=24680", "--A=9", "--e==", "--f=g=h=i",
            };
            var keys = new[] { "a", "A", "b", "c", "d", "e", "f", "=z", "", "tune.MaxPlayers", "net-lag", "server", "port", "missing" };
            var random = new Random(23);
            for (var round = 0; round < 400; round++)
            {
                var list = new string[random.Next(0, 9)];
                for (var i = 0; i < list.Length; i++) { list[i] = vocabulary[random.Next(vocabulary.Length)]; }
                var legacy = Legacy(list);
                var args = new LaunchArgs(list);
                foreach (var key in keys)
                {
                    Assert(args.Get(key) == (legacy.TryGetValue(key, out var expected) ? expected : null), $"{string.Join(' ', list)}: {key}");
                    Assert(args.Has(key) == legacy.ContainsKey(key), $"{string.Join(' ', list)}: Has({key})");
                    Assert(args.GetFloat(key, -9f) == (float.TryParse(legacy.GetValueOrDefault(key), out var number) ? number : -9f), $"{string.Join(' ', list)}: GetFloat({key})");
                }
            }
        });
        runner.Run("launch-args/DevArgs reads the same process cache", () =>
        {
            With(new[] { "--scenario=network", "--seconds=12.5" }, _ =>
            {
                Assert(DevArgs.Get("scenario") == "network" && DevArgs.Get("absent") == null, "DevArgs.Get");
                Assert(DevArgs.GetFloat("seconds", 1) == 12.5f && DevArgs.GetFloat("scenario", 1) == 1f, "DevArgs.GetFloat");
            });
        });
        runner.Run("launch-args/--classic-lobby disables the lobby world whatever its value", () =>
        {
            With(Array.Empty<string>(), _ => Assert(LobbyWorldDefault.Enabled, "lobby worlds are the default"));
            With(new[] { "--classic-lobby" }, _ => Assert(!LobbyWorldDefault.Enabled, "--classic-lobby"));
            With(new[] { "--classic-lobby=false" }, _ => Assert(!LobbyWorldDefault.Enabled, "any value counts"));
            With(new[] { "--classic" }, _ => Assert(LobbyWorldDefault.Enabled, "other arguments do not"));
        });
        runner.Run("launch-args/--fake-version overrides the game version only when given", () =>
        {
            var baked = ProjectSettings.GetSetting("application/config/version").AsString();
            With(Array.Empty<string>(), _ => Assert(BuildInfo.GameVersion == baked, "the project version by default"));
            With(new[] { "--fake-version=0.0.9" }, _ => Assert(BuildInfo.GameVersion == "0.0.9", "--fake-version"));
        });
        runner.Run("launch-args/--net-* options become packet faults in milliseconds, percent and seconds", () =>
        {
            With(Array.Empty<string>(), _ =>
            {
                Assert(PacketTransports.FaultsFrom(LaunchArgs.Current) == null && PacketTransports.FaultSeed(LaunchArgs.Current) == 1, "no faults and seed 1 by default");
            });
            With(new[] { "--net-lag=120", "--net-jitter=30", "--net-loss=3", "--net-seed=7" }, args =>
            {
                Assert(PacketTransports.FaultsFrom(args) == new PacketFaults(0.06, 0.03, 0.03), "the documented faulted-client set");
                Assert(PacketTransports.FaultSeed(args) == 7, "--net-seed");
            });
            With(new[] { "--net-reliable-stall=250" }, args => Assert(PacketTransports.FaultsFrom(args) == new PacketFaults(ReliableStall: 0.25), "--net-reliable-stall"));
            With(new[] { "--net-body-stall=500", "--net-seed=2.9" }, args =>
            {
                Assert(PacketTransports.FaultsFrom(args) == new PacketFaults(BodyStall: 0.5), "--net-body-stall");
                Assert(PacketTransports.FaultSeed(args) == 2, "the seed truncates");
            });
            With(new[] { "--net-lag=0", "--net-jitter=-5", "--net-loss=abc", "--net-reliable-stall", "--net-seed=abc" }, args =>
            {
                Assert(PacketTransports.FaultsFrom(args) == null, "zero, negative and non-numeric values ask for nothing");
                Assert(PacketTransports.FaultSeed(args) == 1, "an unparsable seed is 1");
            });
            With(new[] { "--net-lag=120" }, _ =>
            {
                var faulted = PacketTransports.CreateClient();
                try { Assert(faulted is FaultPacketTransport, "a faulted launch wraps the client transport"); }
                finally { faulted.Close(); }
            });
            With(Array.Empty<string>(), _ =>
            {
                var clean = PacketTransports.CreateClient();
                try { Assert(clean is not FaultPacketTransport, "a clean launch does not"); }
                finally { clean.Close(); }
            });
        });
        runner.Run("launch-args/the client's Hello carries the real protocol and version unless faked", () =>
        {
            var failure = Hello(Array.Empty<string>(), hello => hello.Protocol == Protocol.Version && hello.Version == BuildInfo.GameVersion
                ? null : $"default Hello is {hello.Protocol}/{hello.Version}");
            Assert(failure == null, failure ?? "");
            failure = Hello(new[] { "--fake-protocol=2", "--fake-version=0.0.9" }, hello => hello.Protocol == 2 && hello.Version == "0.0.9"
                ? null : $"faked Hello is {hello.Protocol}/{hello.Version}");
            Assert(failure == null, failure ?? "");
            failure = Hello(new[] { "--fake-protocol=abc" }, hello => hello.Protocol == Protocol.Version ? null : $"unparsable Hello is {hello.Protocol}");
            Assert(failure == null, failure ?? "");
        });
        runner.Run("launch-args/--tune overrides apply to the given tuning, converting each type", () =>
        {
            With(new[] { "--tune.MaxPlayers=7", "--tune.ActorPoseRate=45.5", "--tune.ActorPoseLod=false", "--Tune.MaxPlayers=3", "--tuning.MaxPlayers=4" }, args =>
            {
                var target = new Tuning();
                var untouched = new Tuning();
                args.ApplyTuningOverrides(target);
                Assert(target.MaxPlayers == 7 && target.ActorPoseRate == 45.5f && !target.ActorPoseLod, "int, float and bool tuners");
                Assert(untouched.MaxPlayers == 5 && untouched.ActorPoseRate == 30f && untouched.ActorPoseLod, "other tunings are untouched");
                var viaDev = new Tuning();
                DevArgs.ApplyTuningOverrides(viaDev);
                Assert(viaDev.MaxPlayers == 7 && viaDev.ActorPoseRate == 45.5f && !viaDev.ActorPoseLod, "DevArgs applies the shared arguments");
            });
            With(new[] { "--tune.MaxPlayers=7" }, args =>
            {
                var target = new Tuning();
                args.ApplyTuningOverrides(target);
                new LaunchArgs(Array.Empty<string>()).ApplyTuningOverrides(target);
                Assert(target.MaxPlayers == 7, "no overrides change nothing");
            });
        });
    }
}
