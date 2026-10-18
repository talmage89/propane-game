using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Net;

/// <summary>Synchronous physical and wire operations supplied by the match; no transaction state lives here.</summary>
internal interface IClientAuthorityReplica
{
    BodySync.Entry? FindBody(uint id);
    BodyState? CurrentProposal(uint id);
    void SendAuthority(Msg type, object payload, int actorId, uint lifeId);
    void CancelRoot(uint request);
    (BodyAuthority Authority, BodySync.Entry? Entry)? Adopt(BodyAuthority authority, bool gatherGrant);
    void Explode(BodyAuthority authority);
    void Restore(BodyAuthority authority, BodySync.Entry? entry, ClaimRefusal reason, bool finishedPuncture);
    void RemoveUnknown(uint id);
    event Action<AuthorityMessage>? AuthorityResolved;
    void Notify(AuthorityMessage result);
}

/// <summary>
/// Owns one session's causal authority transactions. All calls, proposal capture, physical reconciliation and
/// notifications are synchronous. Session stopping disposes it after inbox detachment and unsubscription.
/// </summary>
internal sealed class ClientAuthorityTransactions : IDisposable
{
    private sealed record AuthorityRequest(Msg Type, uint Request, uint Cause, uint[] Ids, object Payload, int ActorId = 0, uint LifeId = 0)
    {
        public readonly Dictionary<uint, uint> Expected = new();
    }

    private readonly int me;
    private IClientAuthorityReplica? replica;
    private readonly List<AuthorityRequest> outgoingAuthority = new();
    private readonly Dictionary<uint, AuthorityRequest> pendingAuthority = new();
    private readonly Dictionary<uint, uint> pendingBodies = new();
    private readonly HashSet<uint> sentExplosions = new();
    private readonly Dictionary<uint, Action<AuthorityMessage>> gatherRequests = new();
    private uint nextRequest;

    public ClientAuthorityTransactions(int me, IClientAuthorityReplica replica)
    {
        this.me = me;
        this.replica = replica;
    }

    public ClaimRefusal LastRefusal { get; private set; }
    public uint NewRequest()
    {
        ObjectDisposedException.ThrowIf(replica == null, this);
        return checked(++nextRequest);
    }

    public void Puncture(PunctureMessage puncture, int actorId, uint lifeId) =>
        Queue(new AuthorityRequest(Msg.Puncture, puncture.Request, puncture.Cause, new[] { puncture.Id }, puncture, actorId, lifeId));

    public void Explode(ExplodeMessage explosion, int actorId, uint lifeId) =>
        Queue(new AuthorityRequest(Msg.Explode, explosion.Request, explosion.Cause, new[] { explosion.Id }, explosion, actorId, lifeId));

    public uint Claim(IReadOnlyList<BodyState> claims, uint cause = 0)
    {
        if (claims.Count == 0) { return 0; }
        var request = NewRequest();
        Queue(new AuthorityRequest(Msg.Claim, request, cause, claims.Select(c => c.Id).ToArray(), new ClaimMessage(request, claims, cause)));
        return request;
    }

    public void GrantGather(IReadOnlyList<BodyState> claims, Action<Func<uint, bool>?> done)
    {
        var request = Claim(claims);
        if (request == 0) { done(null); return; }
        if (replica == null) return; // A synchronous send can stop the session before registration.
        void Resolve(AuthorityMessage result)
        {
            if (result.Peer != me || result.Request != request) { return; }
            var live = replica;
            if (live == null) return; // A preceding listener may have retired this session.
            live.AuthorityResolved -= Resolve;
            if (result.Reason != ClaimRefusal.None) { done(null); return; }
            var epochs = result.Bodies.ToDictionary(b => b.Body.Id, b => b.Body.Epoch);
            done(id => replica?.FindBody(id) is { } e && e.Owner == me && !e.ClaimPending && epochs.GetValueOrDefault(id) == e.Epoch);
        }
        gatherRequests.Add(request, Resolve);
        replica!.AuthorityResolved += Resolve;
    }

    private void Queue(AuthorityRequest request)
    {
        var replica = this.replica;
        ObjectDisposedException.ThrowIf(replica == null, this);
        foreach (var id in request.Ids)
        {
            if (replica.FindBody(id) is { } entry)
            {
                request.Expected[id] = entry.Epoch;
                BodySync.MarkPending(entry, predictMotion: request.Type != Msg.Claim || !entry.ObserveWhilePending);
            }
        }
        outgoingAuthority.Add(request);
        if (request.Type != Msg.Puncture || request.Cause == 0) { Flush(); }
    }

