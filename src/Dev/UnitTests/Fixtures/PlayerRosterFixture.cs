using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>A real match with controlled actor/profile mutations and the pre-cache selection oracle.</summary>
internal sealed class PlayerRosterFixture : IDisposable
{
    private readonly LobbyWorldFixture server = new(3);
    private readonly MatchClient client;
    public Match Match => client.Match;
    private readonly Dictionary<int, PlayerProfile> profiles;
    private static readonly MethodInfo add = typeof(Match).GetMethod("AddActor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo update = typeof(Match).GetMethod("UpdateLobbyActor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo? cached = typeof(Match).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? original = typeof(Match).GetProperty("player", BindingFlags.Instance | BindingFlags.NonPublic);

    public PlayerRosterFixture(Node runner, bool botsFirst = false, bool handover = false, bool botsOnly = false)
    {
        var setup = server.World() with { Players = server.Lobby().Members.Select((p, slot) => (p, slot)).ToArray() };
        if (botsFirst)
        {
            var human = setup.Players.First(p => p.Player.OwnerPeerId == 2).Player;
            var bots = Enumerable.Range(40, 8).Select(id => (human with
            {
                Id = id,
                Kind = ActorKind.Bot,
                ControllerConfig = ControllerConfig.ForBot(0),
                AllocationSlot = id,
            }, -1));
            setup = setup with { Players = bots.Concat(setup.Players).ToArray() };
        }
        if (botsOnly)
            setup = setup with { Players = setup.Players.Where(p => p.Player.OwnerPeerId != 2 || p.Player.Kind == ActorKind.Bot).ToArray() };
        if (handover)
            setup = setup with { Players = setup.Players.Select(p => (p.Player with { OwnerPeerId = p.Player.OwnerPeerId == 2 ? 3 : 2 }, p.Spawn)).ToArray() };
        client = new MatchClient(runner, setup, server.Clock, 2);
        profiles = (Dictionary<int, PlayerProfile>)typeof(Match).GetField("profiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Match)!;
    }

    public PlayerCharacter? Selected => cached != null ? (PlayerCharacter?)cached.GetValue(Match) : (PlayerCharacter?)original!.GetValue(Match);
    public PlayerCharacter? OldSelection => Match.Session.OwnedActors.FirstOrDefault(a => profiles[a.Character.ActorId].Kind == ActorKind.Human)?.Character
        ?? Match.Session.OwnedActors.FirstOrDefault()?.Character;

    public void Check(string step) => TestRunner.Assert(ReferenceEquals(Selected, OldSelection), $"{step}: cached selection differs from the old ordered rule");
    public PlayerProfile Profile(int id, ActorKind kind, int owner = 2) => new(id, $"Actor{id}", AppearanceRecord.FromPalette(0))
    {
        Kind = kind,
        OwnerPeerId = owner,
        AllocationSlot = id,
        ControllerConfig = kind == ActorKind.Bot ? ControllerConfig.ForBot(0) : ControllerConfig.Human,
    };
    public void Add(PlayerProfile profile) => add.Invoke(Match, new object[] { profile, -1, new Vector3(profile.Id * 3, .05f, 0), 0f });
    public void Update(PlayerProfile profile) => update.Invoke(Match, new object[] { Match.Session.Actor(profile.Id)!.Character, profile });
    public void Remove(int id)
    {
        var actor = Match.Session.Actor(id)!;
        actor.Character.TreeExiting += () => Check("during character retirement");
        Match.Session.RemoveActor(actor);
    }
    public void Dispose() { client.Dispose(); server.Dispose(); }
}
