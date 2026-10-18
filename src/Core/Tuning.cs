using System.Linq;
using System.Reflection;
using Godot;

namespace Propane.Core;

/// <summary>
/// Every gameplay and effect dial in one place. Edit <c>res://tuning.tres</c> in the inspector, or press F1 in game
/// for live sliders. Systems read these values at the moment they use them, so changes apply immediately.
/// This file holds the machinery and no dials: they are declared by domain in the <c>Tuning.NN.Name.cs</c> parts, whose
/// names and the list in <c>Propane.csproj</c> fix the order of the properties (see there before adding a part).
/// </summary>
[GlobalClass]
public partial class Tuning : Resource
{
    public const string ProjectPath = "res://tuning.tres";
    /// <summary>Where an exported build keeps the player's saved dials, since <c>res://</c> is read-only there.</summary>
    public const string PlayerPath = "user://tuning.tres";

    private static Tuning? savedTemplate;
    private static readonly PropertyInfo[] LocalTuners = typeof(Tuning)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(property => property.CanWrite && !property.IsDefined(typeof(ExportAttribute)) &&
            (property.PropertyType == typeof(float) || property.PropertyType == typeof(int) || property.PropertyType == typeof(bool)))
        .ToArray();

    /// <summary>Godot duplicates exported properties; copy local brain dials too, without adding them to snapshots.</summary>
    public Tuning Copy()
    {
        var copy = (Tuning)Duplicate();
        copy.CopyLocalTunersFrom(this);
        return copy;
    }

    internal void CopyLocalTunersFrom(Tuning source)
    {
        foreach (var property in LocalTuners) { property.SetValue(this, property.GetValue(source)); }
    }

    /// <summary>
    /// The file Save writes to: the project's own tuning in the editor, the player's copy in an exported build.
    /// </summary>
    public static string FilePath => OS.HasFeature("editor") ? ProjectPath : PlayerPath;

    /// <summary>The process-lifetime application template, borrowed only at composition and by application UI. Never dispose it; worlds own copies. Only explicit launch overrides and successful Save update this preference. Loaded on first access from <see cref="FilePath"/>, falling back to the project's.</summary>
    public static Tuning SavedTemplate
    {
        get
        {
            if (savedTemplate != null)
            {
                return savedTemplate;
            }
            var path = ResourceLoader.Exists(FilePath) ? FilePath : ProjectPath;
            savedTemplate = (ResourceLoader.Exists(path) ? ResourceLoader.Load<Tuning>(path, cacheMode: ResourceLoader.CacheMode.Ignore) : null)
                ?? new Tuning();
            return savedTemplate;
        }
    }

    /// <summary>Save a panel's explicit target and update the application's template for future worlds.</summary>
    public static Error SaveTemplate(Tuning target)
    {
        var error = ResourceSaver.Save(target, FilePath);
        if (error == Error.Ok)
        {
            foreach (var name in ValueNames(target)) { SavedTemplate.Set(name, target.Get(name)); }
        }
        return error;
    }

    /// <summary>The values baked into the build (<c>res://tuning.tres</c>), ignoring any the player saved.</summary>
    public static Tuning LoadBaked() =>
        (ResourceLoader.Exists(ProjectPath) ? ResourceLoader.Load<Tuning>(ProjectPath, cacheMode: ResourceLoader.CacheMode.Ignore) : null) ?? new Tuning();

    /// <summary>Every tuning value (numbers and toggles) by property name.</summary>
    public Godot.Collections.Dictionary Snapshot()
    {
        var values = new Godot.Collections.Dictionary();
        foreach (var name in ValueNames(this))
        {
            values[name] = Get(name);
        }
        return values;
    }

    /// <summary>A tuning built from defaults with a <see cref="Snapshot"/> applied over them.</summary>
    public static Tuning FromSnapshot(Godot.Collections.Dictionary values)
    {
        var tuning = new Tuning();
        tuning.Apply(values);
        return tuning;
    }

    private static System.Collections.Generic.Dictionary<string, (Variant.Type Type, double Min, double Max)>? schema;

    /// <summary>Validates every entry before any value changes. Export hints define inclusive network limits.</summary>
    public static void ValidateSnapshot(Godot.Collections.Dictionary values, bool complete = false)
    {
        if (schema == null)
        {
            schema = new();
            using var defaults = new Tuning();
            var names = new System.Collections.Generic.HashSet<string>(ValueNames(defaults));
            foreach (var entry in defaults.GetPropertyList())
            {
                var name = entry["name"].AsString();
                if (!names.Contains(name)) { continue; }
                var type = (Variant.Type)entry["type"].AsInt64();
                var range = entry["hint_string"].AsString().Split(',');
                var min = type == Variant.Type.Int ? int.MinValue : double.MinValue;
                var max = type == Variant.Type.Int ? int.MaxValue : double.MaxValue;
                if ((PropertyHint)entry["hint"].AsInt64() == PropertyHint.Range)
                {
                    min = double.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
                    max = double.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                schema[name] = (type, min, max);
            }
        }
        if (complete && values.Count != schema.Count) { throw new System.IO.InvalidDataException("incomplete tuning snapshot"); }
        foreach (var (key, value) in values)
        {
            if (key.VariantType != Variant.Type.String || !schema.TryGetValue(key.AsString(), out var spec) || value.VariantType != spec.Type)
            {
                throw new System.IO.InvalidDataException($"unknown tuner or wrong type: {key}");
            }
            if (spec.Type == Variant.Type.Bool) { continue; }
            var number = value.AsDouble();
            if (!double.IsFinite(number) || number < spec.Min || number > spec.Max)
            {
                throw new System.IO.InvalidDataException($"tuner outside declared range: {key}");
            }
        }
    }

    /// <summary>Atomically applies a validated snapshot; unknown names and invalid values are refused.</summary>
    public void Apply(Godot.Collections.Dictionary values)
    {
        ValidateSnapshot(values);
        foreach (var (key, value) in values)
        {
            var name = key.AsString();
            Set(name, value);
        }
    }

    /// <summary>Names of the exported number and toggle properties, in declaration order.</summary>
    public static System.Collections.Generic.IEnumerable<string> ValueNames(Tuning tuning)
    {
        foreach (var entry in tuning.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)entry["usage"].AsInt64();
            var type = (Variant.Type)entry["type"].AsInt64();
            if (usage.HasFlag(PropertyUsageFlags.ScriptVariable) && usage.HasFlag(PropertyUsageFlags.Editor) &&
                type is Variant.Type.Float or Variant.Type.Int or Variant.Type.Bool)
            {
                yield return entry["name"].AsString();
            }
        }
    }
}
