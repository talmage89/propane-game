using System.Globalization;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev;

/// <summary>Headless checks of platform downloads and recorder arguments. Exits 1 on failure.</summary>
public partial class PlatformTests : Node
{
    private int failures;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        const string root = "https://github.com/talmage89/propane-game/releases/download/v1.2.3/Propane-1.2.3-";
        Check("Windows download", BuildInfo.DownloadUrl("1.2.3", "Windows") == root + "windows-x86_64.zip");
        Check("macOS download", BuildInfo.DownloadUrl("v1.2.3", "macOS") == root + "macos-arm64.zip");
        Check("Linux download", BuildInfo.DownloadUrl("1.2.3", "Linux") == root + "linux-x86_64.tar.gz");
        Check("unknown platform", BuildInfo.DownloadUrl("1.2.3", "Android") == BuildInfo.ReleasesUrl);
        Check("invalid version", BuildInfo.DownloadUrl("../latest", "Windows") == BuildInfo.ReleasesUrl);

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            const string path = @"C:\Videos with spaces\Propane ü\clip.mp4";
            var start = CaptureDirector.RecorderStartInfo(path, new Vector2I(1920, 1080), 29.97);
            var args = start.ArgumentList.ToArray();
            Check("recorder executable", start.FileName == "ffmpeg" && !start.UseShellExecute && start.RedirectStandardInput && start.CreateNoWindow);
            Check("literal output path", args[^1] == path && start.Arguments.Length == 0);
            Check("invariant frame rate", args[System.Array.IndexOf(args, "-r") + 1] == "29.97");
            Check("literal filter", args[System.Array.IndexOf(args, "-vf") + 1] == "scale='min(1280,iw)':-2");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        GD.Print(failures == 0 ? "[platformtests] ALL PASSED (9 checks)" : $"[platformtests] {failures} FAILURES");
        CoreTests.Complete(this, failures == 0 ? 0 : 1);
    }

    private void Check(string name, bool passed)
    {
        if (!passed)
        {
            failures++;
            GD.PushError($"[platformtests] FAIL {name}");
        }
    }
}
