using System;
using System.Text.Json;
using Godot;
using Propane.Dev;

namespace Propane.Net;

/// <summary>
/// Asks GitHub, once at launch, whether a newer release is out, so the main menu can offer it. Updating is up to the
/// player: a newer patch still plays with this one. Only downloaded builds ask; games run from source do not, unless
/// started with <c>--update-check</c>. <c>--update-url=</c> points the check at another releases API URL.
/// </summary>
public partial class UpdateCheck : Node
{
    private const string LatestReleaseApi = "https://api.github.com/repos/talmage89/propane-game/releases/latest";

    /// <summary>The newer version that is out, once found.</summary>
    public string? Latest { get; private set; }

    public event Action? Found;

    public override async void _Ready()
    {
        if (OS.HasFeature("editor") && DevArgs.Get("update-check") == null)
        {
            return;
        }
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Propane/{BuildInfo.GameVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var json = JsonDocument.Parse(await http.GetStringAsync(DevArgs.Get("update-url") ?? LatestReleaseApi));
            var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            GD.Print($"[update] latest release {tag}, this game {BuildInfo.GameVersion}");
            if (BuildInfo.IsNewer(tag) && SemVer.Parse(tag) is { } latest)
            {
                Latest = latest.ToString();
                Found?.Invoke();
            }
        }
        catch (Exception e)
        {
            // Offline, or GitHub unreachable: say nothing to the player.
            GD.Print($"[update] check failed: {e.Message}");
        }
    }
}
