using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Propane.Dev.UnitTests;

namespace Propane.Dev.Equivalence;

[UnitSuite(-3)]
public static class FixtureReferenceTests
{
    private static readonly string[] Files = { "lab-bank.json.gz", "lab-dissolve.json.gz", "lab-king.json.gz", "lab-shortfuse.json.gz",
        "lab-spill-chain.json.gz", "provenance.json", "publication.json.gz", "segments.json.gz", "tuners.json.gz", "wire.json.gz", "world.json.gz" };

    public static void Register(TestRunner runner)
    {
        runner.Run("reference/fixture inventory, lengths and SHA256 integrity", () =>
        {
            if (DevArgs.Get("write-reference") != null) return;
            var directory = ReferenceFile.DirectoryPath;
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
            var names = manifest.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            TestRunner.Assert(names.SequenceEqual(Files), "reference manifest: incomplete or reordered fixture inventory");
            foreach (var name in Files)
            {
                var path = Path.Combine(directory, name);
                TestRunner.Assert(File.Exists(path), $"reference {name}: missing fixture");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                TestRunner.Assert(hash == manifest.RootElement.GetProperty(name).GetString(), $"reference {name}: SHA256 differs (changed or truncated fixture)");
            }
        });
        runner.Run("reference/observations ignore anonymous implementation names and diagnose exact fields", () =>
        {
            var expected = ReferenceFile.Element(ReferenceFile.Value(new { actor = new { ammo = 3 } }));
            TestRunner.Assert(!expected.GetRawText().Contains("AnonymousType", StringComparison.Ordinal), "compiler-generated name entered a reference");
            var actual = ReferenceFile.Element(ReferenceFile.Value(new { actor = new { ammo = 4 } }));
            try { ReferenceFile.Compare(expected, actual, "trace"); }
            catch (InvalidDataException error)
            {
                TestRunner.Assert(error.Message == "trace.actor.ammo: expected 3, actual 4", "first field was not diagnosed");
                TestRunner.Throws<InvalidDataException>(() => ReferenceFile.Compare(ReferenceFile.Element(ReferenceFile.Value(0f)),
                    ReferenceFile.Element(ReferenceFile.Value(BitConverter.UInt32BitsToSingle(0x8000_0000))), "zero"));
                return;
            }
            throw new InvalidOperationException("changed observation passed");
        });
    }
}
