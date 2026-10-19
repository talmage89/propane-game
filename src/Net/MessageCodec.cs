using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;

namespace Propane.Net;

public sealed record NetMessage(Msg Type, int MatchId, int Sender, object Payload, int ActorId = 0, uint LifeId = 0, uint ActionId = 0);
/// <summary>Roster requests name the roster revision they were made against; the server refuses a stale one.</summary>
public sealed record AddActorMessage(uint RequestId, ActorKind Kind, string Name, AppearanceRecord Appearance, ControllerConfig Config, uint Revision);
public sealed record UpdateActorMessage(uint RequestId, int ActorId, string Name, AppearanceRecord Appearance, ControllerConfig Config, uint Revision);
public sealed record RemoveActorMessage(uint RequestId, int ActorId, uint Revision);
public sealed record RosterStateMessage(int LobbyId, uint Revision, IReadOnlyList<PlayerProfile> Actors);
public sealed record ActorRequestResultMessage(uint RequestId, bool Accepted, uint Revision, string Reason);
public sealed record EmptyMessage;
public sealed record IntMessage(int Value);
public sealed record FloatMessage(float Value);
public sealed record TimeMessage(double Value);
public sealed record TextMessage(string Value);
public sealed record HelloMessage(int Protocol, string Version, string Build, string Name, int Color, bool Debug, AppearanceRecord Appearance);
public sealed record WelcomeMessage(int Id, string? Version);
public sealed record RejectedMessage(string Reason, string? Version);
public sealed record ProfileMessage(string Name, AppearanceRecord Appearance);
public sealed record LobbyListMessage(int Capacity, IReadOnlyList<LobbySummary> Lobbies);
public sealed record PickupMessage(int Index, int Player);
public sealed record PlayerLeftMessage(int Player, int Host);
public sealed record PongMessage(double Sent, double ServerTime);
public sealed record PlayerStateMessage(double Time, PlayerNetState State);
public sealed record MeleeStartMessage(uint Swing, Vector3 Direction);
public sealed record MeleeContactMessage(uint Swing, int Victim, int PropIndex, uint VictimLifeId = 1);
public sealed record ShotMessage(Vector3 End, Vector3 Normal, Vector3 Direction, byte Impact, int Victim, int PropIndex, uint VictimLifeId = 1);
public sealed record PunctureMessage(uint Id, Transform3D Transform, Vector3 Linear, Vector3 Angular, Vector3 Hole, Vector3 Normal, float Swirl, uint Epoch = 1, uint Request = 1, uint Cause = 0);
public sealed record ExplodeMessage(uint Id, Vector3 Center, uint Epoch = 1, uint Request = 1, uint Cause = 0);
public sealed record ClaimMessage(uint Request, IReadOnlyList<BodyState> Bodies, uint Cause = 0);
public enum ClaimRefusal : byte { None, StaleEpoch, Exploded, WrongPhase, Lifecycle, Cause, UnknownBody, DuplicateRequest }
public sealed record BodyAuthority(BodyState Body, int Owner, BodyState.State State, bool Rest, VentParameters? Vent, Vector3 Center, int CauseActorId = 0);
public sealed record AuthorityMessage(int Peer, uint Request, ClaimRefusal Reason, IReadOnlyList<BodyAuthority> Bodies);
public sealed record BodyStatesMessage(double Time, IReadOnlyList<BodyState> Bodies);
public sealed record BodyRestMessage(uint Id, Transform3D Transform, double Time, uint Epoch = 1, uint Sequence = 1);
public sealed record GatherMessage(GatherPhase Phase, IReadOnlyList<BodyState> Bodies);
public sealed record RagdollMessage(Vector3 Position, Vector3 Velocity, Vector3 Spin);
public sealed record GetUpMessage(Vector3 Position, float Yaw);
public sealed record TuningMessage(string Name, Variant Value);
