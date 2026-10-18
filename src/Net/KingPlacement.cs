using System.Collections.Generic;
using System.Linq;
using Propane.Modes;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>Plan-to-rule setup for server matches and the local King composition.</summary>
public static class KingPlacement
{
    /// <summary>A suburb's pile spots: each of the plan's piles at its tanks' mean position on the ground.</summary>
    public static IReadOnlyList<ModeSpot> SpotsFrom(SuburbPlan plan) => plan.Tanks.GroupBy(t => t.Pile).OrderBy(g => g.Key)
        .Select(g => new ModeSpot(g.Average(t => (double)t.Position.X), PlanCoordinates.GroundHeight, g.Average(t => (double)t.Position.Y))).ToArray();
}
