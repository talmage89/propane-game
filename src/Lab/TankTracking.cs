using System;
using System.Collections.Generic;
using Godot;
using Propane.Tank;

namespace Propane.Lab;

/// <summary>
/// A runner's active tank subscriptions. Detonation and content retirement forget individual registrations; one
/// session unsubscribe callback drains the remainder in reverse registration order. No retired registration or
/// captured handler is kept until shutdown.
/// </summary>
internal sealed class TankTracking(Action<PropaneTank> forgotten) : IDisposable
{
    private readonly Dictionary<PropaneTank, Registration> active = new();
    private Registration? last;
    private bool disposed;

    public int Count => active.Count;

    public void Track(PropaneTank tank, Action<PropaneTank> punctured, Action<PropaneTank> detonated)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (active.ContainsKey(tank)) return;
        var registration = new Registration(tank, punctured, detonated) { Previous = last };
        active.Add(tank, registration);
        if (last != null) last.Next = registration;
        last = registration;
    }

    public void Forget(PropaneTank tank)
    {
        if (!active.Remove(tank, out var registration)) return;
        if (registration.Previous != null) registration.Previous.Next = registration.Next;
        if (registration.Next != null) registration.Next.Previous = registration.Previous;
        else last = registration.Previous;
        registration.Detach();
        forgotten(tank);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        List<Exception>? errors = null;
        while (last != null)
        {
            try { Forget(last.Tank!); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        if (errors != null) throw new AggregateException("tank tracking retirement failed", errors);
    }

    // Intrusive links allow constant-time removal without a second collection or a node allocation per tank.
    private sealed class Registration
    {
        public PropaneTank? Tank;
        public Registration? Previous, Next;
        private Action<PropaneTank>? punctured, detonated;

        public Registration(PropaneTank tank, Action<PropaneTank> punctured, Action<PropaneTank> detonated)
        {
            Tank = tank;
            this.punctured = punctured;
            this.detonated = detonated;
            tank.Punctured += punctured;
            tank.Detonated += detonated;
        }

        public void Detach()
        {
            if (Tank == null) return;
            if (GodotObject.IsInstanceValid(Tank))
            {
                Tank.Punctured -= punctured;
                Tank.Detonated -= detonated;
            }
            Tank = null;
            punctured = detonated = null;
            Previous = Next = null;
        }
    }
}
