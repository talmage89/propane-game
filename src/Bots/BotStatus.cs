using System.Globalization;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// What a bot is doing, for logs and the Lab's overlay: a composite format and the values it reads ({0} an actor id,
/// {1} to {3} numbers, {4} a word). A tick sets it without building a string; reading formats it, once per change.
/// </summary>
public sealed class BotStatus
{
    private string format = "";
    private int id;
    private double x, y, z;
    private string word = "";
    private string? text = "";

    public void Set(string message, int actor = 0, double first = 0, double second = 0, double third = 0, string word = "")
    {
        format = message;
        id = actor;
        x = first;
        y = second;
        z = third;
        this.word = word;
        text = null;
    }

    /// <summary>The format last set: a stable key for what the bot is doing, without its values.</summary>
    public string Format => format;

    public override string ToString() => text ??= string.Format(CultureInfo.InvariantCulture, format, id, x, y, z, word);

    /// <summary>A tank state's name without boxing the enum.</summary>
    public static string Name(PropaneTank.TankState state) => state switch
    {
        PropaneTank.TankState.Intact => "Intact",
        PropaneTank.TankState.Venting => "Venting",
        _ => "Exploded",
    };
}
