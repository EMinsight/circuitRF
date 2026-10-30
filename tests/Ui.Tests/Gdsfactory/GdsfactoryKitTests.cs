using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Gdsfactory;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS with a reason unless <c>CRF_GDSFACTORY_PYTHON</c> names an
/// interpreter that has gdsfactory installed — the <c>FixtureFact</c> pattern (brief-gdf-2 §6 Tier B).
/// These start real Python and real gdsfactory, so they are never part of the default gate's work.
/// </summary>
public sealed class GdsfactoryFactAttribute : FactAttribute
{
    public const string Variable = "CRF_GDSFACTORY_PYTHON";

    public static string? Interpreter =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } p && File.Exists(p) ? p : null;

    public GdsfactoryFactAttribute()
    {
        if (Interpreter is null)
            Skip = $"{Variable} is not set to a Python interpreter with gdsfactory installed — set it to run the gdsfactory adapter tests.";
    }
}

/// <summary>
/// brief-gdf-2 §6 Tier B: the synthetic kit, and the generic kit gdsfactory ships, through circuitRF's
/// own worker and <see cref="GdsiiExport"/>, compared per layer against gdsfactory's own GDS of the same
/// cell. Two implementations agreeing is the evidence; the comparison runs on gdsfactory's side, not here.
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class GdsfactoryKitTests : IDisposable
{
    private const string SynthModule = "synthkit";
    private readonly ITestOutputHelper _out;
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-gdf2b-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<PCellWorkerResolver> _resolvers = [];
    private readonly List<string> _reports = [];

    /// <summary>The technology the synthetic kit is drawn against: it deliberately lacks MARK (63/0).</summary>
    private static readonly Technology SynthTech = new()
    {
        Name = "synth",
        Layers =
        {
            new LayerDef { Key = new LayerKey(10, 0), Name = "MET1" },
            new LayerDef { Key = new LayerKey(15, 0), Name = "VIA1" },
            new LayerDef { Key = new LayerKey(20, 0), Name = "MET2" },
        },
    };

    public GdsfactoryKitTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        foreach (var r in _resolvers) { PCellRegistry.RemoveResolver(r); r.Dispose(); }
        PCellRegistry.InvalidateResolved();
        try { Directory.Delete(_ws, recursive: true); } catch { /* best effort */ }
    }

    private static string SynthRoot => FixturePaths.Require("tests/Ui.Tests/Gdsfactory/Fixtures/synthkit");

    private PCellWorkerResolver Declare(GdsfactoryKit kit)
    {
        Assert.NotNull(GdsfactoryKitImport.Declare(_ws, kit, out var problem, out _));
        Assert.Null(problem);
        var resolver = new PCellWorkerResolver(_ws, report: _reports.Add,
                                               moduleInterpreter: _ => GdsfactoryFactAttribute.Interpreter);
        _resolvers.Add(resolver);
        PCellRegistry.AddResolver(resolver);
        return resolver;
    }

    private PCellWorkerResolver Synth()
        => Declare(GdsfactoryKitDiscovery.FindSourceTree(SynthRoot) ?? throw new InvalidOperationException("synthkit not found"));

    /// <summary>Each cell generated at its declared defaults, its cell folder exported to <c>&lt;cell&gt;.gds</c>.</summary>
    private string ExportAll(PCellWorkerResolver resolver, IEnumerable<string> cells, Technology? tech)
        => ExportAll(resolver, cells, tech, out var refused) is var dir && refused.Count == 0
            ? dir
            : throw new InvalidOperationException(string.Join("\n", refused.Select(r => $"{r.Cell}: {r.Why}")));

    /// <summary>As above, collecting the cells the GDSII writer refused rather than stopping at the first.</summary>
    private string ExportAll(PCellWorkerResolver resolver, IEnumerable<string> cells, Technology? tech,
                             out List<(string Cell, string Why)> refused)
    {
        refused = [];
        string dir = Path.Combine(_ws, "gds");
        Directory.CreateDirectory(dir);
        foreach (string id in cells)
        {
            var defaults = resolver.DeclaredDefaults(id) ?? new Dictionary<string, PCellValue>();
            string cellDir = GeneratedCellStore.GetOrCreate(_ws, id, defaults, tech, tech?.Name, PCellLayerSelection.Default);
            var plan = GdsiiExport.Analyze(cellDir, tech, 1000);
            try { GdsiiExport.Write(Path.Combine(dir, id + ".gds"), plan); }
            catch (InvalidDataException ex) { refused.Add((id, ex.Message)); }
        }
        return dir;
    }

    /// <summary>gdsfactory's verdict, per cell, from <c>compare_gds.py</c>.</summary>
    private Dictionary<string, JsonElement> Compare(string module, string gdsDir, IReadOnlyList<string> cells, string? extraPath = null)
    {
        string script = FixturePaths.Require("tests/Ui.Tests/Gdsfactory/Fixtures/compare_gds.py");
        var info = new ProcessStartInfo(GdsfactoryFactAttribute.Interpreter!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(module);
        info.ArgumentList.Add(gdsDir);
        foreach (string c in cells) info.ArgumentList.Add(c);
        info.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator,
            new[] { PCellPythonPackage.RootDirectory, extraPath }.Where(p => p is not null));

        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEndAsync();
        string json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.Trim().Split('\n').Last())!;
    }

    // ── 7 / 8 ─────────────────────────────────────────────────────────────────

    [GdsfactoryFact]
    public void SyntheticKit_EveryOfferedCell_MatchesGdsfactorysOwnGds_PerLayer()
    {
        var resolver = Synth();
        var cells = resolver.KnownGeneratorIds.Where(id => resolver.IsGdsfactoryGenerator(id)).Order().ToList();
        Assert.Contains("frame", cells);   // a hole
        Assert.Contains("turned", cells);  // rotated and mirrored references

        var verdict = Compare(SynthModule, ExportAll(resolver, cells, SynthTech), cells, SynthRoot);
        var wrong = cells.Where(c => !verdict[c].GetProperty("ok").GetBoolean())
                         .Select(c => $"{c}: {verdict[c].GetProperty("differs")}").ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [GdsfactoryFact]
    public void Pins_AreThePorts_ExceptPlacement_WhichIsCounted()
    {
        var resolver = Synth();
        var result = resolver.Resolve("ported")!(resolver.DeclaredDefaults("ported")!, SynthTech, PCellLayerSelection.Default);
        var ports = Compare(SynthModule, ExportAll(resolver, ["ported"], SynthTech), ["ported"], SynthRoot)["ported"]
            .GetProperty("ports").EnumerateArray().ToList();

        var expected = ports.Where(p => p.GetProperty("type").GetString() != "placement")
            .Select(p => (p.GetProperty("name").GetString(), p.GetProperty("x").GetInt64(), p.GetProperty("y").GetInt64(),
                          p.GetProperty("width").GetInt64(), p.GetProperty("orientation").GetDouble()))
            .OrderBy(p => p.Item1).ToList();
        var actual = result.Pins.Select(p => ((string?)p.Name, p.X, p.Y, p.WidthDbu, p.OutwardDirectionDeg)).OrderBy(p => p.Item1).ToList();

        Assert.Equal(expected, actual);
        Assert.Contains("optical", ports.Select(p => p.GetProperty("type").GetString()));  // typed optical, still a pin
        Assert.Contains("1 placement port(s) are alignment marks and were not made pins.", result.Diagnostics ?? []);
    }

    // ── 9 / 10 / 12 ───────────────────────────────────────────────────────────

    [GdsfactoryFact]
    public void TheReport_CountsNonScalarParameters_RefusesRequiredArguments_AndListsTheRaisingCell()
    {
        var kit = GdsfactoryKitDiscovery.FindSourceTree(SynthRoot)!;
        var report = GdsfactoryKitImport.ReadReport(GdsfactoryFactAttribute.Interpreter!, kit, out var problem);
        Assert.NotNull(report);
        Assert.Null(problem);

        Assert.Equal(["nothing", "corners", "helper", "xs"], report!.ParametersNotOffered["kinds"]);
        Assert.Contains(report.NotOffered["required arguments"], c => c[0] == "needs_arg" && c[1] == "size");
        Assert.Contains(report.NotOffered["raised while being built at its defaults"],
                        c => c[0] == "broken" && c[1].Contains("this cell always fails"));
        Assert.DoesNotContain("needs_arg", report.Offered);
        Assert.DoesNotContain("broken", report.Offered);

        // Layer-name text parameters come last; floats are real, never lengths; a model in info is reported.
        Assert.Equal("layer_name", report.Parameters["kinds"].Last());
        Assert.Equal("synth_cap", report.Models["info_model"]);
        Assert.Equal("synth_res", report.Models["kinds"]);
    }

    [GdsfactoryFact]
    public void ALayerTheTechnologyLacks_IsKeptAndReported_NotDropped()
    {
        var resolver = Synth();
        var result = resolver.Resolve("marked")!(resolver.DeclaredDefaults("marked")!, SynthTech, PCellLayerSelection.Default);
        Assert.Contains(result.Shapes, s => s.Layer == new LayerKey(63, 0));
        Assert.Contains(result.Diagnostics ?? [], d => d.Contains("63/0 (MARK)"));
    }

    [GdsfactoryFact]
    public void RegisterPdk_OnAModuleWithNoPdk_IsAProblemNamingTheModule()
    {
        var info = new ProcessStartInfo(GdsfactoryFactAttribute.Interpreter!) { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import gdsfactory_bridge as b; print(b.register_pdk('json').problems)");
        info.Environment["PYTHONPATH"] = PCellPythonPackage.RootDirectory!;
        using var p = Process.Start(info)!;
        string said = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Contains("'json' defines no gdsfactory Pdk", said);
    }

    // ── 11 ────────────────────────────────────────────────────────────────────

    /// <summary>The generic kit gdsfactory ships, every cell at its defaults. A cell gdsfactory itself
    /// cannot build is not offered; how many is printed rather than asserted.</summary>
    [GdsfactoryFact]
    public void TheGenericKit_EveryCellAtItsDefaults_MatchesGdsfactorysOwnGds_PerLayer()
    {
        const string module = "gdsfactory.gpdk";
        var kit = GdsfactoryKitImport.ResolveModule(GdsfactoryFactAttribute.Interpreter!, module, out var refusal);
        Assert.True(kit is not null, refusal);
        var report = GdsfactoryKitImport.ReadReport(GdsfactoryFactAttribute.Interpreter!, kit!, out _)!;
        _out.WriteLine($"{module}: {report.Offered.Count} offered, {report.CellsNotOffered} not offered " +
                       $"({string.Join("; ", report.NotOffered.Select(kv => $"{kv.Key}: {kv.Value.Count}"))}).");

        var resolver = Declare(kit!);
        var cells = resolver.KnownGeneratorIds.Where(id => resolver.IsGdsfactoryGenerator(id) && !PCellRegistry.IsBuiltIn(id))
                                              .Order().ToList();
        Assert.Equal(report.Offered.Order(), cells);
        // Shadowed by a built-in of the same name: reported, never compared — what circuitRF would draw is its own.
        Assert.Contains(report.NotOffered[GdsfactoryImportReport.ShadowedReason], c => c[0] == "via");

        // Draws the date and time it was built, so no two builds agree; gdsfactory's choice, not a defect.
        cells.Remove("version_stamp");

        string dir = ExportAll(resolver, cells, tech: null, out var refused);
        foreach (var (cell, why) in refused) _out.WriteLine($"not exported: {cell}: {why}");
        var exported = cells.Except(refused.Select(r => r.Cell)).ToList();
        var verdict = Compare(module, dir, exported);
        var wrong = exported.Where(c => !verdict[c].GetProperty("ok").GetBoolean())
                            .Select(c => $"{c}: {verdict[c].GetProperty("differs")}").ToList();
        _out.WriteLine($"{exported.Count - wrong.Count} of {exported.Count} exported cells match; {refused.Count} not exported.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.True(refused.Count == 0, string.Join("\n", refused.Select(r => $"{r.Cell}: {r.Why}")));
    }
}
