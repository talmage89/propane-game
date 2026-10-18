using System.Collections.Generic;
using Propane.Tank;

namespace Propane.Lab;

/// <summary>
/// One runner's last direct contacts and unbanked drop identities. TankTracking calls Forget at detonation/content
/// retirement; the runner clears any remaining facts when its services stop. Owns no callbacks, timers or rules.
/// Choosing a semantic chain/blast cause and submitting its consequences remain the runner's responsibility.
/// </summary>
internal sealed class LabTankFacts
{
    private readonly Dictionary<PropaneTank, int> directShots = new();
    private readonly HashSet<PropaneTank> drops = new();

    public void Shot(PropaneTank tank, int actorId) => directShots[tank] = actorId;
    public int LastShooter(PropaneTank tank) => directShots.GetValueOrDefault(tank);
    public void ForgetShot(PropaneTank tank) => directShots.Remove(tank);
    public void Drop(PropaneTank tank) => drops.Add(tank);
    public bool TakeDrop(PropaneTank tank) => drops.Remove(tank);

    public void Forget(PropaneTank tank)
    {
        directShots.Remove(tank);
        drops.Remove(tank);
    }

    public void Clear()
    {
        directShots.Clear();
        drops.Clear();
    }
}
