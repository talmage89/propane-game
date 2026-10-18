using System.Collections.Generic;
using System.Linq;
using Propane.Core;

namespace Propane.Net;

/// <summary>A lobby as the lobby browser lists it, with the mode it will play.</summary>
public sealed record LobbySummary(int Id, string CreatorName, int Players, LobbyPhase Phase, int Capacity = 5,
    byte ModeId = ModeIds.TankBank);

/// <summary>
/// The mode a lobby's creator chose, tank supply for either mode, and King's mint pace (kept while Tank Bank is
/// chosen, so switching back remembers it). The lobby's match length is the round length of either.
/// </summary>
public sealed record ModeSettings(byte ModeId, float MintSeconds, int TankSupply = 1)
{
    public static readonly ModeSettings Default = new(ModeIds.TankBank, (float)LobbyModeLimits.MintPaceDefault);

    /// <summary>Numeric wire bounds; eligibility is supplied by the owner's compiled mode selection.</summary>
    public bool ValidValues => LobbyModeLimits.ValidMintPace(MintSeconds) &&
        TankSupply is >= LobbyModeLimits.TankSupplyMinimum and <= LobbyModeLimits.TankSupplyMaximum;

}

/// <summary>A player in a lobby or match.</summary>
public sealed record PlayerProfile(int Id, string Name, AppearanceRecord Appearance)
{
    public int OwnerPeerId { get; init; } = Id;
    public ActorKind Kind { get; init; }
    public ControllerConfig ControllerConfig { get; init; } = ControllerConfig.Human;
    public bool Present { get; init; } = true;
    public int AllocationSlot { get; init; }
    public uint LifeId { get; init; } = 1;
    public static PlayerProfile From(ActorRecord a) => new(a.ActorId, a.Name, a.Appearance)
    {
        OwnerPeerId = a.OwnerPeerId,
        Kind = a.Kind,
        ControllerConfig = a.ControllerConfig,
        Present = a.Present,
        AllocationSlot = a.AllocationSlot,
        LifeId = a.LifeId,
    };
}

/// <summary>The lobby the player is in, as the server last described it.</summary>
public sealed record LobbyInfo(int Id, int CreatorId, float MatchLength, LobbyPhase Phase, IReadOnlyList<PlayerProfile> Members, int Capacity = 5, uint RosterRevision = 1)
{
    public IReadOnlyList<int> Peers { get; init; } = Members.Select(a => a.OwnerPeerId).Distinct().ToArray();
    /// <summary>The mode its next match plays, and that mode's settings.</summary>
    public ModeSettings Mode { get; init; } = ModeSettings.Default;
    /// <summary>While staging: seconds until the match begins, as the server sent it.</summary>
    public float StartsIn { get; init; }
    /// <summary>The run id of the world its members roam while it waits, or 0 when it has none (in a match, or classic).</summary>
    public int WorldId { get; init; }
}

/// <summary>Everything a client needs to start a match.</summary>
public sealed record MatchSetup(int Id, byte[] Plan, int HostId, bool CreatorDebug, float Length, Godot.Collections.Dictionary Tuning, IReadOnlyList<(PlayerProfile Player, int Spawn)> Players)
{
    public uint RosterRevision { get; init; } = 1;
    public int CreatorId { get; init; } = HostId;
    public IReadOnlyList<PeerCapabilities> Peers { get; init; } = Players.Select(p => p.Player.OwnerPeerId).Distinct()
        .Select(id => new PeerCapabilities(id, id == HostId && CreatorDebug)).ToArray();
    /// <summary>The mode the server runs, by id and envelope schema version; unknown pairs fail decoding.</summary>
    public byte ModeId { get; init; } = ModeIds.TankBank;
    public ushort ModeSchema { get; init; } = ModeIds.TankBankSchema;
}
public sealed record PeerCapabilities(int PeerId, bool DebugTuning);
