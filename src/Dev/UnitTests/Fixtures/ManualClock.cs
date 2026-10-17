using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Tests own network time; CPU scheduling never advances a deadline or a sample's age.</summary>
internal sealed class ManualClock : IMonotonicClock
{
    public double Seconds { get; set; } = 100;
}
