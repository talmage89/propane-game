using System.Collections.Generic;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

public interface IBotBrain : IPlayerInputSource
{
    string Status { get; }
    void Think(Match match, float dt);
}

/// <summary>Harness adapter for a match's bot goal policy, including King's snapshot-driven brain.</summary>
public sealed class Bot : IBotBrain, System.IDisposable
{
    private readonly IBotGoalPolicy brain;
    public Bot(ModeDefinition? mode = null) => brain = (mode ?? ModeRegistry.TankBank).CreateBot!(null);
    public float Skill { get => brain.Skill; set => brain.Skill = value; }
    public string Status => brain.Status;
    public int StuckEvents => (brain as BankingBot)?.StuckEvents ?? 0;
    public int Replans => (brain as BankingBot)?.Replans ?? 0;
    public int Jumps => (brain as BankingBot)?.Jumps ?? 0;
    public int BlacklistedTargets => (brain as BankingBot)?.BlacklistedTargets ?? 0;
    public PlayerIntent Read() => brain.Read();
    public void Think(Match match, float dt) => brain.Think(new MatchWorld(match), dt);
    internal void Face(PlayerCharacter player, Vector3 point, float dt) => ((BankingBot)brain).Face(player, point, dt);
    public void Dispose() => brain.Dispose();

    private sealed class MatchWorld(Match match) : IBotWorld
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
        public IEnumerable<PlayerCharacter> RemoteBodies => match.RemoteBodies;
        public IReadOnlyList<AmmoPickup> Pickups => match.Pickups;
        public IEnumerable<PropaneTank> LiveTanks => match.LiveTanks;
        public IModeView? View => match.ModeView;
    }
}
