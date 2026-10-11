using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Net;

public sealed record NetMessage(Msg Type, int MatchId, int Sender, object Payload);
public sealed record EmptyMessage;
public sealed record IntMessage(int Value);
public sealed record FloatMessage(float Value);
public sealed record TimeMessage(double Value);
public sealed record TextMessage(string Value);
public sealed record HelloMessage(int Protocol, string Version, string Build, string Name, int Color, bool Debug);
public sealed record WelcomeMessage(int Id, string? Version);
public sealed record RejectedMessage(string Reason, string? Version);
public sealed record ProfileMessage(string Name, int Color);
public sealed record LobbyListMessage(int Capacity, IReadOnlyList<LobbySummary> Lobbies);
public sealed record ScoreMessage(int Player, int Score, int Delta);
public sealed record PickupMessage(int Index, int Player);
public sealed record PlayerLeftMessage(int Player, int Host);
public sealed record PongMessage(double Sent, double ServerTime);
public sealed record PlayerStateMessage(double Time, PlayerNetState State);
public sealed record ShotMessage(Vector3 End, Vector3 Normal, Vector3 Direction, byte Impact, int Victim, int PropIndex);
public sealed record PunctureMessage(uint Id, Transform3D Transform, Vector3 Linear, Vector3 Angular, Vector3 Hole, Vector3 Normal, float Swirl);
public sealed record ExplodeMessage(uint Id, Vector3 Center);
public sealed record ClaimMessage(IReadOnlyList<uint> Ids);
public sealed record BodyState(uint Id, Transform3D Transform, Vector3 Linear, Vector3 Angular);
public sealed record BodyStatesMessage(double Time, IReadOnlyList<BodyState> Bodies);
public sealed record BodyRestMessage(uint Id, Transform3D Transform);
public sealed record DropMessage(byte Reason, int Color, IReadOnlyList<BodyState> Tanks);
public sealed record RagdollMessage(Vector3 Position, Vector3 Velocity, Vector3 Spin);
public sealed record GetUpMessage(Vector3 Position, float Yaw);
public sealed record TuningMessage(string Name, Variant Value);

/// <summary>
/// The whole wire schema. Decode finishes and validates every field before returning data to a handler. Existence,
/// membership and score checks belong to the server's match ledger; there is no physics or hit adjudication here.
/// </summary>
public static class MessageCodec
{
    public static NetMessage Decode(byte[] data, bool fromServer)
    {
        if (data.Length == 0 || data.Length > Protocol.MaxPacketBytes) { throw new InvalidDataException("bad packet size"); }
        var type = (Msg)data[0];
        var relay = Protocol.IsRelay(type);
        if (!(fromServer ? type is >= Msg.Welcome and <= Msg.Pong || relay : type is >= Msg.Hello and <= Msg.Ping || relay))
        {
            throw new InvalidDataException($"unknown or wrong-direction message {(byte)type}");
        }
        var r = new NetReader(data);
        var matchId = Protocol.IsMatch(type) ? Positive(r.Int()) : 0;
        var sender = relay && fromServer ? Positive(r.Int()) : 0;
        object payload = type switch
        {
            Msg.Hello => ReadHello(r),
            Msg.CreateLobby or Msg.LeaveLobby or Msg.Loaded => new EmptyMessage(),
            Msg.JoinLobby => new IntMessage(Positive(r.Int())),
            Msg.SetProfile => new ProfileMessage(r.String(), ColorIndex(r.Int())),
            Msg.SetMatchLength => new FloatMessage(Range(r.Float(), 10, 3600)),
            Msg.StartMatch => r.Snapshot(),
            Msg.ClaimPickup => new IntMessage(Nonnegative(r.Int())),
            Msg.Ping => new TimeMessage(Time(r.Double())),
            Msg.Welcome => new WelcomeMessage(Positive(r.Int()), r.AtEnd ? null : r.String()),
            Msg.Rejected => new RejectedMessage(r.String(), r.AtEnd ? null : r.String()),
            Msg.LobbyList => ReadLobbyList(r),
            Msg.LobbyState => ReadLobby(r),
            Msg.MatchStart => ReadSetup(r, matchId),
            Msg.Countdown => new FloatMessage(Range(r.Float(), 0, 10)),
            Msg.MatchGo or Msg.Clock => new FloatMessage(Range(r.Float(), 0, 3600)),
            Msg.Score => new ScoreMessage(Positive(r.Int()), Nonnegative(r.Int()), r.Int()),
            Msg.Notice => new TextMessage(r.String()),
            Msg.PickupTaken => new PickupMessage(Nonnegative(r.Int()), Positive(r.Int())),
            Msg.PickupRespawn => new IntMessage(Nonnegative(r.Int())),
            Msg.MatchEnd => ReadArray(r, r.Int(), Protocol.PlayerColors.Length, () =>
                new Standing(Positive(r.Int()), r.String(), ColorIndex(r.Int()), Nonnegative(r.Int()), r.Bool()), min: 1),
            Msg.PlayerLeft => new PlayerLeftMessage(Positive(r.Int()), Positive(r.Int())),
            Msg.Pong => new PongMessage(Time(r.Double()), Time(r.Double())),
            Msg.PlayerState => ReadPlayerState(r),
            Msg.Shot => ReadShot(r),
            Msg.Puncture => ReadPuncture(r),
            Msg.Explode => new ExplodeMessage(Id(r.UInt()), Vector(r)),
            Msg.Claim => new ClaimMessage(Unique(ReadArray(r, r.UShort(), Protocol.MaxCollection, () => Id(r.UInt()), min: 1))),
            Msg.BodyStates => new BodyStatesMessage(Time(r.Double()), ReadBodies(r, r.UShort(), Protocol.MaxBodyStates)),
            Msg.BodyRest => new BodyRestMessage(Id(r.UInt()), Transform(r)),
            Msg.Drop => ReadDrop(r),
            Msg.Ragdoll => new RagdollMessage(Vector(r), Vector(r), Vector(r)),
            Msg.GetUp => new GetUpMessage(Vector(r), Range(r.Float(), -1_000_000, 1_000_000)),
            Msg.TuningSet => ReadTuning(r),
            _ => throw new InvalidDataException("unsupported message"),
        };
        // These three handshakes may gain trailing fields, even across incompatible protocols.
        if (type is not (Msg.Hello or Msg.Welcome or Msg.Rejected)) { r.End(); }
        return new NetMessage(type, matchId, sender, payload);
    }

