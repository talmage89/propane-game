using System.Collections.Generic;
using System.Reflection;
using Godot;
using Propane.Tank;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Same fixture source inspects pre/post B4b ownership without adding runtime observation APIs.</summary>
internal static class LabRunnerInspection
{
    internal static object TankState(Node runner) =>
        runner.GetType().GetField("tankState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runner) ?? runner;

    internal static Dictionary<PropaneTank, int> Shots(Node runner) =>
        (Dictionary<PropaneTank, int>)Field(TankState(runner), "directShots");
    internal static HashSet<PropaneTank> Drops(Node runner) =>
        (HashSet<PropaneTank>)Field(TankState(runner), "drops");
    internal static object Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
