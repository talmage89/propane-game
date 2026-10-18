using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;

namespace Propane.Net;

/// <summary>
/// The whole wire schema. Decode finishes and validates every field before returning data to a handler. Existence,
/// membership and score checks belong to the server's match ledger; there is no physics or hit adjudication here.
/// </summary>
public sealed class PacketCodec
{
    private readonly Func<byte, ushort, ModeSchema> schema;
    private readonly Func<byte, bool> lobbyMode;
    private readonly Action<Godot.Collections.Dictionary, bool> validateTuners;

    /// <summary>Immutable owner-scoped resolution; callbacks borrow no native resource or mutable registration.</summary>
    public PacketCodec(Func<byte, ushort, ModeSchema> schema, Func<byte, bool> lobbyMode,
        Action<Godot.Collections.Dictionary, bool> validateTuners)
    {
        this.schema = schema ?? throw new ArgumentNullException(nameof(schema));
        this.lobbyMode = lobbyMode ?? throw new ArgumentNullException(nameof(lobbyMode));
        this.validateTuners = validateTuners ?? throw new ArgumentNullException(nameof(validateTuners));
    }

    /// <summary>Only the bounded reader's expected failures qualify as malformed input, before dispatch.</summary>
    public static bool IsDecodeFailure(Exception error) => error is InvalidDataException or EndOfStreamException or
        FormatException or System.Text.DecoderFallbackException;

