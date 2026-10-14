using System;

namespace Propane.Core;

/// <summary>
/// Local UI range; unlike Export, this never enters a match snapshot. <see cref="Scenario"/> names the one Lab
/// scenario a dial lays out (a prototype's), or is null for dials every scenario may read.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LabDialAttribute(double min, double max, double step = 1) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Step { get; } = step;
    public string? Scenario { get; init; }
}

public partial class Tuning
{
    // Larger piles are available for scale trials; retain the ordinary starter size.
    public const int LabStackMinimum = 24;
    public const int LabStackMaximum = 512;
    public const int LabStackStep = 8;
    // Session copies include these via CopyLocalTunersFrom. Layout edits apply on Reset; physics edits are live.
    [LabDial(LabStackMinimum, LabStackMaximum, LabStackStep, Scenario = "stack")] public int LabStackCount { get; set; } = 40;
    [LabDial(5, 30, 5)] public float LabRangeStep { get; set; } = 10f;
    [LabDial(2, 6)] public int LabRangeLanes { get; set; } = 4;
    [LabDial(2, 10, 0.5)] public float LabLaneSpacing { get; set; } = 4f;
    [LabDial(1, 20)] public int LabRangePileCount { get; set; } = 5;
    [LabDial(10, 50)] public float LabStackDistance { get; set; } = 12f;
    [LabDial(0, 1, 0.05)] public float LabBotSkill { get; set; } = 0.6f;
    [LabDial(30, 600, 30)] public float LabDuelSeconds { get; set; } = 180f;
    [LabDial(10, 50)] public float LabArenaRadius { get; set; } = 24f;
    [LabDial(2, 8)] public int LabDuelPiles { get; set; } = 4;
    [LabDial(1, 12)] public int LabDuelPileCount { get; set; } = 7;
    [LabDial(100, 240, 10)] public float LabSuburbSize { get; set; } = 150f;
    [LabDial(0.1, 2, 0.05)] public float LabTuningOpenDelay { get; set; } = 0.5f;
}
