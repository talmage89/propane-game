using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Immediate-parent configuration witness: ordered exact wire deliveries, phase and retirement boundaries.</summary>
[UnitSuite]
public static class ApplicationServerTests
{
    public static void Register(TestRunner runner)
    {
        var transcript = new List<string>();
        var negative = DevArgs.Get("a1-server-negative") != null;
        foreach (var worlds in new[] { false, true })
        {
            foreach (var mode in new[] { ModeRegistry.TankBank, ModeRegistry.King })
            {
                runner.Run($"application/server configuration {mode.Name} worlds={worlds}", () =>
                {
                    using var launch = LaunchArgs.OverrideForTests(new LaunchArgs(new[] { "--match-seed=23" }));
                    var clock = new ManualClock();
                    var server = ModeRegistry.CreateServer(PacketTransports.CreateServer(), clock, initialMode: mode);
                    server.Worlds = worlds;
                    var driver = new ServerDriver(clock, server);
                    var record = server.SendOverride!;
                    var starts = 0;
                    var retired = 0;
                    server.RunCreated = run =>
                    {
                        transcript.Add($"run:{mode.Id}/{worlds}/{run.Id}/{run.IsWorld}");
                        run.Retired += () => { retired++; transcript.Add($"retired:{run.Id}/{run.IsRetired}"); };
                    };
                    server.SendOverride = (peer, bytes, traffic) =>
                    {
                        if (MessageCodec.Decode(bytes, true).Type == Msg.MatchStart) starts++;
                        transcript.Add($"wire:{peer}/{traffic}/{Convert.ToHexString(bytes)}");
                        record(peer, bytes, traffic);
                    };
                    try
                    {
                        driver.Connect(2, 0, true); driver.Connect(3, 1, true);
                        driver.CreateLobby(); driver.Join(3);
                        driver.Receive(new NetWriter(Msg.SetMatchLength).Float(10));
                        using var tuning = Tuning.LoadBaked();
                        tuning.LobbyStageTime = 2;
                        tuning.CountdownTime = 0;
                        tuning.ResultsTime = 1;
                        driver.StartMatch(tuning);
                        if (worlds) driver.Advance(2.1);
                        var setup = driver.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().Last();
                        Assert(setup.ModeId == mode.Id, "match configuration was not delivered");
                        foreach (var peer in new[] { 2, 3 }) driver.Receive(new NetWriter(Msg.Loaded, setup.Id), peer);
                        driver.Advance(); driver.Advance();
                        driver.Receive(new NetWriter(Msg.TuningSet, setup.Id).String("ResultsTime").Scalar(negative ? 1f : 2f));
                        var beforeLockedEdit = driver.Packets.Count;
                        driver.Receive(new NetWriter(Msg.TuningSet, setup.Id).String(nameof(Tuning.TankCountMultiplier)).Scalar(8));
                        Assert(driver.Packets.Count == beforeLockedEdit, "locked supply edit was relayed");
                        driver.Advance(11);
                        Assert(driver.Packets.Count(p => p.Message.Type == Msg.MatchEnd) == 2, "missing terminal standings");
                        var before = retired;
                        driver.Advance(1.1);
                        Assert(retired == before, "live ResultsTime edit was not sampled");
                        driver.Advance(1);
                        Assert(retired == before + 1, "run did not retire after live results duration");
                        server.OnDisconnected(2); server.OnDisconnected(3);
                        Assert(starts >= 2 && retired == (worlds ? 3 : 1), "configuration witness lost run coverage");
                    }
                    finally { server.Free(); }
                });
            }
        }

        if (DevArgs.Get("application-transcript") is { } path)
            File.WriteAllText(path, JsonSerializer.Serialize(transcript));
    }
}
