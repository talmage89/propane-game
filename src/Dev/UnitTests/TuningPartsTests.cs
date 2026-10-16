using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Godot;
using Propane.Core;

namespace Propane.Dev.UnitTests;

/// <summary>
/// Tuning is one class across several files, and the order of its properties is observable (panel, inspector, Lab
/// dials, snapshot bytes). Godot's generator orders them by file name and the compiler by its file order; this keeps
/// the two, and the files on disk, in agreement so that no machine's file system or collation can reorder them.
/// </summary>
[UnitSuite]
public static class TuningPartsTests
{
    private const string Directory = "res://src/Core";

    private static readonly Regex ListedPart = new("""<TuningPart Include="(?<path>[^"]+)" />""");
    private static readonly Regex NumberedPart = new(@"^Tuning\.\d\d\.[A-Z][A-Za-z]*\.cs$");

    public static void Register(TestRunner runner)
    {
        runner.Run("tuning parts/compile list is the file-name order, numbered so no collation can reorder it", () =>
        {
            var project = File.ReadAllText(ProjectSettings.GlobalizePath("res://Propane.csproj"));
            var listed = ListedPart.Matches(project).Select(match => match.Groups["path"].Value).ToArray();
            var onDisk = System.IO.Directory.GetFiles(ProjectSettings.GlobalizePath(Directory), "Tuning*.cs")
                .Select(path => "src/Core/" + Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
            TestRunner.Assert(listed.Order(StringComparer.Ordinal).SequenceEqual(onDisk),
                $"Propane.csproj lists [{string.Join(", ", listed)}] but the Tuning parts on disk are [{string.Join(", ", onDisk)}]");
            // Tuning.cs (the machinery, no dials) compiles second, as it always has, so its static properties keep their place.
            TestRunner.Assert(listed.Length > 1 && listed[1] == "src/Core/Tuning.cs", "Tuning.cs must be the second item of the list");

            var parts = listed.Where(path => path != "src/Core/Tuning.cs").Select(path => Path.GetFileName(path)).ToArray();
            foreach (var part in parts)
            {
                TestRunner.Assert(NumberedPart.IsMatch(part), $"{part} is not named Tuning.NN.Name.cs");
            }
            // Equal-length digit prefixes differ first at a digit, which every ordinal and culture comparison orders alike.
            var numbers = parts.Select(part => part.Substring("Tuning.".Length, 2)).ToArray();
            TestRunner.Assert(numbers.Distinct().Count() == numbers.Length, "two Tuning parts share a number");
            TestRunner.Assert(parts.SequenceEqual(parts.Order(StringComparer.Ordinal)), "Propane.csproj lists the Tuning parts out of file-name order");
            TestRunner.Assert(parts.SequenceEqual(parts.Order(StringComparer.InvariantCulture)), "culture comparison orders the Tuning parts differently");

            var machinery = File.ReadAllText(ProjectSettings.GlobalizePath(Directory + "/Tuning.cs"));
            TestRunner.Assert(!machinery.Contains("[Export", StringComparison.Ordinal) && !machinery.Contains("{ get; set; }", StringComparison.Ordinal),
                "Tuning.cs must hold no dials: a part is where a dial's place in the order is chosen");
        });

        runner.Run("tuning parts/reflection order of the shared tuners is the Godot property-list order", () =>
        {
            // The compiler's file order sets reflection order and Godot's generator the property list; the parts keep them
            // one order. Local dials (not exported) are never in the list: the Lab panel and LocalTuners follow reflection.
            using var tuning = new Tuning();
            var shared = typeof(Tuning).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanWrite && p.IsDefined(typeof(ExportAttribute)) &&
                    (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)))
                .Select(p => p.Name).ToArray();
            TestRunner.Assert(shared.Length > 0, "no shared tuners found");
            TestRunner.Assert(shared.SequenceEqual(Tuning.ValueNames(tuning)), "reflection and the Godot property list order the shared tuners differently");
        });
    }
}
