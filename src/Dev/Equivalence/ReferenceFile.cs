using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;

namespace Propane.Dev.Equivalence;

/// <summary>Exact, named release observations. Only an explicit writer invocation can replace a reference.</summary>
public static class ReferenceFile
{
    public static string DirectoryPath => DevArgs.Get("reference-dir") ?? ProjectSettings.GlobalizePath("res://src/Dev/Equivalence/Fixtures");
    public static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public static object? Value(object? value)
    {
        if (value == null) return null;
        return value switch
        {
            float f => $"f32:{BitConverter.SingleToUInt32Bits(f):X8}",
            double d => $"f64:{BitConverter.DoubleToUInt64Bits(d):X16}",
            string or bool => value,
            Enum e => $"{e.GetType().Name}.{e}:{Convert.ToInt64(e, CultureInfo.InvariantCulture)}",
            byte or sbyte or short or ushort or int or uint or long or ulong => value,
            Variant v => Value(v.VariantType switch
            {
                Variant.Type.Bool => v.AsBool(),
                Variant.Type.Int => (object)v.AsInt64(),
                Variant.Type.Float => v.AsDouble(),
                Variant.Type.String or Variant.Type.StringName => v.AsString(),
                _ => throw new InvalidDataException($"unobserved variant {v.VariantType}")
            }),
            Vector2 v => new[] { Value(v.X), Value(v.Y) },
            Rect2 r => new { position = Value(r.Position), size = Value(r.Size) },
            Vector3 v => new[] { Value(v.X), Value(v.Y), Value(v.Z) },
            Quaternion q => new[] { Value(q.X), Value(q.Y), Value(q.Z), Value(q.W) },
            Basis b => new[] { Value(b.X), Value(b.Y), Value(b.Z) },
            Transform3D t => new { basis = Value(t.Basis), origin = Value(t.Origin) },
            Color c => new[] { Value(c.R), Value(c.G), Value(c.B), Value(c.A) },
            Godot.Collections.Dictionary dictionary => dictionary.Select(p => new { name = p.Key.AsString(), value = Value(p.Value) }).ToArray(),
            byte[] bytes => Convert.ToBase64String(bytes),
            IEnumerable list => list.Cast<object?>().Select(Value).ToArray(),
            _ => Members(value),
        };
    }

    private static object Members(object value)
    {
        var type = value.GetType();
        if (type.Namespace?.StartsWith("Propane", StringComparison.Ordinal) != true && !type.IsValueType && !type.IsAnonymous())
            throw new InvalidDataException($"unobserved type {type}");
        var fields = new Dictionary<string, object?>();
        // Compiler-generated anonymous names change when unrelated code moves; only their fields are observations.
        if (!type.IsAnonymous()) fields["$type"] = type.Name;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) fields.Add(field.Name, Value(field.GetValue(value)));
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 &&
            (type.IsAnonymous() || type.GetField($"<{p.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) != null)))
        {
            fields.Add(property.Name, Value(property.GetValue(value)));
        }

        return fields;
    }

    private static bool IsAnonymous(this Type type) => type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);

    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static void Check(string name, object actual)
    {
        var output = DevArgs.Get("write-reference");
        if (output != null)
        {
            Directory.CreateDirectory(output);
            using var file = File.Create(Path.Combine(output, name + ".json.gz"));
            using var zip = new GZipStream(file, CompressionLevel.SmallestSize);
            JsonSerializer.Serialize(zip, actual, JsonOptions);
            GD.Print($"[reference] wrote {name}");
            return;
        }
        var expected = Read(name);
        Compare(expected, Element(actual), name);
    }

    public static JsonElement Read(string name)
    {
        var path = Path.Combine(DirectoryPath, name + ".json.gz");
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(zip);
            return document.RootElement.Clone();
        }
        catch (Exception e) { throw new InvalidDataException($"reference {name}: missing or invalid fixture: {e.Message}", e); }
    }

    public static void Compare(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind) Fail();
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().ToArray(); var right = actual.EnumerateObject().ToArray();
            if (!left.Select(p => p.Name).SequenceEqual(right.Select(p => p.Name)))
                throw new InvalidDataException($"{path}: field inventory/order differs: {string.Join(',', left.Select(p => p.Name))} -> {string.Join(',', right.Select(p => p.Name))}");
            foreach (var field in left) Compare(field.Value, actual.GetProperty(field.Name), path + "." + field.Name);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength())
                throw new InvalidDataException($"{path}.length: expected {expected.GetArrayLength()}, actual {actual.GetArrayLength()}");
            for (var i = 0; i < expected.GetArrayLength(); i++) Compare(expected[i], actual[i], $"{path}[{i}]");
        }
        else if (expected.GetRawText() != actual.GetRawText())
        {
            Fail();
        }

        return;
        void Fail() => throw new InvalidDataException($"{path}: expected {expected.GetRawText()}, actual {actual.GetRawText()}");
    }

    public static void Bytes(byte[] expected, byte[] actual, string path)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < length; i++)
            if (expected[i] != actual[i]) throw new InvalidDataException($"{path}.byte[{i}]: expected {expected[i]:X2}, actual {actual[i]:X2}");
        if (expected.Length != actual.Length) throw new InvalidDataException($"{path}.length: expected {expected.Length}, actual {actual.Length}");
    }
}
