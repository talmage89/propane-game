using System.IO;
using System.Text.Json;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Called only by the isolated differential build; production has no observer or per-tick work.</summary>
internal static class MatchCompositionObservation
{
    public static void Record(string step, string? detail = null)
    {
        if (DevArgs.Get("composition-transcript") is { } path)
            File.AppendAllText(path, JsonSerializer.Serialize(new { step, detail }) + "\n");
    }
}
