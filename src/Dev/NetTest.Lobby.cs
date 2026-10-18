using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.ReleaseSmoke;

namespace Propane.Dev;

/// <summary>
/// <c>--lobby-roam=S</c>: this client enters its lobby's world, and its bot roams, shoots and throws there for S
/// seconds before the host starts each match, and again after the last one returns everyone to the world.
/// </summary>
public partial class NetTest
{
    private bool roam;
    private float roamSeconds;
    private Match? world;
    private Bot? worldBot;
    private float worldClock;
    private bool worldBottomless;
    private int worldShots, worldsEntered;
    private readonly Dictionary<int, (Vector3 Last, float Path)> worldPaths = new();

    private void EnterLobbyWorld(MatchSetup setup)
    {
        EndLobbyWorld();
        // The server opens the world after its results; this client's own results may still be showing.
        if (match != null) finishMatch?.Invoke();
        world = new Match { Name = "LobbyWorld", Net = net, Setup = setup, Returning = worldsEntered > 0, CreateLocalView = DisplayServer.GetName() != "headless" };
        AddChild(world);
        worldsEntered++;
        worldBot = new Bot(ModeRegistry.Resolve(setup)) { Skill = DevArgs.GetFloat("bot-skill", 1f) };
        world.Player.InputOverride = worldBot;
        // The harness's bot plays by its magazine, as the world's own bots do: where rifles never run dry, one whose
        // reserve stays full.
        worldBottomless = world.Player.Ammo == null;
        if (worldBottomless) world.Player.Ammo = AmmoState.Starting(world.World.Tuning);
        world.Player.Fired += _ => worldShots++;
        worldClock = 0;
        worldShots = 0;
        worldPaths.Clear();
        Log($"lobby world {setup.Id} entered with {world.Session.Actors.Count} actors, {world.LiveTanks.Count()} tanks; rifles {(worldBottomless ? "bottomless" : "limited")}");
    }

    /// <summary>Retires the world's replication before the match (or the next world) registers its own.</summary>
    private void EndLobbyWorld()
    {
        if (world == null) return;
        if (IsInstanceValid(world))
        {
            var paths = string.Join(" ", worldPaths.OrderBy(p => p.Key).Select(p => FormattableString.Invariant($"{p.Key}={p.Value.Path:0}m")));
            Log(FormattableString.Invariant($"lobby world {world.Setup.Id} left after {worldClock:0.0} s: {world.Session.Actors.Count} actors, {world.LiveTanks.Count()} tanks, {worldShots} shots; owned paths {paths}"));
            world.Session.Dispose();
            world.Free();
        }
        world = null;
        worldBot?.Dispose();
        worldBot = null;
    }

    private void ProcessLobbyWorld(float dt)
    {
        if (world == null || !IsInstanceValid(world)) return;
        worldClock += dt;
        if (worldBottomless) world.Player.Ammo!.Reserve = world.World.Tuning.MaxReserve;
        worldBot!.Think(world, dt);
        foreach (var actor in world.Session.OwnedActors)
        {
            var position = actor.Character.GlobalPosition;
            worldPaths[actor.Character.ActorId] = worldPaths.TryGetValue(actor.Character.ActorId, out var path)
                ? (position, path.Path + new Vector2(position.X - path.Last.X, position.Z - path.Last.Z).Length())
                : (position, 0f);
        }
        if (Mathf.FloorToInt(worldClock / 5f) != Mathf.FloorToInt((worldClock - dt) / 5f))
        {
            Log(FormattableString.Invariant($"lobby world {world.Setup.Id} at {worldClock:0} s: {world.Session.Actors.Count} actors, {world.LiveTanks.Count()} tanks, {worldShots} shots, phase {net.Lobby?.Phase}"));
        }
        TryStartMatch();
        if (matches >= matchesWanted && worldClock >= roamSeconds && !quitting)
        {
            quitting = true;
            Log($"roamed the lobby world after {matches} matches; done");
            EndLobbyWorld();
            GetTree().CreateTimer(1.0).Timeout += () => ShutdownGuard.Quit(GetTree());
        }
    }

    private bool quitting;

    /// <summary>A roaming host starts once everyone has roamed the current world long enough.</summary>
    private bool Roamed => !roam || world != null && worldClock >= roamSeconds;
}
