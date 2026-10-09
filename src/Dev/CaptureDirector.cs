using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Dev;

/// <summary>
/// Runs a timeline of scripted actions and screenshots for automated visual checks. Pass
/// <c>-- --capture-dir=/some/dir</c> to save PNGs; without it the timeline still runs.
/// Use <c>--fixed-fps 60</c> so timing is deterministic regardless of render speed.
/// </summary>
public partial class CaptureDirector : Node
{
    private readonly List<(float Time, Action Action)> timeline = new();
    private float elapsed;
    private int next;

    public float QuitAfter { get; set; } = -1;

    public void At(float time, Action action) => timeline.Add((time, action));

    public void ShotAt(float time, string name) => At(time, () => Screenshot(name));

    public override void _Ready()
    {
        timeline.Sort((a, b) => a.Time.CompareTo(b.Time));
        ProcessMode = ProcessModeEnum.Always;
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
        if (QuitAfter > 0 && elapsed >= QuitAfter)
        {
            GetTree().Quit();
        }
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