    public static byte[] Encode(NetMessage message, bool fromServer)
    {
        var w = new NetWriter(message.Type, message.MatchId);
        if (Protocol.IsRelay(message.Type) && fromServer) { w.Int(message.Sender); }
        switch (message.Payload)
        {
            case EmptyMessage: break;
            case IntMessage p: w.Int(p.Value); break;
            case FloatMessage p: w.Float(p.Value); break;
            case TimeMessage p: w.Double(p.Value); break;
            case TextMessage p: w.String(p.Value); break;
            case HelloMessage p: w.Int(p.Protocol).String(p.Version).String(p.Build).String(p.Name).Int(p.Color).Bool(p.Debug); break;
            case WelcomeMessage p:
                w.Int(p.Id); if (p.Version != null) { w.String(p.Version); } break;
            case RejectedMessage p:
                w.String(p.Reason); if (p.Version != null) { w.String(p.Version); } break;
            case ProfileMessage p: w.String(p.Name).Int(p.Color); break;
            case Godot.Collections.Dictionary p: w.Snapshot(p); break;
            case LobbyListMessage p:
                w.Int(p.Capacity).Int(p.Lobbies.Count);
                foreach (var lobby in p.Lobbies) { w.Int(lobby.Id).String(lobby.CreatorName).Int(lobby.Players).Byte((byte)lobby.Phase); }
                break;
            case LobbyInfo p:
                w.Int(p.Id).Int(p.CreatorId).Float(p.MatchLength).Byte((byte)p.Phase).Int(p.Members.Count);
                foreach (var player in p.Members) { WriteProfile(w, player); }
                break;
            case MatchSetup p:
                w.Bytes(p.Plan).Int(p.HostId).Bool(p.CreatorDebug).Float(p.Length).Snapshot(p.Tuning).Int(p.Players.Count);
                foreach (var (player, spawn) in p.Players) { WriteProfile(w, player).Int(spawn); }
                break;
            case ScoreMessage p: w.Int(p.Player).Int(p.Score).Int(p.Delta); break;
            case PickupMessage p: w.Int(p.Index).Int(p.Player); break;
            case IReadOnlyList<Standing> p:
                w.Int(p.Count);
                foreach (var player in p) { w.Int(player.Id).String(player.Name).Int(player.Color).Int(player.Score).Bool(player.Present); }
                break;
            case PlayerLeftMessage p: w.Int(p.Player).Int(p.Host); break;
            case PongMessage p: w.Double(p.Sent).Double(p.ServerTime); break;
            case PlayerStateMessage p:
                var s = p.State;
                w.Double(p.Time).Vec3(s.Position).Vec3(s.Velocity).Float(s.Yaw).Vec3(s.AimTarget)
                    .Byte(ToByte(s.Aim)).Byte(ToByte(s.Sprint)).Byte(ToByte(s.Ready)).Byte(ToByte(s.Reload))
                    .Byte((byte)((s.Grounded ? 1 : 0) | ((byte)s.State << 1)))
                    .Vec3(s.PelvisPosition).Vec3(s.PelvisVelocity).Byte(s.Staggers);
                break;
            case ShotMessage p: w.Vec3(p.End).Vec3(p.Normal).Vec3(p.Direction).Byte(p.Impact).Int(p.Victim).Int(p.PropIndex); break;
            case PunctureMessage p:
                w.UInt(p.Id).Transform(p.Transform).Vec3(p.Linear).Vec3(p.Angular).Vec3(p.Hole).Vec3(p.Normal).Float(p.Swirl); break;
            case ExplodeMessage p: w.UInt(p.Id).Vec3(p.Center); break;
            case ClaimMessage p:
                w.UShort((ushort)p.Ids.Count); foreach (var id in p.Ids) { w.UInt(id); } break;
            case BodyStatesMessage p:
                w.Double(p.Time).UShort((ushort)p.Bodies.Count); foreach (var body in p.Bodies) { WriteBody(w, body); } break;
            case BodyRestMessage p: w.UInt(p.Id).Transform(p.Transform); break;
            case DropMessage p:
                w.Byte(p.Reason).Int(p.Tanks.Count).Int(p.Color); foreach (var body in p.Tanks) { WriteBody(w, body); } break;
            case RagdollMessage p: w.Vec3(p.Position).Vec3(p.Velocity).Vec3(p.Spin); break;
            case GetUpMessage p: w.Vec3(p.Position).Float(p.Yaw); break;
            case TuningMessage p: w.String(p.Name).Scalar(p.Value); break;
            default: throw new InvalidDataException("unsupported message payload");
        }
        return w.ToArray();
    }

