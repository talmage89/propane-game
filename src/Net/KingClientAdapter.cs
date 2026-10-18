using System;
using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Player;

namespace Propane.Net;

/// <summary>Session-owned King snapshot and pile policy, composing Bank's scores/spills without duplicating them.</summary>
public sealed class KingClientAdapter : IModeClient
{
    private readonly BankClientAdapter bank;
    private readonly ClientModePiles piles;
    private KingState? kingState;

    public KingClientAdapter(IClientModeReplica replica)
    {
        bank = new BankClientAdapter(replica);
        piles = new ClientModePiles(replica, ClientPileView.Zone);
    }
    public IReadOnlyDictionary<int, int> Scores => bank.Scores;
    public ModeClientDisplay Display => bank.Display;
    public bool SuppressHud => false;
    public KingState? KingSnapshot => kingState;
    public IModeView? ViewAt(double elapsed) => kingState?.At(elapsed);
    public void ActorAdded(int id) => bank.ActorAdded(id);
    public void ActorRemoved(int id) => bank.ActorRemoved(id);
    public bool LimitedAmmo(bool supplied) => bank.LimitedAmmo(supplied);
    public void PhysicsTick() { }
    public void HitByShot(PlayerCharacter victim) => bank.HitByShot(victim);
    public void Thrown(PlayerCharacter actor, Vector3 at) => bank.Thrown(actor, at);

    public void Apply(ModeEnvelope envelope)
    {
        switch (envelope.Payload)
        {
            case KingState state: kingState = state; break;
            case KingPileWanted wanted: piles.PlacePile(wanted); break;
            case KingPileSpawned spawned: piles.SpawnPile(spawned); break;
            default: bank.Apply(envelope); break;
        }
    }

    public void Dispose()
    {
        kingState = null;
        bank.Dispose();
        piles.Dispose();
    }
}