    public NetMessage Decode(byte[] data, bool fromServer)
    {
        if (data.Length == 0 || data.Length > Protocol.MaxPacketBytes) { throw new InvalidDataException("bad packet size"); }
        var type = (Msg)data[0];
        var relay = Protocol.IsRelay(type);
        if (!(fromServer ? type is >= Msg.Welcome and <= Msg.ModeSnapshot || relay : type is >= Msg.Hello and <= Msg.CancelStart || relay))
        {
            throw new InvalidDataException($"unknown or wrong-direction message {(byte)type}");
        }
        if (fromServer && type is Msg.Claim or Msg.Puncture or Msg.Explode) { throw new InvalidDataException("authority requests cannot be relayed"); }
        var r = new NetReader(data);
        var matchId = Protocol.IsMatch(type) ? Positive(r.Int()) : 0;
        var actor = Protocol.IsActorMessage(type) ? Positive(r.Int()) : 0;
        var life = Protocol.IsActorMessage(type) ? Id(r.UInt()) : 0;
        var action = Protocol.IsActorMessage(type) ? Id(r.UInt()) : 0;
        var sender = relay && fromServer ? Positive(r.Int()) : 0;
        object payload = type switch
        {
            Msg.Hello => ReadHello(r),
            Msg.CreateLobby or Msg.LeaveLobby or Msg.Loaded or Msg.CancelStart => new EmptyMessage(),
            Msg.JoinLobby => new IntMessage(Positive(r.Int())),
            Msg.SetProfile => new ProfileMessage(r.String(), r.Appearance()),
            Msg.SetCapacity => new IntMessage(Range(r.Int(), 2, Protocol.TestedActorCeiling)),
            Msg.AddActor => ReadAddActor(r),
            Msg.UpdateActor => new UpdateActorMessage(Id(r.UInt()), Positive(r.Int()), r.String(), r.Appearance(), ReadController(r), r.UInt()),
            Msg.RemoveActor => new RemoveActorMessage(Id(r.UInt()), Positive(r.Int()), r.UInt()),
            Msg.RosterState => new RosterStateMessage(Positive(r.Int()), Id(r.UInt()), ReadProfiles(r)),
            Msg.ActorRequestResult => new ActorRequestResultMessage(Id(r.UInt()), r.Bool(), Id(r.UInt()), r.String()),
            Msg.SetMatchLength => new FloatMessage(Range(r.Float(), 10, 3600)),
            Msg.SetMode => ReadModeSettings(r),
            Msg.StartMatch => r.Snapshot(validateTuners),
            Msg.ClaimPickup => new IntMessage(Nonnegative(r.Int())),
            Msg.Ping => new TimeMessage(Time(r.Double())),
            Msg.Welcome => new WelcomeMessage(Positive(r.Int()), r.AtEnd ? null : r.String()),
            Msg.Rejected => new RejectedMessage(r.String(), r.AtEnd ? null : r.String()),
            Msg.LobbyList => ReadLobbyList(r),
            Msg.LobbyState => ReadLobby(r),
            Msg.MatchStart => ReadSetup(r, matchId),
            Msg.Countdown => new FloatMessage(Range(r.Float(), 0, 10)),
            Msg.MatchGo or Msg.Clock => new FloatMessage(Range(r.Float(), 0, 3600)),
            Msg.Notice => new TextMessage(r.String()),
            Msg.PickupTaken => new PickupMessage(Nonnegative(r.Int()), Positive(r.Int())),
            Msg.PickupRespawn => new IntMessage(Nonnegative(r.Int())),
            Msg.MatchEnd => ReadArray(r, r.Int(), Protocol.MaxWireActors, () =>
                new Standing(Positive(r.Int()), r.String(), r.Appearance(), Nonnegative(r.Int()), r.Bool()), min: 1),
            Msg.PlayerLeft => new PlayerLeftMessage(Positive(r.Int()), Positive(r.Int())),
            Msg.Pong => new PongMessage(Time(r.Double()), Time(r.Double())),
            Msg.Authority => ReadAuthority(r),
            Msg.PlayerState => ReadPlayerState(r),
            Msg.Shot => ReadShot(r),
            Msg.MeleeStart => new MeleeStartMessage(Id(r.UInt()), HorizontalNormal(r)),
            Msg.MeleeContact => ReadMeleeContact(r),
            Msg.Puncture => ReadPuncture(r),
            Msg.Explode => new ExplodeMessage(Id(r.UInt()), Vector(r), Id(r.UInt()), Id(r.UInt()), r.UInt()),
            Msg.Claim => ReadClaim(r),
            Msg.Gather => ReadGather(r),
            Msg.BodyStates => ReadStates(r),
            Msg.BodyRest => new BodyRestMessage(Id(r.UInt()), Transform(r), Time(r.Double()), Id(r.UInt()), Id(r.UInt())),
            Msg.Ragdoll => new RagdollMessage(Vector(r), Vector(r), Vector(r)),
            Msg.GetUp => new GetUpMessage(Vector(r), Range(r.Float(), -1_000_000, 1_000_000)),
            Msg.TuningSet => ReadTuning(r),
            Msg.ModeEvent or Msg.ModeCommand or Msg.ModeSnapshot => ModeSchemas.Read(r, type, schema),
            _ => throw new InvalidDataException("unsupported message"),
        };
        // These three handshakes may gain trailing fields, even across incompatible protocols.
        if (type is not (Msg.Hello or Msg.Welcome or Msg.Rejected)) { r.End(); }
        // A client event's reporter and actor-local sequence live in its envelope; expose them like any actor action.
        if (payload is ModeEnvelope { } reported && type == Msg.ModeEvent) { (actor, life, action) = (reported.ActorId, reported.LifeId, reported.EventId); }
        return new NetMessage(type, matchId, sender, payload, actor, life, action);
    }

