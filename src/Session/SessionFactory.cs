using System;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Session;

/// <summary>A source builds content in an already constructed world; modes consume that content.</summary>
public interface ISessionWorldSource
{
    void Build(SessionContext session);
}

/// <summary>Local assembly policy, not gameplay rules. New modes/sources do not change the factory's order.</summary>
public interface ISessionMode
{
    void Configure(SessionContext session) { }
    void CreateActors(SessionContext session);
    void CreateViews(SessionContext session);
    void RegisterReplication(SessionContext session) { }
    void AttachInbox(SessionContext session) { }
    void Ready(SessionContext session) { }
}

public static class SessionFactory
{
    public static SessionContext Build(Node owner, Tuning template, ISessionWorldSource source, ISessionMode mode,
        Action<SessionContext> bind, bool networked = false, bool present = true)
    {
        InputSetup.Register();
        _ = GameAssets.Debris;
        ExplosionEffect.Preload();
        var session = new SessionContext(owner, template, source, mode, networked, present);
        try
        {
            bind(session);
            mode.Configure(session);
            source.Build(session);
            mode.CreateActors(session);
            mode.CreateViews(session);
            mode.RegisterReplication(session);
            mode.AttachInbox(session);
            mode.Ready(session);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}
