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
