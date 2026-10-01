using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A VProbe comes OFF the wire when it is dragged on its own, and stays on it when the wire is
/// dragged with it (owner, 2026-09-06).
///
/// <para>Every other component in the editor owes the user the opposite guarantee — moving the
/// picture never re-wires the circuit — and the drag path enforces it in five separate places (the
/// live preview's port map, the commit's, the pin-on-pin auto-wire in both directions, the
/// stationary-pin tiebreak, and the wire-slide clamp). A probe stamps nothing, so none of those
/// reasons apply to it, and being made to obey them would deform the schematic every time somebody
/// moved a probe to look somewhere else. These tests pin BOTH halves: the probe detaches, and
/// nothing else does.</para>
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class VProbeDragDetachTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────

    private static EditableComponent Resistor(string name, double cx, double cy)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = cx, Y = cy };

    /// <summary>A VProbe whose single pin lands at (pinX, pinY) — its pin is at (-100, +100)
    /// from the body origin.</summary>
    private static EditableComponent Probe(string name, double pinX, double pinY)
        => new() { InstanceName = name, Symbol = SymbolKind.VProbe, X = pinX + 100, Y = pinY - 100 };

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static (double X, double Y) PinOf(SchematicEditModel m, EditableComponent c)
    {
        var def = m.PortDefsOf(c)[0];
        return m.PortWorldOf(c, def);
    }

    private static (double X, double Y)[] Pts(EditableWire w) => w.Points.ToArray();

    /// <summary>Whether the probe reaches the circuit at all, asked of extraction rather than of
    /// geometry — the only answer that matters, and the one the Messages panel reports.</summary>
    private static bool ProbeReportsANet(SchematicEditModel m, string probeName)
    {
        var r = NetExtractor.Extract(m);
        return r.TestBench.Instances.Any(i => i.InstanceName == probeName);
    }

    /// <summary>A resistor from (0,-200) to (0,200), a wire on down to (0,600), and a probe
    /// tapping the wire's far endpoint.</summary>
    private static (SchematicEditModel M, EditableComponent R, EditableWire W, EditableComponent P)
        ProbedRun()
    {
        var m = new SchematicEditModel();
        var r = Resistor("R1", 0, 0);
        var w = Wire((0, 200), (0, 600));
        var p = Probe("Vtap", 0, 600);
        m.Components.Add(r);
        m.Wires.Add(w);
        m.Components.Add(p);
        return (m, r, w, p);
    }

    // ── dragged alone: it comes off ───────────────────────────────────────────

    [Fact]
    public void DraggedOnItsOwn_TheProbeMoves_AndTheWireDoesNotFollowIt()
    {
        var (m, _, w, p) = ProbedRun();
        var before = Pts(w);
        Assert.True(ProbeReportsANet(m, "Vtap"));

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(p.Id);
        vm.SimulateDragCommit(dx: 400, dy: 0);

        Assert.Equal(before, Pts(w));                       // the drawing is untouched
        Assert.Equal((400.0, 600.0), PinOf(m, p));          // the probe went where it was put
        Assert.False(ProbeReportsANet(m, "Vtap"));          // and it is off the net
    }

    [Fact]
    public void DraggedOffAWiresBody_ItGrowsNoStubBackToTheWire()
    {
        // The mid-span tap — the case BuildTapStubs exists for. Any other component leaves a stub
        // behind so the contact survives; a probe must leave nothing at all.
        var (m, _, w, p) = ProbedRun();
        int wiresBefore = m.Wires.Count;
        var mid = Probe("Vmid", 0, 400);
        m.Components.Add(mid);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(mid.Id);
        vm.SimulateDragCommit(dx: 400, dy: 0);

        Assert.Equal(wiresBefore, m.Wires.Count);
        Assert.Equal([(0.0, 200.0), (0.0, 600.0)], Pts(w));
    }

    [Fact]
    public void DraggedOffAnotherComponentsPin_NoAutoWireIsGrown()
    {
        // Pin-on-pin with no wire between: separating any other pair auto-forms a wire to hold the
        // contact. A probe resting directly on a resistor lead must simply come off it.
        var m = new SchematicEditModel();
        var r = Resistor("R1", 0, 0);                  // port 1 at (0,-200)
        var p = Probe("Vtap", 0, -200);
        m.Components.Add(r);
        m.Components.Add(p);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(p.Id);
        vm.SimulateDragCommit(dx: 400, dy: 0);

        Assert.Empty(m.Wires);
        Assert.Equal((400.0, -200.0), PinOf(m, p));
    }

    // ── dragged together: it stays on ─────────────────────────────────────────

    [Fact]
    public void DraggedTogetherWithItsWire_TheProbeStaysOnTheNet()
    {
        // Nothing in the follow machinery is involved here: both move by the same delta, so the pin
        // is still on the wire when the gesture ends. This is the half the exclusion must not break.
        var (m, _, w, p) = ProbedRun();

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(p.Id);
        vm.Selection.Add(w.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);

        Assert.True(ProbeReportsANet(m, "Vtap"));
        Assert.Equal(PinOf(m, p), w.Points[^1]);
    }

    // ── it holds nothing back ─────────────────────────────────────────────────

    [Fact]
    public void AWireDraggedOutFromUnderAStationaryProbe_IsNotHeldBackByIt()
    {
        // The slide clamp: a stationary tap on a dragged wire's BODY normally pins the wire's
        // perpendicular delta so the tap stays on it. A probe must not bound the drag at all.
        var m = new SchematicEditModel();
        m.Wires.Add(Wire((0, 0), (0, 600)));
        var p = Probe("Vtap", 0, 300);                 // resting on the wire's mid-span
        m.Components.Add(p);
        var w = m.Wires[0];

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(w.Id);
        vm.SimulateDragCommit(dx: 400, dy: 0);

        Assert.Equal([(400.0, 0.0), (400.0, 600.0)], Pts(w));   // moved the full delta
        Assert.Equal((0.0, 300.0), PinOf(m, p));                // the probe stayed put
    }

    [Fact]
    public void AComponentDraggedAwayTakesItsWire_EvenThoughAProbeSharesThePoint()
    {
        // The stationary-pin tiebreak: a wire endpoint held by BOTH a moving pin and a stationary
        // pin stays with the stationary one. A probe must lose that tiebreak — otherwise dropping a
        // probe on a lead would quietly freeze every later drag of the part it is watching.
        var m = new SchematicEditModel();
        var r = Resistor("R1", 0, 0);                  // port 2 at (0, 200)
        var w = Wire((0, 200), (600, 200));
        var p = Probe("Vtap", 0, 200);                 // sharing the resistor's own pin
        m.Components.Add(r);
        m.Wires.Add(w);
        m.Components.Add(p);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(r.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);

        Assert.Equal((0.0, 500.0), w.Points[0]);       // the wire followed the resistor
        Assert.Equal((0.0, 200.0), PinOf(m, p));       // the probe stayed where it was
    }

    // ── rotate and mirror, the same rule ──────────────────────────────────────

    [Fact]
    public void RotatedOnItsOwn_TheProbeDoesNotDragTheWireRoundWithIt()
    {
        var (m, _, w, p) = ProbedRun();
        var before = Pts(w);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(p.Id);
        vm.RotateSelection();

        Assert.Equal(before, Pts(w));
    }

    // ── the picture tells the truth WHILE the drag is happening ───────────────

    /// <summary>
    /// Mid-drag, before the button is released, the probe's pin must already read unconnected and so
    /// must the wire endpoint it has left.
    ///
    /// <para>Connectivity is deferred to drag-end for everything else and that is right for
    /// everything else — a component takes its wires with it, so the deferred answer never goes
    /// stale. A probe comes off, and until this was fixed both it and the wire it had left kept
    /// drawing as connected for the whole gesture, at exactly the moment the user is deciding where
    /// to drop it (owner, 2026-09-06).</para>
    /// </summary>
    [Fact]
    public void MidDrag_ThePinAndTheWireEndAlreadyReadUnconnected()
    {
        var (m, _, w, p) = ProbedRun();
        var vm = new SchematicViewModel(m);

        // Before the gesture: both ends of the contact read connected.
        var rest = vm.Overlay;
        Assert.Null(rest.LivePortConnected);            // nothing to override at rest
        Assert.True(m.BuildRenderModel().Model.Wires.Single().EndConnected);

        vm.Selection.SelectOne(p.Id);
        vm.SimulateDragTo(dx: 400, dy: 0);

        var live = vm.Overlay;
        Assert.NotNull(live.LivePortConnected);
        Assert.NotNull(live.LiveWireEndpointConnected);

        var pin = PinOf(m, p);
        Assert.False(live.LivePortConnected!(pin.X, pin.Y),
            "the probe's pin is in empty space and must read unconnected before the button is released");
        Assert.False(live.LiveWireEndpointConnected!(w.Points[^1].X, w.Points[^1].Y),
            "the wire end the probe left is holding nothing and must say so");

        vm.SimulateDragAbandon();
    }

    [Fact]
    public void MidDrag_AProbeStillOnItsWire_StillReadsConnected()
    {
        // The other half: the live test must not simply report everything as loose. Dragging the
        // probe ALONG the wire keeps it on the run, and the pin stays green throughout.
        var (m, _, w, p) = ProbedRun();
        var vm = new SchematicViewModel(m);

        vm.Selection.SelectOne(p.Id);
        vm.SimulateDragTo(dx: 0, dy: -200);            // (0,600) -> (0,400), still on the run

        var pin = PinOf(m, p);
        Assert.Equal((0.0, 400.0), pin);
        Assert.True(vm.Overlay.LivePortConnected!(pin.X, pin.Y),
            "the probe is on the wire's BODY now, which is still a connection");

        // And the endpoint it slid off is genuinely holding nothing any more, which is the same
        // live answer read from the other side.
        Assert.False(vm.Overlay.LiveWireEndpointConnected!(w.Points[^1].X, w.Points[^1].Y));

        vm.SimulateDragAbandon();
    }

    [Fact]
    public void MidDrag_ASeparatingPinOnPinPair_DoesNotFlashUnconnected()
    {
        // A pin-on-pin contact that separates gets an auto-wire AT COMMIT, so during the drag the
        // route exists only as a preview. Asking the model alone would call both ends loose and
        // every such drag would flash red for its whole length — so the preview counts.
        var m = new SchematicEditModel();
        var a = Resistor("R1", 0, 0);                  // port 2 at (0, 200)
        var b = Resistor("R2", 0, 400);                // port 1 at (0, 200) — pin on pin
        m.Components.Add(a);
        m.Components.Add(b);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(b.Id);
        vm.SimulateDragTo(dx: 400, dy: 0);

        Assert.NotNull(vm.Overlay.PinOnPinPreviewWires);
        var moved = m.PortWorldOf(b, m.PortDefsOf(b)[0]);
        Assert.True(vm.Overlay.LivePortConnected!(moved.X, moved.Y),
            "the pin the commit is about to wire up must not read unconnected mid-drag");

        vm.SimulateDragAbandon();
    }

    // ── and nothing else behaves this way ─────────────────────────────────────

    [Fact]
    public void EveryOtherKindStillCarriesItsWires()
    {
        var (m, r, w, _) = ProbedRun();

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(r.Id);
        vm.SimulateDragCommit(dx: 400, dy: 0);

        Assert.Equal((400.0, 200.0), w.Points[0]);     // the resistor took its wire with it
    }

    [Fact]
    public void TheExemptionIsOneKind_AndItIsTheProbe()
    {
        var free = System.Enum.GetValues<SymbolKind>()
            .Where(ComponentTypeRegistry.DetachesFreely)
            .ToArray();

        Assert.Equal([SymbolKind.VProbe], free);
    }
}
