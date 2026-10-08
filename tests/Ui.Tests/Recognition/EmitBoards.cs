// The synthetic board for brief-artsch-6-emit-and-target-cell.md's gates, on RecognitionBoards' two-layer
// technology with a top mask. Coordinates in µm. A 30 × 20 mm board with a Bottom plane and a top pour stitched to
// it above y = 12 mm:
//   - a line in from the left edge at y = 5 mm to C1, an 0603 in series, its value 10 pF from the bill of materials;
//   - L_A1, an 0603 standing on that line with its far pad strapped to the pour: a shunt inductor, value unknown;
//   - from C1 a line on to x = 22 mm that turns 90° down to the bottom edge — a bend.
// Two edge launches are the two ports.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Workspace;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

internal static class EmitBoards
{
    public static (LayoutView View, PlacementTable Placement, BomTable Bom) SeriesCShuntLBend()
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 30_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 12_000, 30_000, 20_000));
        for (int i = 0; i < 6; i++) view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 16_000));

        var c1 = Land("0603", DensityLevel.Nominal, 10_000, 5_000);
        var (l, r) = Ends(c1);
        double pitch = r - l;
        view.Shapes.AddRange(c1);
        view.Shapes.Add(Rect(Top, 0, 4_525, l, 5_475));

        view.Shapes.AddRange(Land("0603", DensityLevel.Nominal, 5_000, 5_000 + pitch / 2, vertical: true));
        view.Shapes.Add(Rect(Top, 4_700, 5_000 + pitch, 5_300, 12_500));

        view.Shapes.Add(new PolygonShape
        {
            Layer = Top,
            Xy = [.. new double[] { r, 4_525, 21_525, 4_525, 21_525, 0, 22_475, 0, 22_475, 5_475, r, 5_475 }.Select(Um)],
        });

        PlacementRow Row(string refdes, double x, double y) => new(refdes, Um(x), Um(y), 0, false, "0603", 1);
        var placement = new PlacementTable(
            "board.pos", null, PlacementOrigin.BodyCentre, PlacementOriginEvidence.Chosen, LayoutUnit.Mm,
            BoardNetlistUnitsEvidence.Declared, ',',
            [Row("C1", 10_000, 5_000), Row("L_A1", 5_000, 5_000 + pitch / 2)],
            2, 0, DrillExtents.Empty, []);
        var bom = new BomTable(
            "board.csv", null, ',',
            [new BomRow("C1", null, "10pF", "0603", "Capacitor"), new BomRow("L_A1", null, null, "0603", "Inductor")],
            2, 0, [], []);
        return (view, placement, bom);
    }

    /// <summary>
    /// The board saved as a workspace: <c>.cws</c> (no default technology), <c>tech/board.ctech</c>, and the cell
    /// <c>Board</c> whose layout names that technology — the shape of an imported board. Returns the
    /// recognition's input.
    /// </summary>
    public static RecognitionInput Saved(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "tech"));
        string tech = Path.Combine(root, "tech", "board.ctech");
        TechPersistence.SaveToFile(tech, TwoLayerWithMask());
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile());

        var (view, placement, bom) = SeriesCShuntLBend();
        view.TechRef = "../../tech/board.ctech";
        string cell = CellFolder.CreateCellFolder(root, "Board");
        string clay = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Layout), "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        return new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, Placement = placement, Bom = bom,
            ClayPath = clay, TechnologyPath = tech,
        };
    }
}
