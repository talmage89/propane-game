using System;

namespace Propane.Core;

/// <summary>Monotonic positive identities. Exhaustion fails before mutation; retired ids are never recycled.</summary>
public sealed class IdentitySequence(int first = 1, int ceiling = int.MaxValue)
{
    private long next = first > 0 && first <= ceiling ? first : throw new ArgumentOutOfRangeException(nameof(first));
    public bool Available => next <= ceiling;
    public int Allocate() => next <= ceiling ? (int)next++ : throw new InvalidOperationException("identity space exhausted");
}

public enum ActorKind : byte { Human, Bot }
public enum ControllerKind : byte { Device, Bot }
/// <summary>Versioned, bounded controller selection. A bot's difficulty selects a local brain profile on its owner.</summary>
public sealed record ControllerConfig(byte Version, ControllerKind Kind, byte Difficulty)
{
    public static readonly ControllerConfig Human = new(1, ControllerKind.Device, 0);
    public static readonly ControllerConfig Bot = new(1, ControllerKind.Bot, 1);
    /// <summary>A bot controller at a difficulty: 0 Easy, 1 Normal, 2 Hard (<see cref="Bots.BotDifficulty"/>).</summary>
    public static ControllerConfig ForBot(byte difficulty) => new(1, ControllerKind.Bot, difficulty);
    public bool ValidFor(ActorKind kind) => kind is ActorKind.Human or ActorKind.Bot && Version == 1 && Difficulty <= 2 &&
        (kind == ActorKind.Human ? Kind == ControllerKind.Device && Difficulty == 0 : Kind == ControllerKind.Bot);
}

public sealed record ActorRecord(int ActorId, int OwnerPeerId, ActorKind Kind, string Name,
    AppearanceRecord Appearance, ControllerConfig ControllerConfig, bool Present, int AllocationSlot, uint LifeId);
