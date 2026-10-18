using System.Collections.Generic;
using Propane.Bots;
using Propane.Tank;

namespace Propane.Net;

public partial class Match
{
    internal Dictionary<uint, PropaneTank>.ValueCollection BotTanks => worldReplica.BotTanks;
    internal KingBotView? BotKing => ownedBots.BotKing;
    internal (int Bots, long Thinks, long Ticks, double Seconds, double Worst, long Bytes, long Queries, long Rays, double NavigationSeconds) BotCost => ownedBots.BotCost;
    internal (long Ticks, double Seconds, double Worst, long Bytes, long AllocatingTicks, long PeakBytes) WarmBotCost => ownedBots.WarmBotCost;
    internal double BotTickPercentile(double fraction) => ownedBots.BotTickPercentile(fraction);
    internal IBotGoalPolicy? BotBrain(int actorId) => ownedBots.BotBrain(actorId);
    internal (long Ticks, long QueryLimited, long ExpansionLimited) BotNavigationSaturation => ownedBots.BotNavigationSaturation;
    internal BotActivity? BotActivityOf(int actorId) => ownedBots.BotActivityOf(actorId);
}
