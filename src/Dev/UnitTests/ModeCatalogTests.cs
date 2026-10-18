using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Compiled completeness and exception-safe test overlays, with identity/order checks after every case.</summary>
[UnitSuite]
public static class ModeCatalogTests
{
    private static ModeBundle Synthetic(byte id = 201) => new(ModeRegistry.TankBank with { Id = id, Name = "Catalog probe" },
        new ModeSchema(id, 1, Array.Empty<ModeKind>()), () => new TankBankWire());

    private static void Fails(Action action, string diagnostic)
    {
        try { action(); }
        catch (InvalidOperationException error)
        {
            TestRunner.Assert(error.Message.Contains(diagnostic, StringComparison.Ordinal), $"expected '{diagnostic}', got '{error.Message}'");
            return;
        }
        throw new InvalidOperationException($"expected failure: {diagnostic}");
    }

    private sealed class InjectedFailure : Exception { }

    private sealed class RecordingRules(List<string> calls) : IModeRules
    {
        public void Initialize(ModeSetup setup) => calls.Add("initialize");
        public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => Array.Empty<ModeCommand>();
        public ModeState Snapshot() => throw new NotSupportedException();
        public bool Permits(ModeGate gate, ModeClock clock) => true;
    }

    public static void Register(TestRunner runner)
    {
        var shipped = ModeRegistry.Shipped;
        var definitions = ModeRegistry.All;
        var bundles = shipped.Bundles.ToArray();
        var lobby = ModeRegistry.Lobby;
        var prototypes = ModeRegistry.Prototypes;
        void Restored()
        {
            TestRunner.Assert(ReferenceEquals(shipped, ModeRegistry.Shipped) && ReferenceEquals(definitions, ModeRegistry.All), "scope did not restore catalog identity");
            TestRunner.Assert(ReferenceEquals(lobby, ModeRegistry.Lobby) && ReferenceEquals(prototypes, ModeRegistry.Prototypes), "scope replaced eligibility lists");
            for (var i = 0; i < bundles.Length; i++)
            {
                var expected = bundles[i];
                TestRunner.Assert(ReferenceEquals(shipped.Bundles[i], expected) && ReferenceEquals(ModeRegistry.All[i], expected.Definition) &&
                    ReferenceEquals(ModeRegistry.Find(expected.Definition!.Id, expected.Definition.SchemaVersion), expected.Definition) &&
                    ReferenceEquals(ModeRegistry.Schema(expected.Definition.Id, expected.Definition.SchemaVersion), expected.Schema), "shipped entry identity/order changed");
            }
            TestRunner.Assert(ModeRegistry.All.Count == bundles.Length, "synthetic entry escaped its scope");
        }
        void Run(string name, Action test) => runner.Run($"mode catalog/{name}", () =>
        {
            Restored();
            try { test(); }
            finally { Restored(); }
        });

        var complete = Synthetic();
        foreach (var (member, incomplete) in new (string, ModeBundle)[]
        {
            ("definition", complete with { Definition = null }),
            ("rules factory", complete with { Definition = complete.Definition! with { CreateRules = null! } }),
            ("supply", complete with { Definition = complete.Definition! with { Supply = null! } }),
            ("suburb factory", complete with { Definition = complete.Definition! with { Supply = complete.Definition.Supply with { Suburb = null! } } }),
            ("HUD factory", complete with { Definition = complete.Definition! with { CreateHud = null! } }),
            ("tuners", complete with { Definition = complete.Definition! with { Tuners = null! } }),
            ("schema", complete with { Schema = null }),
            ("server wire factory", complete with { CreateServerWire = null }),
            ("schema kinds", complete with { Schema = complete.Schema! with { Kinds = null! } }),
        })
        {
            Run($"missing {member} fails before publication", () =>
            {
                Fails(() => _ = new ModeCatalog(new[] { incomplete }), $"missing {member}");
                if (incomplete.Definition != null)
                    Fails(() => _ = new ModeCatalog(new[] { incomplete }), "mode 201/1 (Catalog probe)");
                Fails(() => { using var registration = ModeRegistry.RegisterScoped(incomplete); }, $"missing {member}");
            });
        }
        Run("incomplete schema codecs and null bundles fail validation", () =>
        {
            Fails(() => _ = new ModeCatalog(new ModeBundle[] { null! }), "missing bundle");
            var kind = ModeRegistry.Schema(2, 1).Kinds[0];
            foreach (var (incomplete, member) in new[]
            {
                (kind with { Read = null! }, "Spill reader"),
                (kind with { Write = null! }, "Spill writer"),
                ((ModeKind)null!, "schema kind"),
            })
            {
                Fails(() => _ = new ModeCatalog(new[] { complete with { Schema = complete.Schema! with { Kinds = new[] { incomplete } } } }), $"missing {member}");
            }
        });
        Run("schema identity and duplicate bundle diagnostics", () =>
        {
            Fails(() => _ = new ModeCatalog(new[] { complete with { Schema = complete.Schema! with { ModeId = 202 } } }), "schema identity 202/1");
            Fails(() => _ = new ModeCatalog(new[] { complete with { Schema = complete.Schema! with { Version = 2 } } }), "schema identity 201/2");
            Fails(() => _ = new ModeCatalog(new[] { complete, complete }), "duplicate mode identity");
        });
        Run("shipped containers cannot be mutated", () =>
        {
            TestRunner.Throws<NotSupportedException>(() => ((IList<ModeBundle>)shipped.Bundles)[0] = complete);
            TestRunner.Throws<NotSupportedException>(() => ((IList<ModeDefinition>)ModeRegistry.All)[0] = complete.Definition!);
            TestRunner.Throws<NotSupportedException>(() => ((IList<ModeDefinition>)lobby)[0] = complete.Definition!);
            TestRunner.Throws<NotSupportedException>(() => ((IList<ModeDefinition>)prototypes)[0] = complete.Definition!);
            TestRunner.Throws<NotSupportedException>(() => ((IList<string>)ModeRegistry.TankBank.Tuners)[0] = "changed");
            TestRunner.Throws<NotSupportedException>(() => ((IList<ModeKind>)ModeRegistry.Schema(2, 1).Kinds)[0] = null!);
            var source = new[] { complete };
            var catalog = new ModeCatalog(source);
            source[0] = null!;
            TestRunner.Assert(ReferenceEquals(catalog.Bundles[0], complete), "catalog retained caller's mutable array");
        });
        Run("Sandbox explicitly selects silence; unregistered modes cannot", () =>
        {
            var sandbox = shipped.Find(ModeRegistry.SandboxId, 1)!;
            TestRunner.Assert(sandbox.CreateServerWire!() is SilentModeWire && sandbox.Schema!.Kinds.Count == 0, "Sandbox lost its explicit silent wire");
            // Missing integration is diagnosed before even a deliberately failing rule factory runs.
            Fails(() => ModeRegistry.CreateRunner(complete.Definition! with { CreateRules = () => throw new InjectedFailure() }, null!), "no compiled mode bundle 201/1");
            TestRunner.Throws<System.IO.InvalidDataException>(() => ModeRegistry.Schema(201, 1));
            TestRunner.Assert(shipped.Find(201, 1) == null, "unknown mode found a default bundle");
            foreach (var id in new[] { ModeRegistry.TankBankId, ModeRegistry.KingId, ModeRegistry.LobbyWorldId })
                TestRunner.Assert(shipped.Find(id, 1)!.CreateServerWire!() is not SilentModeWire, "networked mode silently disabled");
        });
        Run("addition, replacement and nested scopes restore exact entries", () =>
        {
            using var added = ModeRegistry.RegisterScoped(complete);
            var outer = ModeRegistry.All;
            TestRunner.Assert(outer.Count == definitions.Count + 1 && ReferenceEquals(outer[^1], complete.Definition), "addition changed registration order");
            var replacement = complete with { Definition = complete.Definition! with { Name = "Nested" } };
            using (ModeRegistry.RegisterScoped(replacement))
            {
                TestRunner.Assert(ReferenceEquals(ModeRegistry.All[^1], replacement.Definition), "nested replacement missing");
                var bank = shipped.Find(2, 1)!;
                var bankReplacement = bank with { Definition = bank.Definition! with { Name = "Temporary Bank" } };
                using (ModeRegistry.RegisterScoped(bankReplacement))
                {
                    TestRunner.Assert(ReferenceEquals(ModeRegistry.All[1], bankReplacement.Definition), "replacement moved registration position");
                    TestRunner.Assert(ReferenceEquals(ModeRegistry.ForLobby(2), ModeRegistry.TankBank), "synthetic replacement changed lobby eligibility");
                }
                TestRunner.Assert(ReferenceEquals(ModeRegistry.Find(2, 1), bank.Definition), "nested shipped replacement not restored");
            }
            TestRunner.Assert(ReferenceEquals(ModeRegistry.All, outer) && ReferenceEquals(ModeRegistry.Find(201, 1), complete.Definition), "outer entry not restored");
        });
        Run("exception unwinding restores additions and replacements", () =>
        {
            try
            {
                using var added = ModeRegistry.RegisterScoped(complete);
                using var replaced = ModeRegistry.RegisterScoped(shipped.Find(2, 1)! with { CreateServerWire = () => throw new InjectedFailure() });
                throw new InjectedFailure();
            }
            catch (InjectedFailure) { }
        });
        Run("runner initializes rules before constructing a fresh wire; factory failure restores scope", () =>
        {
            var calls = new List<string>();
            var bundle = complete with
            {
                Definition = complete.Definition! with { CreateRules = () => { calls.Add("rules"); return new RecordingRules(calls); } },
                CreateServerWire = () => { calls.Add("wire"); return new TankBankWire(); },
            };
            using (ModeRegistry.RegisterScoped(bundle))
            {
                var first = ModeRegistry.CreateRunner(bundle.Definition!, null!);
                var second = ModeRegistry.CreateRunner(bundle.Definition!, null!);
                TestRunner.Assert(calls.SequenceEqual(new[] { "rules", "initialize", "wire", "rules", "initialize", "wire" }), "construction callback order changed");
                TestRunner.Assert(!ReferenceEquals(first.Rules, second.Rules) && !ReferenceEquals(first.Wire, second.Wire), "catalog shared per-run mutable state");
            }
            try
            {
                using var registration = ModeRegistry.RegisterScoped(bundle with { CreateServerWire = () => throw new InjectedFailure() });
                ModeRegistry.CreateRunner(bundle.Definition!, null!);
                throw new InvalidOperationException("wire failure was swallowed");
            }
            catch (InjectedFailure) { }
            using (ModeRegistry.RegisterScoped(bundle with { CreateServerWire = () => null! }))
                Fails(() => ModeRegistry.CreateRunner(bundle.Definition!, null!), "server wire factory returned null");
        });
        Run("out-of-order disposal fails without losing restoration; disposal is idempotent", () =>
        {
            var outer = ModeRegistry.RegisterScoped(complete);
            try
            {
                using var inner = ModeRegistry.RegisterScoped(Synthetic(202));
                Fails(outer.Dispose, "reverse order");
                TestRunner.Assert(ModeRegistry.All.Count == definitions.Count + 2, "failed disposal altered current catalog");
            }
            finally { outer.Dispose(); }
            outer.Dispose();
        });
    }
}
