// ================================================================
//  ArtworkToSchematicExampleAuthoring.cs — the Artwork to Schematic example (brief-artsch-9 R-as9-4), BUILT with the
//  functions the application's own commands call. Skipped unless CRF_AUTHOR_ARTWORK=1, because it WRITES
//  examples/Artwork to Schematic (the folder is replaced; its README.md is kept).
//
//  The example is a real flattened board, as a user's would be: a small board authored in circuitRF — the round trip's
//  design (ArtworkRoundTripBoards: Update Layout from Schematic, the parts arranged, a plane drawn) — exported to Gerber
//  + Excellon + placement + BOM into the example's own fab/ folder, then imported as the workspace's artwork cell, its
//  technology's stackup typed in as the import asks. The schematic it was drawn from ships beside it as the cell
//  "Board design", so there is something to compare the recognised schematic against.
//
//  The copper is merged into non-overlapping regions on the way out, as a fabricator's CAM output usually is: the bend's
//  and tee's cells draw arms over the lines beside them, and the imported board showed every overlap (round 15).
//
//  Two edits to the companion files, both what an assembly house's files look like rather than circuitRF's own:
//  the placement and BOM list only what is soldered (circuitRF's writers list every placed instance, lines included),
//  and L1's BOM row is DELETED, so recognising the board gives L1 a variable to tune.
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ArtworkToSchematicExampleAuthoring
{
    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_ARTWORK") != "1")
                Skip = "re-builds examples/Artwork to Schematic only with CRF_AUTHOR_ARTWORK=1";
        }
    }

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string ws = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Artwork to Schematic");
        string readme = Path.Combine(ws, "README.md");
        string? keptReadme = File.Exists(readme) ? File.ReadAllText(readme) : null;
        if (Directory.Exists(ws)) Directory.Delete(ws, true);

        string scratch = Path.Combine(Path.GetTempPath(), "crf-as9-example-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            // The board, designed and laid out.
            var (cellDir, schematic, clay, tech) = ArtworkRoundTripBoards.Original(scratch);

            // Out to the fabricator: Gerber + Excellon, placement, BOM.
            string fab = Path.Combine(ws, "fab");
            string gerbers = Path.Combine(fab, "Board");
            string pos = Path.Combine(fab, "Board.pos"), bom = Path.Combine(fab, "Board-bom.csv");
            ArtworkRoundTripBoards.WriteFab(cellDir, clay, tech, gerbers, pos, bom, unionCopper: true);
            KeepRows(pos, "C1", "L1", "R1");
            KeepRows(bom, "C1", "R1");

            // Back in as a user's board: the workspace, its artwork cell, and the design it came from.
            ArtworkRoundTripBoards.ImportBoard(gerbers, ws, ArtworkRoundTripBoards.TwoLayer());
            WorkspacePolicyFiles.Ensure(ws);
            var (model, _, _) = SchematicPersistence.LoadFromFile(schematic);
            CellCreate.Create(ws, "Board design", CellViews.Schematic, model);

            if (keptReadme is not null) File.WriteAllText(readme, keptReadme);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Keeps a companion file's comment lines, its header and the rows for <paramref name="refdes"/>.</summary>
    private static void KeepRows(string path, params string[] refdes)
    {
        var lines = File.ReadAllLines(path);
        int header = Array.FindIndex(lines, l => l.StartsWith("Refdes,", StringComparison.Ordinal));
        Assert.True(header >= 0, $"{path} has no Refdes header");
        File.WriteAllLines(path, lines.Where((l, i) => i <= header || refdes.Contains(l.Split(',')[0])));
    }
}
