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
    // ── review fixes (consent, environment, process, record) ──────────────────

    /// <summary>A gdsfactory declaration of <paramref name="kitDir"/> in a fresh workspace, and that workspace.</summary>
    private (string Ws, string Folder) Declared(string kitDir, string name = "ws")
    {
        string ws = Path.Combine(_root, name);
        Directory.CreateDirectory(ws);
        string folder = KitPCellLibrary.EnsureDeclaredGdsfactory(
            ws, new GdsfactoryKit("synth-rf-kit", "0.4.1", null, "synth_rf_kit", kitDir), out _, out _)!;
        return (ws, folder);
    }

    /// <summary>The positive half of R-gdf2-2b: the interpreter this user chose for the module is the one
    /// launched, probed through the seam, with -P so the entry script's folder is not on the path.</summary>
    [Fact]
    public void TheRecordedInterpreter_IsTheOneLaunched_WithTheScriptFolderOffThePath()
    {
        string site = Site();
        Distribution(site, "synth-rf-kit", "0.4.1", "synth_rf_kit", ["gdsfactory"]);
        var (ws, folder) = Declared(Path.Combine(site, "synth_rf_kit"));

        var probed = new List<string>();
        var reports = new List<string>();
        using var resolver = new PCellWorkerResolver(ws, report: reports.Add, trust: _ => PCellTrustDecision.Unknown,
            moduleInterpreter: m => m == "synth_rf_kit" ? "/envs/rf/bin/python" : null,
            runProbe: FakeProbe("3.12.4", [site], probed));

        var interpreter = resolver.DefaultInterpreter(PCellGeneratorManifest.TryRead(folder, out _)!, folder);
        Assert.True(interpreter is not null, string.Join("\n", reports));
        Assert.Equal("/envs/rf/bin/python", interpreter!.Command);
        Assert.Equal(["-P"], interpreter.Arguments);
        Assert.Equal(["/envs/rf/bin/python"], probed);
    }

    /// <summary>Finding 1: consent is for the installed directory the declaration names. An environment
    /// whose own copy of the module is elsewhere is different code, and is refused rather than run.</summary>
    [Fact]
    public void AnEnvironmentWhoseKitIsADifferentDirectory_IsRefused_NotRunOnTheOtherCopysPermission()
    {
        string siteA = Site("envA");
        Distribution(siteA, "synth-rf-kit", "0.4.1", "synth_rf_kit", ["gdsfactory"]);
        string siteB = Site("envB");
        Distribution(siteB, "synth-rf-kit", "0.4.1", "synth_rf_kit", ["gdsfactory"]);
        var (ws, folder) = Declared(Path.Combine(siteA, "synth_rf_kit"));

        var reports = new List<string>();
        using var resolver = new PCellWorkerResolver(ws, report: reports.Add, trust: _ => PCellTrustDecision.Allowed,
            moduleInterpreter: _ => "/envs/b/bin/python", runProbe: FakeProbe("3.12.4", [siteB], []));

        Assert.Null(resolver.DefaultInterpreter(PCellGeneratorManifest.TryRead(folder, out _)!, folder));
        Assert.Contains(reports, r => r.Contains("cannot run in '/envs/b/bin/python'") && r.Contains(Path.Combine(siteB, "synth_rf_kit")));
    }

    /// <summary>Finding 2: permission given to a kit covers a declaration that adds no code of its own. An
    /// edited entry script, or a pythonPath reaching elsewhere, is keyed on the declaration folder.</summary>
    [Fact]
    public void ADeclarationThatAddsCode_IsKeyedOnItsFolder_NotOnTheKitsPermission()
    {
        string kitDir = Path.Combine(Site(), "synth_rf_kit");
        Directory.CreateDirectory(kitDir);
        var (ws, folder) = Declared(kitDir);
        string entry = Path.Combine(folder, KitPCellLibrary.EntryScriptName);

        PCellKit Kit()
        {
            using var r = new PCellWorkerResolver(ws, trust: _ => PCellTrustDecision.Unknown, moduleInterpreter: _ => null);
            return Assert.Single(r.Kits);
        }

        // As generated, with a comment of the user's own added: the kit's permission.
        File.AppendAllText(entry, "# a note of mine\n");
        Assert.Equal(PCellTrustStore.ResolvedDirectory(kitDir), Kit().TrustKey);

        // Code added: the declaration folder.
        File.AppendAllText(entry, "import os\n");
        Assert.Equal(PCellTrustStore.Normalize(folder), PCellTrustStore.Normalize(Kit().TrustKey));

        // Restored, but a pythonPath of the workspace's own: the declaration folder again.
        File.WriteAllText(entry, File.ReadAllText(entry).Replace("import os\n", ""));
        Assert.Equal(PCellTrustStore.ResolvedDirectory(kitDir), Kit().TrustKey);
        string manifestPath = Path.Combine(folder, PCellGeneratorManifest.FileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"pythonPath\": []", "\"pythonPath\": [\"lib\"]"));
        Assert.Equal(PCellTrustStore.Normalize(folder), PCellTrustStore.Normalize(Kit().TrustKey));
    }

    /// <summary>Finding 7: an editable install is NAMED, with the folder to import, rather than skipped and
    /// the environment called kit-less.</summary>
    [Fact]
    public void AnEditableKit_IsReportedWithItsSourceFolder_NotSkipped()
    {
        string site = Site();
        string source = Path.Combine(_root, "src", "synth-rf-kit");
        Directory.CreateDirectory(source);
        string info = Path.Combine(site, "synth_rf_kit-0.4.1.dist-info");
        Directory.CreateDirectory(info);
        File.WriteAllLines(Path.Combine(info, "METADATA"), ["Metadata-Version: 2.1", "Name: synth-rf-kit", "Version: 0.4.1", "Requires-Dist: gdsfactory>=9", ""]);
        File.WriteAllText(Path.Combine(info, "top_level.txt"), "synth_rf_kit\n");
        File.WriteAllText(Path.Combine(info, "direct_url.json"),
            JsonSerializer.Serialize(new { url = new Uri(source).AbsoluteUri, dir_info = new { editable = true } }));

        var found = GdsfactoryKitDiscovery.Discover("py", FakeProbe("3.12.4", [site], []));

        Assert.Empty(found.Kits);
        var refused = Assert.Single(found.Refused);
        Assert.Contains("editable install", refused.Reason);
        Assert.Contains(source, refused.Reason);
        Assert.Contains("Choose folder", refused.Reason);
        Assert.DoesNotContain("holds no gdsfactory kit", found.Refusal);
    }

    /// <summary>Finding 9: the entry script (outside the kit) and the kit's non-script files are in the key.</summary>
    [Fact]
    public void TheContentKey_FollowsTheEntryScript_AndTheKitsDataFiles()
    {
        string kitDir = Path.Combine(Site(), "synth_rf_kit");
        Directory.CreateDirectory(kitDir);
        File.WriteAllText(Path.Combine(kitDir, "cells.py"), "WIDTH = 1.0\n");
        File.WriteAllText(Path.Combine(kitDir, "fixed.gds"), "v1");
        var (_, folder) = Declared(kitDir);
        var manifest = PCellGeneratorManifest.TryRead(folder, out _)!;
        string Key() => PCellGeneratorContentHash.Compute(folder, manifest, out _);

        string first = Key();
        File.AppendAllText(Path.Combine(folder, KitPCellLibrary.EntryScriptName), "\n# edited\n");
        string second = Key();
        Assert.NotEqual(first, second);

        File.WriteAllText(Path.Combine(kitDir, "fixed.gds"), "version two");
        Assert.NotEqual(second, Key());
    }

    /// <summary>Finding 11: the prompt states the INSTALLED version; re-importing updates the recorded one;
    /// and the changed-version note is said once per resolver, not on every reload.</summary>
    [Fact]
    public void TheVersion_PromptShowsTheInstalledOne_TheNoteIsSaidOnce_AndAReimportUpdatesIt()
    {
        string site = Site();
        Distribution(site, "synth-rf-kit", "0.5.0", "synth_rf_kit", ["gdsfactory"]);
        string kitDir = Path.Combine(site, "synth_rf_kit");
        var (ws, folder) = Declared(kitDir);   // records 0.4.1

        var notes = new List<string>();
        using (var resolver = new PCellWorkerResolver(ws, trust: _ => PCellTrustDecision.Unknown, moduleInterpreter: _ => null, note: notes.Add))
        {
            var kit = Assert.Single(resolver.Kits);
            Assert.Equal(("0.5.0", "0.4.1"), (kit.ModuleVersion, kit.RecordedVersion));
            resolver.Rescan();
        }
        Assert.Single(notes, n => n.Contains("was 0.4.1") && n.Contains("is 0.5.0 now"));

        KitPCellLibrary.EnsureDeclaredGdsfactory(ws, new GdsfactoryKit("synth-rf-kit", "0.5.0", null, "synth_rf_kit", kitDir), out _, out _);
        Assert.Equal("0.5.0", PCellGeneratorManifest.TryRead(folder, out _)!.ModuleVersion);
    }

    /// <summary>Finding 3: a child that never exits is killed at the deadline instead of hanging the caller.</summary>
    [Fact]
    public void AChildThatHangs_IsKilledAtTheDeadline()
    {
        if (OperatingSystem.IsWindows()) return;   // /bin/sh is the portable hang here
        var info = new System.Diagnostics.ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("echo started; sleep 30");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = BoundedProcess.Run(info, TimeSpan.FromMilliseconds(500));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.True(outcome!.Value.TimedOut);
    }
}
