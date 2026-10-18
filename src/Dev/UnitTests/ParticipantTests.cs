using System;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ParticipantTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("participants/identities fail before wrapping", () =>
        {
            foreach (var invalid in new[] { -1, BodyIds.MaxWorldEntities })
            {
                TestRunner.Throws<ArgumentOutOfRangeException>(() => BodyIds.MapTankId(invalid));
                TestRunner.Throws<ArgumentOutOfRangeException>(() => BodyIds.CarId(invalid));
            }
            TestRunner.Assert(BodyIds.MapTankId(BodyIds.MaxWorldEntities - 1) == BodyIds.DropTankBase - 1, "world entity spaces overlap");
            var ids = new IdentitySequence(int.MaxValue);
            TestRunner.Assert(ids.Allocate() == int.MaxValue, "last id skipped");
            TestRunner.Throws<InvalidOperationException>(() => ids.Allocate());
            TestRunner.Throws<InvalidOperationException>(() => ids.Allocate());
            TestRunner.Throws<ArgumentOutOfRangeException>(() => BodyIds.DropTankId(0, BodyIds.MaxDropsPerActor));
            TestRunner.Throws<ArgumentOutOfRangeException>(() => BodyIds.DropTankId(BodyIds.MaxAllocationSlots, 0));
            TestRunner.Assert(BodyIds.DropTankId(BodyIds.MaxAllocationSlots - 1, BodyIds.MaxDropsPerActor - 1) == BodyIds.CarBase - 1,
                "drop space overlaps cars");
            var roster = new ParticipantRoster(firstActor: int.MaxValue, firstSlot: BodyIds.MaxAllocationSlots - 1);
            var last = roster.Add(2, ActorKind.Human, "Last actor", AppearanceRecord.FromPalette(0), ControllerConfig.Human);
            TestRunner.Assert(last.ActorId == int.MaxValue && last.AllocationSlot == BodyIds.MaxAllocationSlots - 1 && !roster.CanAdd,
                "last actor/slot was skipped or admission remained open");
            TestRunner.Throws<InvalidOperationException>(() => roster.Add(3, ActorKind.Bot, "Overflow", last.Appearance, ControllerConfig.Bot));
            TestRunner.Assert(roster.Revision == 1 && roster.Present.Count == 1, "exhaustion mutated the roster");
        });
        runner.Run("participants/drop exhaustion never advances or wraps the allocation counter", () =>
        {
            var actor = new PlayerCharacter { World = runner.World };
            try
            {
                var state = ClientActorAccess.Standalone(actor);
                state.AllocationSlot = 3;
                state.DropCounter = BodyIds.MaxDropsPerActor - 1;
                TestRunner.Assert(state.AllocateDropId() == BodyIds.DropTankId(3, BodyIds.MaxDropsPerActor - 1), "last drop skipped");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    TestRunner.Throws<ArgumentOutOfRangeException>(() => state.AllocateDropId());
                    TestRunner.Assert(state.DropCounter == BodyIds.MaxDropsPerActor, "exhaustion mutated counter");
                }
            }
            finally { actor.Runtime.Dispose(); actor.Free(); }
        });
        runner.Run("participants/life exhaustion cannot partially respawn the roster", () =>
        {
            var roster = new ParticipantRoster(firstLife: uint.MaxValue);
            var a = roster.Add(2, ActorKind.Human, "Last life", AppearanceRecord.FromPalette(0), ControllerConfig.Human);
            roster.Add(3, ActorKind.Bot, "Bot", a.Appearance, ControllerConfig.Bot);
            TestRunner.Throws<OverflowException>(() => roster.Respawn(a.ActorId));
            TestRunner.Throws<OverflowException>(roster.RespawnAll);
            TestRunner.Assert(roster.Revision == 2 && roster.Get(a.ActorId)!.LifeId == uint.MaxValue,
                "exhaustion partially changed life or revision");
        });
        runner.Run("participants/ownership, revisions, departure and lives", () =>
        {
            var roster = new ParticipantRoster();
            TestRunner.Throws<ArgumentException>(() => roster.Add(44, (ActorKind)255, "Invalid", AppearanceRecord.FromPalette(0), ControllerConfig.Bot));
            TestRunner.Assert(roster.Revision == 0 && roster.Present.Count == 0, "invalid actor mutated roster");
            var human = roster.Add(44, ActorKind.Human, "Human", AppearanceRecord.FromPalette(0), ControllerConfig.Human);
            var bot = roster.Add(44, ActorKind.Bot, "Bot", AppearanceRecord.FromPalette(0), ControllerConfig.Bot);
            TestRunner.Assert(human.ActorId != human.OwnerPeerId && roster.Owns(44, bot.ActorId, 1), "peer and actor conflated");
            TestRunner.Assert(!roster.Update(45, bot.ActorId, "Spoof", bot.Appearance, bot.ControllerConfig) && roster.Revision == 2,
                "spoof changed revision");
            TestRunner.Assert(roster.Update(44, bot.ActorId, "New", bot.Appearance, bot.ControllerConfig) && roster.Revision == 3, "update failed");
            var life = roster.Respawn(human.ActorId);
            TestRunner.Assert(life.LifeId == 2 && !roster.Owns(44, human.ActorId, 1), "old life accepted");
            var revision = roster.Revision;
            roster.RemovePeer(44);
            TestRunner.Assert(roster.Present.Count == 0 && !roster.Get(bot.ActorId)!.Present && roster.Revision == revision + 1,
                "peer bots survived departure or removal was not one roster revision");
            var next = roster.Add(45, ActorKind.Human, "Next", human.Appearance, ControllerConfig.Human);
            TestRunner.Assert(next.ActorId > bot.ActorId && next.AllocationSlot > bot.AllocationSlot, "retired identity reused");
        });
    }
}
