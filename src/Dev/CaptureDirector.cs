using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Dev;

/// <summary>
/// Runs a timeline of scripted actions and screenshots for automated visual checks. Pass
/// <c>-- --capture-dir=/some/dir</c> to save PNGs; without it the timeline still runs.
/// Use <c>--fixed-fps 60</c> so timing is deterministic regardless of render speed.
/// <c>--record=/some/file.mp4</c> films the window in real time (30 fps, or <c>--record-fps</c>) through ffmpeg,
/// which must be on PATH; unlike Godot's movie maker it keeps a networked game in step with the others.
/// </summary>
public partial class CaptureDirector : Node
{
    private readonly List<(float Time, Action Action)> timeline = new();
    private float elapsed;
    private int next;
    private System.Diagnostics.Process? recorder;
    private Vector2I recordSize;
    private double recordFps;
    private ulong recordStart;
    private long framesWritten;
    private long framesSent;
    private int recordPending;
    private bool recordUnavailable;
    private System.Threading.Thread? recordWriter;
    private readonly System.Collections.Concurrent.BlockingCollection<(byte[] Data, int Repeat)> recordQueue = new(3);

    public float QuitAfter { get; set; } = -1;

    public void At(float time, Action action) => timeline.Add((time, action));

    public void ShotAt(float time, string name) => At(time, () => Screenshot(name));

    public override void _Ready()
    {
        timeline.Sort((a, b) => a.Time.CompareTo(b.Time));
        ProcessMode = ProcessModeEnum.Always;
        recordFps = DevArgs.GetFloat("record-fps", 30f);
    }

    public override void _ExitTree()
    {
        if (recorder == null)
        {
            return;
        }
        recordQueue.CompleteAdding();
        recordWriter?.Join();
        // Drain managed GPU readback callbacks while .NET is still alive. Otherwise RenderingDevice.finalize
        // invokes them during engine shutdown, after Godot has torn down its C# bindings.
        var device = RenderingServer.GetRenderingDevice();
        for (var frame = 0; recordPending > 0 && frame <= device.GetFrameDelay(); frame++)
        {
            RenderingServer.ForceDraw();
        }
        recorder.WaitForExit();
        GD.Print($"[record] {framesWritten / recordFps:0.0} s, {framesSent} distinct frames of {framesWritten}, ffmpeg exit {recorder.ExitCode}");
        recorder = null;
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
            GetTree().Quit();
        }
    }

    /// <summary>
    /// Hands the frame to the writer thread as often as the wall clock calls for. When the encoder falls behind, frames
    /// are skipped and the next one is repeated in their place, so the video keeps real time without stalling the game.
    /// </summary>
    private void Record()
    {
        var path = DevArgs.Get("record");
        if (path == null || recordUnavailable || DisplayServer.GetName() == "headless")
        {
            return;
        }
        var now = Time.GetTicksUsec();
        if (recorder == null)
        {
            // The rendered texture, which can be larger than the viewport's logical size.
            var format = RenderingServer.GetRenderingDevice().TextureGetFormat(RenderingServer.TextureGetRdTexture(GetViewport().GetTexture().GetRid()));
            recordSize = new Vector2I((int)format.Width, (int)format.Height);
            try
            {
                recorder = System.Diagnostics.Process.Start(RecorderStartInfo(path, recordSize, recordFps))!;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                recordUnavailable = true;
                GD.PushWarning($"[record] Could not start ffmpeg; put it on PATH to record: {e.Message}");
                return;
            }
            recordStart = now;
            recordWriter = new System.Threading.Thread(() =>
            {
                var stream = recorder.StandardInput.BaseStream;
                foreach (var (data, repeat) in recordQueue.GetConsumingEnumerable())
                {
                    for (var k = 0; k < repeat; k++)
                    {
                        stream.Write(data, 0, data.Length);
                    }
                }
                stream.Close();
            });
            recordWriter.Start();
            GD.Print($"[record] {recordSize.X}x{recordSize.Y} at {recordFps} fps to {path}");
        }
        var due = (long)((now - recordStart) / 1e6 * recordFps) + 1;
        if (due <= framesWritten || recordPending >= recordQueue.BoundedCapacity)
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
            if (!recordQueue.IsAddingCompleted && data.Length == recordSize.X * recordSize.Y * 4)
            {
                recordQueue.Add((data, repeat));
                framesSent++;
            }
        }));
    }

    /// <summary>Pass literal arguments on every OS, including Windows paths with spaces or a trailing backslash.</summary>
    internal static System.Diagnostics.ProcessStartInfo RecorderStartInfo(string path, Vector2I size, double fps)
    {
        var start = new System.Diagnostics.ProcessStartInfo
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

    public void Screenshot(string name)
    {
        var dir = DevArgs.Get("capture-dir");
        if (dir == null || DisplayServer.GetName() == "headless")
        {
            return;
        }
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var image = GetViewport().GetTexture().GetImage();
        var path = $"{dir}/{name}.png";
        image.SavePng(path);
        GD.Print($"[capture] {path}");
    }
}
