using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Layout.TechImport;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Gdsfactory;

/// <summary>
/// brief-gdf-3 §4 Tier B: a kit's layer stack and material cards through the real
/// <c>gdsfactory_bridge.technology</c> and real gdsfactory. Skips with a reason unless
/// <c>CRF_GDSFACTORY_PYTHON</c> names an interpreter that has gdsfactory.
/// </summary>
public sealed class GdsfactoryTechnologyTests(ITestOutputHelper output)
{
    private static string SynthRoot => FixturePaths.Require("tests/Ui.Tests/Gdsfactory/Fixtures/synthkit");

    // ── 6 ──────────────────────────────────────────────────────────────────────

    /// <summary>The synthetic kit's stack and cards (<c>synthkit/process.py</c>) come back exactly as stated.</summary>
    [GdsfactoryFact]
    public void TheSyntheticKitsStack_RoundTripsThroughTheRealReader()
    {
        var kit = GdsfactoryKitDiscovery.FindSourceTree(SynthRoot)!;
        var doc = GdsfactoryKitImport.ReadTechnology(GdsfactoryFactAttribute.Interpreter!, kit, out var table, out var problem);
        Assert.True(doc is not null, problem);
        Assert.Empty(doc!.Problems);

        Assert.Equal(
        [
            ("substrate", new LayerKey(99, 0), -200.0, 200.0, "synth_bulk"),
            ("well",      new LayerKey(5, 0),    -1.0,   1.2, "synth_bulk"),
            ("ild",       new LayerKey(98, 0),    0.0,   1.0, "synth_oxide"),
            ("met1",      new LayerKey(10, 0),    1.0,   0.5, "synth_metal"),
            ("via1",      new LayerKey(15, 0),    1.5,   0.7, "synth_plug"),
            ("met2",      new LayerKey(20, 0),    2.2,   1.0, "synth_metal"),
        ], doc.Stack.Select(l => (l.Name, l.Key!.Value, l.Zmin!.Value, l.Thickness!.Value, l.Material!)));
        Assert.Equal(5.0, doc.Stack.Single(l => l.Name == "via1").SidewallAngle);

        // Found by what they expose, in a module circuitRF never names; a value the card leaves out is null.
        Assert.Equal(["synth_bulk", "synth_metal", "synth_oxide", "synth_plug"], doc.Materials.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((3.0e7, (double?)null), (doc.Materials["synth_metal"].Sigma!.Value, doc.Materials["synth_metal"].Epsr));
        Assert.Equal((4.0, 0.002), (doc.Materials["synth_oxide"].Epsr!.Value, doc.Materials["synth_oxide"].Tand!.Value), new Close());

        // No layer views in this kit: the layer map stands in, with no temporary file left behind.
        Assert.Null(doc.LayerViews);
        Assert.Equal(["MARK", "MET1", "MET2", "VIA1"], table!.Entries.Select(e => e.BaseName).Order(StringComparer.Ordinal));

        var r = GdsfactoryStackConverter.Convert(doc, table, kit.Module);
        output.WriteLine(r.Summary);
        Assert.Equal(["met2", "met1"], r.Technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name));
        var via = Assert.Single(r.Technology.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal(("via1", "met1", "met2"), (via.Name, via.SpanFromLayer, via.SpanToLayer));
        Assert.Contains(r.Notes, n => n.Text.StartsWith("Sidewall angles") && n.Text.Contains("via1 (5°)"));
    }

    // ── 7 ──────────────────────────────────────────────────────────────────────

    /// <summary>The generic kit gdsfactory ships builds with nothing refused; its note count is printed.</summary>
    [GdsfactoryFact]
    public void TheGenericKitsStack_BuildsWithZeroRefusals()
    {
        const string module = "gdsfactory.gpdk";
        var kit = GdsfactoryKitImport.ResolveModule(GdsfactoryFactAttribute.Interpreter!, module, out var refusal);
        Assert.True(kit is not null, refusal);

        var doc = GdsfactoryKitImport.ReadTechnology(GdsfactoryFactAttribute.Interpreter!, kit!, out var table, out var problem);
        Assert.True(doc is not null, problem);
        Assert.Empty(doc!.Problems);
        Assert.NotEmpty(doc.Stack);

        var r = GdsfactoryStackConverter.Convert(doc, table, module);
        output.WriteLine($"{module}: {r.Summary}; {r.Notes.Count} notes, {r.Warnings} warnings.");
        foreach (var n in r.Notes) output.WriteLine("  " + n);
        Assert.Contains(r.Technology.Stackup.Layers, l => l.Kind == StackupKind.Conductor);
        Assert.Contains(r.Technology.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.NotEmpty(r.Technology.Layers);
    }

    private sealed class Close : System.Collections.Generic.IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b)
            => Math.Abs(a.Item1 - b.Item1) < 1e-12 && Math.Abs(a.Item2 - b.Item2) < 1e-12;
        public int GetHashCode((double, double) v) => 0;
    }
}
