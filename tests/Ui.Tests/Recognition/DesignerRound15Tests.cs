// Designer report, round 15 — Create Schematic from Artwork on two field boards. One test per claim:
//   * the bill of materials is read from the files a designer has: a spreadsheet with its unit in the next column,
//     saved as Windows-1252; a circuitRF netlist (whatever it is called); circuitRF's own BOM export;
//   * a kind the bill of materials states beats the designator's prefix (a ferrite bead listed as an R);
//   * a physical TLIN is drawn with its length.

using System;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class DesignerRound15Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r15-" + Guid.NewGuid().ToString("N")[..12]);

    public DesignerRound15Tests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The designer's spreadsheet: no part-number, quantity or description column (BomFile finds no header),
    /// the unit in the column after the number, grouped and DNP rows, and "µ" saved as Windows-1252's single byte.</summary>
    [Fact]
    public void ASpreadsheetBomWithTheUnitInItsOwnColumn_IsRead()
    {
        string text = "Component,Type,Manufacturer,Value,Package,Size\n" +
                      "\"M1,M23\",Capacitor,Acme,100,pF,0402\n" +
                      "M2,Resistor,Various,49.9,?,0402\n" +
                      "M11,Capacitor,Acme,0.1,µF,0402\n" +
                      "\"M9,M13\",DNP,--,--,--,--\n";
        var bom = RecognitionBom.ReadFile(Write("bom.csv", Encoding.Latin1.GetBytes(text)))!;

        Assert.Null(bom.Refusal);
        Assert.Equal(("100 pF", "Capacitor"), (bom.RowsFor("M23").Single().Value, bom.RowsFor("M23").Single().Description));
        Assert.Equal("0.1 µF", bom.RowsFor("M11").Single().Value);
        Assert.Equal("DNP", bom.RowsFor("M13").Single().Value);
    }

    /// <summary>The netlist of the drawn schematic, saved as .txt: every value exactly, a bead as its DC resistance.</summary>
    [Fact]
    public void ACircuitRFNetlist_IsReadAsTheCircuitItIs()
    {
        string text = "; netlist.cnl — generated\n\n" +
                      "C:M1  n1  RF_in  C=100 pF  Footprint=smt:0402@N\n" +
                      "R:M2  n3  n1  R=49.9 Ohm  Footprint=smt:0402@N\n" +
                      "Bead:FB1  n3  0  Rdc=0.01 Ohm  L=0 uH  Rp=0 Ohm  Cp=0 pF\n" +
                      "Port:P1  RF_in  0  Num=1  Z=50 Ohm\n";
        var bom = RecognitionBom.ReadFile(Write("netlist1.txt", Encoding.UTF8.GetBytes(text)))!;

        Assert.Null(bom.Refusal);
        Assert.Equal(3, bom.Rows.Count);
        Assert.Equal(("100 pF", "smt:0402@N"), (bom.RowsFor("M1").Single().Value, bom.RowsFor("M1").Single().Footprint));
        Assert.Equal(("0.01 Ohm", "Resistor"), (bom.RowsFor("FB1").Single().Value, bom.RowsFor("FB1").Single().Description));
    }

    /// <summary>File ▸ Export ▸ Bill of materials from a schematic reads back through the BOM… button.</summary>
    [Fact]
    public void ASchematicsBillOfMaterials_ReadsBack()
    {
        var model = new SchematicEditModel();
        EditableComponent Part(SymbolKind kind, string name, string param, string expr, string unit)
        {
            var c = new EditableComponent { Symbol = kind, InstanceName = name };
            c.Parameters.Add(new EditableParameter { Name = param, Expression = expr, Unit = unit, ShowOnSchematic = true });
            c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0603@N" });
            model.Components.Add(c);
            return c;
        }
        Part(SymbolKind.Capacitor, "C1", "C", "2.2", "pF");
        Part(SymbolKind.Resistor, "R1", "R", "10", "Ohm").Disable = DisableState.Open;
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Term, InstanceName = "P1" });

        var bom = RecognitionBom.Read(Path.Combine(_root, "board-bom.csv"), SchematicBom.Text(model, "board"));

        Assert.Null(bom.Refusal);
        Assert.Equal(["C1", "R1"], bom.Rows.Select(r => r.Refdes));
        Assert.Equal(("2.2 pF", "smt:0603@N"), (bom.RowsFor("C1").Single().Value, bom.RowsFor("C1").Single().Footprint));
        Assert.Equal("DNP", bom.RowsFor("R1").Single().Value);
    }

    /// <summary>A part whose designator says C and whose bill-of-materials row says R is an R, with its value.</summary>
    [Fact]
    public void TheBillOfMaterialsKind_BeatsTheDesignatorPrefix()
    {
        var (view, placement, _) = PartsBoard();
        var bom = new BomTable("board.csv", null, ',',
            [new BomRow("R1", null, "4R7", "0603", "R"), new BomRow("C2", null, "0.01 Ohm", "0603", "R")], 2, 0, [], []);

        var result = RecognizeParts(view, placement, bom);

        Assert.True(result.Ok, result.Refusal);
        var c2 = result.Parts.Row("C2")!;
        Assert.Equal((PartKind.R, PartEvidenceSource.Bom), (c2.Kind, c2.Evidence[PartField.Kind]));
        Assert.Equal(0.01, c2.Value!.Value, 12);
    }

    /// <summary>A copper layer one technology calls "drawing" (every shipped starter does, the stackup making it copper)
    /// and another "conductor" (what a Gerber import writes) is the same layer: no difference is reported.</summary>
    [Fact]
    public void ACopperLayerLabelledDrawingOrConductor_IsTheSameLayer()
    {
        var a = TwoLayerWithMask();
        var b = TwoLayerWithMask();
        a.Layers.Single(l => l.Key == Top).Purpose = "drawing";
        b.Layers.Single(l => l.Key == Top).Purpose = "conductor";
        string pa = Path.Combine(_root, "a.ctech"), pb = Path.Combine(_root, "b.ctech");
        TechPersistence.SaveToFile(pa, a);
        TechPersistence.SaveToFile(pb, b);

        Assert.Null(CircuitRF.Design.Workspace.ExternalWorkspaceGate.CompareTechnologies(pa, pb));
    }

    /// <summary>A physical TLIN is drawn with Z, L, Eeff and F showing, and its L is a length.</summary>
    [Fact]
    public void APhysicalTlin_IsDrawnWithItsLength()
    {
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("P1", "Port", ["a", "0"], [new("Num", "1"), new("Z", "50", "Ohm")]));
        tb.Instances.Add(new Instance("TF1", "TLIN", ["a", "b"],
            [new("Z", "82.4", "Ohm"), new("L", "2.03", "mm"), new("Eeff", "3.96"), new("F", "6", "GHz"), new("Ac", "0"), new("Ad", "0")]));
        tb.Instances.Add(new Instance("P2", "Port", ["b", "0"], [new("Num", "2"), new("Z", "50", "Ohm")]));

        var drawn = NetlistSchematic.Build(new Library("netlist"), tb).Schematic!;

        var tlin = drawn.Components.Single(c => c.InstanceName == "TF1");
        var l = tlin.Parameters.Single(p => p.Name == "L");
        Assert.Equal((true, UnitDimension.Length), (l.ShowOnSchematic, l.Dimension));
        Assert.False(tlin.Parameters.Single(p => p.Name == "Ad").ShowOnSchematic);
    }
}
