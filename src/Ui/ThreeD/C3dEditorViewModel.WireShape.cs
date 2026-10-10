// 3D editor round 4 — a drawn bond wire's loop height and span, typed in the Properties Inspector.
//
// THE SAME EDITS AS wBond AND THE LAYOUT EDITOR. Both are wBond's own primitives on the wire's axis: a loop height scales
// every point's rise above the foot-to-foot chord and keeps every x and y (WireEdits.SetLoopHeightPreservingPath); a span
// moves the END foot along the chord in plan, the start pinned, each interior point keeping its place along the chord and
// its height above it (WireEdits.ScaleSpan). One undo entry each.
//
// THE LOOP HEIGHT IS THE 3D ONE (em-3d.md §6.6, decided): the top of the lower pad to the top of the wire at its apex —
// the number the Wire tool takes and reports — not wBond's axis max z − min z. The axis is scaled, the resolved solid
// measured, and the axis target corrected by the difference, exactly as the Wire tool's arch is (C3dWires.ForAssemblyHeight).
//
// A SPAN MOVES AN END, so its foot is re-seated as a Vertex-mode drag's is: an end moved to where no pad is under it is
// refused with the drag's own sentence.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.WBond;
using WPoint3 = CircuitRF.WBond.Point3;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A wire's foot-to-foot distance in plan, DBU.</summary>
    public static long WireSpanDbu(C3dWire w)
    {
        if (w.Points.Count < 2) return 0;
        double dx = w.Points[^1].X - w.Points[0].X, dy = w.Points[^1].Y - w.Points[0].Y;
        return (long)Math.Round(Math.Sqrt(dx * dx + dy * dy), MidpointRounding.AwayFromZero);
    }

    /// <summary>A wire's assembly loop height, DBU — null when it lands on no pad, so there is no pad top to measure from.</summary>
    public long? WireLoopHeightDbu(C3dWire w)
        => MeasureAssembly(w) is { } m ? (long)Math.Round(m, MidpointRounding.AwayFromZero) : null;

    /// <summary>The loop height typed in the display unit (a suffix is honoured). Null on success, else why not.</summary>
    public string? SetWireLoopHeight(int index, string text)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        var d = C3dDimension.Parse(text, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) return d.Why ?? "A loop height is a positive length.";
        if (LoopHeightRefusal(was) is { } bound) return bound;
        long target = d.Dbu;
        if (MeasureAssembly(was) is not { } now)
            return $"{was.Name} lands on no pad, so its loop height — from the pad's top to the top of the wire — has nothing to be " +
                   "measured from. Re-Seat Wire Ends, or move its ends onto pads.";
        if (Math.Abs(now - target) <= 0.5) return null;

        long footDrop = Math.Abs(was.Points[^1].Z - was.Points[0].Z);
        double axis = AxisHeight(was) + (target - now);
        List<C3dPoint3>? best = null;
        double bestMiss = double.PositiveInfinity;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            long axisTarget = (long)Math.Round(axis, MidpointRounding.AwayFromZero);
            if (axisTarget <= footDrop)
                return $"{Length(target)} is below what {was.Name} can stand at: its feet are {Length(footDrop)} apart in height, " +
                       "and a straight wire already measures that much above the lower pad.";
            var wire = ToWBond(was);
            if (!WireEdits.SetLoopHeightPreservingPath(wire, axisTarget))
                return $"{was.Name} is straight: it has no rise above its feet to scale. Move a point of it up (Vertex mode) first.";
            var candidate = WithPoints(was, wire);
            if (MeasureAssembly(candidate) is not { } got) break;
            if (Math.Abs(got - target) < bestMiss) (best, bestMiss) = (candidate.Points, Math.Abs(got - target));
            if (bestMiss <= 0.5) break;
            double next = axis + (target - got);
            axis = Math.Abs(next - axis) < 0.5 ? axis + Math.Sign(target - got) : next;
        }
        if (best is null) return $"{was.Name} would no longer resolve at that height.";
        string before = C3dPersistence.SerializeObject(was);
        var after = (C3dWire)C3dPersistence.DeserializeObject(before);
        after.Points = best;
        if (!Push(new C3dEdit($"Loop height of {was.Name}", [new C3dEditSlot(false, index, before, C3dPersistence.SerializeObject(after))], ApplySlots)))
            return StatusMessage;
        StatusMessage = $"'{was.Name}' stands {Length(target)} above its lower pad.";
        return null;
    }

    /// <summary>The span typed in the display unit: the end foot moves along the chord in plan, the start stays. The end
    /// is re-seated on the pad under it, and refused where there is none. Null on success, else why not.</summary>
    public string? SetWireSpan(int index, string text)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        var d = C3dDimension.Parse(text, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) return d.Why ?? "A span is a positive length.";
        long now = WireSpanDbu(was);
        if (now == d.Dbu) return null;
        if (now <= 0) return $"{was.Name}'s feet are one above the other: there is no plan direction to lengthen it along.";
        var wire = ToWBond(was);
        WireEdits.ScaleSpan(wire, (double)d.Dbu / now, moveOutputFoot: true);
        var moved = WithPoints(was, wire);
        C3dWires.OffsetBoundPoints(was, moved, Document.DisplayUnit, Document.DbuPerMicron);   // brief-em3d-133: D2
        if (SeatEditedWire(was, ref moved) is { } refusal) return refusal;
        string before = C3dPersistence.SerializeObject(was);
        if (!Push(new C3dEdit($"Span of {was.Name}", [new C3dEditSlot(false, index, before, C3dPersistence.SerializeObject(moved))], ApplySlots)))
            return StatusMessage;
        StatusMessage = $"'{was.Name}' spans {Length(d.Dbu)}; its end moved, its start stayed.";
        return null;
    }

    /// <summary>
    /// brief-em3d-133 R-em3d133-4 — Loop height rewrites every interior point's z, so it is refused on a wire holding any
    /// point expression, naming the first, except one whose only expressions are its ends' z: nothing it changes is bound,
    /// and the ends stay on their pads. The drag rule is not used here: it would rewrite a variable other geometry may use.
    /// </summary>
    private static string? LoopHeightRefusal(C3dWire w)
    {
        int last = w.Points.Count - 1;
        var first = C3dBindings.BoundOf(w.Name, w)
                               .FirstOrDefault(f => f.Spec.Element is { } k && !(f.Component == 2 && (k == 0 || k == last)));
        return first is null ? null
            : $"'{w.Name}' {C3dBindings.Label(first.Spec, first.Component, first.Path)} holds {first.Expr.Expr}: Loop height rewrites " +
              "every point between the ends, so it cannot keep an expression there. Replace it with a number, or edit the expression.";
    }

    private static long AxisHeight(C3dWire w) => w.Points.Count == 0 ? 0 : w.Points.Max(p => p.Z) - w.Points.Min(p => p.Z);

    /// <summary>The wire's axis as a wBond wire, in DBU — wBond's edits are unit-free arithmetic on whole numbers.</summary>
    private static Wire ToWBond(C3dWire w)
    {
        var wire = new Wire();
        foreach (var p in w.Points) wire.Points.Add(new WPoint3(p.X, p.Y, p.Z));
        return wire;
    }

    private static C3dWire WithPoints(C3dWire w, Wire shaped)
    {
        var copy = (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));
        copy.Points = [.. shaped.Points.Select(p => new C3dPoint3(p.X, p.Y, p.Z))];
        return copy;
    }
}
