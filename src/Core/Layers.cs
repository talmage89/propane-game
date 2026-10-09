namespace Propane.Core;

/// <summary>Physics layer bits, matching the names in project.godot.</summary>
public static class Layers
{
    public const uint World = 1 << 0;
    public const uint Props = 1 << 1;
    public const uint Tanks = 1 << 2;
    public const uint Debris = 1 << 3;
    public const uint Player = 1 << 4;
    public const uint Ragdoll = 1 << 5;
    public const uint Floor = 1 << 6;

    /// <summary>Everything a bullet can hit.</summary>
    public const uint Shootable = World | Props | Tanks | Debris | Floor;

    /// <summary>What a shot can hit: the world, plus other players' bodies (a player's own are excluded from its shots).</summary>
    public const uint ShotMask = Shootable | Player | Ragdoll;

    /// <summary>Everything a blast pushes.</summary>
    public const uint Pushable = Props | Tanks | Debris;

    /// <summary>Solid things that block line of sight for blasts.</summary>
    public const uint BlastOccluders = World;

    /// <summary>What loose props and debris collide with.</summary>
    public const uint DynamicMask = World | Props | Tanks | Debris | Floor | Ragdoll;

    /// <summary>What the player's capsule collides with.</summary>
    public const uint PlayerMask = World | Props | Tanks | Floor;
}
