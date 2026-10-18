using Propane.Modes;
namespace Propane.Dev.UnitTests.Fixtures;

// Frozen pre-B3b2 presenter inputs. Keep these oracles independent of the new projections.
internal sealed record LegacyPrototypeInput(int LocalActor, ModePhase Phase, float TimeLeft, IModeRules Rules, ModeClock Clock);
