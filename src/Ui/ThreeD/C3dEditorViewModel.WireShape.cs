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
//
// brief-em3d-135 — EITHER MAY BE HELD BY AN EXPRESSION. A typed expression (h_loop, 2*pitch) is bound to the wire's own
// LoopHeight or Span field, and the arch is shaped to it now and again whenever the document resolves (C3dWires.Hold), so
// changing the variable changes the wire. A typed number is the one-shot edit it always was, and lets go of a held value.
// An EMPTY field lets go and keeps the wire exactly as it is now: the held value simply becomes the shape's own number.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A wire's foot-to-foot distance in plan, DBU.</summary>
    public static long WireSpanDbu(C3dWire w) => C3dWires.SpanDbu(w);

    /// <summary>A wire's assembly loop height, DBU — null when it lands on no pad, so there is no pad top to measure from.</summary>
    public long? WireLoopHeightDbu(C3dWire w)
        => MeasureAssembly(w) is { } m ? (long)Math.Round(m, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// The loop height typed in the display unit (a suffix is honoured): a number sets it once, an expression holds it
    /// (brief-em3d-135), and an empty text lets go of a held one and keeps the wire as it is. Null on success, else why not.
    /// </summary>
    public string? SetWireLoopHeight(int index, string text)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        if (HeldEdit(index, was, nameof(C3dWire.LoopHeight), text, "loop height", out var held) is { } done) return done.Refusal;
        long target;
        if (held is { } typed) target = typed.Dbu;
        else
        {
            var d = C3dDimension.Parse(text, Document.DisplayUnit, Document.DbuPerMicron);
            if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) return d.Why ?? "A loop height is a positive length.";
            target = d.Dbu;
        }
        var after = Copy(was);
        if (held is { } h) Hold(after, nameof(C3dWire.LoopHeight), h.Expr, target);
        else Release(after, nameof(C3dWire.LoopHeight));
        // brief-em3d-133 R-em3d133-4 for a number; a held loop height refuses only what it would overwrite (an interior z).
        if ((held is null ? LoopHeightRefusal(was) : C3dWires.HeldConflict(after, was.Name)) is { } bound) return bound;
        var (outcome, points, footDrop) = C3dWires.FitLoopHeight(after, target, MeasureAssembly);
        switch (outcome)
        {
            case C3dWires.LoopFit.NoMeasure when MeasureAssembly(was) is null:
                return $"{was.Name} lands on no pad, so its loop height — from the pad's top to the top of the wire — has nothing to be " +
                       "measured from. Re-Seat Wire Ends, or move its ends onto pads.";
            case C3dWires.LoopFit.NoMeasure: return $"{was.Name} would no longer resolve at that height.";
            case C3dWires.LoopFit.BelowFeet:
                return $"{Length(target)} is below what {was.Name} can stand at: its feet are {Length(footDrop)} apart in height, " +
                       "and a straight wire already measures that much above the lower pad.";
            case C3dWires.LoopFit.Straight:
                return $"{was.Name} is straight: it has no rise above its feet to scale. Move a point of it up (Vertex mode) first.";
        }
        after.Points = points!;
        string before = C3dPersistence.SerializeObject(was), next = C3dPersistence.SerializeObject(after);
        if (next == before) return null;
        if (!Push(new C3dEdit($"Loop height of {was.Name}", [new C3dEditSlot(false, index, before, next)], ApplySlots)))
            return StatusMessage;
        StatusMessage = held is { } e
            ? $"'{was.Name}' holds its loop height at {e.Expr.Expr}, {Length(target)} above its lower pad."
            : $"'{was.Name}' stands {Length(target)} above its lower pad.";
        return null;
    }

    /// <summary>The span typed in the display unit: the end foot moves along the chord in plan, the start stays. The end
    /// is re-seated on the pad under it, and refused where there is none. A number sets it once, an expression holds it
    /// (brief-em3d-135), and an empty text lets go of a held one and keeps the wire as it is. Null on success, else why not.</summary>
    public string? SetWireSpan(int index, string text)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        if (HeldEdit(index, was, nameof(C3dWire.Span), text, "span", out var held) is { } done) return done.Refusal;
        long target;
        if (held is { } h) target = h.Dbu;
        else
        {
            var d = C3dDimension.Parse(text, Document.DisplayUnit, Document.DbuPerMicron);
            if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) return d.Why ?? "A span is a positive length.";
            target = d.Dbu;
        }
        var start = Copy(was);
        if (held is { } e) Hold(start, nameof(C3dWire.Span), e.Expr, target);
        else Release(start, nameof(C3dWire.Span));
        if (held is not null && C3dWires.HeldConflict(start, was.Name) is { } bound) return bound;
        if (WireSpanDbu(was) == target && C3dPersistence.SerializeObject(start) == C3dPersistence.SerializeObject(was)) return null;
        if (WireSpanDbu(was) <= 0) return $"{was.Name}'s feet are one above the other: there is no plan direction to lengthen it along.";
        var moved = Copy(start);
        moved.Points = C3dWires.WithSpan(start, target)!;
        C3dWires.OffsetBoundPoints(start, moved, Document.DisplayUnit, Document.DbuPerMicron);   // brief-em3d-133: D2
        if (SeatEditedWire(was, ref moved) is { } refusal) return refusal;
        string before = C3dPersistence.SerializeObject(was), next = C3dPersistence.SerializeObject(moved);
        if (next == before) return null;
        if (!Push(new C3dEdit($"Span of {was.Name}", [new C3dEditSlot(false, index, before, next)], ApplySlots)))
            return StatusMessage;
        StatusMessage = held is { } x
            ? $"'{was.Name}' holds its span at {x.Expr.Expr}, {Length(target)}; its end moved, its start stayed."
            : $"'{was.Name}' spans {Length(target)}; its end moved, its start stayed.";
        return null;
    }

    /// <summary>
    /// brief-em3d-135 — what a typed loop height or span says before it is a length: EMPTY lets go of a held value (one undo
    /// entry; the points stay, so the wire keeps the number it has) and is done; an EXPRESSION is parsed and evaluated in
    /// the document's scope (<paramref name="held"/>, to be held at that value); a number leaves both null.
    /// </summary>
    private HeldDone? HeldEdit(int index, C3dWire was, string property, string text, string noun, out (C3dExpr Expr, long Dbu)? held)
    {
        held = null;
        var spec = C3dBindings.SpecOf(typeof(C3dWire), property)!;
        string t = text.Trim();
        if (t.Length == 0)
        {
            if (C3dBindings.GetNumber(was, spec, 0) is null && C3dBindings.GetExpr(was, spec, 0) is null) return new(null);
            var after = Copy(was);
            Release(after, property);
            if (!Push(new C3dEdit($"Let go of the {noun} of {was.Name}",
                                  [new C3dEditSlot(false, index, C3dPersistence.SerializeObject(was), C3dPersistence.SerializeObject(after))], ApplySlots)))
                return new(StatusMessage);
            StatusMessage = $"'{was.Name}' no longer holds its {noun}; it keeps the shape it has.";
            return new(null);
        }
        var d = C3dDimension.Parse(t, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind == C3dDimensionKind.Value) return null;
        // An expression, or one with its site unit after it (`h_loop mil`), as every dimension field takes one.
        var (expr, unit) = SplitUnit(t, Document.DisplayUnit);
        if (d.Kind == C3dDimensionKind.Invalid && (expr == t || !Parses(expr))) return new(d.Why ?? $"'{t}' is not a length.");
        var e = new C3dExpr(expr, C3dUnits.Stored(unit));
        double dbu;
        try { dbu = C3dVariableEdits.Evaluate(Resolution, e, C3dFieldKind.Length) * 1e6 * Document.DbuPerMicron; }
        catch (Exception ex) when (ex is Core.Expressions.ExpressionException or InvalidCastException)
        {
            // A name nothing defines (a mistyped VAR) is refused and the wire keeps what it has — the owner's rule for the
            // Inspector, which says so before it gets here (UndefinedNamesIn).
            return new($"{expr}: {ex.Message}");
        }
        long n = (long)Math.Round(dbu, MidpointRounding.AwayFromZero);
        if (n <= 0) return new($"{expr} is {Length(n)}: a {noun} is a positive length.");
        held = (e, n);
        return null;
    }

    /// <summary>A typed loop height or span that needs no more: its refusal, or null when it succeeded.</summary>
    private sealed record HeldDone(string? Refusal);

    private static C3dWire Copy(C3dWire w) => (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));

    /// <summary>brief-em3d-135 — the field bound to <paramref name="e"/>, at its value now.</summary>
    private static void Hold(C3dWire w, string property, C3dExpr e, long dbu)
    {
        var spec = C3dBindings.SpecOf(typeof(C3dWire), property)!;
        C3dBindings.SetNumber(w, spec, 0, dbu);
        C3dBindings.SetExpr(w, spec, 0, e);
    }

    /// <summary>brief-em3d-135 — the field let go: no expression, no number, so the points are the whole shape again.</summary>
    private static void Release(C3dWire w, string property)
    {
        C3dBindings.SetExpr(w, C3dBindings.SpecOf(typeof(C3dWire), property)!, 0, null);
        if (property == nameof(C3dWire.LoopHeight)) w.LoopHeight = null;
        else w.Span = null;
    }

    /// <summary>
    /// brief-em3d-135 — every top-level wire with a held loop height or span shaped to it, in place: run on every resolve,
    /// so what is edited is what elaboration builds. A refusal leaves the wire as it is; elaboration names it.
    /// </summary>
    private void HoldWires()
    {
        WireBondWorkspace? workspace = null;
        foreach (var w in Document.Objects.OfType<C3dWire>())
        {
            if (w.LoopHeight is null && w.Span is null) continue;
            if (_resolution?.FieldErrors.Any(f => f.Item == w.Name) == true) continue;
            C3dWires.Hold(w, w.Name, Document.DbuPerMicron, workspace ??= WireWorkspace());
        }
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
}
