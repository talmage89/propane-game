using System;
using Propane.Net;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ParticipantTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("participants/identities fail before wrapping", () =>
        {
            var ids = new IdentitySequence(int.MaxValue);
            TestRunner.Assert(ids.Allocate() == int.MaxValue, "last id skipped");
            TestRunner.Throws<InvalidOperationException>(() => ids.Allocate());
            TestRunner.Throws<InvalidOperationException>(() => ids.Allocate());
            TestRunner.Throws<ArgumentOutOfRangeException>(() => BodySync.DropTankId(0, BodySync.MaxDropsPerActor));
            TestRunner.Throws<ArgumentOutOfRangeException>(() => BodySync.DropTankId(BodySync.MaxAllocationSlots, 0));
            TestRunner.Assert(BodySync.DropTankId(BodySync.MaxAllocationSlots - 1, BodySync.MaxDropsPerActor - 1) == BodySync.CarBase - 1,
                "drop space overlaps cars");
        });
        runner.Run("participants/ownership, revisions, departure and lives", () =>
        {
            var roster = new ParticipantRoster();
            var human = roster.Add(44, ActorKind.Human, "Human", AppearanceRecord.FromPalette(0), ControllerConfig.Human);
            var bot = roster.Add(44, ActorKind.Bot, "Bot", AppearanceRecord.FromPalette(0), ControllerConfig.Bot);
            TestRunner.Assert(human.ActorId != human.OwnerPeerId && roster.Owns(44, bot.ActorId, 1), "peer and actor conflated");
            TestRunner.Assert(!roster.Update(45, bot.ActorId, "Spoof", bot.Appearance, bot.ControllerConfig) && roster.Revision == 2,
                "spoof changed revision");
            TestRunner.Assert(roster.Update(44, bot.ActorId, "New", bot.Appearance, bot.ControllerConfig) && roster.Revision == 3, "update failed");
            var life = roster.Respawn(human.ActorId);
            TestRunner.Assert(life.LifeId == 2 && !roster.Owns(44, human.ActorId, 1), "old life accepted");
            roster.RemovePeer(44);
            TestRunner.Assert(roster.Present.Count == 0 && !roster.Get(bot.ActorId)!.Present, "peer bots survived departure");
            var next = roster.Add(45, ActorKind.Human, "Next", human.Appearance, ControllerConfig.Human);
            TestRunner.Assert(next.ActorId > bot.ActorId && next.AllocationSlot > bot.AllocationSlot, "retired identity reused");
        });
    }
}
