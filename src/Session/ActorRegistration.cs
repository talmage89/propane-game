using System;

namespace Propane.Session;

/// <summary>
/// One client actor attachment, owned by SessionContext until actor removal or session retirement.
/// Unsubscription retains its original place in session shutdown; cache retirement precedes native free.
/// </summary>
internal sealed class ActorRegistration(Action removing) : IDisposable
{
    private Action? removing = removing;
    private Action? unsubscribe;
    public bool IsActive => removing != null;
    public bool IsSubscribed => unsubscribe != null && IsActive;

    internal void Subscribe(Action detach)
    {
        ObjectDisposedException.ThrowIf(!IsActive, this);
        if (unsubscribe != null) throw new InvalidOperationException("actor is already subscribed");
        unsubscribe = detach;
    }

    internal void Unsubscribe()
    {
        var callback = unsubscribe;
        unsubscribe = null;
        callback?.Invoke();
    }

    public void Dispose()
    {
        var callback = removing;
        if (callback == null) return;
        removing = null; // Synchronous callbacks may try to retire this attachment again.
        Exception? failure = null;
        try { Unsubscribe(); }
        catch (Exception error) { failure = error; }
        try { callback(); }
        catch (Exception error)
        {
            if (failure != null) throw new AggregateException("actor attachment retirement failed", failure, error);
            throw;
        }
        if (failure != null) throw new AggregateException("actor attachment unsubscription failed", failure);
    }
}