    private PunctureMessage PunctureProposal(PunctureMessage puncture, uint epoch)
    {
        var pose = replica!.CurrentProposal(puncture.Id);
        return puncture with
        {
            Epoch = epoch,
            Transform = pose?.Transform ?? puncture.Transform,
            Linear = pose?.Linear ?? puncture.Linear,
            Angular = pose?.Angular ?? puncture.Angular
        };
    }

    public void Flush()
    {
        var replica = this.replica;
        if (replica == null) return; // An in-flight reconciliation can synchronously stop the session.
        for (var i = 0; i < outgoingAuthority.Count;)
        {
            var request = outgoingAuthority[i];
            if (request.Ids.Any(pendingBodies.ContainsKey) || request.Cause != 0 && !sentExplosions.Contains(request.Cause)) { i++; continue; }
            outgoingAuthority.RemoveAt(i);
            pendingAuthority.Add(request.Request, request);
            foreach (var id in request.Ids) { pendingBodies[id] = request.Request; if (replica.FindBody(id) is { } entry) { entry.ClaimPending = true; } }
            // A chain's root is sent before its children, even when a puncture/ownership grant held it back.
            if (request.Type == Msg.Explode) { sentExplosions.Add(request.Request); }
            var payload = request.Payload switch
            {
                PunctureMessage p => (object)PunctureProposal(p, request.Expected[p.Id]),
                ExplodeMessage p => p with { Epoch = request.Expected[p.Id] },
                ClaimMessage p => p with { Bodies = p.Bodies.Select(c => (replica.CurrentProposal(c.Id) ?? c) with { Epoch = request.Expected[c.Id] }).ToArray() },
                _ => throw new InvalidOperationException("unknown authority request"),
            };
            replica.SendAuthority(request.Type, payload, request.ActorId, request.LifeId);
        }
    }

    public void Resolve(AuthorityMessage result)
    {
        var replica = this.replica;
        ObjectDisposedException.ThrowIf(replica == null, this);
        AuthorityRequest? finished = null;
        if (result.Peer == me && pendingAuthority.Remove(result.Request, out finished))
        {
            foreach (var id in finished.Ids) { pendingBodies.Remove(id); if (replica.FindBody(id) is { } body) { body.ClaimPending = false; } }
            if (result.Reason == ClaimRefusal.None)
            {
                foreach (var next in outgoingAuthority)
                {
                    foreach (var grant in result.Bodies.Where(b => next.Expected.ContainsKey(b.Body.Id)))
                    {
                        next.Expected[grant.Body.Id] = grant.Body.Epoch;
                    }
                }
            }
            if (result.Reason != ClaimRefusal.None)
            {
                LastRefusal = result.Reason;
                if (finished.Type == Msg.Explode) { replica.CancelRoot(finished.Request); }
                // Keep dependent predictions' original epochs. They also need explicit refusals to restore
                // their bodies; silently canceling a queued group could strand its other members.
            }
        }
        foreach (var reported in result.Bodies)
        {
            var gatherGrant = result.Peer == me && result.Reason == ClaimRefusal.None && gatherRequests.ContainsKey(result.Request);
            if (replica.Adopt(reported, gatherGrant) is not { } adopted) { continue; }
            var authority = adopted.Authority;
            var id = authority.Body.Id;
            if (authority.State == TankLedger.State.Exploded)
            {
                foreach (var canceled in outgoingAuthority.Where(r => r.Type != Msg.Claim && r.Ids.Contains(id)).ToArray())
                {
                    outgoingAuthority.Remove(canceled);
                    if (canceled.Type == Msg.Explode) { sentExplosions.Add(canceled.Request); }
                }
                replica.Explode(authority);
                continue;
            }
            // A predicted second shot can already have freed the node before our first-shot grant arrives.
            // Resolve that shot after this grant; don't resurrect the winning prediction between the two echoes.
            if (result.Peer == me && result.Reason == ClaimRefusal.None && outgoingAuthority.Any(r => r.Ids.Contains(id))) { continue; }
            replica.Restore(authority, adopted.Entry, result.Reason, finished?.Type == Msg.Puncture);
        }
        if (finished != null && result.Reason == ClaimRefusal.UnknownBody)
        {
            foreach (var id in finished.Ids.Except(result.Bodies.Select(b => b.Body.Id))) replica.RemoveUnknown(id);
        }
        Flush();
        replica.Notify(result);
        if (result.Peer == me) { gatherRequests.Remove(result.Request); }
    }

    public void Dispose()
    {
        if (replica == null) return;
        foreach (var callback in gatherRequests.Values) replica.AuthorityResolved -= callback;
        gatherRequests.Clear();
        pendingAuthority.Clear();
        outgoingAuthority.Clear();
        pendingBodies.Clear();
        sentExplosions.Clear();
        nextRequest = 0;
        replica = null;
    }
}
