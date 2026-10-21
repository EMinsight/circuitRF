using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Designer round 24 (2026-10-21): on a schematic made by Create Schematic from Artwork, dragging a line whose open
/// end carried a net label left the label behind — it named nothing where it stood, the pin lost its net name, and the
/// label could not be dragged back. Recognition draws a net that has no wire as a label straight on the pin, with no
/// wire under it; such a label now rides its pin as a ground drawn on a pin does.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class DesignerRound24Tests
{
    private static EditableComponent Comp(SchematicEditModel m, SymbolKind kind, string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        m.Components.Add(c);
        return c;
    }

    /// <summary>R2 wired to R1's top pin; R1's bottom pin open and named "n16" by a label on the pin.</summary>
    private static (SchematicEditModel M, EditableComponent R1, EditableNetLabel Label) OpenEndWithLabel()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(m, SymbolKind.Resistor, "R1", 0, 0);                      // pins (0,-200) (0,200)
        Comp(m, SymbolKind.Resistor, "R2", 0, -600);                            // pins (0,-800) (0,-400)
        var w = new EditableWire();
        w.Points.AddRange([(0, -400), (0, -200)]);
        m.Wires.Add(w);
        var label = new EditableNetLabel { Name = "n16", X = 0, Y = 200 };
        m.NetLabels.Add(label);
        return (m, r1, label);
    }

    private static string NetOf(SchematicEditModel m, string inst, int pin)
        => NetExtractor.Extract(m).TestBench.Instances.Single(i => i.InstanceName == inst).NetBindings[pin];

    [Fact]
    public void DraggingAPart_CarriesTheLabelOnItsOpenPin()
    {
        var (m, r1, label) = OpenEndWithLabel();
        Assert.Equal("n16", NetOf(m, "R1", 1));

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(400, 0);

        Assert.Equal((400.0, 200.0), (label.X, label.Y));
        Assert.Equal("n16", NetOf(m, "R1", 1));

        vm.UndoRedo.Undo();
        Assert.Equal((0.0, 200.0), (label.X, label.Y));
    }

    [Fact]
    public void RotatingAPart_CarriesTheLabelOnItsOpenPin()
    {
        var (m, r1, label) = OpenEndWithLabel();

        var cmd = new RotateCommand(m, [r1.Id], clockwise: true);
        cmd.Execute();
        Assert.Equal("n16", NetOf(m, "R1", 1));
        Assert.NotEqual((0.0, 200.0), (label.X, label.Y));

        cmd.Undo();
        Assert.Equal((0.0, 200.0), (label.X, label.Y));
    }

    /// <summary>A label on a pin-on-pin contact with a part that stays belongs to that part too, and stays.</summary>
    [Fact]
    public void ALabelAlsoOnAStationaryPin_Stays()
    {
        var (m, r1, label) = OpenEndWithLabel();
        Comp(m, SymbolKind.Resistor, "R3", 0, 400);                             // pins (0,200) (0,600)

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(400, 0);

        Assert.Equal((0.0, 200.0), (label.X, label.Y));
        Assert.Equal("n16", NetOf(m, "R3", 0));
        Assert.Equal("n16", NetOf(m, "R1", 1));
    }
}
