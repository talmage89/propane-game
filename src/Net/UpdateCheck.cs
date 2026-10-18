using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Propane.Core;

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

    // One request per node lifetime. The request owns/disposes its cancellation source in finally; retirement
    // cancels it and suppresses both success and failure delivery even if a transport ignores cancellation.
    private CancellationTokenSource? pending;
    private bool retired;
    internal Func<string, CancellationToken, Task<string>> Request { get; init; } = Fetch;
    internal Task? Completion { get; private set; }

    public override void _Ready()
    {
        if (OS.HasFeature("editor") && !LaunchArgs.Current.Has("update-check"))
        {
            return;
        }
        Completion = Check();
    }

    private async Task Check()
    {
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        try
        {
            // Resume on Godot's main thread: Found updates the menu's nodes.
            var response = await Request(LaunchArgs.Current.Get("update-url") ?? LatestReleaseApi, cancellation.Token).ConfigureAwait(true);
            if (retired || cancellation.IsCancellationRequested) return;
            using var json = JsonDocument.Parse(response);
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
            if (!retired) GD.Print($"[update] check failed: {e.Message}");
        }
        finally { pending = null; }
    }

    private static async Task<string> Fetch(string url, CancellationToken cancellation)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Propane/{BuildInfo.GameVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return await http.GetStringAsync(url, cancellation).ConfigureAwait(true);
    }

    private void Retire()
    {
        if (retired) return;
        retired = true;
        Found = null;
        pending?.Cancel();
    }

    public override void _ExitTree() => Retire();

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete) Retire();
    }
}
