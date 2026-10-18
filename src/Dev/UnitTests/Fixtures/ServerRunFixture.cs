using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Tracks native resource release and routing at every run's retirement, including failed construction.</summary>
internal sealed class ServerRunFixture : IDisposable
{
    public readonly ManualClock Clock = new();
    public readonly LobbyServer Server;
    public readonly ServerDriver Driver;
    public readonly Tuning Tuning = new() { CountdownTime = 0, ResultsTime = 1, LobbyStageTime = 0 };
    public readonly List<Release> Runs = new();

    public ServerRunFixture(bool worlds = false, Func<ParticipantRoster>? roster = null, ModeDefinition? mode = null)
    {
        Server = new LobbyServer(PacketTransports.CreateServer(), Clock) { Worlds = worlds, LobbyMode = mode ?? ModeRegistry.TankBank };
        if (roster != null) Server.RosterFactory = roster;
        Server.RunCreated = run => Runs.Add(new Release(Server, run));
        Driver = new ServerDriver(Clock, Server);
        Driver.Connect(2, 0, true);
        Driver.Connect(3, 1, true);
        Driver.CreateLobby();
        Driver.Join(3);
        Driver.Receive(new NetWriter(Msg.SetMatchLength).Float(10));
    }

    public void Start()
    {
        Driver.StartMatch(Tuning);
        if (Runs.Last().Run.IsWorld) return; // Staging has not replaced it yet.
        var id = Runs.Last().Run.Id;
        foreach (var peer in new[] { 2, 3 }) Driver.Receive(new NetWriter(Msg.Loaded, id), peer);
        Driver.Advance();
        Driver.Advance();
    }

    public void Dispose()
    {
        Server._ExitTree();
        Server.Free();
        Tuning.Dispose();
    }

    internal sealed class Release
    {
        public readonly ServerRun Run;
        public readonly Tuning Tuning;
        public int Count { get; private set; }
        public Release(LobbyServer server, ServerRun run)
        {
            Run = run;
            Tuning = ((IModeHost)run).Tuning;
            TestRunner.Assert(GodotObject.IsInstanceValid(Tuning), "run started with a disposed resource");
            run.Retired += () =>
            {
                Count++;
                TestRunner.Assert(run.IsRetired && !GodotObject.IsInstanceValid(Tuning), "retirement did not release its native tuning");
                var lobbies = (IDictionary)typeof(LobbyServer).GetField("lobbies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                // Exhaustion removes the lobby dictionary entry before rejecting peers. Their lobby references
                // must not keep a route to a released run during that interval.
                var clients = (IDictionary)typeof(LobbyServer).GetField("clients", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                var reachable = lobbies.Values.Cast<object>().Concat(clients.Values.Cast<object>()
                    .Select(client => client.GetType().GetField("Lobby")!.GetValue(client)).OfType<object>()).Distinct();
                foreach (var lobby in reachable)
                {
                    foreach (var name in new[] { "Match", "World" })
                        TestRunner.Assert(lobby.GetType().GetField(name)!.GetValue(lobby) != run, "tuning released before routing detached");
                }
            };
        }

        public void AssertLive() => TestRunner.Assert(Count == 0 && !Run.IsRetired && GodotObject.IsInstanceValid(Tuning), "active run retired early");
        public void AssertOnce()
        {
            TestRunner.Assert(Count == 1 && Run.IsRetired && !GodotObject.IsInstanceValid(Tuning), $"expected one release, got {Count}");
            Run.Dispose();
            Run.Dispose();
            TestRunner.Assert(Count == 1, "repeated retirement released resources again");
        }
    }
}
