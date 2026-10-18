using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Godot;
using Propane.Core;
using Thread = System.Threading.Thread;

namespace Propane.ReleaseSmoke;

/// <summary>
/// Runs a timeline of scripted actions and screenshots for automated visual checks. Pass
/// <c>-- --capture-dir=/some/dir</c> to save PNGs; without it the timeline still runs.
/// Use <c>--fixed-fps 60</c> so timing is deterministic regardless of render speed.
/// <c>--record=/some/file.mp4</c> films the window in real time (30 fps, or <c>--record-fps</c>) through ffmpeg,
/// which must be on PATH; unlike Godot's movie maker it keeps a networked game in step with the others.
/// A capture that cannot be delivered fails the run (exit status 1) instead of being skipped: an ffmpeg that is missing,
/// stalls, dies, exits nonzero or writes no file, and a screenshot that cannot be saved. No recorder wait lasts longer
/// than <c>--record-timeout</c> seconds (default 30), after which the encoder is killed.
/// </summary>
public partial class CaptureDirector : Node
{
    private readonly List<(float Time, Action Action)> timeline = new();
    private float elapsed;
    private int next;
    private FrameEncoder? encoder;
    private Vector2I recordSize;
    private double recordFps;
    private TimeSpan recordTimeout;
    private ulong recordStart;
    private long framesWritten;
    private long framesSent;
    private int recordPending;
    private int screenshotPending;
    private readonly List<(string Dir, string Name, int Width, int Height, byte[] Data)> screenshots = new();
    private bool recordUnavailable;
    private bool recordFailureReported;

    public bool CollectFrameMetrics { get; init; } = true;

    public float QuitAfter { get; set; } = -1;

    public void At(float time, Action action) => timeline.Add((time, action));

    public void ShotAt(float time, string name) => At(time, () => Screenshot(name));

    public override void _Ready()
    {
        if (CollectFrameMetrics && LaunchArgs.Current.Get("frame-metrics") != null)
            AddChild(new FrameMetrics { ProcessPriority = int.MaxValue });
        timeline.Sort((a, b) => a.Time.CompareTo(b.Time));
        ProcessMode = ProcessModeEnum.Always;
        recordFps = LaunchArgs.Current.GetFloat("record-fps", 30f);
        recordTimeout = TimeSpan.FromSeconds(Math.Max(LaunchArgs.Current.GetFloat("record-timeout", 30f), 0.1f));
    }

    public override void _ExitTree()
    {
        if (screenshotPending > 0)
        {
            var screenshotDevice = RenderingServer.GetRenderingDevice();
            for (var frame = 0; screenshotPending > 0 && frame <= screenshotDevice.GetFrameDelay(); frame++)
                RenderingServer.ForceDraw();
        }
        FlushScreenshots();
        if (encoder == null)
        {
            return;
        }
        encoder.Complete(recordTimeout);
        // Drain managed GPU readback callbacks while .NET is still alive. Otherwise RenderingDevice.finalize
        // invokes them during engine shutdown, after Godot has torn down its C# bindings.
        var device = RenderingServer.GetRenderingDevice();
        for (var frame = 0; recordPending > 0 && frame <= device.GetFrameDelay(); frame++)
        {
            RenderingServer.ForceDraw();
        }
        var exitCode = encoder.Finish(recordTimeout);
        GD.Print($"[record] {framesWritten / recordFps:0.0} s, {framesSent} distinct frames of {framesWritten}, ffmpeg exit {exitCode?.ToString() ?? "killed"}");
        ReportRecordFailure();
        encoder.Dispose();
        encoder = null;
    }

    public override void _Process(double delta)
    {
        // Real (unscaled) frame time, so hitstop does not stall the script.
        elapsed += (float)(delta / Mathf.Max(Engine.TimeScale, 0.0001));
        while (next < timeline.Count && timeline[next].Time <= elapsed)
        {
            timeline[next].Action();
            next++;
        }
        Record();
        if (QuitAfter > 0 && elapsed >= QuitAfter)
        {
            ShutdownGuard.Quit(GetTree());
        }
    }

