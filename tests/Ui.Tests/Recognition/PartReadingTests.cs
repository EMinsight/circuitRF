// brief-artsch-4-parts-and-parts-table.md §4 — PartReading on synthetic boards: a placed instance wins
// over the land pattern under it; the bill of materials gives values, refuses a wrong dimension and marks
// a DNP part open; series, shunt and bridged are measured.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PartReadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as4-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void APlacedInstanceWinsOverALandPatternOnTheSamePads()
    {
        // The land drawn at the root stands for the flattened artwork; the instance places a cell whose
        // two pins sit on the same lands.
        var land = Land("0603", DensityLevel.Nominal, 10_000, 5_000);
        var (l, r) = Ends(land);
        string landDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Land"), ViewType.Layout);
        Directory.CreateDirectory(landDir);
        var cell = new LayoutView();
        cell.Pins.Add(new LayoutPin { Name = "1", X = Um(l - 10_000), Y = 0, Layer = Top });
        cell.Pins.Add(new LayoutPin { Name = "2", X = Um(r - 10_000), Y = 0, Layer = Top });
        LayoutPersistence.SaveToFile(Path.Combine(landDir, "Land.clay"), cell);

        var view = PlaneAndLine(0, l);
        view.Shapes.Add(Line(Top, r, 40_000, 5_000));
        view.Shapes.AddRange(land);
        view.Instances.Add(new LayoutInstance { CellRef = Path.Combine("..", "..", "Land"), X = Um(10_000), Y = Um(5_000), RefDes = "C6" });
        string boardDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Board"), ViewType.Layout);
        Directory.CreateDirectory(boardDir);
        string clay = Path.Combine(boardDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var result = ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, ClayPath = clay,
        });

        Assert.True(result.Ok, result.Refusal);
        var row = Assert.Single(result.Parts.Rows);
        Assert.Equal(("C6", PartEvidenceSource.Instance, PartKind.C), (row.Refdes, row.Evidence[PartField.Refdes], row.Kind));
        Assert.Equal((PartConnection.Series, PartConfidence.High), (row.Connection, row.Confidence));
    }

    [Fact]
    public void TheBillOfMaterialsGivesValuesRefusesAWrongDimensionAndMarksDnpOpen()
    {
        var (view, placement, bom) = PartsBoard();

        var result = RecognizeParts(view, placement, bom);

        Assert.True(result.Ok, result.Refusal);
        var r1 = result.Parts.Row("R1")!;
        Assert.Equal((PartKind.R, PartEvidenceSource.Placement, PartEvidenceSource.Bom),
                     (r1.Kind, r1.Evidence[PartField.Refdes], r1.Evidence[PartField.Value]));
        Assert.Equal(4.7, r1.Value!.Value, 12);

        var c2 = result.Parts.Row("C2")!;
        Assert.Null(c2.Value);
        Assert.Equal("C2_C", c2.Variable);
        Assert.Contains(c2.Notes, n => n.Contains("inductance", StringComparison.Ordinal));
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.PartValueWrongDimension));

        Assert.Equal(PartKind.Open, result.Parts.Row("C3")!.Kind);
    }

    [Fact]
    public void SeriesShuntAndBridgedAreMeasuredOffTheIslands()
    {
        var (view, _, _) = PartsBoard();

        var parts = RecognizeParts(view, null, null).Parts;

        // With no placement file every part is a land pattern alone, named top to bottom, left to right.
        Assert.All(parts.Rows, r => Assert.Equal(PartEvidenceSource.Generated, r.Evidence[PartField.Refdes]));
        var byX = parts.Rows.OrderBy(r => r.X).ToList();
        Assert.Equal([PartConnection.Series, PartConnection.Shunt, PartConnection.Bridged], byX.Select(r => r.Connection));
        Assert.All(byX, r => Assert.Equal("0603", r.Case!.Code));
        Assert.False(byX[2].IsModelled);
        Assert.True(byX[1].Terminals[1].OnGround);
    }
}
