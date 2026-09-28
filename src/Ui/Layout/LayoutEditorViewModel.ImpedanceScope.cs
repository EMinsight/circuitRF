// The Impedance review's scope on the canvas (brief-impedance-4): drawing a region (a rectangle, or a
// freehand lasso), picking the traces to review, and the scope drawn over the artwork while the
// Impedance panel is open.
//
// The scope's selectors live on the layout's saved review (LayoutView.ImpedanceReview) and are edited
// ONLY here, through EditImpedanceScope — the panel's lists, the canvas gestures and the context menu's
// "Add to impedance review" all come through it, so there is one copy of the scope and the panel reads
// it back. Review state, not artwork: dirty, and not undoable (brief-impedance-2 R-imp2-3b).
//
// The gestures follow the EM solve-region drag (LayoutEditorViewModel.EmRegion.cs): armed from outside
// the canvas, they own the press whatever tool is active, and Escape puts them back.
//
// A region belongs to a LAYER (brief-impedance-6): the one the reviewer is working on when it is drawn
// (ImpedanceRegionLayer), shown only while that layer is, and selecting only there. One that names no
// layer — every region saved before — is drawn always, in its own outline, so it is not read as a
// leftover from another layer.

using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Render;

namespace CircuitRF.Ui.Layout;

/// <summary>Which scope gesture the canvas is armed with.</summary>
public enum ImpedanceScopeTool { None, Rectangle, Lasso, Pick }

public sealed partial class LayoutEditorViewModel
{
    /// <summary>The armed scope gesture; None when the canvas is its own.</summary>
    [ObservableProperty] private ImpedanceScopeTool _impedanceScopeTool;

    /// <summary>Set by the Impedance panel while it is on screen: the scope is drawn only then.</summary>
    [ObservableProperty] private bool _showImpedanceScope;

    /// <summary>The region the panel's list has selected, highlighted on the canvas; −1 for none.</summary>
    [ObservableProperty] private int _selectedImpedanceRegion = -1;

    /// <summary>Bumped whenever the scope's selectors change, so the panel rebuilds its lists.</summary>
    [ObservableProperty] private int _impedanceScopeVersion;

    /// <summary>The press of a region gesture, then every point a lasso has passed through; null
    /// while armed but not pressed.</summary>
    private List<(long X, long Y)>? _scopeDrag;

    partial void OnShowImpedanceScopeChanged(bool value)
    {
        if (!value) CancelImpedanceScopeTool();
        RebuildOverlay();
    }

    partial void OnSelectedImpedanceRegionChanged(int value) => RebuildOverlay();

    partial void OnImpedanceScopeToolChanged(ImpedanceScopeTool value)
    {
        _scopeDrag = null;
        RebuildOverlay();
    }

    /// <summary>The scope's selectors as saved, or an empty scope.</summary>
    public TraceImpedanceScope ImpedanceScope => Model.ImpedanceReview?.Scope ?? new TraceImpedanceScope();

    /// <summary>
    /// The ONE way the scope's selectors change: <paramref name="edit"/> works on a copy of the saved
    /// review's scope, which is then saved (dirty, not undoable) and drawn.
    /// </summary>
    public void EditImpedanceScope(Action<TraceImpedanceScope> edit)
    {
        var review = Model.ImpedanceReview?.Clone() ?? new TraceImpedanceReview();
        var scope = review.Scope ?? new TraceImpedanceScope();
        edit(scope);
        review.Scope = scope.IsEmpty ? null : scope;
        if (!SaveImpedanceReview(review)) return;
        if (SelectedImpedanceRegion >= scope.Regions.Count) SelectedImpedanceRegion = -1;
        ImpedanceScopeVersion++;
        RebuildOverlay();
    }

    /// <summary>Arms a gesture (or, with None, disarms), and says what it does.</summary>
    public void ArmImpedanceScopeTool(ImpedanceScopeTool tool)
    {
        ImpedanceScopeTool = tool;
        string on = ImpedanceRegionLayer() is { } layer ? $" on {layer}" : " on every analysed layer";
        switch (tool)
        {
            case ImpedanceScopeTool.Rectangle:
                ReportMessage($"Impedance scope: drag a rectangle round the traces to review{on}. Escape cancels.");
                break;
            case ImpedanceScopeTool.Lasso:
                ReportMessage($"Impedance scope: drag round the traces to review{on}; the outline closes when you let go. Escape cancels.");
                break;
            case ImpedanceScopeTool.Pick:
                ReportMessage("Impedance scope: click a trace to review it; Shift-click adds every trace joined to it " +
                              "through vias. Escape stops picking.");
                break;
        }
    }

    public void CancelImpedanceScopeTool() => ImpedanceScopeTool = ImpedanceScopeTool.None;