    public byte[] Encode(NetMessage message, bool fromServer)
    {
        var w = new NetWriter(message.Type, message.MatchId, message.ActorId, message.LifeId, message.ActionId);
        if (Protocol.IsRelay(message.Type) && fromServer) { w.Int(message.Sender); }
        switch (message.Payload)
        {
            case AddActorMessage p: w.UInt(p.RequestId).Byte((byte)p.Kind).String(p.Name).Appearance(p.Appearance); WriteController(w, p.Config); w.UInt(p.Revision); break;
            case UpdateActorMessage p: w.UInt(p.RequestId).Int(p.ActorId).String(p.Name).Appearance(p.Appearance); WriteController(w, p.Config); w.UInt(p.Revision); break;
            case RemoveActorMessage p: w.UInt(p.RequestId).Int(p.ActorId).UInt(p.Revision); break;
            case RosterStateMessage p:
                w.Int(p.LobbyId).UInt(p.Revision).Int(p.Actors.Count);
                foreach (var actor in p.Actors) WriteProfile(w, actor);
                break;
            case ActorRequestResultMessage p: w.UInt(p.RequestId).Bool(p.Accepted).UInt(p.Revision).String(p.Reason); break;
            case EmptyMessage: break;
            case IntMessage p: w.Int(p.Value); break;
            case FloatMessage p: w.Float(p.Value); break;
            case TimeMessage p: w.Double(p.Value); break;
            case TextMessage p: w.String(p.Value); break;
            case HelloMessage p: w.Int(p.Protocol).String(p.Version).String(p.Build).String(p.Name).Int(p.Color).Bool(p.Debug).Appearance(p.Appearance); break;
            case WelcomeMessage p:
                w.Int(p.Id); if (p.Version != null) { w.String(p.Version); }
                break;
            case RejectedMessage p:
                w.String(p.Reason); if (p.Version != null) { w.String(p.Version); }
                break;
            case ProfileMessage p: w.String(p.Name).Appearance(p.Appearance); break;
            case Godot.Collections.Dictionary p: w.Snapshot(p); break;
            case LobbyListMessage p:
                w.Int(p.Capacity).Int(p.Lobbies.Count);
                foreach (var lobby in p.Lobbies) { w.Int(lobby.Id).String(lobby.CreatorName).Int(lobby.Players).Byte((byte)lobby.Phase).Int(lobby.Capacity).Byte(lobby.ModeId); }
                break;
            case LobbyInfo p:
                w.Int(p.Id).Int(p.CreatorId).Float(p.MatchLength).Byte((byte)p.Phase).Int(p.Capacity).UInt(p.RosterRevision).Int(p.Peers.Count);
                foreach (var peer in p.Peers) w.Int(peer);
                w.Int(p.Members.Count);
                foreach (var player in p.Members) { WriteProfile(w, player); }
                w.Byte(p.Mode.ModeId).Float(p.Mode.MintSeconds).Int(p.Mode.TankSupply).Float(p.StartsIn).Int(p.WorldId);
                break;
            case ModeSettings p: w.Byte(p.ModeId).Float(p.MintSeconds).Int(p.TankSupply); break;
            case MatchSetup p:
                w.Bytes(p.Plan).Int(p.HostId).Bool(p.CreatorDebug).Float(p.Length).Snapshot(p.Tuning).UInt(p.RosterRevision).Int(p.CreatorId).Int(p.Peers.Count);
                foreach (var peer in p.Peers) w.Int(peer.PeerId).Bool(peer.DebugTuning);
                w.Int(p.Players.Count);
                foreach (var (player, spawn) in p.Players) { WriteProfile(w, player).Int(spawn); }
                w.Byte(p.ModeId).UShort(p.ModeSchema);
                break;
            case PickupMessage p: w.Int(p.Index).Int(p.Player); break;
            case IReadOnlyList<Standing> p:
                w.Int(p.Count);
                foreach (var player in p) { w.Int(player.Id).String(player.Name).Appearance(player.Appearance).Int(player.Score).Bool(player.Present); }
                break;
            case PlayerLeftMessage p: w.Int(p.Player).Int(p.Host); break;
            case PongMessage p: w.Double(p.Sent).Double(p.ServerTime); break;
            case AuthorityMessage p:
                w.Int(p.Peer).UInt(p.Request).Byte((byte)p.Reason).UShort((ushort)p.Bodies.Count);
                foreach (var authority in p.Bodies)
                {
                    WriteBody(w, authority.Body);
                    w.Int(authority.Owner).Byte((byte)authority.State).Bool(authority.Rest).Bool(authority.Vent != null);
                    if (authority.Vent is { } vent) { w.Vec3(vent.Hole).Vec3(vent.Normal).Float(vent.Swirl); }
                    w.Vec3(authority.Center).Int(authority.CauseActorId);
                }
                break;
            case PlayerStateMessage p:
                var s = p.State;
                w.Double(p.Time).Vec3(s.Position).Vec3(s.Velocity).Float(s.Yaw).Vec3(s.AimTarget)
                    .Byte(ToByte(s.Aim)).Byte(ToByte(s.Sprint)).Byte(ToByte(s.Ready)).Byte(ToByte(s.Reload))
                    .Byte((byte)((s.Grounded ? 1 : 0) | ((byte)s.State << 1)))
                    .Vec3(s.PelvisPosition).Vec3(s.PelvisVelocity).Byte(s.Staggers).Byte(ToByte(s.Melee))
                    .Byte((byte)s.Gait).Byte((byte)s.Support).Byte((byte)s.Weapon);
                break;
            case MeleeStartMessage p: w.UInt(p.Swing).Vec3(p.Direction); break;
            case MeleeContactMessage p: w.UInt(p.Swing).Int(p.Victim).Int(p.PropIndex).UInt(p.VictimLifeId); break;
            case ShotMessage p: w.Vec3(p.End).Vec3(p.Normal).Vec3(p.Direction).Byte(p.Impact).Int(p.Victim).Int(p.PropIndex).UInt(p.VictimLifeId); break;
            case PunctureMessage p:
                w.UInt(p.Id).Transform(p.Transform).Vec3(p.Linear).Vec3(p.Angular).Vec3(p.Hole).Vec3(p.Normal).Float(p.Swirl).UInt(p.Epoch).UInt(p.Request).UInt(p.Cause); break;
            case ExplodeMessage p: w.UInt(p.Id).Vec3(p.Center).UInt(p.Epoch).UInt(p.Request).UInt(p.Cause); break;
            case ClaimMessage p:
                w.UInt(p.Request).UInt(p.Cause).UShort((ushort)p.Bodies.Count);
                foreach (var claim in p.Bodies) { WriteBody(w, claim); }
                break;
            case GatherMessage p:
                w.Byte((byte)p.Phase).UShort((ushort)p.Bodies.Count);
                foreach (var body in p.Bodies) { WriteBody(w, body); }
                break;
            case BodyStatesMessage p:
                w.Double(p.Time).UShort((ushort)p.Bodies.Count); foreach (var body in p.Bodies) { WriteBody(w, body); }
                break;
            case BodyRestMessage p: w.UInt(p.Id).Transform(p.Transform).Double(p.Time).UInt(p.Epoch).UInt(p.Sequence); break;
            case RagdollMessage p: w.Vec3(p.Position).Vec3(p.Velocity).Vec3(p.Spin); break;
            case GetUpMessage p: w.Vec3(p.Position).Float(p.Yaw); break;
            case TuningMessage p: w.String(p.Name).Scalar(p.Value); break;
            case ModeEnvelope p: ModeSchemas.Write(w, message.Type, p, schema(p.ModeId, p.SchemaVersion)); break;
            default: throw new InvalidDataException("unsupported message payload");
        }
        return w.ToArray();
    }

