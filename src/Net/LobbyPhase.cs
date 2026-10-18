namespace Propane.Net;

/// <summary>Lobby phases shared by server, clients and the wire. Numeric values are stable.</summary>
public enum LobbyPhase : byte
{
    Waiting,
    Loading,
    Countdown,
    Playing,
    Results,
    /// <summary>The creator started: the stage counts down in the lobby world before the match loads.</summary>
    Staging,
}
