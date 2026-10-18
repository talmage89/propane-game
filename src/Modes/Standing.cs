using Propane.Core;

namespace Propane.Modes;

/// <summary>A final standing, from the server.</summary>
public sealed record Standing(int Id, string Name, AppearanceRecord Appearance, int Score, bool Present);
