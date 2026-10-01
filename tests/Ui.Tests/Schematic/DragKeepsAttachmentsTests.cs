using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Dragging a part never detaches another part (owner report, bad_drag.csch, 2026-10-01).
///
/// The sheet: one wire runs C1's top pin → along a row → down to L1's top pin, and R1's top pin taps
/// that row mid-span. Dragging C1 down used to carry the whole row with C1's end (the follow rule
/// handed the vertical delta to the row's far corner), leaving R1's pin in the air.
///
/// The oracle is the netlist, not the drawing: every two pins on one net before a drag are on one
/// net after it. Part of the <c>Suite=SchematicConnectivity</c> set — run it with
/// <c>dotnet test tests/Ui.Tests --filter Suite=SchematicConnectivity</c>.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class DragKeepsAttachmentsTests
{
    private static EditableComponent Comp(SymbolKind kind, double x, double y, string name)
        => new() { Symbol = kind, X = x, Y = y, InstanceName = name };

    /// <summary>bad_drag.csch, rebuilt: C1 at (-1400,100), R1 at (-700,100), L1 at (0,300); pins sit
    /// 200 above and below each centre.</summary>
    private static (SchematicEditModel Model, EditableComponent C1, EditableComponent R1, EditableComponent L1, EditableWire Wire)
        BadDrag(bool withR1 = true)
    {
        var m = new SchematicEditModel();
        var c1 = Comp(SymbolKind.Capacitor, -1400, 100, "C1");
        var r1 = Comp(SymbolKind.Resistor,   -700, 100, "R1");
        var l1 = Comp(SymbolKind.Inductor,      0, 300, "L1");
        m.Components.Add(c1);
        if (withR1) m.Components.Add(r1);
        m.Components.Add(l1);
        var w = new EditableWire();
        w.Points.AddRange([(-1400.0, -100.0), (0.0, -100.0), (0.0, 100.0)]);
        m.Wires.Add(w);
        return (m, c1, r1, l1, w);
    }

    /// <summary>Each pin's net, keyed "inst.port".</summary>
    private static Dictionary<string, string> Nets(SchematicEditModel m)
    {
        var tb = NetExtractor.Extract(m).TestBench;
        var nets = new Dictionary<string, string>();
        foreach (var inst in tb.Instances)
            for (int p = 0; p < inst.NetBindings.Count; p++)
                nets[$"{inst.InstanceName}.{p}"] = inst.NetBindings[p];
        return nets;
    }

    /// <summary>Every pair of pins sharing a net in <paramref name="before"/> still shares one in <paramref name="after"/>.</summary>
    private static void AssertNoPinDetached(Dictionary<string, string> before, Dictionary<string, string> after, string what)
    {
        foreach (var group in before.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
        {
            var pins = group.Select(kv => kv.Key).ToList();
            var netsAfter = pins.Select(p => after[p]).Distinct().ToList();
            Assert.True(netsAfter.Count == 1,
                $"{what}: pins {string.Join(", ", pins)} shared a net before the drag and are on {netsAfter.Count} nets after it.");
        }
    }

    private static string Fmt(IEnumerable<(double X, double Y)> pts) => string.Join(" ", pts.Select(p => $"({p.X},{p.Y})"));

    /// <summary>The reported bug, and its mirror: C1 dragged straight down or up keeps R1 (and L1) on its net.</summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(0, 200)]
    [InlineData(0, -200)]
    [InlineData(-300, 200)]   // down and away from R1: the row lengthens
    [InlineData(400, 200)]    // down and TOWARD R1, still short of its pin
    [InlineData(1000, 200)]   // down and PAST R1's pin: the row would be trimmed past the tap
    [InlineData(1000, 0)]     // along the row, past R1's pin
    public void DraggingC1_KeepsEveryPinOnItsNet(double dx, double dy)
    {
        var (m, c1, _, _, w) = BadDrag();
        var before = Nets(m);
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(c1.Id);
        vm.SimulateDragCommit(dx, dy);
        AssertNoPinDetached(before, Nets(m), $"C1 by ({dx},{dy}), wire {Fmt(w.Points)}");
    }

    /// <summary>The far end too: L1 dragged left past R1's column would trim the row back past the tap.</summary>
    [Theory]
    [InlineData(-800, 0)]
    [InlineData(-800, 200)]
    [InlineData(300, 0)]
    [InlineData(0, 200)]
    public void DraggingL1_KeepsEveryPinOnItsNet(double dx, double dy)
    {
        var (m, _, _, l1, w) = BadDrag();
        var before = Nets(m);
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(l1.Id);
        vm.SimulateDragCommit(dx, dy);
        AssertNoPinDetached(before, Nets(m), $"L1 by ({dx},{dy}), wire {Fmt(w.Points)}");
    }

    /// <summary>The fix keeps the row: C1 down grows a jog at C1's end and the row R1 hangs from stays put.</summary>
    [Fact]
    public void DraggingC1Down_LeavesTheTappedRowWhereItWas()
    {
        var (m, c1, _, _, w) = BadDrag();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(c1.Id);
        vm.SimulateDragCommit(0, 200);
        Assert.Equal([(-1400.0, 100.0), (-1400.0, -100.0), (0.0, -100.0), (0.0, 100.0)], w.Points);
    }

    /// <summary>
    /// Nothing tapping the row: the compact rule is unchanged — the row moves with C1 rather than
    /// growing a jog nobody needs.
    /// </summary>
    [Fact]
    public void WithNothingOnTheRow_TheRowStillMovesWithC1()
    {
        var (m, c1, _, _, w) = BadDrag(withR1: false);
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(c1.Id);
        vm.SimulateDragCommit(0, 200);
        Assert.Equal([(-1400.0, 100.0), (0.0, 100.0)], w.Points);
    }

    /// <summary>The wire the user watches during the drag is the wire they get when they let go.</summary>
    [Fact]
    public void LivePreview_MatchesTheCommit()
    {
        var (live, liveC1, _, _, liveW) = BadDrag();
        var liveVm = new SchematicViewModel(live);
        liveVm.Selection.SelectOne(liveC1.Id);
        liveVm.SimulateDragTo(0, 200);
        var watched = liveW.Points.ToList();
        liveVm.SimulateDragAbandon();

        var (done, doneC1, _, _, doneW) = BadDrag();
        var doneVm = new SchematicViewModel(done);
        doneVm.Selection.SelectOne(doneC1.Id);
        doneVm.SimulateDragCommit(0, 200);

        Assert.Equal(doneW.Points, watched);
    }

    /// <summary>Undo puts the wire back exactly as drawn.</summary>
    [Fact]
    public void Undo_RestoresTheOriginalWire()
    {
        var (m, c1, _, _, w) = BadDrag();
        var original = w.Points.ToList();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(c1.Id);
        vm.SimulateDragCommit(0, 200);
        vm.UndoRedo.Undo();
        Assert.Equal(original, m.Wires.Single().Points);
    }
}
