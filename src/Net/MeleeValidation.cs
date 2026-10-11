using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Net;

/// <summary>Receipt-clock bounds for client-owned contact. No movement, distance or physics adjudication.
/// Reliable order establishes a swing before its contacts; ids and target keys cannot be reused.</summary>
public sealed class MeleeValidation
{
    private uint swing;
    private double nextStart;
    private double contactUntil;
    private readonly HashSet<(int Victim, int Prop)> contacts = new();

    public bool Start(MeleeStartMessage message, double now, Tuning t)
    {
        if (message.Swing <= swing || now < nextStart) return false;
        swing = message.Swing;
        var duration = t.MeleeWindup + t.MeleeActive + t.MeleeRecovery;
        // Receipt jitter can compress consecutive starts. It cannot admit a burst faster than the cooldown.
        nextStart = now + Math.Max(t.MeleeCooldown, duration + t.MeleeCooldown - t.MeleeRelayGrace);
        contactUntil = now + t.MeleeWindup + t.MeleeActive + t.MeleeRelayGrace;
        contacts.Clear();
        return true;
    }

    public bool Contact(MeleeContactMessage message, double now, Tuning t) =>
        message.Swing == swing && swing != 0 && now <= contactUntil && contacts.Count < t.MeleeMaxContacts &&
        contacts.Add((message.Victim, message.PropIndex));
}
