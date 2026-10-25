namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// brief-dfr26-7 R-dfr26-7-2 — Cut Clearances into Planes: a placed <c>TESTPAD</c>'s declared cut-out, cut out of
/// the DRAWN shapes on each conductor its <c>ClearLayers</c> names. On a board whose planes are imported artwork
/// nothing pours them, so <see cref="GroundPourPlanner"/>'s cut never happens; this is the same disc, cut with the
/// layout editor's own Cut Out boolean (<see cref="LayoutBooleans.CutOut"/>).
///
/// <para><b>Here, not in the editor</b>, so the operation exists without a window; the editor's command applies the
/// plan as one undo entry and posts the report.</para>
///
/// <para><b>A cut only removes metal.</b> Running it again after <c>ClearD</c> grows widens each hole; after it
/// shrinks it changes nothing, because the larger hole already holds the smaller disc — the report says so and
/// that undoing the earlier cut is how to make a hole smaller.</para>
/// </summary>
public static class TestPadClearances
{
    /// <summary>What one conductor's cut did.</summary>
    /// <param name="Conductor">The stackup conductor.</param>
    /// <param name="Changed">Shapes the disc cut into.</param>
    /// <param name="Removed">Shapes that lay wholly inside it.</param>
    /// <param name="Drawn">Whether anything is drawn on the conductor at all.</param>
    public sealed record LayerCut(string Conductor, int Changed, int Removed, bool Drawn);

    /// <summary>The cut, before it is applied: the shapes to replace (indices into the view's shapes), what replaces
    /// them, and what to report. <paramref name="Refusal"/> is set, and nothing else, when there is nothing to cut.</summary>
    public sealed record Plan(
        IReadOnlyList<int> Indices,
        IReadOnlyList<LayoutShape> Shapes,
        IReadOnlyList<LayerCut> Layers,
        string Report,
        string? Refusal = null);

    /// <summary>Whether <paramref name="inst"/> is a placed test pad.</summary>
    public static bool IsTestPad(LayoutView view, LayoutInstance inst)
        => SnapshotOf(view, inst) is not null;

    public static Plan For(LayoutView view, LayoutInstance inst, Technology? technology)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(inst);
        if (SnapshotOf(view, inst) is not { } snap)
            return Refused("The instance is not a TESTPAD, so it declares no clearances.");
        if (technology is null)
            return Refused("No technology resolves for this layout, so its ClearLayers name no drawing layer.");

        var r = TestPadPCell.Resolve(snap.Parameters, technology);
        if (r.ClearLayers.Count == 0)
            return Refused("The test pad's ClearLayers is empty, so there is nothing to cut. Name the conductors whose " +
                           "plane should be cleared under the pad, then run it again.");

        long radius = (long)Math.Round(r.ClearD / 2 * 1e6 * view.DbuPerMicron);
        var stencil = new CircleShape { Layer = r.PadLayer, Cx = inst.X, Cy = inst.Y, R = radius };
        var stencilBox = LayoutGeometry.BboxOf(stencil);

        var indices = new List<int>();
        var shapes = new List<LayoutShape>();
        var cuts = new List<LayerCut>();
        var unknown = new List<string>();
        foreach (string name in r.ClearLayers)
        {
            var conductor = technology.Stackup.Layers.FirstOrDefault(l =>
                l.Kind == StackupKind.Conductor && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (conductor is null) { unknown.Add(name); continue; }

            var onLayer = Enumerable.Range(0, view.Shapes.Count)
                .Where(i => conductor.DrawingLayers.Contains(view.Shapes[i].Layer) && LayoutBooleans.IsClipperOperand(view.Shapes[i]))
                .ToList();
            var touching = onLayer.Where(i => LayoutGeometry.BboxOf(view.Shapes[i]).Intersects(stencilBox)).ToList();
            if (touching.Count == 0)
            {
                cuts.Add(new LayerCut(conductor.Name, 0, 0, onLayer.Count > 0));
                continue;
            }
            var result = LayoutBooleans.CutOut([.. touching.Select(i => view.Shapes[i])], stencil, technology);
            indices.AddRange(touching);
            shapes.AddRange(result.Shapes);
            cuts.Add(new LayerCut(conductor.Name, result.OperandsChanged, result.OperandsRemoved, true));
        }

        string at = $"({LayoutUnits.Format(inst.X, view.DisplayUnit, view.DbuPerMicron)}, " +
                    $"{LayoutUnits.Format(inst.Y, view.DisplayUnit, view.DbuPerMicron)} {LayoutUnits.Suffix(view.DisplayUnit)})";
        string size = $"{LayoutUnits.Format(2 * radius, view.DisplayUnit, view.DbuPerMicron)} {LayoutUnits.Suffix(view.DisplayUnit)}";
        var parts = cuts.Select(c =>
            c.Changed + c.Removed > 0
                ? $"'{c.Conductor}': cut into {Count(c.Changed + c.Removed)}" + (c.Removed > 0 ? $" ({c.Removed} lay wholly inside and went)" : "")
                : c.Drawn
                    ? $"'{c.Conductor}': no metal inside the circle — it is already clear there (a cut only removes metal: to make an " +
                      "earlier, larger cut-out smaller, undo that cut and run this again)"
                    : $"'{c.Conductor}': nothing drawn on it (a poured plane gets the cut-out when the pour is redrawn)").ToList();
        if (unknown.Count > 0)
            parts.Add($"{string.Join(", ", unknown.Select(u => $"'{u}'"))} {(unknown.Count == 1 ? "is" : "are")} not a conductor of " +
                      $"technology '{technology.Name}'");
        string report = $"Cut Clearances into Planes: a {size} circle at {at} — {string.Join("; ", parts)}.";
        return new Plan(indices, shapes, cuts, report);

        static string Count(int n) => n == 1 ? "1 shape" : $"{n} shapes";
    }

    private static Plan Refused(string why) => new([], [], [], why, why);

    private static PCellSnapshot? SnapshotOf(LayoutView view, LayoutInstance inst)
    {
        string cell = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
        return view.PCellSnapshots.TryGetValue(cell, out var snap)
               && string.Equals(snap.GeneratorId, TestPadPCell.GeneratorId, StringComparison.OrdinalIgnoreCase)
            ? snap : null;
    }
}
