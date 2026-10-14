using System.Collections.Generic;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev;

public interface IBotBrain : IPlayerInputSource
{
    string Status { get; }
    void Think(Match match, float dt);
}

/// <summary>Protocol 2 harness adapter for Tank Bank's bot goal policy (the runtime banking brain).</summary>
public sealed class Bot : IBotBrain, System.IDisposable
{
    private readonly BankingBot brain = (BankingBot)ModeRegistry.TankBank.CreateBot!(null);
    public float Skill { get => brain.Skill; set => brain.Skill = value; }
    public string Status => brain.Status;
    public int StuckEvents => brain.StuckEvents;
    public int Replans => brain.Replans;
    public int Jumps => brain.Jumps;
    public int BlacklistedTargets => brain.BlacklistedTargets;
    public PlayerIntent Read() => brain.Read();
    private MatchWorld? view;
    public void Think(Match match, float dt)
    {
        if (view?.Match != match)
        {
            view?.Dispose();
            view = new MatchWorld(match);
            brain.Prepare(view);
        }
        view.Refresh();
        brain.Think(view, dt);
    }
    internal void Face(PlayerCharacter player, Vector3 point, float dt) => brain.Face(player, point, dt);
    public void Dispose() { brain.Dispose(); view?.Dispose(); view = null; }

    private sealed class MatchWorld(Match match) : IBotWorld, System.IDisposable
    {
        public WorldContext World => match.World;
        public PlayerCharacter Player => match.Player;
        public bool IsPlaying => match.IsPlaying;
        public float TimeLeft => match.TimeLeft;
        public int MyScore => match.MyScore;
        public IReadOnlyDictionary<int, int> Scores => match.Scores;
        public double Now => MonotonicClock.Now;
        public double LastHitTime => match.LastHitTime;
        public int LastHitBy => match.LastHitBy;
        public Match Match => match;
        private readonly List<PlayerCharacter> others = new();
        public IReadOnlyList<PlayerCharacter> RemoteBodies => others;
        public BotServices Services { get; } = new(match.World);
        public void Refresh()
        {
            others.Clear();
            var actors = match.Session.Actors;
            for (var i = 0; i < actors.Count; i++)
                if (actors[i].Character != Player) others.Add(actors[i].Character);
            Services.BeginTick(match.BotTanks);
        }
        public void Dispose() => Services.Dispose();
        public IReadOnlyList<AmmoPickup> Pickups => match.Pickups;
    }
}
