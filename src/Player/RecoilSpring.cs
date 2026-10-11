using Godot;

namespace Propane.Player;

/// <summary>Exact underdamped spring response. Shots add momentum; render rate cannot change recovery.</summary>
public sealed class RecoilSpring
{
    public float Position { get; private set; }
    public float Velocity { get; private set; }

    public void Kick(float frequency, float impulse) => Velocity += Mathf.Tau * frequency * impulse;

    public void Reset() => Position = Velocity = 0;

    public void Update(float dt, float frequency, float damping)
    {
        var omega = Mathf.Tau * frequency;
        var decay = omega * damping;
        var oscillation = omega * Mathf.Sqrt(1 - damping * damping);
        var sine = Mathf.Sin(oscillation * dt);
        var cosine = Mathf.Cos(oscillation * dt);
        var envelope = Mathf.Exp(-decay * dt);
        var old = Position;
        Position = envelope * (old * cosine + (Velocity + decay * old) / oscillation * sine);
        Velocity = envelope * (Velocity * cosine - (decay * Velocity + omega * omega * old) / oscillation * sine);
    }
}
