using System;
using Propane.Core;

namespace Propane.Net;

/// <summary>
/// The lobby world: while a lobby waits, its members roam a void world together (the Lobby mode: no score, refilling
/// piles, rifles that never run dry). It is a run like a match, with its own id from the same sequence, its own ledger
/// and relays, so traffic for a world that has closed is stale by id. Unlike a match its membership follows the
/// lobby's roster live: whoever joins is admitted with a world start of their own (the void plan, every live body and
/// Go), and every peer reconciles its actors with the roster it is sent. The creator's start closes it.
/// </summary>
public partial class LobbyServer
{
    /// <summary>The longest stage a start may count down, in seconds (the tuner's ceiling; a wire bound on StartsIn).</summary>
    public const float MaxStage = 10;

    /// <summary>
    /// Whether waiting lobbies run a world. Off, lobbies wait in the menu as before (and tests that count packets may
    /// turn it off).
    /// </summary>
    public bool Worlds { get; set; } = LobbyWorldDefault.Enabled;

    /// <summary>The run a match message names: this lobby's current match or its current world.</summary>
    private static ServerRun? RunFor(Client client, int id) => client.Lobby switch
    {
        { Match: { } match } when match.Id == id => match,
        { World: { } world } when world.Id == id => world,
        _ => null,
    };

    /// <summary>
    /// A waiting lobby without a world gets one, stocked; its lobby state (naming the world) goes to every member, who
    /// are then admitted. False when it has one already or runs none.
    /// </summary>
    private bool OpenWorld(Lobby lobby)
    {
        if (!Worlds || lobby.World != null || lobby.Phase != LobbyPhase.Waiting || lobby.Members.Count == 0) return false;
        var now = clock.Seconds;
        // The server's own dials build roaming worlds; the lobby's choices apply too.
        var tuning = Tuning.SavedTemplate.Copy();
        ServerRun? world = null;
        try
        {
            tuning.KingMintSeconds = lobby.Settings.MintSeconds;
            tuning.TankCountMultiplier = lobby.Settings.TankSupply;
            world = NewRun(lobby, false, Math.Clamp(lobby.MatchLength, 10, 3600), true, tuning);
            RunCreated?.Invoke(world);
            world.InitializeWorld();
            lobby.World = world;
            world.StartWorld(now);
        }
        catch
        {
            if (lobby.World == world) lobby.World = null;
            if (world != null) world.Dispose();
            else tuning.Dispose();
            throw;
        }
        SendLobbyState(lobby);
        BroadcastLobbyList();
        return true;
    }

    /// <summary>The creator started: the world retires, and anything still sent to it is stale.</summary>
    private void CloseWorld(Lobby lobby)
    {
        if (lobby.World is not { } world) return;
        Retire(ref lobby.World);
        Log($"lobby {lobby.Id}: world {world.Id} closed");
    }

    /// <summary>
    /// Mirrors the roster into the world: new actors join (with their peers, if new), removed ones leave, and changed
    /// names and colors follow. Every lobby state broadcast ends here, so the world never lags the roster.
    /// </summary>
    private void SyncWorld(Lobby lobby)
    {
        if (lobby.World is not { } world) return;
        var now = clock.Seconds;
        world.SyncWorld(lobby.Roster.Present, lobby.Members, lobby.Roster.Revision, lobby.CreatorId, now);
    }
}

/// <summary>
/// Whether this process plays lobbies in a lobby world: the default. <c>--classic-lobby</c> keeps the lobby menu over
/// the backdrop instead (a client then ignores its lobby's world; a server started with it runs none), until the owner
/// has reviewed the world's look.
/// </summary>
public static class LobbyWorldDefault
{
    public static bool Enabled => !LaunchArgs.Current.Has("classic-lobby");
}