    private static HelloMessage ReadHello(NetReader r)
    {
        var protocol = r.Int(); var version = r.String(); var build = r.String(); var name = r.String(); var color = ColorIndex(r.Int());
        return new HelloMessage(protocol, version, build, name, color, protocol == Protocol.Version ? r.Bool() : !r.AtEnd && r.Bool());
    }

    private static LobbyListMessage ReadLobbyList(NetReader r) => new(Range(r.Int(), 1, Protocol.PlayerColors.Length),
        ReadArray(r, r.Int(), 64, () => new LobbySummary(Positive(r.Int()), r.String(), Range(r.Int(), 1, Protocol.PlayerColors.Length), Phase(r.Byte()))));

    private static LobbyInfo ReadLobby(NetReader r)
    {
        var id = Positive(r.Int()); var creator = Positive(r.Int()); var length = Range(r.Float(), 10, 3600); var phase = Phase(r.Byte());
        var members = ReadArray(r, r.Int(), Protocol.PlayerColors.Length, () => ReadProfile(r), min: 1);
        Unique(members.Select(p => p.Id).ToArray());
        if (!members.Any(p => p.Id == creator)) { throw new InvalidDataException("creator missing from lobby"); }
        return new LobbyInfo(id, creator, length, phase, members);
    }

    private static MatchSetup ReadSetup(NetReader r, int matchId)
    {
        var plan = r.Bytes(); var host = Positive(r.Int()); var debug = r.Bool(); var length = Range(r.Float(), 10, 3600); var tuning = r.Snapshot();
        var players = ReadArray(r, r.Int(), Protocol.PlayerColors.Length, () => (ReadProfile(r), Range(r.Int(), 0, Protocol.PlayerColors.Length - 1)), min: 1);
        Unique(players.Select(p => p.Item1.Id).ToArray());
        Unique(players.Select(p => p.Item2).ToArray());
        if (!players.Any(p => p.Item1.Id == host)) { throw new InvalidDataException("host missing from match"); }
        return new MatchSetup(matchId, plan, host, debug, length, tuning, players);
    }

    private static PlayerProfile ReadProfile(NetReader r) => new(Positive(r.Int()), r.String(), ColorIndex(r.Int()));

