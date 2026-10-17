using System;
using System.Collections.Generic;
using System.Reflection;
using Propane.Core;
using Propane.Modes;

namespace Propane.Lab;

/// <summary>
/// Runner-owned live scalar sampling. Resolve getters once, then retain the immutable rules snapshot until a
/// value changes. Polling the resource (rather than UI notifications) also sees direct, CLI and snapshot edits.
/// This caches values only: the runner still delivers every TunersChanged event at its original sampling point.
/// </summary>
internal sealed class ModeTunerSampler
{
    private readonly string[] names;
    private readonly Func<Tuning, double>[] getters;
    private readonly double[] values;
    private ModeTuners? snapshot;

    public ModeTunerSampler(ModeDefinition mode)
    {
        names = new string[mode.Tuners.Count];
        getters = new Func<Tuning, double>[names.Length];
        values = new double[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = mode.Tuners[i];
            var property = typeof(Tuning).GetProperty(names[i]) ?? throw new InvalidOperationException($"no tuner {names[i]}");
            getters[i] = Getter(property);
        }
    }

    public ModeTuners Sample(Tuning tuning)
    {
        var changed = snapshot == null;
        for (var i = 0; i < getters.Length; i++)
        {
            var value = getters[i](tuning);
            // Compare bits so direct edits to signed zero and NaN payloads retain CopyTuners' exact scalar value.
            changed |= BitConverter.DoubleToInt64Bits(value) != BitConverter.DoubleToInt64Bits(values[i]);
            values[i] = value;
        }
        if (changed)
        {
            var copy = new Dictionary<string, double>(names.Length);
            for (var i = 0; i < names.Length; i++) copy[names[i]] = values[i];
            snapshot = new ModeTuners(copy);
        }
        return snapshot!;
    }

    // These are Tuning's scalar types. Widening floats/integers and mapping bool to 0/1 matches invariant
    // Convert.ToDouble in CopyTuners, without reflection invocation or boxing at the sampling point.
    private static Func<Tuning, double> Getter(PropertyInfo property)
    {
        var method = property.GetMethod ?? throw new InvalidOperationException($"no getter for tuner {property.Name}");
        if (property.PropertyType == typeof(float))
        {
            var get = method.CreateDelegate<Func<Tuning, float>>();
            return tuning => get(tuning);
        }
        if (property.PropertyType == typeof(int))
        {
            var get = method.CreateDelegate<Func<Tuning, int>>();
            return tuning => get(tuning);
        }
        if (property.PropertyType == typeof(bool))
        {
            var get = method.CreateDelegate<Func<Tuning, bool>>();
            return tuning => get(tuning) ? 1 : 0;
        }
        if (property.PropertyType == typeof(double)) return method.CreateDelegate<Func<Tuning, double>>();
        throw new InvalidOperationException($"unsupported tuner type {property.PropertyType}: {property.Name}");
    }
}
