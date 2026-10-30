using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using Xunit;

namespace CircuitRF.Ui.Tests.Gdsfactory;

/// <summary>
/// brief-gdf-2 §6 Tier A: the gdsfactory adapter's C# half, with no Python package anywhere. Every
/// environment here is a SYNTHETIC site-packages tree of made-up distributions, and the one process
/// discovery may start — the interpreter's version/paths probe — is a counting seam.
/// </summary>
public sealed class GdsfactoryAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-gdf2-" + Guid.NewGuid().ToString("N")[..8]);

    public GdsfactoryAdapterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A site-packages directory, with gdsfactory itself in it unless told otherwise.</summary>
    private string Site(string name = "site", bool withGdsfactory = true)
    {
        string site = Path.Combine(_root, name, "site-packages");
        Directory.CreateDirectory(site);
        if (withGdsfactory) Distribution(site, "gdsfactory", "9.51.0", "gdsfactory", requiresDist: []);
        return site;
    }

    private static void Distribution(string site, string name, string version, string module,
                                     string[] requiresDist, string? requiresPython = null)
    {
        string info = Path.Combine(site, $"{name}-{version}.dist-info");
        Directory.CreateDirectory(info);
        var lines = new List<string> { "Metadata-Version: 2.1", $"Name: {name}", $"Version: {version}" };
        if (requiresPython is not null) lines.Add($"Requires-Python: {requiresPython}");
        lines.AddRange(requiresDist.Select(r => $"Requires-Dist: {r}"));
        lines.Add("");
        lines.Add("Requires-Dist: gdsfactory  (this line is the long description, not a header)");
        File.WriteAllLines(Path.Combine(info, "METADATA"), lines);
        File.WriteAllText(Path.Combine(info, "top_level.txt"), module + "\n");
        Directory.CreateDirectory(Path.Combine(site, module));
        File.WriteAllText(Path.Combine(site, module, "__init__.py"), "raise SystemExit('kit code must never run here')\n");
    }

    /// <summary>The probe, faked: answers as <paramref name="version"/> with <paramref name="sites"/>, and counts.</summary>
    private static Func<string, string?> FakeProbe(string version, IEnumerable<string> sites, List<string> calls)
        => interpreter => { calls.Add(interpreter); return version + "\n" + string.Join("\n", sites) + "\n"; };

    // ── 1. discovery ───────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_FindsKitsRequiringGdsfactory_IgnoresOthers_ReadsRequiresPython_AndStartsOnlyTheProbe()
    {
        string site = Site();
        Distribution(site, "synth-rf-kit", "0.4.1", "synth_rf_kit", ["gdsfactory>=9.0", "numpy"], ">=3.12");
        Distribution(site, "plain-lib", "2.0", "plain_lib", ["numpy"]);
        Distribution(site, "optional-user", "1.0", "optional_user", ["gdsfactory; extra == 'layout'"]);

        var calls = new List<string>();
        var found = GdsfactoryKitDiscovery.Discover("/env/bin/python", FakeProbe("3.12.4", [site], calls));

        Assert.Null(found.Refusal);
        var kit = Assert.Single(found.Kits);
        Assert.Equal(("synth-rf-kit", "0.4.1", ">=3.12", "synth_rf_kit"), (kit.Distribution, kit.Version, kit.RequiresPython, kit.Module));
        Assert.Equal(Path.Combine(site, "synth_rf_kit"), kit.InstalledDirectory);
        Assert.Equal("9.51.0", found.GdsfactoryVersion);
        Assert.Equal(["/env/bin/python"], calls);
    }

    // ── 2. refusals ────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_RefusesMissingGdsfactory_LowPython_AndAnEmptyEnvironment_EachInItsOwnSentence()
    {
        string noGf = Site("nogf", withGdsfactory: false);
        Distribution(noGf, "synth-rf-kit", "0.4.1", "synth_rf_kit", ["gdsfactory"]);
        var missing = GdsfactoryKitDiscovery.Discover("py", FakeProbe("3.12.1", [noGf], []));
        Assert.Contains("gdsfactory is not installed in the Python environment at 'py', though synth-rf-kit 0.4.1 requires it", missing.Refusal);
        Assert.Contains("--no-deps", missing.Refusal);

        string site = Site("old");
        Distribution(site, "synth-rf-kit", "0.4.1", "synth_rf_kit", ["gdsfactory"]);
        var old = GdsfactoryKitDiscovery.Discover("py", FakeProbe("3.11.9", [site], []));
        Assert.Equal("'py' is Python 3.11.9; gdsfactory kits need Python 3.12 or newer. circuitRF installs nothing: " +
                     "choose an environment that has a newer Python.", old.Refusal);

        var empty = GdsfactoryKitDiscovery.Discover("py", FakeProbe("3.12.1", [Site("empty")], []));
        Assert.StartsWith("The Python environment at 'py' holds no gdsfactory kit", empty.Refusal);

        // …and a kit whose own floor is above the interpreter is refused by name, not hidden.
        string strict = Site("strict");
        Distribution(strict, "future-kit", "1.0", "future_kit", ["gdsfactory"], ">=3.13");
        var tooOld = GdsfactoryKitDiscovery.Discover("py", FakeProbe("3.12.1", [strict], []));
        Assert.Equal("future-kit 1.0 needs Python >=3.13; 'py' is Python 3.12.1.", Assert.Single(tooOld.Refused).Reason);
    }

    // ── 3. consent keying ──────────────────────────────────────────────────────

    [Fact]
    public void Consent_IsKeyedOnTheResolvedDirectory_ALinkAndItsTargetAreOneDecision_TwoEnvironmentsAreTwo()
    {
        string target = Path.Combine(Site("envA"), "synth_rf_kit");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_root, "linked-site");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(target)!);
        string viaLink = Path.Combine(link, "synth_rf_kit");
        string other = Path.Combine(Site("envB"), "synth_rf_kit");
        Directory.CreateDirectory(other);

        var store = new PCellTrustStore();
        store.Record(PCellTrustStore.ResolvedDirectory(viaLink), allowed: true);

        Assert.Equal(PCellTrustDecision.Allowed, store.Decide(PCellTrustStore.ResolvedDirectory(target)));
        Assert.Equal(PCellTrustDecision.Unknown, store.Decide(PCellTrustStore.ResolvedDirectory(other)));

        // …and the resolver keys a gdsfactory kit's prompt the same way, wherever the manifest names it from.
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        string folder = KitPCellLibrary.EnsureDeclaredGdsfactory(
            ws, new GdsfactoryKit("synth-rf-kit", "0.4.1", null, "synth_rf_kit", viaLink), out _, out _)!;
        using var resolver = new PCellWorkerResolver(ws, trust: _ => PCellTrustDecision.Unknown, moduleInterpreter: _ => null);
        var kit = Assert.Single(resolver.Kits);
        Assert.Equal(PCellTrustStore.ResolvedDirectory(target), kit.TrustKey);
        Assert.NotEqual(PCellTrustStore.Normalize(folder), kit.TrustKey);
    }

    // ── 4. the manifest ────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_RoundTripsModuleAndVersion_AndAnExistingManifestIsByteIdenticalAfterLoadAndSave()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);

        // An existing (cni) manifest: read and written again, the same bytes — no new key appears.
        string kitRoot = Path.Combine(_root, "kit");
        Directory.CreateDirectory(Path.Combine(kitRoot, "cells"));
        string cni = KitPCellLibrary.EnsureDeclared(ws, "kitx", new KitPCellPackage(kitRoot, "cells", 1), out _, out _, kitRoot)!;
        string before = File.ReadAllText(Path.Combine(cni, PCellGeneratorManifest.FileName));
        Assert.Equal(before, PCellGeneratorManifest.TryRead(cni, out _)!.ToJson());
        Assert.DoesNotContain("module", before, StringComparison.OrdinalIgnoreCase);

        // A gdsfactory one carries the module and the version seen, and no interpreter.
        string site = Site();
        string gf = KitPCellLibrary.EnsureDeclaredGdsfactory(
            ws, new GdsfactoryKit("synth-rf-kit", "0.4.1", null, "synth_rf_kit", Path.Combine(site, "synth_rf_kit")), out _, out _)!;
        var read = PCellGeneratorManifest.TryRead(gf, out _)!;
        Assert.Equal(("synth_rf_kit", "0.4.1", null), (read.Module, read.ModuleVersion, read.Interpreter));
        Assert.Equal([PCellGeneratorManifest.KitToken], read.Sources);
        Assert.Equal(File.ReadAllText(Path.Combine(gf, PCellGeneratorManifest.FileName)), read.ToJson());
    }

    /// <summary>R-gdf2-2c: <c>Sources = ["${kit}"]</c> hashes the KIT. Before the fix a <c>${kit}</c>
    /// source was combined with the manifest's folder, named nothing, and an edited kit kept its key.</summary>
    [Fact]
    public void TheContentKey_FollowsTheKitsOwnSources_ThroughTheKitAnchor()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        string kitDir = Path.Combine(Site(), "synth_rf_kit");
        string folder = KitPCellLibrary.EnsureDeclaredGdsfactory(
            ws, new GdsfactoryKit("synth-rf-kit", "0.4.1", null, "synth_rf_kit", kitDir), out _, out _)!;
        var manifest = PCellGeneratorManifest.TryRead(folder, out _)!;

        Directory.CreateDirectory(kitDir);
        File.WriteAllText(Path.Combine(kitDir, "cells.py"), "WIDTH = 1.0\n");
        string before = PCellGeneratorContentHash.Compute(folder, manifest, out _);
        File.WriteAllText(Path.Combine(kitDir, "cells.py"), "WIDTH = 2.0\n");
        Assert.NotEqual(before, PCellGeneratorContentHash.Compute(folder, manifest, out _));
    }

    // ── 5. the per-user interpreter ────────────────────────────────────────────

    [Fact]
    public void Interpreter_ComesFromThePerUserChoiceByModule_AndNoneRecordedRefusesNamingPython()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        KitPCellLibrary.EnsureDeclaredGdsfactory(
            ws, new GdsfactoryKit("synth-rf-kit", "0.4.1", null, "synth_rf_kit", Path.Combine(Site(), "synth_rf_kit")), out _, out _);

        var asked = new List<string>();
        var reports = new List<string>();
        using (var resolver = new PCellWorkerResolver(ws, report: reports.Add,
                   moduleInterpreter: m => { asked.Add(m); return null; }))
            Assert.Empty(resolver.KnownGeneratorIds);

        Assert.Contains("synth_rf_kit", asked);
        Assert.Contains(GdsfactoryInterpreterPreferences.NoInterpreterSentence("synth_rf_kit"), reports);
        Assert.Contains("--python", GdsfactoryInterpreterPreferences.NoInterpreterSentence("synth_rf_kit"));

        // The recorded table is read by module name from this user's preferences file.
        string prefs = Path.Combine(_root, "preferences.json");
        File.WriteAllText(prefs, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [GdsfactoryInterpreterPreferences.PreferenceKey] = new Dictionary<string, string> { ["synth_rf_kit"] = "/envs/rf/bin/python" },
            ["pcell_trust"] = new Dictionary<string, bool>(),
        }));
        Assert.Equal("/envs/rf/bin/python", GdsfactoryInterpreterPreferences.FromPreferencesFile(prefs)["synth_rf_kit"]);
    }

    // ── 6. floats pass through ─────────────────────────────────────────────────

    [Fact]
    public void ARealParameter_ReachesTheGenerateRequestUnchanged_EvenFarBelowTheDbuGrid()
    {
        const double inductance = 3.3303e-11;
        var frame = PCellWireCodec.EncodeGenerate(
            "inductor",
            new Dictionary<string, PCellValue> { ["inductance"] = PCellValue.Real(inductance), ["width"] = PCellValue.Real(2.5) },
            [
                new PCellWireParameterDecl { Name = "inductance", Kind = PCellValueKind.Real },
                new PCellWireParameterDecl { Name = "width", Kind = PCellValueKind.Real },
            ],
            technology: null, PCellLayerSelection.Default, dbuPerMicron: 1000);

        var request = JsonSerializer.Deserialize<PCellWireGenerateRequest>(frame.Json, PCellWireJson.Options)!;
        Assert.Equal(inductance, request.Parameters["inductance"].AsReal());
        Assert.Equal(2.5, request.Parameters["width"].AsReal());
    }
}
