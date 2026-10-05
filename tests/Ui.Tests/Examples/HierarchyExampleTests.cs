// ================================================================
//  HierarchyExampleTests.cs — the "Hierarchy" example workspace, which exists to show hierarchy in a schematic that
//  simulates, in the layouts Update Layout from Schematic produced from it, and in a 3D view. Each test is one of the
//  claims its README makes; HierarchyExampleAuthoring rebuilds the files.
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class HierarchyExampleTests : IDisposable
{
    private readonly string _ws;

    /// <summary>A copy: running the generator writes the git-ignored <c>.generated-cells/</c> cache into the
    /// workspace, which has no business appearing in the source tree.</summary>
    public HierarchyExampleTests()
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Hierarchy");
        _ws = Path.Combine(Path.GetTempPath(), "crf-hierarchy-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, f);
            if (rel.Split(Path.DirectorySeparatorChar)[0] == ".generated-cells") continue;
            string dst = Path.Combine(_ws, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>Both layouts are LINKED to their schematics: every instance names the schematic component it came
    /// from, and running Update Layout from Schematic again changes nothing — at Pad's level and at Board's, where
    /// two of the instances are Pad's own layout.</summary>
    [Theory]
    [InlineData("Pad",   new[] { "R1", "R2", "R3" })]
    [InlineData("Board", new[] { "TL1", "X1", "TL2", "X2", "TL3" })]
    public void UpdateLayoutFromSchematic_AgainChangesNothing(string cell, string[] placed)
    {
        string cellDir = Path.Combine(_ws, cell);
        string schematicDir = Path.Combine(cellDir, "schematic");
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(schematicDir, cell + ".csch"));
        model.SchematicDirectory = schematicDir;
        var view = LayoutPersistence.LoadFromFile(Path.Combine(cellDir, "layout", cell + ".clay"));
        string techPath = Path.Combine(_ws, "tech", ShippedTechnologies.DefaultId + ".ctech");

        Assert.Equal(placed.Order(), view.Instances.Select(i => i.SchematicId!).Order());

        var r = SchematicToLayoutGenerator.Run(model, view, schematicDir, _ws, Path.Combine(cellDir, "layout"),
                                               TechPersistence.LoadFromFile(techPath), techPath, DiskCellResolver.Instance);
        Assert.Empty(r.NoLayoutWarnings);
        Assert.True(r.Command is null, string.Join(" | ", r.Lines.Select(l => l.Text)));

        if (cell == "Board")
            Assert.All(view.Instances.Where(i => i.SchematicId!.StartsWith('X')), i => Assert.Equal("../../Pad", i.CellRef));
    }

    /// <summary>The bench simulates through two levels: Board is defined once, Pad is defined once and placed twice
    /// with its parameter overridden per instance.</summary>
    [Fact]
    public void Bench_ElaboratesThroughBoardIntoPad()
    {
        string cnl = SchematicCircuit.CnlTextOf(Path.Combine(_ws, "Bench", "schematic", "Bench.csch"));

        Assert.Contains("define Pad (IN OUT)", cnl);
        Assert.Contains("define Board (IN OUT)", cnl);
        Assert.Contains("Pad:X1  n1  n2  dB=3", cnl);
        Assert.Contains("Pad:X2  n3  n4  dB=6", cnl);
        Assert.Contains("Board:X1", cnl);
    }

    /// <summary>The 3D hierarchy: Assembly places Board's 3D VIEW (which Push Into Cell edits in context) and Launch's
    /// twice; Board's 3D view places Board's own layout, which places Pad's — so the levels run 3D → 3D → layout →
    /// layout, and every one of them resolves.</summary>
    [Fact]
    public void Assembly_PlacesThreeDViews_WhichPlaceTheLayout()
    {
        string c3d = Path.Combine(_ws, "Assembly", "3d", "Assembly.c3d");
        var placed = C3dPersistence.LoadFromFile(c3d).Instances;
        Assert.Equal(["B1 ../../Board", "J1 ../../Launch", "J2 ../../Launch"], placed.Select(i => $"{i.Name} {i.CellRef}"));
        Assert.All(placed, i => Assert.Equal(C3dInstanceView.ThreeD, i.View));
        Assert.All(placed, i => Assert.Null(C3dHierarchy.MissingView(Path.Combine(_ws, Path.GetFileName(i.CellRef)), i.View)));

        string boardC3d = Path.Combine(_ws, "Board", "3d", "Board.c3d");
        var l1 = Assert.Single(C3dPersistence.LoadFromFile(boardC3d).Instances);
        Assert.Equal(C3dInstanceView.Layout, l1.View);
        var board = CellLayoutResolver.Resolve(l1.CellRef, Path.GetDirectoryName(boardC3d)!);
        Assert.Equal(CellLayoutState.Resolved, board.State);
        Assert.Equal(2, board.View!.Instances.Count(i => i.CellRef == "../../Pad"));
    }

    /// <summary>
    /// The board's silkscreen carries the designators of the parts INSIDE its modules — R1–R3 under each Pad, in the
    /// sub-cell's own text — and none for the modules themselves (X1/X2 are switched off: a module is not a part).
    /// This is the flat reading Gerber and DRC take; the screen draws the same labels from the same function
    /// (FootprintLabel.NestedShapesFor). Before, a board drew its parts' designators only once you pushed in.
    /// </summary>
    [Fact]
    public void BoardSilkscreen_CarriesTheModulesPartDesignators_AndNotTheModules()
    {
        string boardDir = Path.Combine(_ws, "Board");
        var view = LayoutPersistence.LoadFromFile(Path.Combine(boardDir, "layout", "Board.clay"));
        var tech = TechPersistence.LoadFromFile(Path.Combine(_ws, "tech", ShippedTechnologies.DefaultId + ".ctech"));

        var flat = LayoutDesignFlatten.Flatten(view, boardDir, tech, null, null);
        var designators = flat.Shapes.OfType<LabelShape>().Select(l => l.Text).Order().ToList();

        Assert.Equal(["R1", "R1", "R2", "R2", "R3", "R3", "TL1", "TL2", "TL3"], designators);

        // Each lands inside the Pad placement it belongs to: three left of the board's centre, three right.
        var parts = flat.Shapes.OfType<LabelShape>().Where(l => l.Text.StartsWith('R')).ToList();
        long centre = view.Pins.Single(p => p.Name == "OUT").X / 2;
        Assert.Equal(3, parts.Count(l => l.X < centre));
        Assert.Equal(3, parts.Count(l => l.X > centre));
    }

    /// <summary>Every generated ground via sits wholly in pad-layer copper — its tie is as wide as the via's pad and runs
    /// half a pad past its centre (GroundArtwork.Beside) — and the tie adds nothing to the gap between the part's two
    /// lands: it is flush at the pin, so it reaches no further back than the pin itself.</summary>
    [Fact]
    public void PadGroundVias_AreWhollyCoveredByTheirTie()
    {
        var view = LayoutPersistence.LoadFromFile(Path.Combine(_ws, "Pad", "layout", "Pad.clay"));
        var vias = view.Shapes.OfType<ViaShape>().Where(v => v.Generated?.StartsWith(GroundArtwork.ViaTagPrefix) == true).ToList();
        Assert.Equal(2, vias.Count);
        foreach (var via in vias)
        {
            var tie = Assert.Single(view.Shapes.OfType<PathShape>(),
                                    p => p.Generated is { } g && via.Generated!.StartsWith(g + " @", StringComparison.Ordinal));
            Assert.Equal(PathEndStyle.Flush, tie.End);
            Assert.True(tie.Width >= via.PadSize, $"tie {tie.Width} narrower than the via pad {via.PadSize}");
            // The far end is half a pad beyond the via's centre, on the line from the pin through it.
            long run = (long)Math.Round(Math.Sqrt(Math.Pow(tie.Xy[^2] - via.X, 2) + Math.Pow(tie.Xy[^1] - via.Y, 2)));
            Assert.InRange(run, via.PadSize / 2 - 1, via.PadSize / 2 + 1);
        }
    }
}
