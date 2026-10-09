using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Dragging parts WITH their wiring selected never leaves a wire end in the air (designer report, round 15).
///
/// The sheet, from the designer's recognised schematic: M14 (an inductor, not selected) stands above a row; M16's and
/// M15's top pins hang from that row — M16's at its start, M15's through a short tail wire whose top end taps the row.
/// Selecting M15, M16 and both wires and dragging them used to redraw the row (held at M14) as a bare L from its moved
/// start to M14's pin, while the tail moved rigidly — so the tail's top end came off the row. The net label the
/// designer's row carried kept the netlist right, which is why it read as "the trace slips".
///
/// Two oracles: every two pins on one net before are on one net after (the suite's), and no wire end is loose after
/// that was not loose before (the editor's own endpoint test). Part of <c>Suite=SchematicConnectivity</c>.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class DragSelectedWireKeepsTapsTests
{
    private static (SchematicEditModel M, EditableComponent M15, EditableComponent M16, EditableWire Row, EditableWire Tail) Sheet()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent { Symbol = SymbolKind.Inductor, X = 11300, Y = 5400, Rotation = SymbolRotation.R180, InstanceName = "M14" });
        var m15 = new EditableComponent { Symbol = SymbolKind.Capacitor, X = 10900, Y = 6200, InstanceName = "M15" };
        var m16 = new EditableComponent { Symbol = SymbolKind.Capacitor, X = 10200, Y = 6200, InstanceName = "M16" };
        m.Components.AddRange([m15, m16]);
        var row = new EditableWire();
        row.Points.AddRange([(10200.0, 6000.0), (10200.0, 5700.0), (11300.0, 5700.0), (11300.0, 5600.0)]);
        var tail = new EditableWire();
        tail.Points.AddRange([(10900.0, 5700.0), (10900.0, 6000.0)]);
        m.Wires.Add(row);
        m.Wires.Add(tail);
        return (m, m15, m16, row, tail);
    }

    private static Dictionary<string, string> Nets(SchematicEditModel m)
    {
        var nets = new Dictionary<string, string>();
        foreach (var inst in NetExtractor.Extract(m).TestBench.Instances)
            for (int p = 0; p < inst.NetBindings.Count; p++) nets[$"{inst.InstanceName}.{p}"] = inst.NetBindings[p];
        return nets;
    }

    private static int LooseEnds(SchematicEditModel m)
    {
        var live = m.ComputeLiveConnectivity();
        return m.Wires.Sum(w => (live.IsWireEndpointConnected(w.Points[0].X, w.Points[0].Y) ? 0 : 1)
                              + (live.IsWireEndpointConnected(w.Points[^1].X, w.Points[^1].Y) ? 0 : 1));
    }

    [Theory]
    [InlineData(0, -200, true)]
    [InlineData(0, 200, true)]
    [InlineData(300, 0, true)]
    [InlineData(-300, -100, true)]
    [InlineData(300, 0, false)]
    [InlineData(0, -200, false)]
    public void DraggingTheCapacitors_KeepsEveryPinOnItsNetAndNoWireEndLoose(double dx, double dy, bool withWires)
    {
        var (m, m15, m16, row, tail) = Sheet();
        var before = Nets(m);
        int looseBefore = LooseEnds(m);
        var vm = new SchematicViewModel(m);
        vm.Selection.SetAll(withWires ? [m15.Id, m16.Id, row.Id, tail.Id] : [m15.Id, m16.Id]);
        vm.SimulateDragCommit(dx, dy);

        var after = Nets(m);
        foreach (var group in before.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
            Assert.Single(group.Select(kv => after[kv.Key]).Distinct());
        Assert.Equal(looseBefore, LooseEnds(m));
    }

    /// <summary>The held row keeps its bends: only the leg at M14's pin gives.</summary>
    [Fact]
    public void TheHeldRow_MovesWithTheSelection_AndOnlyItsLastLegGives()
    {
        var (m, m15, m16, row, tail) = Sheet();
        var vm = new SchematicViewModel(m);
        vm.Selection.SetAll([m15.Id, m16.Id, row.Id, tail.Id]);
        vm.SimulateDragCommit(300, 0);
        Assert.Equal([(10500.0, 6000.0), (10500.0, 5700.0), (11300.0, 5700.0), (11300.0, 5600.0)], row.Points);
    }

    /// <summary>The wire the user watches during the drag is the wire they get when they let go.</summary>
    [Fact]
    public void LivePreview_MatchesTheCommit()
    {
        var (live, l15, l16, lRow, lTail) = Sheet();
        var liveVm = new SchematicViewModel(live);
        liveVm.Selection.SetAll([l15.Id, l16.Id, lRow.Id, lTail.Id]);
        liveVm.SimulateDragTo(0, -200);
        var watched = lRow.Points.ToList();
        liveVm.SimulateDragAbandon();

        var (done, d15, d16, dRow, dTail) = Sheet();
        var doneVm = new SchematicViewModel(done);
        doneVm.Selection.SetAll([d15.Id, d16.Id, dRow.Id, dTail.Id]);
        doneVm.SimulateDragCommit(0, -200);
        Assert.Equal(dRow.Points, watched);
    }
}