    /// <summary>
    /// The copper layer a region drawn now belongs to (R-imp6-1), by name: the CURRENT drawing layer
    /// where it is a conductor the review analyses — that is the layer the reviewer is looking at; else
    /// the one layer analysed, where only one is; else null, every layer.
    /// </summary>
    public string? ImpedanceRegionLayer()
    {
        if (Technology is not { } tech) return null;
        var analysed = ImpedanceAnalysedLayers();
        foreach (var layer in tech.Stackup.Layers)
            if (layer.Kind == StackupKind.Conductor && layer.DrawingLayers.Contains(CurrentLayerKey)
                && TraceImpedanceLayers().FirstOrDefault(c => layer.DrawingLayers.Contains(c.Key)) is { } current
                && analysed.Contains(current.Name, StringComparer.OrdinalIgnoreCase))
                return current.Name;
        return analysed.Count == 1 ? analysed[0] : null;
    }

    /// <summary>The copper layers the saved review analyses, by name — every one with copper when it
    /// names none, as the panel ticks them.</summary>
    public IReadOnlyList<string> ImpedanceAnalysedLayers()
    {
        var saved = Model.ImpedanceReview?.Layers;
        return [.. TraceImpedanceLayers()
                   .Where(c => c.HasCopper && (saved is null || saved.Count == 0 || saved.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
                   .Select(c => c.Name)];
    }

    /// <summary>Focus left the canvas mid-gesture: the release may never arrive here, so a drag in
    /// progress is dropped (the tool stays armed) rather than left latched.</summary>
    public void AbandonImpedanceScopeDrag()
    {
        if (_scopeDrag is null) return;
        _scopeDrag = null;
        RebuildOverlay();
    }

    /// <summary>The context menu's "Add to impedance review": the trace at the click, on the layer a
    /// probe there would read.</summary>
    public bool AddImpedancePickAt(long x, long y, TracePickExtent extent)
    {
        if (TraceImpedanceLayerAt(x, y) is not { } layer) return false;
        string name = ResolveLayerDef(layer).Name;
        var pick = new TracePick(name, x, y, extent);
        EditImpedanceScope(s => { if (!s.Picks.Contains(pick)) s.Picks.Add(pick); });
        ReportMessage($"Impedance scope: added the trace on {name} at {FormatPoint(x, y)}" +
                      $"{(extent == TracePickExtent.Connected ? " and every trace joined to it" : "")}.");
        return true;
    }

    /// <summary>"(12.5, 4) mm" — a point as this layout displays coordinates.</summary>
    public string FormatPoint(long x, long y) =>
        $"({LayoutUnits.Format(x, DisplayUnit, Model.DbuPerMicron)}, {LayoutUnits.Format(y, DisplayUnit, Model.DbuPerMicron)}) " +
        LayoutUnits.Suffix(DisplayUnit);

    // ── the gestures ─────────────────────────────────────────────────────────────────────────

    private bool ImpedanceScopeArmed => ImpedanceScopeTool != ImpedanceScopeTool.None;

    private void ImpedanceScopePress(double wx, double wy, KeyModifiers mods)
    {
        long x = (long)Math.Round(wx), y = (long)Math.Round(wy);
        if (ImpedanceScopeTool == ImpedanceScopeTool.Pick)
        {
            // A click that lands on no copper says so and stays armed.
            if (!AddImpedancePickAt(x, y, (mods & KeyModifiers.Shift) != 0 ? TracePickExtent.Connected : TracePickExtent.Trace))
                ReportWarning("Impedance scope: there is no stackup-bound copper there. Escape stops picking.");
            return;
        }
        if (ImpedanceScopeTool == ImpedanceScopeTool.Rectangle)
            (x, y) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
        _scopeDrag = [(x, y)];
        RebuildOverlay();
    }

    private void ImpedanceScopeMove(double wx, double wy, bool leftDown)
    {
        if (_scopeDrag is null || !leftDown) return;
        if (ImpedanceScopeTool == ImpedanceScopeTool.Rectangle)
        {
            var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
            if (_scopeDrag.Count == 1) _scopeDrag.Add((sx, sy)); else _scopeDrag[1] = (sx, sy);
        }
        else
        {
            // One point per screen pixel of travel is all a hand-drawn outline carries.
            long x = (long)Math.Round(wx), y = (long)Math.Round(wy);
            var last = _scopeDrag[^1];
            if (Math.Abs(x - last.X) + Math.Abs(y - last.Y) >= PixelDbu()) _scopeDrag.Add((x, y));
        }
        RebuildOverlay();
    }

    private void ImpedanceScopeRelease(double wx, double wy)
    {
        if (_scopeDrag is not { } drag) return;
        _scopeDrag = null;
        TraceScopeRegion? region = null;
        if (ImpedanceScopeTool == ImpedanceScopeTool.Rectangle)
        {
            var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
            if (sx != drag[0].X && sy != drag[0].Y)
                region = TraceScopeRegion.Rectangle(null, drag[0].X, drag[0].Y, sx, sy, ImpedanceRegionLayer());
        }
        else
        {
            drag.Add(((long)Math.Round(wx), (long)Math.Round(wy)));
            var simple = SimplifyLasso(drag, 3 * PixelDbu());
            if (simple.Count >= 3)
                region = new TraceScopeRegion(null, [.. simple.SelectMany(p => new[] { p.X, p.Y })], ImpedanceRegionLayer());
        }

        if (region is null)
        {
            RebuildOverlay();
            ReportWarning(ImpedanceScopeTool == ImpedanceScopeTool.Rectangle
                ? "Impedance scope: drag a rectangle — a click encloses no area. Escape cancels."
                : "Impedance scope: the outline encloses no area — drag round the traces. Escape cancels.");
            return;
        }
        CancelImpedanceScopeTool();
        EditImpedanceScope(s => s.Regions.Add(region));
        SelectedImpedanceRegion = ImpedanceScope.Regions.Count - 1;
    }

    /// <summary>One screen pixel in DBU at the last zoom the canvas reported.</summary>
    private double PixelDbu() => LastZoomPxPerDbu > 0 ? 1.0 / LastZoomPxPerDbu : Model.DbuPerMicron;

    /// <summary>
    /// A hand-drawn outline reduced to a polygon — Douglas–Peucker within <paramref name="tolerance"/>
    /// DBU (a few screen pixels), the closing point dropped. Fewer than three vertices encloses nothing.
    /// </summary>
    internal static List<(long X, long Y)> SimplifyLasso(IReadOnlyList<(long X, long Y)> points, double tolerance)
    {
        if (points.Count < 3) return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = -1;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = Distance(points[i], points[a], points[b]);
                if (d > best) { best = d; index = i; }
            }
            if (index < 0 || best <= tolerance) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
        var result = new List<(long X, long Y)>();
        for (int i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
        // A lasso ends where it began: the last point closes the outline and is not a vertex of it.
        if (result.Count > 1 && Math.Abs(result[^1].X - result[0].X) + Math.Abs(result[^1].Y - result[0].Y) <= 2 * tolerance)
            result.RemoveAt(result.Count - 1);
        return result;

        static double Distance((long X, long Y) p, (long X, long Y) a, (long X, long Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
            double ex = p.X - a.X - t * dx, ey = p.Y - a.Y - t * dy;
            return Math.Sqrt(ex * ex + ey * ey);
        }
    }

    // ── the overlay ──────────────────────────────────────────────────────────────────────────

    private ImpedanceScopeOverlay? BuildImpedanceScopeOverlay()
    {
        if (!ShowImpedanceScope) return null;
        var scope = ImpedanceScope;
        var missing = ImpedanceReport?.PicksWithoutCopper ?? [];

        long[]? drawing = null;
        if (_scopeDrag is { Count: >= 2 } drag)
            drawing = ImpedanceScopeTool == ImpedanceScopeTool.Rectangle
                ? TraceScopeRegion.Rectangle(null, drag[0].X, drag[0].Y, drag[1].X, drag[1].Y).Xy is var r
                  ? [.. r, r[0], r[1]] : null
                : [.. drag.SelectMany(p => new[] { p.X, p.Y })];

        // A region on a layer is drawn while that layer is both shown and analysed (R-imp6-1); one on
        // every layer always, marked so.
        var analysed = ImpedanceAnalysedLayers();
        bool Shown(TraceScopeRegion r) =>
            r.LayerName is null
            || (analysed.Contains(r.LayerName, StringComparer.OrdinalIgnoreCase)
                && TraceImpedanceLayers().FirstOrDefault(c => string.Equals(c.Name, r.LayerName, StringComparison.OrdinalIgnoreCase)) is { } c
                && ResolveLayerDef(c.Key).Visible);
        var regions = scope.Regions.Select((r, i) => (Region: r, Index: i)).Where(p => Shown(p.Region)).ToList();

        if (regions.Count == 0 && scope.Picks.Count == 0 && drawing is null) return null;
        return new ImpedanceScopeOverlay(
            [.. regions.Select(p => (p.Region.Xy, p.Index == SelectedImpedanceRegion, p.Region.LayerName is null))],
            [.. scope.Picks.Select(p => (p.X, p.Y, p.Extent == TracePickExtent.Connected, missing.Contains(p)))],
            drawing);
    }
}
