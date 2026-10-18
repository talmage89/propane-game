using System;
using System.Collections.Generic;

namespace Propane.Net;

/// <summary>Live tuner values sampled by the caller for one swing admission.</summary>
public readonly record struct MeleeTiming(float Windup, float Active, float Recovery, float Cooldown, float RelayGrace);

/// <summary>Receipt-clock bounds for client-owned contact. No movement, distance or physics adjudication.
/// Reliable order establishes a swing before its contacts; ids and target keys cannot be reused.</summary>
public sealed class MeleeValidation
{
    private uint swing;
    private double nextStart;
    private double contactUntil;
    private readonly HashSet<(int Victim, int Prop)> contacts = new();

    public bool Start(MeleeStartMessage message, double now, MeleeTiming t)
    {
        if (message.Swing <= swing || now < nextStart) return false;
        swing = message.Swing;
        var duration = t.Windup + t.Active + t.Recovery;
        // Receipt jitter can compress consecutive starts. It cannot admit a burst faster than the cooldown.
        nextStart = now + Math.Max(t.Cooldown, duration + t.Cooldown - t.RelayGrace);
        contactUntil = now + t.Windup + t.Active + t.RelayGrace;
        contacts.Clear();
        return true;
    }

    public bool Contact(MeleeContactMessage message, double now, int maxContacts) =>
        message.Swing == swing && swing != 0 && now <= contactUntil && contacts.Count < maxContacts &&
        contacts.Add((message.Victim, message.PropIndex));
}