    private static GatherMessage ReadGather(NetReader r)
    {
        var phase = (GatherPhase)Range(r.Byte(), 0, (int)GatherPhase.Settled);
        var bodies = ReadArray(r, r.UShort(), Protocol.MaxCollection, () => ReadBody(r), min: 1);
        Unique(bodies.Select(b => b.Id).ToArray());
        if (bodies.Any(b => b.Id >= BodyIds.CarBase || b.Sequence == 0)) { throw new InvalidDataException("invalid gather body"); }
        return new GatherMessage(phase, bodies);
    }

    private static HelloMessage ReadHello(NetReader r)
    {
        var protocol = r.Int(); var version = r.String(); var build = r.String(); var name = r.String(); var color = ColorIndex(r.Int());
        // Do not interpret extensions from an incompatible build: it must still receive a useful Rejected.
        if (BuildInfo.Mismatch(protocol, version) != null)
        {
            return new HelloMessage(protocol, version, build, name, color, false, AppearanceRecord.FromPalette(color));
        }
        var debug = r.Bool();
        var appearance = r.AtEnd ? AppearanceRecord.FromPalette(color) : r.Appearance();
        return new HelloMessage(protocol, version, build, name, color, debug, appearance);
    }

    private LobbyListMessage ReadLobbyList(NetReader r)
    {
        var capacity = Range(r.Int(), 2, Protocol.TestedActorCeiling);
        var count = NetReader.Count(r.Int(), 64);
        if (count > r.Remaining) throw new InvalidDataException("truncated collection");
        var lobbies = new LobbySummary[count];
        // The owner resolver must not add a receiver-capturing closure to each list decode.
        for (var i = 0; i < count; i++)
            lobbies[i] = new LobbySummary(Positive(r.Int()), r.String(), Range(r.Int(), 0, Protocol.MaxWireActors), Phase(r.Byte()),
                Range(r.Int(), 2, Protocol.TestedActorCeiling), LobbyMode(r.Byte()));
        return new LobbyListMessage(capacity, lobbies);
    }

