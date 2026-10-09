using System.Collections.Generic;
using Godot;
using Propane.Player;

namespace Propane.Dev;

/// <summary>A timeline of held inputs and one-frame presses that drives the player in test scenes.</summary>
public sealed class ScriptedInput : IPlayerInputSource
{
    private readonly List<(float Start, float End, PlayerIntent Intent)> holds = new();
    private readonly List<(float Time, bool Fire, bool Jump)> presses = new();
    private readonly List<(float Start, float End, Vector2 PerSecond)> looks = new();
    private float time;
    private int nextPress;

    public void Hold(float start, float end, PlayerIntent intent) => holds.Add((start, end, intent));

    public void Fire(float time) => presses.Add((time, true, false));

    public void Jump(float time) => presses.Add((time, false, true));

    /// <summary>Mouse movement in pixels per second over an interval.</summary>
    public void Look(float start, float end, Vector2 pixelsPerSecond) => looks.Add((start, end, pixelsPerSecond));

    public void Advance(float delta) => time += delta;

    /// <summary>The script's clock, for scheduling relative to now.</summary>
    public float Now => time;

    public PlayerIntent Read()
    {
        var intent = new PlayerIntent();
        foreach (var (start, end, held) in holds)
        {
            if (time >= start && time < end)
            {
                intent.Move += held.Move;
                intent.Sprint |= held.Sprint;
                intent.Aim |= held.Aim;
                intent.FireHeld |= held.FireHeld;
            }
        }
        foreach (var (start, end, perSecond) in looks)
        {
            if (time >= start && time < end)
            {
                intent.Look += perSecond / Engine.PhysicsTicksPerSecond;
            }
        }
        presses.Sort((a, b) => a.Time.CompareTo(b.Time));
        while (nextPress < presses.Count && presses[nextPress].Time <= time)
        {
            intent.FirePressed |= presses[nextPress].Fire;
            intent.JumpPressed |= presses[nextPress].Jump;
            nextPress++;
        }
        return intent;
    }
}
