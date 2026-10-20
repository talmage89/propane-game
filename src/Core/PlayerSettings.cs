using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Propane.Core;

/// <summary>
/// One application's preferences. Owns its lazy ConfigFile until disposal; each setter saves immediately.
/// Main owns the application store. Tests borrow an explicitly scoped isolated store instead.
/// </summary>
public sealed class PlayerSettings : IPlayerSettings, IDisposable
{
    private readonly string path;
    private bool disposed;
    public PlayerSettings(string path) => this.path = path;

    /// <summary>Main-thread application/test binding. The caller owns this store and disposes the binding first.</summary>
    internal static IDisposable Bind(PlayerSettings store)
    {
        ObjectDisposedException.ThrowIf(store.disposed, store);
        return PlayerPreferences.Bind(store);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        PreferenceChanged = null;
        BindingsChanged = null;
        bindings?.Clear();
        values.Clear();
        file?.Dispose();
        file = null;
    }

    private ConfigFile? file;

    private ConfigFile File
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (file == null)
            {
                file = new ConfigFile();
                file.Load(path);
            }
            return file;
        }
    }

    public string Name
    {
        get => File.GetValue("player", "name", "").AsString();
        set => Set("player", "name", value);
    }

    /// <summary>The wire record, persisted unchanged. A saved palette index migrates only if no record exists.</summary>
    public AppearanceRecord? Appearance
    {
        get
        {
            if (File.HasSectionKey("player", "appearance"))
            {
                var value = File.GetValue("player", "appearance");
                if (value.VariantType != Variant.Type.PackedByteArray) { return null; }
                try { return AppearanceCodec.Decode(value.AsByteArray()); }
                catch (InvalidDataException) { return null; }
            }
            var legacy = File.GetValue("player", "color", -1);
            if (legacy.VariantType != Variant.Type.Int || legacy.AsInt64() < 0 || legacy.AsInt64() >= AppearancePalette.PlayerColors.Length) { return null; }
            var migrated = AppearanceRecord.FromPalette(legacy.AsInt32());
            Appearance = migrated;
            return migrated;
        }
        set
        {
            if (value == null) { throw new System.ArgumentNullException(nameof(value)); }
            Set("player", "appearance", AppearanceCodec.Encode(value));
        }
    }

    /// <summary>The last server, as "host" or "host:port".</summary>
    public string Server
    {
        get => File.GetValue("net", "server", "").AsString();
        set => Set("net", "server", value);
    }

    private readonly Dictionary<string, double> values = new();
    public event Action<PreferenceDefinition>? PreferenceChanged;

    public bool ShowLeaderboard
    {
        get => GetPreference(PreferenceCatalog.ShowLeaderboard) != 0;
        set => SetPreference(PreferenceCatalog.ShowLeaderboard, value ? 1 : 0);
    }

    public double GetPreference(PreferenceDefinition preference)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (values.TryGetValue(preference.Id, out var cached)) return cached;
        var saved = File.GetValue(preference.Section, preference.Id, Variant.From(preference.Default));
        var numeric = saved.VariantType is Variant.Type.Int or Variant.Type.Float;
        var value = preference.Kind == PreferenceKind.Toggle
            ? saved.VariantType == Variant.Type.Bool ? (saved.AsBool() ? 1 : 0) : preference.Default
            : numeric ? saved.AsDouble() : preference.Default;
        return values[preference.Id] = preference.Normalize(value);
    }

    public bool SetPreference(PreferenceDefinition preference, double value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = preference.Normalize(value);
        values[preference.Id] = normalized;
        File.SetValue(preference.Section, preference.Id, preference.Kind == PreferenceKind.Toggle
            ? Variant.From(normalized != 0) : Variant.From(normalized));
        // Commit before synchronous apply hooks. No debounce clock or pending write survives a menu/session.
        var saved = File.Save(path) == Error.Ok;
        PreferenceChanged?.Invoke(preference);
        return saved;
    }

    public bool ResetPreferences(string category)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var saved = true;
        foreach (var preference in PreferenceCatalog.All)
            if (preference.Category == category) saved &= SetPreference(preference, preference.Default);
        return saved;
    }

    // Store-lived values, loaded once. Invalid/unknown entries cannot replace a default or claim its input.
    private Dictionary<string, ControlBinding>? bindings;
    public event Action? BindingsChanged;
    private Dictionary<string, ControlBinding> Bindings
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (bindings != null) return bindings;
            bindings = ControlBindings.Slots.ToDictionary(s => s.Slot, s => s.Default);
            foreach (var (slot, fallback) in ControlBindings.Slots)
            {
                if (!File.HasSectionKey("bindings", slot)) continue;
                var saved = File.GetValue("bindings", slot);
                if (saved.VariantType == Variant.Type.String && ControlBinding.TryParse(saved.AsString(), out var value) &&
                    (value.Kind == BindingKind.None || value.Gamepad == fallback.Gamepad))
                {
                    bindings[slot] = value;
                }
            }
            // Corrupt duplicate assignments fall back as a group, never silently steal a default. Iterate because
            // restoring one default can expose another collision. Valid files include explicit unbound victims.
            while (true)
            {
                var duplicates = bindings.Where(p => p.Value.Kind != BindingKind.None).GroupBy(p => p.Value)
                    .Where(g => g.Count() > 1).SelectMany(g => g.Select(p => p.Key)).ToArray();
                var changed = false;
                foreach (var slot in duplicates)
                {
                    var fallback = ControlBindings.Slots.Single(s => s.Slot == slot).Default;
                    if (bindings[slot] != fallback) { bindings[slot] = fallback; changed = true; }
                }
                if (!changed) break;
            }
            return bindings;
        }
    }

    public ControlBinding GetBinding(string slot) => Bindings.TryGetValue(slot, out var value)
        ? value : throw new ArgumentException("Unknown control slot", nameof(slot));

    public bool SetBinding(string slot, ControlBinding binding)
    {
        var fallback = ControlBindings.Slots.Single(s => s.Slot == slot).Default;
        if (!binding.Valid || binding.Kind != BindingKind.None && binding.Gamepad != fallback.Gamepad)
            throw new ArgumentException("Invalid control binding", nameof(binding));
        AssignBinding(slot, binding);
        return SaveBindings();
    }

    private void AssignBinding(string slot, ControlBinding binding)
    {
        var current = Bindings;
        if (binding.Kind != BindingKind.None)
        {
            foreach (var other in current.Keys.ToArray())
                if (other != slot && current[other] == binding) current[other] = default;
        }

        current[slot] = binding;
    }

    public bool ResetBindings(string? action = null)
    {
        if (action != null) _ = ControlBindings.Action(action);
        foreach (var (slot, fallback) in ControlBindings.Slots)
            if (action == null || slot.StartsWith(action + ".", StringComparison.Ordinal)) AssignBinding(slot, fallback);
        return SaveBindings();
    }

    private bool SaveBindings()
    {
        if (File.HasSection("bindings")) File.EraseSection("bindings");
        foreach (var (slot, fallback) in ControlBindings.Slots)
            if (Bindings[slot] != fallback) File.SetValue("bindings", slot, Bindings[slot].ToString());
        var saved = File.Save(path) == Error.Ok;
        BindingsChanged?.Invoke();
        return saved;
    }

    private void Set(string section, string key, Variant value)
    {
        File.SetValue(section, key, value);
        File.Save(path);
    }
}
