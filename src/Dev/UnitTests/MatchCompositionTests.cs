using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Exercise the same callback and lifetime workload on both sides of Match's composition extraction.</summary>
[UnitSuite]
public static class MatchCompositionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("composition/repeated retired matches release their managed owners", () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var references = Retire(runner);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(references.All(reference => !reference.IsAlive), "retired match retained an owner or actor attachment");
            }
        });
        foreach (var mode in new[] { ModeRegistry.TankBank, ModeRegistry.King })
        {
            runner.Run($"composition/{mode.Name} remote delivery follows the current input source", () =>
            {
                using var server = new ServerFixture(mode: mode);
                using var client = new MatchClient(runner, server.Setup, server.Clock, 2, createLocalView: false);
                var remote = client.Match.RemoteBodies.First();
                var original = (ReplicatedIntent)remote.InputOverride!;
                remote.InputOverride = null;
                var state = remote.CaptureNetState();
                client.Match.OnMessage(new NetMessage(Msg.PlayerState, server.Id, remote.OwnerPeerId,
                    new PlayerStateMessage(server.Clock.Seconds, state), remote.ActorId, remote.LifeId, 1));
                Assert(!original.Snapshots.HasData, "detached input source received a snapshot");
                var replacement = new ReplicatedIntent(client.Match.World.Tuning);
                remote.InputOverride = replacement;
                client.Match.OnMessage(new NetMessage(Msg.PlayerState, server.Id, remote.OwnerPeerId,
                    new PlayerStateMessage(server.Clock.Seconds, state), remote.ActorId, remote.LifeId, 2));
                Assert(replacement.Snapshots.HasData && !original.Snapshots.HasData, "snapshot did not reach the replacement input source");
            });
            runner.Run($"composition/{mode.Name} snapshot application follows the current input source", () =>
            {
                using var server = new ServerFixture(mode: mode);
                using var client = new MatchClient(runner, server.Setup, server.Clock, 2, createLocalView: false);
                var remote = client.Match.RemoteBodies.First();
                var original = (ReplicatedIntent)remote.InputOverride!;
                var state = remote.CaptureNetState();
                original.Snapshots.Add(server.Clock.Seconds, state, server.Clock.Seconds);
                var input = new CountingInput();
                remote.InputOverride = input;
                client.Match._PhysicsProcess(1d / 120);
                Assert(!(bool)ActorLifetimeFixture.Field(remote, "hasRemoteState") && input.Reads == 0,
                    "snapshot application bypassed a temporary input override");
                remote.InputOverride = null;
                client.Match._PhysicsProcess(1d / 120);
                Assert(!(bool)ActorLifetimeFixture.Field(remote, "hasRemoteState"), "snapshot application bypassed an absent input source");
                var replacement = new ReplicatedIntent(client.Match.World.Tuning);
                state.Position += Vector3.Right * 5;
                replacement.Snapshots.Add(server.Clock.Seconds, state, server.Clock.Seconds);
                remote.InputOverride = replacement;
                client.Match._PhysicsProcess(1d / 120);
                Assert((bool)ActorLifetimeFixture.Field(remote, "hasRemoteState") &&
                    ((PlayerNetState)ActorLifetimeFixture.Field(remote, "remoteState")).Position == state.Position,
                    "snapshot application ignored the replacement input source");
            });
            runner.Run($"composition/{mode.Name} callbacks without a camera", () =>
            {
                using var server = new ServerFixture(bots: 1, mode: mode);
                using var client = new MatchClient(runner, server.Setup, server.Clock, 2, createLocalView: false);
                var match = client.Match;
                Assert(match.Session.LocalViews.Count == 0, "no-camera match attached a local view");
                var owned = match.Player;
                var remote = match.RemoteBodies.First();
                var bot = match.Session.OwnedActors.First(a => a.Character != owned).Character;
                Assert(match.BotBrain(bot.ActorId) != null, "no-camera match has no bot brain");
                var input = new CountingInput();
                owned.InputOverride = input;
                match.OnMessage(new NetMessage(Msg.Countdown, server.Id, 0, new FloatMessage(1)));
                match._Process(1d / 60);
                match.OnMessage(new NetMessage(Msg.MatchGo, server.Id, 0, new FloatMessage(10)));
                match.OnMessage(new NetMessage(Msg.PlayerState, server.Id, remote.OwnerPeerId,
                    new PlayerStateMessage(server.Clock.Seconds, remote.CaptureNetState()), remote.ActorId, remote.LifeId, 1));
                match._PhysicsProcess(1d / 120);
                Assert(input.Reads == 0, "Match read actor input ahead of the physical callback");
                owned._PhysicsProcess(1d / 120);
                owned._Process(1d / 60);
                remote._PhysicsProcess(1d / 120);
                remote._Process(1d / 60);
                bot._PhysicsProcess(1d / 120);
                bot._Process(1d / 60);
                Assert(input.Reads == 1 && owned.Runtime.Intent.FirePressed, "physical callback did not consume exactly one intent");
                Assert(owned.Locomotion != null && remote.Locomotion != null && bot.Locomotion != null, "camera-free actors lost animation");
                owned.ReceiveMelee(Vector3.Forward);
                Assert(owned.IsRagdolled, "camera-free actor did not enter ragdoll");
                ActorLifetimeFixture.Call(owned, "BeginGetUp");
                match.OnMessage(new NetMessage(Msg.MatchEnd, server.Id, 0, Array.Empty<Standing>()));
                Assert(match.IsOver && owned.InputLocked && match.Session.IsAlive, "results changed actor/session lifetime");
                match.Session.Dispose();
                match.Session.Dispose();
                Assert(match.Session.Actors.Count == 0, "session retirement retained actors");
            });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Retire(TestRunner runner)
    {
        using var server = new ServerFixture(bots: 1);
        using var client = new MatchClient(runner, server.Setup, server.Clock, 2);
        var match = client.Match;
        var references = new List<WeakReference> { new(match), new(match.Session), new(match.World) };
        foreach (var name in new[] { "worldReplica", "actorReplication", "ownedBots", "viewBinding" })
        {
            if (typeof(Match).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(match) is { } owner)
                references.Add(new WeakReference(owner));
        }

        foreach (var actor in match.Session.Actors) references.Add(new WeakReference(ClientActorAccess.For(actor.Character).State));
        return references.ToArray();
    }

    private sealed class CountingInput : IPlayerInputSource
    {
        public int Reads { get; private set; }
        public PlayerIntent Read() { Reads++; return new PlayerIntent { FirePressed = true }; }
    }
}
