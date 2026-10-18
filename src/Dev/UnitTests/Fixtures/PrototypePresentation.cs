using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Same test source exercises the parent rule input and the candidate value input.</summary>
internal static class PrototypePresentation
{
    internal static object State(IModeRules rules, ModeClock clock) =>
        rules.GetType().GetMethod("State", new[] { typeof(ModeClock) })?.Invoke(rules, new object[] { clock }) ?? rules;

    internal static ModeHudInput Input(int actor, ModePhase phase, float left, IReadOnlyDictionary<int, int> scores,
        IReadOnlyList<Vector3> objectives, bool show, IModeRules rules, ModeClock clock)
    {
        var constructor = typeof(ModeHudInput).GetConstructors().Single(c => c.GetParameters().Length > 1);
        var view = State(rules, clock);
        var values = constructor.GetParameters().Select(p => p.Name switch
        {
            "LocalActor" => (object)actor,
            "Phase" => phase,
            "TimeLeft" => left,
            "Scores" => scores,
            "Objectives" => objectives,
            "ShowObjectives" => show,
            "Rules" => rules,
            "Clock" => clock,
            "View" => view is IModeView ? view : null,
            _ => throw new InvalidOperationException(p.Name),
        }).ToArray();
        return (ModeHudInput)constructor.Invoke(values);
    }

    internal static string Banner(ShortFuseRules rules, int actor) =>
        Call(typeof(ShortFuseHud), "Banner", rules, actor);
    internal static string Line(ShortFuseRules rules, ActorDied death) =>
        Call(typeof(ShortFuseHud), "Line", rules, death);
    private static string Call(Type presenter, string method, IModeRules rules, object value)
    {
        var target = presenter.GetMethod(method)!;
        var input = target.GetParameters()[0].ParameterType.IsInstanceOfType(rules) ? rules : State(rules, default);
        return (string)target.Invoke(null, new[] { input, value })!;
    }
}