    /// <summary>A lobby's mode, mint pace on its steps, and bounded tank supply.</summary>
    private ModeSettings ReadModeSettings(NetReader r)
    {
        var settings = new ModeSettings(LobbyMode(r.Byte()), r.Float(), r.Int());
        if (!settings.ValidValues) { throw new InvalidDataException($"invalid lobby settings: mint pace {settings.MintSeconds}, tank supply {settings.TankSupply}"); }
        return settings;
    }

    private byte LobbyMode(byte id) => lobbyMode(id) ? id : throw new InvalidDataException($"no lobby mode {id}");

    private LobbyInfo ReadLobby(NetReader r)
    {
        var id = Positive(r.Int()); var creator = Positive(r.Int()); var length = Range(r.Float(), 10, 3600); var phase = Phase(r.Byte());
        var capacity = Range(r.Int(), 2, Protocol.TestedActorCeiling); var revision = Id(r.UInt());
        var peers = Unique(ReadArray(r, r.Int(), Protocol.MaxWirePeers, () => Positive(r.Int()), min: 1));
        var members = ReadProfiles(r);
        Unique(members.Select(p => p.Id).ToArray());
        if (!peers.Contains(creator) || members.Any(p => !peers.Contains(p.OwnerPeerId))) { throw new InvalidDataException("creator missing from lobby"); }
        var mode = ReadModeSettings(r);
        var startsIn = Range(r.Float(), 0, Protocol.MaxStage);
        if (startsIn > 0 && phase != LobbyPhase.Staging) { throw new InvalidDataException("a start countdown outside staging"); }
        var world = Nonnegative(r.Int());
        if (world != 0 && phase is not (LobbyPhase.Waiting or LobbyPhase.Staging)) { throw new InvalidDataException("a lobby world during a match"); }
        return new LobbyInfo(id, creator, length, phase, members, capacity, revision) { Peers = peers, Mode = mode, StartsIn = startsIn, WorldId = world };
    }

    private MatchSetup ReadSetup(NetReader r, int matchId)
    {
        var plan = r.Bytes(); var host = Positive(r.Int()); var debug = r.Bool(); var length = Range(r.Float(), 10, 3600); var tuning = r.Snapshot(validateTuners);
        var revision = Id(r.UInt()); var creator = Positive(r.Int());
        var peers = ReadArray(r, r.Int(), Protocol.MaxWirePeers, () => new PeerCapabilities(Positive(r.Int()), r.Bool()), min: 1);
        Unique(peers.Select(p => p.PeerId).ToArray());
        var players = ReadArray(r, r.Int(), Protocol.MaxWireActors, () => (ReadProfile(r), Range(r.Int(), 0, Protocol.MaxWireActors - 1)), min: 1);
        Unique(players.Select(p => p.Item1.Id).ToArray());
        Unique(players.Select(p => p.Item2).ToArray());
        if (!players.Any(p => p.Item1.OwnerPeerId == host)) { throw new InvalidDataException("host missing from match"); }
        Unique(players.Select(p => p.Item1.AllocationSlot).ToArray());
        var mode = r.Byte(); var version = r.UShort();
        _ = schema(mode, version);
        if (players.Any(p => !p.Item1.Present || !peers.Any(peer => peer.PeerId == p.Item1.OwnerPeerId)) ||
            !peers.Any(p => p.PeerId == creator) || peers.Any(p => p.DebugTuning && (p.PeerId != creator || !debug)))
        {
            throw new InvalidDataException("invalid frozen capabilities or actor ownership");
        }

        var decoded = PlanCodec.Decode(plan);
        if (decoded.Spawns.Count != players.Length || players.Any(p => p.Item2 >= decoded.Spawns.Count))
        {
            throw new InvalidDataException("match players and plan starts disagree");
        }
        return new MatchSetup(matchId, plan, host, debug, length, tuning, players) { RosterRevision = revision, CreatorId = creator, Peers = peers, ModeId = mode, ModeSchema = version };
    }

