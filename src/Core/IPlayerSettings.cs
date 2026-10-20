using System;

namespace Propane.Core;

/// <summary>Borrowed application preferences. The application owns persistence and retirement; UI only reads/edits.</summary>
public interface IPlayerSettings
{
    string Name { get; set; }
    AppearanceRecord? Appearance { get; set; }
    string Server { get; set; }
    bool ShowLeaderboard { get; set; }
    double GetPreference(PreferenceDefinition preference);
    bool SetPreference(PreferenceDefinition preference, double value);
    bool ResetPreferences(string category);
    ControlBinding GetBinding(string slot);
    bool SetBinding(string slot, ControlBinding binding);
    bool ResetBindings(string? action = null);
    event Action? BindingsChanged;
    event Action<PreferenceDefinition>? PreferenceChanged;
}

/// <summary>Main-thread selection of a borrowed application contract. Owns no resource or persistence.</summary>
public static class PlayerPreferences
{
    private static Binding? binding;
    internal static IPlayerSettings? Current => binding?.Store;
    public static IPlayerSettings Application => Current ?? throw new InvalidOperationException("application settings are not bound");

    internal static IDisposable Bind(IPlayerSettings store) => binding = new Binding(store, binding);

    private sealed class Binding(IPlayerSettings store, Binding? previous) : IDisposable
    {
        internal IPlayerSettings Store { get; } = store;
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (binding != this) throw new InvalidOperationException("settings bindings must retire in reverse order");
            binding = previous;
            disposed = true;
        }
    }
}
