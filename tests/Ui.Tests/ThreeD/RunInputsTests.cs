// brief-em3d-87 — a 3D result is stale when ANY file it was solved from has changed, not only the .c3d.
//
//    Gate 1  a placed layout's edit (a via moved) names the .clay; a field plot added to the document makes nothing stale.
//    Gate 2  an edit to a material library the technology looks through names the library.
//    Gate 3  a circuit's sub-cell edit names the SUB-CELL (not the schematic above it), and the extraction that lists the
//            files is the one a run consumes, byte for byte.
//
// The submodel's re-solve is in ThermalInterfacesBlocksTests (it needs Gmsh); `render --field`'s note in FieldRenderCliTests.

using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class RunInputsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-87-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void Gate1_ALayoutEdit_NamesTheClay_AndAFieldPlotEditMakesNothingStale()
    {
        var (ws, c3d, doc, run) = DieToHeatsink();
        string clay = Path.Combine(ws, "Board", "layout", "Board.clay");
        var e = C3dElaborator.ElaborateOnce(doc, c3d, Path.Combine(ws, ".cws"));
        Assert.Contains(clay, e.FilesRead);
        Assert.Contains(Path.Combine(ws, "tech", "die-to-heatsink.ctech"), e.FilesRead);

        doc.FieldPlots.Add(new C3dFieldPlot { Name = "Added" });       // display (R-em3d83-2)
        Assert.False(C3dRunDocument.Check(run, doc, c3d)!.Stale);

        Edit(clay, "\"X\": -2000000, \"Y\": -1000000", "\"X\": -1900000, \"Y\": -1000000");   // a via moved
        var check = C3dRunDocument.Check(run, doc, c3d)!;
        Assert.Equal(["Board.clay"], check.Changed);
        Assert.Equal("'Board.clay'", check.What);
        Assert.False(check.DocumentChanged);
    }

    [Fact]
    public void Gate2_AMaterialLibraryEdit_NamesTheLibrary()
    {
        var (ws, c3d, doc, run) = DieToHeatsink();
        Edit(Path.Combine(ws, "tech", "generic-materials.cmat"), "\"ThermalK\": 318.202,", "\"ThermalK\": 300,");   // gold's k
        Assert.Equal(["generic-materials.cmat"], C3dRunDocument.Check(run, doc, c3d)!.Changed);
    }

    [Fact]
    public void Gate3_ASubCellEdit_NamesTheSubCell_NotTheSchematicAboveIt()
    {
        string ws = Copy("Harmonic Balance");
        string top = Path.Combine(ws, "PowerAmplifier", "schematic", "PowerAmplifier.csch");
        string fet = Path.Combine(ws, "FET", "schematic", "FET.csch");
        var (text, files) = SchematicCircuit.CnlTextAndFilesOf(top);
        Assert.Equal(SchematicCircuit.CnlTextOf(top), text);
        Assert.Equal([top, fet], files.Where(f => f.EndsWith(".csch", StringComparison.Ordinal)));

        string c3d = Path.Combine(ws, "View", "3d", "View.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
        var doc = new C3dDocument();
        C3dPersistence.SaveToFile(c3d, doc);
        string run = Path.Combine(ws, "results", "run");
        C3dRunInputs.Take(doc, c3d, files).KeepIn(run);

        Edit(fet, "\"Expression\": \"-0.837\"", "\"Expression\": \"-0.9\"");
        Assert.Equal(["FET.csch"], C3dRunDocument.Check(run, doc, c3d)!.Changed);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Die to Heatsink example (a placed board layout, a technology looking through a material library) with a
    /// run's inputs kept as the run service keeps them.</summary>
    private (string Ws, string C3d, C3dDocument Doc, string Run) DieToHeatsink()
    {
        string ws = Copy("Thermal Die to Heatsink");
        string c3d = Path.Combine(ws, "Die to Heatsink", "3d", "Die to Heatsink.c3d");
        var doc = C3dPersistence.LoadFromFile(c3d);
        var e = C3dElaborator.ElaborateOnce(doc, c3d, Path.Combine(ws, ".cws"));
        string run = Path.Combine(ws, "results", "run");
        C3dRunInputs.Take(doc, c3d, e.FilesRead).KeepIn(run);
        Assert.Equal("die-to-heatsink.ctech", Path.GetFileName(Assert.Single(e.FilesRead, f => f.EndsWith(".ctech", StringComparison.Ordinal))));
        Assert.Contains(Path.Combine(ws, "tech", "generic-materials.cmat"), e.FilesRead);
        Assert.False(C3dRunDocument.Check(run, doc, c3d)!.Stale);
        return (ws, c3d, doc, run);
    }

    private string Copy(string example)
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", example);
        string dst = Path.Combine(_root, example);
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return Path.GetFullPath(dst);
    }

    private static void Edit(string path, string from, string to)
    {
        string text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }
}
