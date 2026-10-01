using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Designer feedback round 11 — a wire running through a T is one polyline, so clicking it and
/// pressing Delete used to take the whole segment and every connection along it. A click on a segment
/// something joins between its vertices now selects only the stretch between the junctions either side,
/// and Delete removes exactly that stretch: the rest still reaches every pin it reached.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class WireSpanAtJunctionTests
{
    // Three resistors in a row; each one's top pin (port 0) is 200 above its centre.
    //   V  at (0,0)     → top pin (0,-200)       — the supply stand-in
    //   M5 at (400,-200) → top pin (400,-400)    — lands on the wire's INTERIOR: a T by pin
    //   M1 at (800,0)   → top pin (800,-200)
    // One wire V → up → across → down to M1, passing through M5's pin.
    private static (SchematicEditModel Model, SchematicViewModel Vm, EditableWire Wire) Bench()
    {
        var model = new SchematicEditModel { GridSnap = false };
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor, X = 0,   Y = 0,    InstanceName = "V" });
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor, X = 400, Y = -200, InstanceName = "M5" });
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor, X = 800, Y = 0,    InstanceName = "M1" });
        var wire = new EditableWire();
        wire.Points.AddRange([(0, -200), (0, -400), (800, -400), (800, -200)]);
        model.Wires.Add(wire);
        return (model, new SchematicViewModel(model), wire);
    }

    private static string TopNet(SchematicEditModel model, string name)
        => NetExtractor.Extract(model).TestBench.Instances.First(i => i.InstanceName == name).NetBindings[0];

    private static void Click(SchematicViewModel vm, double x, double y)
    {
        vm.OnPointerPressed(x, y, default);
        vm.OnPointerReleased(x, y);
    }

    [Fact]
    public void DeletingTheStretchPastTheT_KeepsTheSupplyOnTheBranch_AndUndoRestoresTheWire()
    {
        var (model, vm, wire) = Bench();
        Assert.Equal(TopNet(model, "V"), TopNet(model, "M1"));   // precondition: one net

        Click(vm, 600, -400);                                     // between M5's pin and M1
        var span = Assert.Single(vm.Selection.GetSelectedSpans(model)).Value;
        Assert.Equal((400.0, -400.0), span.A);                    // what is highlighted …
        Assert.Equal((800.0, -400.0), span.B);
        Assert.Equal(span, vm.Overlay.SelectedWireSpans.Single().Value);

        vm.DeleteSelection();                                     // … is what goes

        Assert.Equal(TopNet(model, "V"), TopNet(model, "M5"));   // the supply still reaches M5
        Assert.NotEqual(TopNet(model, "V"), TopNet(model, "M1")); // only the M5–M1 stretch is gone
        Assert.Contains(model.Wires, w => w.Points.SequenceEqual([(0.0, -200.0), (0.0, -400.0), (400.0, -400.0)]));
        Assert.Contains(model.Wires, w => w.Points.SequenceEqual([(800.0, -400.0), (800.0, -200.0)]));

        vm.UndoRedo.Undo();
        Assert.Same(wire, Assert.Single(model.Wires));
        Assert.Equal(TopNet(model, "V"), TopNet(model, "M1"));
    }

    [Fact]
    public void AWireEndingOnTheSegment_IsAJunctionToo_AndASegmentWithNoneIsStillDeletedWhole()
    {
        var (model, vm, _) = Bench();
        var stub = new EditableWire();
        stub.Points.AddRange([(200, -400), (200, -600)]);         // a T by wire, between V's corner and M5
        model.Wires.Add(stub);

        Click(vm, 100, -400);                                     // between the corner and the stub's T
        var span = Assert.Single(vm.Selection.GetSelectedSpans(model)).Value;
        Assert.Equal(((0.0, -400.0), (200.0, -400.0)), (span.A, span.B));
        vm.DeleteSelection();
        Assert.Equal(TopNet(model, "M5"), TopNet(model, "M1"));
        Assert.NotEqual(TopNet(model, "V"), TopNet(model, "M5"));

        Click(vm, 0, -300);                                       // V's riser: nothing joins it mid-way
        Assert.Empty(vm.Selection.GetSelectedSpans(model));
        Assert.True(vm.Selection.HasSelectedSegments);
    }
}