    private static PlayerProfile ReadProfile(NetReader r)
    {
        var actor = Positive(r.Int()); var owner = Positive(r.Int()); var kind = (ActorKind)Range(r.Byte(), 0, 1);
        var name = r.String(); var appearance = r.Appearance(); var config = ReadController(r);
        var present = r.Bool(); var slot = Range(r.Int(), 0, BodyIds.MaxAllocationSlots - 1); var life = Id(r.UInt());
        if (!config.ValidFor(kind)) throw new InvalidDataException("controller does not match actor kind");
        return new(actor, name, appearance)
        {
            OwnerPeerId = owner,
            Kind = kind,
            ControllerConfig = config,
            Present = present,
            AllocationSlot = slot,
            LifeId = life
        };
    }
    private static PlayerProfile[] ReadProfiles(NetReader r)
    {
        var profiles = ReadArray(r, r.Int(), Protocol.MaxWireActors, () => ReadProfile(r));
        Unique(profiles.Select(p => p.Id).ToArray());
        Unique(profiles.Select(p => p.AllocationSlot).ToArray());
        return profiles;
    }
    private static ControllerConfig ReadController(NetReader r)
    {
        var config = new ControllerConfig(r.Byte(), (ControllerKind)Range(r.Byte(), 0, 1), (byte)Range(r.Byte(), 0, 2));
        if (config.Version != 1) throw new InvalidDataException("unsupported controller version");
        return config;
    }
    private static void WriteController(NetWriter w, ControllerConfig config) => w.Byte(config.Version).Byte((byte)config.Kind).Byte(config.Difficulty);
    private static AddActorMessage ReadAddActor(NetReader r)
    {
        var request = Id(r.UInt()); var kind = (ActorKind)Range(r.Byte(), 0, 1);
        var name = r.String(); var appearance = r.Appearance(); var config = ReadController(r);
        if (!config.ValidFor(kind)) throw new InvalidDataException("controller does not match actor kind");
        return new(request, kind, name, appearance, config, r.UInt());
    }

    private static PlayerStateMessage ReadPlayerState(NetReader r)
    {
        var time = Time(r.Double());
        var state = new PlayerNetState
        {
            Position = Vector(r),
            Velocity = Vector(r),
            Yaw = Range(r.Float(), -1_000_000, 1_000_000),
            AimTarget = Vector(r),
            Aim = r.Byte() / 255f,
            Sprint = r.Byte() / 255f,
            Ready = r.Byte() / 255f,
            Reload = r.Byte() / 255f
        };
        var flags = Range(r.Byte(), 0, 5);
        state.Grounded = (flags & 1) != 0; state.State = (PlayerNetState.Mode)(flags >> 1);
        state.PelvisPosition = Vector(r); state.PelvisVelocity = Vector(r); state.Staggers = r.Byte(); state.Melee = r.Byte() / 255f;
        state.Gait = (PlayerGait)Range(r.Byte(), (int)PlayerGait.Default, (int)PlayerGait.Creep);
        state.Support = (PlayerSupport)Range(r.Byte(), (int)PlayerSupport.Grounded, (int)PlayerSupport.Landing);
        state.Weapon = (WeaponAction)Range(r.Byte(), (int)WeaponAction.Carry, (int)WeaponAction.Melee);
        return new PlayerStateMessage(time, state);
    }

    private static Vector3 HorizontalNormal(NetReader r)
    {
        var direction = Normal(r);
        if (Mathf.Abs(direction.Y) > 0.001f) throw new InvalidDataException("nonhorizontal melee direction");
        return direction;
    }

    private static MeleeContactMessage ReadMeleeContact(NetReader r)
    {
        var contact = new MeleeContactMessage(Id(r.UInt()), Nonnegative(r.Int()), Range(r.Int(), -1, int.MaxValue), Id(r.UInt()));
        if (contact.Victim == 0 ? contact.PropIndex < 0 : contact.PropIndex != -1)
            throw new InvalidDataException("melee contact needs exactly one target");
        return contact;
    }

