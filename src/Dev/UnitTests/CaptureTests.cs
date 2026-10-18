using System;
using System.Diagnostics;
using System.IO;
using Godot;
using Propane.ReleaseSmoke;

namespace Propane.Dev.UnitTests;

/// <summary>
/// Capture failures reach the exit status, headless: a recorder's encoder that dies, never reads, stays alive, exits
/// nonzero or writes nothing is reported within its bound (fake encoders are /bin/sh scripts), the ffmpeg command line
/// for a good recording is unchanged, and a screenshot that cannot be saved says why.
/// </summary>
[UnitSuite]
public static class CaptureTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(500);

    private static string Scratch() => Directory.CreateTempSubdirectory("capture-tests-").FullName;

    private static ProcessStartInfo Shell(string script, string output)
    {
        // The script sees the output path as $0.
        var start = new ProcessStartInfo { FileName = "/bin/sh", RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(output);
        return start;
    }

    private sealed record Outcome(int Offered, string? Failure, int? Exit, long OutputBytes, TimeSpan Elapsed);

    /// <summary>Feeds <paramref name="frames"/> frames of <paramref name="frameBytes"/> to a fake encoder and shuts it down.</summary>
    private static Outcome Record(string script, int frames, int frameBytes = 4096, int repeat = 1)
    {
        var directory = Scratch();
        try
        {
            var output = Path.Combine(directory, "out.mp4");
            var clock = Stopwatch.StartNew();
            using var encoder = new FrameEncoder(Shell(script, output), output);
            var offered = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                if (encoder.Offer(new byte[frameBytes], repeat, Bound))
                {
                    offered++;
                }
            }
            encoder.Complete(Bound);
            var exit = encoder.Finish(Bound);
            return new Outcome(offered, encoder.Failure, exit, File.Exists(output) ? new FileInfo(output).Length : -1, clock.Elapsed);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    public static void Register(TestRunner runner)
    {
        if (File.Exists("/bin/sh"))
        {
            runner.Run("capture: a healthy encoder receives every frame as often as asked", () =>
            {
                var outcome = Record("cat > \"$0\"", 6, repeat: 3);
                TestRunner.Assert(outcome.Failure == null && outcome.Exit == 0, $"healthy encoder failed: {outcome}");
                TestRunner.Assert(outcome.Offered == 6 && outcome.OutputBytes == 6 * 3 * 4096, $"frames were lost: {outcome}");
            });
            runner.Run("capture: an encoder that dies at once fails the recording without blocking the game", () =>
            {
                var outcome = Record("exit 3", 12, frameBytes: 262144);
                TestRunner.Assert(outcome.Failure != null && outcome.Exit == 3, $"a dead encoder was not reported: {outcome}");
                TestRunner.Assert(outcome.Elapsed < TimeSpan.FromSeconds(10), $"a dead encoder held up the game: {outcome}");
            });
            runner.Run("capture: an encoder that exits nonzero after reading every frame fails the recording", () =>
            {
                var outcome = Record("cat > /dev/null; exit 4", 4);
                TestRunner.Assert(outcome.Exit == 4 && outcome.Failure != null && outcome.Failure.Contains("code 4", StringComparison.Ordinal),
                    $"a nonzero encoder status was not reported: {outcome}");
            });
            runner.Run("capture: an encoder that reports success but writes no file fails the recording", () =>
            {
                var outcome = Record("cat > /dev/null", 4);
                TestRunner.Assert(outcome.Exit == 0 && outcome.Failure != null && outcome.Failure.Contains("wrote no file", StringComparison.Ordinal),
                    $"a missing output was not reported: {outcome}");
            });
            runner.Run("capture: an encoder that never reads is cut off within the bounds", () =>
            {
                var outcome = Record("exec sleep 60", 10, frameBytes: 262144);
                TestRunner.Assert(outcome.Failure != null && outcome.Offered < 10, $"a stalled encoder was not reported: {outcome}");
                TestRunner.Assert(outcome.Elapsed < TimeSpan.FromSeconds(20), $"a stalled encoder was waited on without a bound: {outcome}");
            });
            runner.Run("capture: an encoder that reads everything but never exits is killed at the exit bound", () =>
            {
                var outcome = Record("cat > /dev/null; exec sleep 60", 3);
                TestRunner.Assert(outcome.Exit == null && outcome.Failure != null && outcome.Failure.Contains("did not finish", StringComparison.Ordinal),
                    $"a lingering encoder was not reported: {outcome}");
                TestRunner.Assert(outcome.Elapsed < TimeSpan.FromSeconds(20), $"a lingering encoder was waited on without a bound: {outcome}");
            });
        }
        runner.Run("capture: the ffmpeg command line of a good recording is unchanged", () =>
        {
            var start = CaptureDirector.RecorderStartInfo("out dir/a b.mp4", new Vector2I(640, 360), 30);
            TestRunner.Assert(start.FileName == "ffmpeg" && start.RedirectStandardInput && !start.UseShellExecute, "ffmpeg is started differently");
            var expected = new[]
            {
                "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", "640x360", "-r", "30", "-i", "-", "-vf",
                "scale='min(1280,iw)':-2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
                "-movflags", "+faststart", "out dir/a b.mp4",
            };
            TestRunner.Assert(string.Join('\n', start.ArgumentList) == string.Join('\n', expected), "the ffmpeg arguments changed");
        });
        runner.Run("capture: screenshots save, repeatedly into one directory", () =>
        {
            var directory = Path.Combine(Scratch(), "shots");
            try
            {
                using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
                TestRunner.Assert(CaptureDirector.SaveScreenshot(image, directory, "a") == null, "the first screenshot failed");
                TestRunner.Assert(CaptureDirector.SaveScreenshot(image, directory, "b") == null, "a second screenshot into the directory failed");
                TestRunner.Assert(File.Exists(Path.Combine(directory, "a.png")) && File.Exists(Path.Combine(directory, "b.png")), "screenshots are missing");
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, true);
            }
        });
        runner.Run("capture: a screenshot that cannot be saved says why", () =>
        {
            // Godot logs its own ERROR for each refused directory and save; they are the failures this test provokes.
            GD.Print("[unit] the next two engine ERROR lines are the screenshot failures under test");
            var root = Scratch();
            try
            {
                using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
                File.WriteAllText(Path.Combine(root, "file"), "not a directory");
                var made = CaptureDirector.SaveScreenshot(image, Path.Combine(root, "file", "shots"), "a");
                TestRunner.Assert(made != null && made.Contains("could not create", StringComparison.Ordinal), $"a directory failure was not reported: {made}");
                Directory.CreateDirectory(Path.Combine(root, "taken.png"));
                var saved = CaptureDirector.SaveScreenshot(image, root, "taken");
                TestRunner.Assert(saved != null && saved.Contains("could not save", StringComparison.Ordinal), $"a save failure was not reported: {saved}");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });
    }
}