    /// <summary>Fails the run: logs why and asks for an orderly exit with status 1.</summary>
    private void Fail(string message)
    {
        GD.PushError(message);
        if (IsInsideTree())
        {
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    /// <summary>Reports the recorder's first failure once, whether it surfaced while filming or while shutting down.</summary>
    private void ReportRecordFailure()
    {
        recordUnavailable = true;
        if (encoder?.Failure is { } failure && !recordFailureReported)
        {
            recordFailureReported = true;
            Fail($"[record] FAIL {failure}");
        }
    }

    /// <summary>
    /// Hands the frame to the writer thread as often as the wall clock calls for. When the encoder falls behind, frames
    /// are skipped and the next one is repeated in their place, so the video keeps real time without stalling the game.
    /// </summary>
    private void Record()
    {
        var path = LaunchArgs.Current.Get("record");
        if (path == null || recordUnavailable || DisplayServer.GetName() == "headless")
        {
            return;
        }
        if (encoder?.Failure != null)
        {
            ReportRecordFailure();
            return;
        }
        var now = Time.GetTicksUsec();
        if (encoder == null)
        {
            // The rendered texture, which can be larger than the viewport's logical size.
            var format = RenderingServer.GetRenderingDevice().TextureGetFormat(RenderingServer.TextureGetRdTexture(GetViewport().GetTexture().GetRid()));
            recordSize = new Vector2I((int)format.Width, (int)format.Height);
            try
            {
                encoder = new FrameEncoder(RecorderStartInfo(path, recordSize, recordFps), path);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                recordUnavailable = true;
                recordFailureReported = true;
                Fail($"[record] FAIL could not start ffmpeg (put it on PATH to record): {e.Message}");
                return;
            }
            recordStart = now;
            GD.Print($"[record] {recordSize.X}x{recordSize.Y} at {recordFps} fps to {path}");
        }
        var due = (long)((now - recordStart) / 1e6 * recordFps) + 1;
        if (due <= framesWritten || recordPending >= FrameEncoder.Capacity)
        {
            return;
        }
        // Read the frame back without stalling the GPU; it arrives a frame or two later, still in order.
        var repeat = (int)(due - framesWritten);
        framesWritten = due;
        recordPending++;
        var texture = RenderingServer.TextureGetRdTexture(GetViewport().GetTexture().GetRid());
        RenderingServer.GetRenderingDevice().TextureGetDataAsync(texture, 0, Callable.From((byte[] data) =>
        {
            recordPending--;
            // A resized window no longer matches the stream; its frames are left out.
            if (encoder != null && data.Length == recordSize.X * recordSize.Y * 4 && encoder.Offer(data, repeat, recordTimeout))
            {
                framesSent++;
            }
        }));
    }

    /// <summary>Pass literal arguments on every OS, including Windows paths with spaces or a trailing backslash.</summary>
    internal static ProcessStartInfo RecorderStartInfo(string path, Vector2I size, double fps)
    {
        var start = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{size.X}x{size.Y}",
                     "-r", fps.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", "-", "-vf", "scale='min(1280,iw)':-2",
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", path,
                 })
        {
            start.ArgumentList.Add(arg);
        }
        return start;
    }

    /// <summary>Network phase captures cannot stall the GPU to read a frame: a stall can skip the whole contact
    /// window. Hold raw readbacks until exit so PNG encoding cannot stall the connection either.
    /// ExitTree drains callbacks before C# teardown.</summary>
    public void ScreenshotAsync(string name)
    {
        var dir = LaunchArgs.Current.Get("capture-dir");
        if (dir == null || DisplayServer.GetName() == "headless") return;
        var made = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (made != Error.Ok)
        {
            Fail($"[capture] FAIL could not create {dir}: {made}");
            return;
        }
        var device = RenderingServer.GetRenderingDevice();
        var texture = RenderingServer.TextureGetRdTexture(GetViewport().GetTexture().GetRid());
        var format = device.TextureGetFormat(texture);
        var width = (int)format.Width;
        var height = (int)format.Height;
        screenshotPending++;
        var status = device.TextureGetDataAsync(texture, 0, Callable.From((byte[] data) =>
        {
            try
            {
                if (data.Length != width * height * 4) throw new InvalidOperationException("unexpected screenshot texture format");
                screenshots.Add((dir, name, width, height, data));
            }
            catch (Exception error) { Fail($"[capture] FAIL {error.Message}"); }
            finally { screenshotPending--; }
        }));
        if (status != Error.Ok)
        {
            screenshotPending--;
            Fail($"[capture] FAIL readback request: {status}");
        }
    }

    private void FlushScreenshots()
    {
        foreach (var shot in screenshots)
        {
            using var image = Image.CreateFromData(shot.Width, shot.Height, false, Image.Format.Rgba8, shot.Data);
            if (SaveScreenshot(image, shot.Dir, shot.Name) is { } failure)
            {
                Fail($"[capture] FAIL {failure}");
            }
            else
            {
                GD.Print($"[capture] {shot.Dir}/{shot.Name}.png");
            }
        }
        screenshots.Clear();
    }

