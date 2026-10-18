using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record ArtifactPolicy(string[] SmokeTypes, Dictionary<string, string[]> EntryMethods, string[] Resources);

internal sealed partial class Scanner
{
    // Uses the same metadata names/module map as the dependency gate, without requiring shipping PDBs or loading code.
    public int Inventory(string packPath, string policyPath)
    {
        var expected = JsonSerializer.Deserialize<ArtifactPolicy>(File.ReadAllText(policyPath))!;
        var errors = new List<string>();
        foreach (var type in definitions.Keys.Where(t => t.StartsWith("Propane.", StringComparison.Ordinal)))
            if (ModuleOf(type) == "Dev") errors.Add("Dev type in artifact: " + type);
        foreach (var type in expected.SmokeTypes)
            if (!definitions.ContainsKey(type)) errors.Add("Missing smoke type: " + type);
        var smokeOwners = expected.SmokeTypes.Select(Owner).ToHashSet(StringComparer.Ordinal);
        foreach (var type in definitions.Keys.Where(t => t.StartsWith("Propane.ReleaseSmoke.", StringComparison.Ordinal)))
            if (!smokeOwners.Contains(Owner(type))) errors.Add("Unreviewed smoke type: " + type);
        foreach (var (type, methods) in expected.EntryMethods)
        {
            if (!definitions.TryGetValue(type, out var handle)) { errors.Add("Missing entry type: " + type); continue; }
            var present = reader.GetTypeDefinition(handle).GetMethods()
                .Select(m => reader.GetString(reader.GetMethodDefinition(m).Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var method in methods)
                if (!present.Contains(method)) errors.Add($"Missing entry method: {type}.{method}");
        }
        using var pack = new ArtifactPack(packPath);
        foreach (var path in pack.Paths)
        {
            if (path.StartsWith("src/Dev/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("scenes/dev/", StringComparison.OrdinalIgnoreCase))
                errors.Add("Dev resource in artifact: " + path);
        }
        foreach (var path in expected.Resources)
            if (!pack.Paths.Contains(path)) errors.Add("Missing artifact resource: " + path);
        // The main scene is converted to .scn. Require both its remap and its actual packed target.
        if (pack.Paths.Contains("scenes/main.tscn.remap"))
        {
            var remap = Encoding.UTF8.GetString(pack.Read("scenes/main.tscn.remap"));
            var target = Regex.Match(remap, "path=\"res://([^\"]+)\"");
            if (!target.Success || !pack.Paths.Contains(target.Groups[1].Value)) errors.Add("Missing main scene remap target");
        }
        if (pack.Paths.Contains("project.binary"))
        {
            var config = PackedSettings(pack.Read("project.binary"));
            foreach (var (key, value) in new[] { ("application/run/main_scene", "res://scenes/main.tscn"), ("autoload/ShutdownGuard", "*res://src/Core/ShutdownGuard.cs") })
                if (config.GetValueOrDefault(key) != value) errors.Add("Wrong or missing packed entry setting: " + key);
        }
        foreach (var error in errors) Console.Error.WriteLine(error);
        Console.WriteLine($"Artifact inventory: {definitions.Count} types, {pack.Paths.Count} resources, {errors.Count} failures");
        return errors.Count == 0 ? 0 : 1;
    }

    // ProjectSettings::_load_settings_binary framing; decode only string Variants needed for entry identity.
    private static Dictionary<string, string> PackedSettings(byte[] data)
    {
        using var input = new BinaryReader(new MemoryStream(data));
        if (input.ReadUInt32() != 0x47464345) throw new InvalidDataException("Expected ECFG project.binary");
        var count = input.ReadInt32();
        if (count is < 0 or > 10000) throw new InvalidDataException("Invalid packed setting count");
        byte[] Block()
        {
            var length = input.ReadInt32();
            if (length < 0 || length > input.BaseStream.Length - input.BaseStream.Position) throw new InvalidDataException("Invalid packed setting length");
            return input.ReadBytes(length);
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = new UTF8Encoding(false, true).GetString(Block());
            var value = Block();
            var text = "";
            if (value.Length >= 8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value) == 4)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(4));
                if (length < 0 || length > value.Length - 8) throw new InvalidDataException("Invalid packed string setting: " + key);
                text = new UTF8Encoding(false, true).GetString(value, 8, length);
            }
            if (!result.TryAdd(key, text)) throw new InvalidDataException("Duplicate packed setting: " + key);
        }
        if (input.BaseStream.Position != input.BaseStream.Length) throw new InvalidDataException("Trailing packed settings data");
        return result;
    }
}

// Standalone unencrypted Godot 4.7 PCK v4. Deliberately reject new formats/flags rather than silently skipping data.
// Format: Godot 4.7.2 core/io/file_access_pack.cpp, PackedSourcePCK::try_open_pack.
internal sealed class ArtifactPack : IDisposable
{
    private readonly BinaryReader input;
    private readonly Dictionary<string, (long Offset, int Size)> entries = new(StringComparer.Ordinal);
    public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);

    public ArtifactPack(string path)
    {
        input = new BinaryReader(File.OpenRead(path));
        try
        {
            if (input.ReadUInt32() != 0x43504447 || input.ReadUInt32() != 4) throw new InvalidDataException("Expected standalone PCK v4");
            if (input.ReadUInt32() != 4 || input.ReadUInt32() != 7 || input.ReadUInt32() != 2) throw new InvalidDataException("Expected Godot 4.7.2 pack");
            if (input.ReadUInt32() != 2) throw new InvalidDataException("Expected unencrypted relative-filebase pack");
            var fileBase = checked((long)input.ReadUInt64());
            var directory = checked((long)input.ReadUInt64());
            if (fileBase < 40 || directory < fileBase || directory >= input.BaseStream.Length) throw new InvalidDataException("Invalid PCK offsets");
            input.BaseStream.Position = directory;
            var count = input.ReadUInt32();
            if (count > 100000) throw new InvalidDataException("Invalid PCK file count");
            for (var index = 0; index < count; index++)
            {
                var length = input.ReadInt32();
                if (length <= 0 || length > 16384) throw new InvalidDataException("Invalid PCK path length");
                var name = new UTF8Encoding(false, true).GetString(input.ReadBytes(length)).TrimEnd('\0');
                if (name.StartsWith("res://", StringComparison.Ordinal)) name = name[6..];
                if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(p => p is "" or "." or ".."))
                    throw new InvalidDataException("Invalid PCK path: " + name);
                var offset = checked(fileBase + (long)input.ReadUInt64());
                var size = checked((int)input.ReadUInt64());
                if (input.ReadBytes(16).Length != 16 || input.ReadUInt32() != 0) throw new InvalidDataException("Unsupported PCK entry flags");
                if (offset < fileBase || size < 0 || offset > directory - size || !Paths.Add(name)) throw new InvalidDataException("Invalid PCK entry: " + name);
                entries.Add(name, (offset, size));
            }
        }
        catch { input.Dispose(); throw; }
    }

    public byte[] Read(string path)
    {
        var (offset, size) = entries[path];
        if (size > 1024 * 1024) throw new InvalidDataException("Oversized entry metadata: " + path);
        input.BaseStream.Position = offset;
        var data = input.ReadBytes(size);
        if (data.Length != size) throw new EndOfStreamException(path);
        return data;
    }

    public void Dispose() => input.Dispose();
}