    private static PlayerStateMessage ReadPlayerState(NetReader r)
    {
        var time = Time(r.Double());
        var state = new PlayerNetState { Position = Vector(r), Velocity = Vector(r), Yaw = Range(r.Float(), -1_000_000, 1_000_000),
            AimTarget = Vector(r), Aim = r.Byte() / 255f, Sprint = r.Byte() / 255f, Ready = r.Byte() / 255f, Reload = r.Byte() / 255f };
        var flags = Range(r.Byte(), 0, 5);
        state.Grounded = (flags & 1) != 0; state.State = (PlayerNetState.Mode)(flags >> 1);
        state.PelvisPosition = Vector(r); state.PelvisVelocity = Vector(r); state.Staggers = r.Byte();
        return new PlayerStateMessage(time, state);
    }

    private static ShotMessage ReadShot(NetReader r)
    {
        var end = Vector(r); var normal = Normal(r, allowZero: true); var direction = Normal(r);
        return new ShotMessage(end, normal, direction, (byte)Range(r.Byte(), 0, 7), Nonnegative(r.Int()), Range(r.Int(), -1, int.MaxValue));
    }

    private static PunctureMessage ReadPuncture(NetReader r) => new(Id(r.UInt()), Transform(r), Vector(r), Vector(r), Vector(r), Normal(r), Range(r.Float(), -1, 1));

    private static DropMessage ReadDrop(NetReader r)
    {
        var reason = (byte)Range(r.Byte(), 0, 1); var count = r.Int(); var color = ColorIndex(r.Int());
        return new DropMessage(reason, color, ReadBodies(r, count, Protocol.MaxCollection));
    }

    private static BodyState[] ReadBodies(NetReader r, int count, int max)
    {
        var bodies = ReadArray(r, count, max, () => new BodyState(Id(r.UInt()), Transform(r), Vector(r), Vector(r)), min: 1);
        Unique(bodies.Select(b => b.Id).ToArray());
        return bodies;
    }

    private static TuningMessage ReadTuning(NetReader r)
    {
        var name = r.String(); var value = r.Scalar();
        Tuning.ValidateSnapshot(new Godot.Collections.Dictionary { [name] = value });
        return new TuningMessage(name, value);
    }

    private static T[] ReadArray<T>(NetReader r, int count, int max, Func<T> read, int min = 0)
    {
        NetReader.Count(count, max, min);
        if (count > r.Remaining) { throw new InvalidDataException("truncated collection"); }
        var array = new T[count];
        for (var i = 0; i < count; i++) { array[i] = read(); }
        return array;
    }

    private static T[] Unique<T>(T[] values)
    {
        if (values.Distinct().Count() != values.Length) { throw new InvalidDataException("duplicate ids"); }
        return values;
    }

    private static int Positive(int n) => Range(n, 1, int.MaxValue);
    private static int Nonnegative(int n) => Range(n, 0, int.MaxValue);
    private static uint Id(uint id) => id != 0 ? id : throw new InvalidDataException("zero body id");
    private static int ColorIndex(int n) => Range(n, 0, Protocol.PlayerColors.Length - 1);
    private static LobbyServer.LobbyPhase Phase(byte n) => (LobbyServer.LobbyPhase)Range(n, 0, (int)LobbyServer.LobbyPhase.Results);
    private static int Range(int n, int min, int max) => n >= min && n <= max ? n : throw new InvalidDataException($"integer out of range: {n}");
    private static float Range(float n, float min, float max) => n >= min && n <= max ? n : throw new InvalidDataException($"float out of range: {n}");
    private static double Time(double n) => n is >= 0 and <= 1e12 ? n : throw new InvalidDataException("bad timestamp");
    private static byte ToByte(float n) => (byte)Mathf.RoundToInt(n * 255);
    private static Vector3 Vector(NetReader r)
    {
        var v = r.Vec3();
        if (Mathf.Abs(v.X) > 1_000_000 || Mathf.Abs(v.Y) > 1_000_000 || Mathf.Abs(v.Z) > 1_000_000) { throw new InvalidDataException("vector outside wire limits"); }
        return v;
    }
    private static Vector3 Normal(NetReader r, bool allowZero = false)
    {
        var v = Vector(r);
        if (!(allowZero && v == Vector3.Zero) && v.LengthSquared() is < 0.9f or > 1.1f) { throw new InvalidDataException("bad direction vector"); }
        return v;
    }
    private static Transform3D Transform(NetReader r)
    {
        var origin = Vector(r);
        return new Transform3D(new Basis(r.Quat()), origin);
    }
    private static NetWriter WriteProfile(NetWriter w, PlayerProfile p) => w.Int(p.Id).String(p.Name).Int(p.Color);
    private static void WriteBody(NetWriter w, BodyState p) => w.UInt(p.Id).Transform(p.Transform).Vec3(p.Linear).Vec3(p.Angular);
}
