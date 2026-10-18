using System;
using System.IO;
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

    private bool? showLeaderboard;

    /// <summary>Whether the match HUD shows the leaderboard; the match menu toggles it. Read every frame, so cached.</summary>
    public bool ShowLeaderboard
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return showLeaderboard ??= File.GetValue("hud", "leaderboard", true).AsBool();
        }
        set
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            showLeaderboard = value;
            Set("hud", "leaderboard", value);
        }
    }

    private void Set(string section, string key, Variant value)
    {
        File.SetValue(section, key, value);
        File.Save(path);
    }
}
