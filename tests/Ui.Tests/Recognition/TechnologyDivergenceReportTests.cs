// brief-artsch-6-emit-and-target-cell.md §4 — TechnologyDivergenceReport compares the layout's technology with the
// schematic's RESOLVED one (R-as6-1): a schematic whose TechRef names its layout's technology says nothing; the same
// schematic on the workspace default still warns — and it warns when the two technologies differ only in their
// stackups, which the layer-table comparison alone cannot see.

using System;
using System.IO;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class TechnologyDivergenceReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as6-diverge-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void ASchematicWhoseTechRefMatchesItsLayoutHasNothingToSay()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        // One layer table, two substrates: 1.6 mm of FR-4 against 0.5 mm of εr 3.5.
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "fr4.ctech"), SchematicTechRefTests.Board(1_600, 4.4));
        string board = Path.Combine(_root, "tech", "board.ctech");
        TechPersistence.SaveToFile(board, SchematicTechRefTests.Board(500, 3.5));
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/fr4.ctech" });
        string dir = Path.Combine(_root, "Model", "schematic");
        Directory.CreateDirectory(dir);

        var model = new SchematicEditModel { SchematicDirectory = dir };
        model.Components.Add(new EditableComponent { InstanceName = "TL1", Symbol = SymbolKind.Mlin, FromArtwork = true });

        string warning = Assert.IsType<string>(TechnologyDivergenceReport.Describe(model, SchematicTechnology.PathOf(model), board));
        Assert.Contains("1600 µm thick in 'fr4' and 500 µm in 'board'", warning);

        model.TechRef = "../../tech/board.ctech";
        Assert.Null(TechnologyDivergenceReport.Describe(model, SchematicTechnology.PathOf(model), board));
    }
}