    public void Screenshot(string name)
    {
        var dir = LaunchArgs.Current.Get("capture-dir");
        if (dir == null || DisplayServer.GetName() == "headless")
        {
            return;
        }
        using var image = GetViewport().GetTexture().GetImage();
        if (image == null)
        {
            Fail($"[capture] FAIL no image to save for {dir}/{name}.png");
            return;
        }
        if (SaveScreenshot(image, dir, name) is { } failure)
        {
            Fail($"[capture] FAIL {failure}");
            return;
        }
        GD.Print($"[capture] {dir}/{name}.png");
    }

    /// <summary>Saves <paramref name="dir"/>/<paramref name="name"/>.png, creating the directory; returns why it could not, or null.</summary>
    internal static string? SaveScreenshot(Image image, string dir, string name)
    {
        var made = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (made != Error.Ok)
        {
            return $"could not create {dir}: {made}";
        }
        var path = $"{dir}/{name}.png";
        var saved = image.SavePng(path);
        return saved == Error.Ok ? null : $"could not save {path}: {saved}";
    }
}

/// <summary>
/// Streams raw frames to an encoder process (ffmpeg) from a worker thread. It never throws into the game and never
/// waits without a bound: the first failure (a broken pipe, an encoder that stalls, dies, exits nonzero or leaves no
/// file) is kept in <see cref="Failure"/> for the caller to report, and an encoder that stops responding is killed.
/// The worker calls no Godot API.
/// </summary>
internal sealed class FrameEncoder : IDisposable
{
    /// <summary>Frames that may wait for the worker. Bounded so a slow encoder cannot make memory grow.</summary>
    public const int Capacity = 3;

    private readonly Process process;
    private readonly string outputPath;
    private readonly BlockingCollection<(byte[] Data, int Repeat)> queue = new(Capacity);
    private readonly Thread writer;
    private string? failure;

    public FrameEncoder(ProcessStartInfo start, string outputPath)
    {
        this.outputPath = outputPath;
        process = Process.Start(start) ?? throw new InvalidOperationException("the encoder did not start");
        writer = new Thread(WriteFrames) { Name = "CaptureEncoder", IsBackground = true };
        writer.Start();
    }

    /// <summary>Why recording failed, or null. The first failure wins; thread-safe.</summary>
    public string? Failure => Volatile.Read(ref failure);

    private void Fail(string message) => Interlocked.CompareExchange(ref failure, message, null);

    private void WriteFrames()
    {
        try
        {
            var stream = process.StandardInput.BaseStream;
            foreach (var (data, repeat) in queue.GetConsumingEnumerable())
            {
                if (Failure != null)
                {
                    // Keep draining so the game thread never blocks on a dead encoder.
                    continue;
                }
                try
                {
                    for (var k = 0; k < repeat; k++)
                    {
                        stream.Write(data, 0, data.Length);
                    }
                }
                catch (IOException e)
                {
                    Fail($"the encoder stopped reading frames: {e.Message}");
                }
            }
            stream.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail($"the encoder's input failed: {e.Message}");
        }
    }

    /// <summary>
    /// Queues a frame (repeated <paramref name="repeat"/> times in the stream). Returns false when the frame was not
    /// queued because recording already failed or ended, or because the encoder took none for <paramref name="stall"/>.
    /// </summary>
    public bool Offer(byte[] data, int repeat, TimeSpan stall)
    {
        if (Failure != null || queue.IsAddingCompleted)
        {
            return false;
        }
        if (process.HasExited)
        {
            Fail($"the encoder exited early with code {process.ExitCode}");
            return false;
        }
        if (queue.TryAdd((data, repeat), stall))
        {
            return true;
        }
        Fail($"the encoder accepted no frame for {stall.TotalSeconds:0.#} s");
        return false;
    }

    /// <summary>Stops accepting frames and waits up to <paramref name="timeout"/> for the queued ones to be written.</summary>
    public void Complete(TimeSpan timeout)
    {
        queue.CompleteAdding();
        if (writer.Join(timeout))
        {
            return;
        }
        Fail($"the encoder did not take its last frames within {timeout.TotalSeconds:0.#} s");
        Kill();
        // Killing the encoder breaks the pipe, which frees a writer blocked in a write.
        writer.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the encoder to finish the file, then checks its status and output.
    /// Returns its exit code, or null when it had to be killed.
    /// </summary>
    public int? Finish(TimeSpan timeout)
    {
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            Fail($"the encoder did not finish within {timeout.TotalSeconds:0.#} s");
            Kill();
            return null;
        }
        if (process.ExitCode != 0)
        {
            Fail($"the encoder exited with code {process.ExitCode}");
        }
        else if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            Fail($"the encoder wrote no file at {outputPath}");
        }
        return process.ExitCode;
    }

    private void Kill()
    {
        try
        {
            process.Kill(true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        if (!process.HasExited)
        {
            Kill();
        }
        process.Dispose();
        queue.Dispose();
    }
}