    private static ShotMessage ReadShot(NetReader r)
    {
        var end = Vector(r); var normal = Normal(r, allowZero: true); var direction = Normal(r);
        return new ShotMessage(end, normal, direction, (byte)Range(r.Byte(), 0, 7), Nonnegative(r.Int()), Range(r.Int(), -1, int.MaxValue), Id(r.UInt()));
    }

    private static PunctureMessage ReadPuncture(NetReader r) => new(Id(r.UInt()), Transform(r), Vector(r), Vector(r), Vector(r), Normal(r), Swirl(r), Id(r.UInt()), Id(r.UInt()), r.UInt());

    private static BodyStatesMessage ReadStates(NetReader r)
    {
        var time = Time(r.Double()); var bodies = ReadBodies(r, r.UShort(), Protocol.MaxBodyStates);
        if (bodies.Any(b => b.Sequence == 0)) { throw new InvalidDataException("zero publication sequence"); }
        return new BodyStatesMessage(time, bodies);
    }

    private static BodyState[] ReadBodies(NetReader r, int count, int max)
    {
        var bodies = ReadArray(r, count, max, () => ReadBody(r), min: 1);
        Unique(bodies.Select(b => b.Id).ToArray());
        return bodies;
    }

    private TuningMessage ReadTuning(NetReader r)
    {
        var name = r.String(); var value = r.Scalar();
        validateTuners(new Godot.Collections.Dictionary { [name] = value }, false);
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
    private static float Swirl(NetReader r)
    {
        var value = r.Float();
        return value is -1 or 1 ? value : throw new InvalidDataException("swirl must be a sign");
    }
    private static int ColorIndex(int n) => Range(n, 0, AppearancePalette.PlayerColors.Length - 1);
    private static LobbyPhase Phase(byte n) => (LobbyPhase)Range(n, 0, (int)LobbyPhase.Staging);
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
    internal static NetWriter WriteProfile(NetWriter w, PlayerProfile p)
    {
        w.Int(p.Id).Int(p.OwnerPeerId).Byte((byte)p.Kind).String(p.Name).Appearance(p.Appearance);
        WriteController(w, p.ControllerConfig);
        return w.Bool(p.Present).Int(p.AllocationSlot).UInt(p.LifeId);
    }
    internal static BodyState ReadBody(NetReader r) => new(Id(r.UInt()), Transform(r), Vector(r), Vector(r), Id(r.UInt()), r.UInt());
    /// <summary>A position or velocity inside the wire limits, for mode payloads.</summary>
    internal static Vector3 ReadVector(NetReader r) => Vector(r);
    /// <summary>A transform inside the wire limits with a valid rotation, for mode payloads.</summary>
    internal static Transform3D ReadTransform(NetReader r) => Transform(r);
    internal static void WriteBody(NetWriter w, BodyState p) => w.UInt(p.Id).Transform(p.Transform).Vec3(p.Linear).Vec3(p.Angular).UInt(p.Epoch).UInt(p.Sequence);

    private static ClaimMessage ReadClaim(NetReader r)
    {
        var request = Id(r.UInt()); var cause = r.UInt();
        var claims = ReadArray(r, r.UShort(), Protocol.MaxCollection, () => ReadBody(r), min: 1);
        Unique(claims.Select(c => c.Id).ToArray());
        return new ClaimMessage(request, claims, cause);
    }

    private static AuthorityMessage ReadAuthority(NetReader r)
    {
        var peer = Positive(r.Int()); var request = r.UInt(); var reason = (ClaimRefusal)Range(r.Byte(), 0, (int)ClaimRefusal.DuplicateRequest);
        var bodies = ReadArray(r, r.UShort(), Protocol.MaxCollection, () =>
        {
            var body = ReadBody(r); var owner = Positive(r.Int()); var state = (BodyState.State)Range(r.Byte(), 0, (int)BodyState.State.Exploded);
            var rest = r.Bool(); VentParameters? vent = r.Bool() ? new(Vector(r), Normal(r), Swirl(r)) : null;
            if (state == BodyState.State.Venting && vent == null) { throw new InvalidDataException("missing canonical vent"); }
            return new BodyAuthority(body, owner, state, rest, vent, Vector(r), Nonnegative(r.Int()));
        });
        Unique(bodies.Select(b => b.Body.Id).ToArray());
        return new AuthorityMessage(peer, request, reason, bodies);
    }
}
