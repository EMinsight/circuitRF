// brief-artsch-6-emit-and-target-cell.md §4 — the L5 commands honour FromArtwork (R-as6-7, D5): Update Layout from
// Schematic generates nothing for recognised lines and says how many; Update Schematic from Layout does not
// duplicate a recognised part a placement names.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class FromArtworkSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as6-sync-" + Guid.NewGuid().ToString("N")[..12]);

    public FromArtworkSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static EditableComponent Recognised(string name, SymbolKind kind)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = kind, FromArtwork = true };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        return comp;
    }

    [Fact]
    public void UpdateLayoutFromSchematic_GeneratesNothingForRecognisedLines_AndCountsThem()
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        model.Components.Add(Recognised("TL1", SymbolKind.Mlin));
        model.Components.Add(Recognised("TL2", SymbolKind.Mlin));
        var layout = new LayoutView();

        var result = SchematicToLayoutGenerator.Run(model, layout, _root, _root, _root, null, null, null);

        Assert.Equal((0, 0, 0), (result.AddedCount, result.UpdatedCount, result.RemovedCount));
        Assert.Empty(result.NoLayoutWarnings);
        Assert.Empty(layout.Instances);
        var line = Assert.Single(result.Lines);
        Assert.Equal(("", "2 components model existing artwork — not generated"), (line.InstanceName, line.Text));
    }

    /// <summary>
    /// Designer report (round 15): the artwork is in the layout the schematic was recognised FROM, and nowhere else. A
    /// recognised schematic updating its own new cell's empty layout generates its lines; updating the artwork's layout
    /// still generates nothing.
    /// </summary>
    [Fact]
    public void UpdateLayoutFromSchematic_GeneratesRecognisedLines_IntoAnyLayoutButTheArtworks()
    {
        var model = new SchematicEditModel
        {
            SchematicDirectory = _root,
            ArtworkSource = new ArtworkProvenance { Layout = "../Board/layout/Board.clay" },
        };
        model.Components.Add(Recognised("TL1", SymbolKind.Mlin));
        string artwork = Path.Combine(_root, "..", "Board", "layout", "Board.clay");
        string own = Path.Combine(_root, "Board_model.clay");

        var intoOwn = SchematicToLayoutGenerator.Run(model, new LayoutView(), _root, _root, _root, null, null, null, targetLayoutPath: own);
        var intoArtwork = SchematicToLayoutGenerator.Run(model, new LayoutView(), _root, _root, _root, null, null, null, targetLayoutPath: artwork);

        Assert.Equal(1, intoOwn.AddedCount);
        Assert.Equal(0, intoArtwork.AddedCount);
    }

    /// <summary>
    /// Designer report (round 16): synced back into a new layout, a recognised board came out as a grid, and its
    /// grounded coplanar lines were missing — nothing could draw one. A recognised line is drawn where the board had
    /// it: pin 1 on its first anchor point, running toward the second, with its two side grounds.
    /// </summary>
    [Fact]
    public void UpdateLayoutFromSchematic_PlacesARecognisedLineWhereTheBoardHadIt()
    {
        var model = new SchematicEditModel
        {
            SchematicDirectory = _root,
            ArtworkSource = new ArtworkProvenance { Layout = "../Board/layout/Board.clay" },
        };
        var cp = Recognised("CP1", SymbolKind.Cpwg);
        cp.ArtworkAnchor.AddRange([(10_000_000, 5_000_000), (10_000_000, 9_000_000)]);
        model.Components.Add(cp);
        var layout = new LayoutView();

        var result = SchematicToLayoutGenerator.Run(model, layout, _root, _root, _root, null, null, null,
                                                    targetLayoutPath: Path.Combine(_root, "Board_model.clay"));
        result.Command!.Execute();

        var inst = Assert.Single(layout.Instances);
        Assert.Equal((10_000_000L, 5_000_000L, 90.0), (inst.X, inst.Y, inst.RotationDegrees));
        var cell = CellLayoutResolver.Resolve(inst.CellRef, _root);
        Assert.Equal(3, cell.View!.Shapes.OfType<RectShape>().Count());   // the strip and its two side grounds
    }

    /// <summary>The same report: a recognised part is centred where the board has it and lies the way the board has
    /// it — pad 1 toward pad 2 along the direction the recognition measured — whatever its symbol's rotation.</summary>
    [Fact]
    public void UpdateLayoutFromSchematic_LaysARecognisedPartAsTheBoardHasIt()
    {
        var model = new SchematicEditModel
        {
            SchematicDirectory = _root,
            ArtworkSource = new ArtworkProvenance { Layout = "../Board/layout/Board.clay" },
        };
        var c = Recognised("C1", SymbolKind.Capacitor);
        c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0603@N" });
        c.ArtworkAnchor.Add((5_000_000, 7_000_000));
        c.ArtworkMeasured[CircuitRF.Design.Layout.Recognition.RecognitionEmit.PadAxisKey] = 0;
        model.Components.Add(c);
        var layout = new LayoutView();

        var result = SchematicToLayoutGenerator.Run(model, layout, _root, _root, _root, ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"),
                                                    null, null, targetLayoutPath: Path.Combine(_root, "Board_model.clay"));
        Assert.True(result.Command is not null, string.Join(" ", result.NoLayoutWarnings));
        result.Command.Execute();

        var inst = Assert.Single(layout.Instances);
        var box = CellHierarchy.InstanceBbox(inst, _root);
        Assert.Equal((5_000_000L, 7_000_000L), ((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2));
        Assert.True(box.MaxX - box.MinX > box.MaxY - box.MinY, "an 0603 with its pads along x is wider than it is tall");
    }

    [Fact]
    public void UpdateSchematicFromLayout_DoesNotDuplicateARecognisedPart()
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        model.Components.Add(Recognised("C6", SymbolKind.Capacitor));
        var layout = new LayoutView();
        layout.Instances.Add(new LayoutInstance { CellRef = "../../Land", RefDes = "C6", PartKind = "C" });

        var result = LayoutToSchematicGenerator.Run(layout, model, _root);

        Assert.Equal(0, result.CreatedCount);
        Assert.Null(result.Command);
        Assert.Single(model.Components);
        Assert.Contains(result.Lines, l => l.Text == "C6 is modelled from artwork — unchanged");
    }
}
