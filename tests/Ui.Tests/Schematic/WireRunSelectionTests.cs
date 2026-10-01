using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Alt + double-click on a wire selects its run up to the component pins (owner request, 2026-10-01);
/// a plain double-click still opens the net-label editor. The run follows corners, T-junctions and
/// wires meeting at a pin, in every direction; a pin never blocks it, and the wiring past a part is
/// another run.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class WireRunSelectionTests
{
    // Three resistors; each top pin (port 0) is 200 above its centre.
    //   V  at (0,0)       → top pin (0,-200)
    //   M5 at (400,-200)  → top pin (400,-400), on the wire's INTERIOR
    //   M1 at (800,0)     → top pin (800,-200)
    // One wire V → up → across (through M5's pin) → down to M1.
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

    /// <summary>bad_drag.csch: Alt + double-click beside C1 selects the wire all the way to L1, past
    /// R1's pin tapping the row (owner, 2026-10-01).</summary>
    [Fact]
    public void BadDrag_BesideC1_SelectsTheWireThroughR1sTapToL1()
    {
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Capacitor, X = -1400, Y = 100, InstanceName = "C1" });
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor,  X = -700,  Y = 100, InstanceName = "R1" });
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Inductor,  X = 0,     Y = 300, InstanceName = "L1" });
        var wire = new EditableWire();
        wire.Points.AddRange([(-1400.0, -100.0), (0.0, -100.0), (0.0, 100.0)]);
        model.Wires.Add(wire);
        var vm = new SchematicViewModel(model);

        Assert.True(vm.SelectWireRun(wire.Id, -1300, -100));

        Assert.Equal([(wire.Id, 0), (wire.Id, 1)], vm.Selection.GetSelectedSegments(model).OrderBy(s => s.SegmentIndex));
        Assert.Empty(vm.Selection.GetSelectedSpans(model));                 // both segments whole
    }

    [Theory]
    [InlineData(600, -400)]   // past M5's tapping pin
    [InlineData(0, -300)]     // on V's riser
    public void APinTappingTheWire_DoesNotCutTheRun(double x, double y)
    {
        var (model, vm, wire) = Bench();
        Assert.True(vm.SelectWireRun(wire.Id, x, y));

        Assert.Equal([(wire.Id, 0), (wire.Id, 1), (wire.Id, 2)], vm.Selection.GetSelectedSegments(model).OrderBy(s => s.SegmentIndex));
        Assert.Empty(vm.Selection.GetSelectedSpans(model));
    }

    /// <summary>
    /// PowerAmplifier.csch (owner, 2026-10-01): three wires end on C3's top pin — the output run to the
    /// current probe, the riser to the drain choke, and the stub to the FET's drain, where a fourth wire
    /// continues up to a capacitor. Alt + double-click on any of them selects all four, in every direction;
    /// the wiring past a PART (here the probe stand-in) is another run.
    /// </summary>
    [Fact]
    public void WiresMeetingAtAPin_AreOneRun_AndAPartEndsIt()
    {
        var model = new SchematicEditModel { GridSnap = false };
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Capacitor, X = 300, Y = -100,  InstanceName = "C3" });    // top pin (300,-300)
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor,  X = 200, Y = -100,  InstanceName = "Drain" }); // top pin (200,-300)
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor,  X = 300, Y = -1000, InstanceName = "L2" });    // bottom pin (300,-800)
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor,  X = 800, Y = -100,  InstanceName = "Probe" }); // top pin (800,-300)
        EditableWire W(params (double, double)[] pts) { var w = new EditableWire(); w.Points.AddRange(pts); model.Wires.Add(w); return w; }
        var output = W((300, -300), (800, -300));
        var riser  = W((300, -800), (300, -300));
        var stub   = W((300, -300), (200, -300));
        var up     = W((200, -300), (200, -1400), (100, -1400));
        var beyond = W((800, 100), (1200, 100));                            // past the probe's other pin
        var vm = new SchematicViewModel(model);

        foreach (var (clicked, x, y) in new[] { (riser, 300.0, -500.0), (output, 500.0, -300.0), (up, 150.0, -1400.0) })
        {
            Assert.True(vm.SelectWireRun(clicked.Id, x, y));
            var selected = vm.Selection.GetSelectedSegments(model).Select(s => s.WireId).ToHashSet();
            Assert.Equal(new[] { output.Id, riser.Id, stub.Id, up.Id }.ToHashSet(), selected);
            Assert.DoesNotContain(beyond.Id, selected);
        }
    }

    [Fact]
    public void ATeeBetweenWires_IsFollowed_AndACrossingWithNoVertexIsNot()
    {
        var (model, vm, wire) = Bench();
        model.Components.RemoveAll(c => c.InstanceName == "M5");           // no pin on the row now
        var branch = new EditableWire();
        branch.Points.AddRange([(400, -400), (400, -100)]);                // T onto the row, free end
        model.Wires.Add(branch);
        var crossing = new EditableWire();
        crossing.Points.AddRange([(-200, -300), (200, -300)]);             // crosses V's riser, no vertex there
        model.Wires.Add(crossing);

        Assert.True(vm.SelectWireRun(wire.Id, 600, -400));

        var selected = vm.Selection.GetSelectedSegments(model);
        Assert.Equal(3, selected.Count(s => s.WireId == wire.Id));
        Assert.Contains((branch.Id, 0), selected);
        Assert.DoesNotContain(selected, s => s.WireId == crossing.Id);
        Assert.Empty(vm.Selection.GetSelectedSpans(model));                 // every segment whole
    }

    [Fact]
    public void DeletingTheRun_RemovesItAndLeavesOtherWiresAlone()
    {
        var (model, vm, wire) = Bench();
        var other = new EditableWire();
        other.Points.AddRange([(800, 200), (1200, 200)]);                   // past M1, on its bottom pin
        model.Wires.Add(other);

        vm.SelectWireRun(wire.Id, 600, -400);
        vm.DeleteSelection();

        Assert.DoesNotContain(model.Wires, w => w.Id == wire.Id);
        Assert.Contains(model.Wires, w => w.Points.SequenceEqual([(800.0, 200.0), (1200.0, 200.0)]));
        Assert.NotEqual(TopNet(model, "V"), TopNet(model, "M1"));
    }

    [Fact]
    public void ANameThatIsNoWire_SelectsNothing()
    {
        var (_, vm, _) = Bench();
        Assert.False(vm.SelectWireRun("no-such-wire", 0, 0));
        Assert.True(vm.Selection.IsEmpty);
    }
}
