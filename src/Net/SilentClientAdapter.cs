using System;
using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Player;

namespace Propane.Net;

/// <summary>
/// Explicit Sandbox integration and common zero-bank roster lifetime. No wire consequences or mode view.
/// Derived network adapters borrow the same synchronous replica; disposal empties banks and releases it.
/// </summary>
public class SilentClientAdapter : IModeClient
{
    protected IClientModeReplica replica;
    protected readonly Dictionary<int, int> scores = new();

    public SilentClientAdapter(IClientModeReplica replica) { this.replica = replica; }
    public IReadOnlyDictionary<int, int> Scores => scores;
    public virtual ModeClientDisplay Display => new(true, false, "Waiting for everyone to arrive", "match");
    public virtual bool SuppressHud => false;
    public virtual KingState? KingSnapshot => null;
    public virtual IModeView? ViewAt(double elapsed) => null;
    public void ActorAdded(int id) => scores[id] = 0;
    public virtual void ActorRemoved(int id) { }
    public virtual void Apply(ModeEnvelope envelope) { }
    public virtual bool LimitedAmmo(bool supplied) => supplied;
    public virtual void PhysicsTick() { }
    public virtual void Thrown(PlayerCharacter actor, Vector3 at) { }

    // Owner-side hit grace historically also advances in non-banking worlds. It uses simulation time, not wire time.
    protected bool OpenHitGrace(PlayerCharacter victim)
    {
        var now = replica.World.Clock.Now;
        var grace = victim.Runtime.GraceUntil;
        if (!replica.IsPlaying || !TankBankRules.TryOpenGrace(now, ref grace, replica.World.Tuning.HitGraceTime)) return false;
        victim.Runtime.GraceUntil = grace;
        return true;
    }

    public virtual void HitByShot(PlayerCharacter victim) => OpenHitGrace(victim);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        scores.Clear();
        replica = null!;
    }
}
