// The canvas's "Trace Impedance" command (round-7 field report): right-click copper, get its Z0.
// The work is TraceImpedanceProbe in src/Design; this file picks the layer the click means, hands
// the probe the same flattened artwork a DRC run checks, and posts the answer to Messages.
//
// And the Impedance panel (brief-impedance-3): every trace on the chosen layers, held here as a
// report until the next run, drawn over the canvas, and exported as a PDF of the report already held.
// The work is TraceImpedanceAnalysis and the page TraceImpedanceReportDocument — the two calls
// `circuitrf impedance` makes — so this file only gathers the artwork, holds the result and says what
// it did. The panel itself is Layout/Impedance/ImpedancePanelViewModel.

using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Engine;
using CircuitRF.Render;

namespace CircuitRF.Ui.Layout;

public partial class LayoutEditorViewModel
{
    /// <summary>
    /// The copper layer a probe at (<paramref name="x"/>, <paramref name="y"/>) means, or null where
    /// there is no stackup-bound copper there. The CURRENT drawing layer when it has copper at the
    /// point — that is how a user says which of two stacked traces they mean — otherwise the highest
    /// visible copper in the stackup, never the draw order, which an imported technology can invert.
    /// </summary>
    public LayerKey? TraceImpedanceLayerAt(long x, long y)
    {
        if (Technology is not { } tech) return null;

        var under = Regions.CopperLayersAt(Model.Shapes, tech, x, y);
        if (under.Count == 0) return null;
        if (under.Contains(CurrentLayerKey) && ResolveLayerDef(CurrentLayerKey).Visible) return CurrentLayerKey;

        return under.OrderBy(k => ResolveLayerDef(k).Visible ? 0 : 1).ThenBy(k => StackRank(tech, k))
                    .Select(k => (LayerKey?)k).FirstOrDefault();
    }

    /// <summary>Probes the trace at the point and posts the result to Messages. Null when there is
    /// no technology or no copper there — the menu item is not offered then.</summary>
    public TraceImpedanceResult? ProbeTraceImpedance(long x, long y)
    {
        if (Technology is not { } tech || TraceImpedanceLayerAt(x, y) is not { } layer) return null;

        // The artwork a DRC run sees, so a trace drawn inside a placed cell is probed too.
        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;

        // The layer the click means first, then every other copper layer at the point, visible ones
        // first and in stackup order: the round-8 board's current layer was the inner PLANE under
        // the trace, and the probe answered for the plane.
        var candidates = new List<LayerKey> { layer };
        foreach (var k in Regions.CopperLayersAt(Model.Shapes, tech, x, y)
                                 .OrderBy(k => ResolveLayerDef(k).Visible ? 0 : 1).ThenBy(k => StackRank(tech, k)))
            if (!candidates.Contains(k)) candidates.Add(k);

        var result = TraceImpedanceProbe.ProbeFirst(shapes, tech, Model.DbuPerMicron, x, y, candidates, DisplayUnit);

        if (_messageSink is { } messages)
        {
            // ONE line (owner, 2026-09-24): each warning rides on it as a short tag.
            if (result.Ok && result.Flags.Count == 0) messages.Info(result.Summary());
            else messages.Warning(result.Summary());
        }
        return result;
    }

    private static int StackRank(Technology tech, LayerKey k)
    {
        for (int i = 0; i < tech.Stackup.Layers.Count; i++)
            if (tech.Stackup.Layers[i].DrawingLayers.Contains(k)) return i;
        return int.MaxValue;
    }

    /// <summary>One copper layer the Impedance panel offers.</summary>
    public sealed record TraceImpedanceLayerChoice(LayerKey Key, string Name, Rgba Color, bool HasCopper);

    /// <summary>
    /// The copper layers of this layout's technology — every drawing layer bound to a conductor of the
    /// stackup, one per conductor, top of the stack first — or empty with no technology.
    /// </summary>
    public IReadOnlyList<TraceImpedanceLayerChoice> TraceImpedanceLayers()
    {
        if (Technology is not { } tech) return [];
        var withCopper = Model.Shapes.Select(s => s.Layer).ToHashSet();
        var list = new List<TraceImpedanceLayerChoice>();
        foreach (var layer in tech.Stackup.Layers)
        {
            if (layer.Kind != StackupKind.Conductor) continue;
            var key = layer.DrawingLayers.FirstOrDefault(k => tech.Layers.Any(l => l.Key == k));
            if (layer.DrawingLayers.Count == 0 || tech.Layers.FirstOrDefault(l => l.Key == key) is not { } def) continue;
            list.Add(new TraceImpedanceLayerChoice(key, def.Name, def.Color,
                layer.DrawingLayers.Any(withCopper.Contains) || Model.Instances.Count > 0));
        }
        return list;
    }

    /// <summary>The Impedance Analysis settings and scope saved on this layout, or null.</summary>
    public TraceImpedanceReview? SavedImpedanceReview => Model.ImpedanceReview;

    /// <summary>
    /// Saves the review on the layout (brief-impedance-2 R-imp2-3) when it says something the saved one
    /// does not. Dirty, and deliberately NOT undoable — review state, not artwork; see
    /// <see cref="LayoutView.ImpedanceReview"/>. Returns whether anything changed.
    /// </summary>
    public bool SaveImpedanceReview(TraceImpedanceReview review)
    {
        if (review.SameAs(Model.ImpedanceReview)) return false;
        Model.ImpedanceReview = review;
        IsDirty = true;
        // The analysed layers decide which regions are drawn (brief-impedance-6).
        if (ShowImpedanceScope) RebuildOverlay();
        return true;
    }

    /// <summary>
    /// The trace widths on every copper layer (<see cref="TraceImpedanceAnalysis.Survey(IReadOnlyList{LayoutShape},
    /// Technology, int, TraceImpedanceOptions, RunControl?)"/>), on a worker thread; the artwork is
    /// flattened here, on the caller's thread, as the analysis flattens it. Null with no technology;
    /// cancelling throws.
    /// </summary>
    public async Task<TraceWidthSurvey?> SurveyTraceWidthsAsync(RunControl control)
    {
        if (Technology is not { } tech) return null;
        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;
        int dbu = Model.DbuPerMicron;
        return await Task.Run(() => TraceImpedanceAnalysis.Survey(shapes, tech, dbu, new TraceImpedanceOptions(), control));
    }

    /// <summary>A length in µm as this layout displays lengths, with its unit.</summary>
    public string FormatMicrons(double microns) =>
        $"{LayoutUnits.Format((long)Math.Round(microns * Model.DbuPerMicron), DisplayUnit, Model.DbuPerMicron, 3)} " +
        LayoutUnits.Suffix(DisplayUnit);

    // ── the run, the held report, and the export (brief-impedance-3) ─────────────────────────

    /// <summary>
    /// The last Impedance Analysis of this layout, held for the panel, the canvas overlay and Export
    /// PDF until the next run (R-imp3-2e). An edit does not clear it; it sets
    /// <see cref="IsImpedanceStale"/>.
    /// </summary>
    [ObservableProperty] private TraceImpedanceReport? _impedanceReport;

    /// <summary>The layout has been edited since <see cref="ImpedanceReport"/> was run.</summary>
    [ObservableProperty] private bool _isImpedanceStale;

    /// <summary>The panel's Show on canvas (R-imp3-3b).</summary>
    [ObservableProperty] private bool _showImpedanceOverlay = true;

    private TraceRun? _impedanceSelectedTrace;
    private TraceIssue? _impedanceSelectedIssue;

    partial void OnImpedanceReportChanged(TraceImpedanceReport? value)
    {
        _impedanceSelectedTrace = null;
        _impedanceSelectedIssue = null;
        RebuildOverlay();
    }

    partial void OnShowImpedanceOverlayChanged(bool value) => RebuildOverlay();

    /// <summary>Called from the model's own change notification.</summary>
    internal void MarkImpedanceStaleOnEdit()
    {
        if (ImpedanceReport is not null && !IsImpedanceStale) IsImpedanceStale = true;
    }

    /// <summary>
    /// Drops the held report, so the canvas shows the artwork alone and the next Run starts from
    /// nothing (field report, 2026-09-28: there was no way to clear a run before a second, clean one).
    /// The review — target, layers, scope — and the accepted findings are the DOCUMENT's and are kept;
    /// only the result goes.
    /// </summary>
    public void ClearTraceImpedance()
    {
        ImpedanceReport = null;
        IsImpedanceStale = false;
    }

    /// <summary>
    /// Runs the analysis on a worker thread and holds the report (R-imp3-2a). The artwork is flattened
    /// HERE, on the caller's (UI) thread, because the model is the editor's and not the worker's. A
    /// cancelled run keeps the layers it finished (owner, 2026-09-25); one cancelled before the first
    /// layer finished, or refused, leaves the report held before it in place. Posts the one Messages
    /// line (R-imp3-4b). Returns the report, or null when there is none.
    /// </summary>
    public async Task<TraceImpedanceReport?> RunTraceImpedanceAsync(TraceImpedanceOptions options, RunControl control)
    {
        if (Technology is not { } tech) { ReportError("Impedance Analysis: this layout has no technology, so no stackup."); return null; }

        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;
        int dbu = Model.DbuPerMicron;
        var opts = options with { DisplayUnit = options.DisplayUnit ?? DisplayUnit };
        string title = CurrentLayoutPath is { Length: > 0 } fp ? TraceImpedanceAnalysis.CellTitle(fp) : "Untitled layout";
        string? source = CurrentLayoutPath;

        TraceImpedanceReport report;
        try
        {
            report = await Task.Run(() => TraceImpedanceAnalysis.Analyze(shapes, tech, dbu, opts, control));
        }
        catch (OperationCanceledException)
        {
            _messageSink?.Info("Impedance Analysis: cancelled before the first layer finished.");
            return null;
        }
        if (report.Refusal is { } why) { ReportError($"Impedance Analysis: {why}"); return null; }
        if (report.Layers.Count == 0)
        {
            _messageSink?.Info("Impedance Analysis: cancelled before the first layer finished.");
            return null;
        }

        ImpedanceReport = TraceImpedanceAcceptance.Apply(
            report with { Title = title, SourcePath = source, TechnologyPath = ResolvedTechPath }, Model.ImpedanceAcceptances);
        IsImpedanceStale = false;
        PostImpedanceVerdict(ImpedanceReport, pdfPath: null);
        return ImpedanceReport;
    }

    // ── accepted findings (brief-impedance-5) ────────────────────────────────────────────────

    /// <summary>The acceptances saved on this layout.</summary>
    public IReadOnlyList<TraceImpedanceAcceptance> ImpedanceAcceptances => Model.ImpedanceAcceptances;

    /// <summary>
    /// Accepts each finding with <paramref name="reason"/> (R-imp5-3a) — one reason for all of them, as a
    /// multi-select accepts — replacing an acceptance already saved under the same key, and re-applies to
    /// the report held, so the counts and verdicts update with no re-run. Dirty, deliberately NOT undoable
    /// (<see cref="LayoutView.ImpedanceAcceptances"/>' rule). Returns false for an empty reason, which is
    /// refused, or nothing to accept.
    /// </summary>
    public bool AcceptImpedanceFindings(IEnumerable<(TraceRun Trace, TraceIssue Issue)> findings, string reason)
    {
        if (ImpedanceReport is not { } r || string.IsNullOrWhiteSpace(reason)) return false;
        var now = DateTime.UtcNow;
        bool any = false;
        foreach (var (trace, issue) in findings)
        {
            var a = TraceImpedanceAcceptance.For(r, trace, issue, reason, now);
            Model.ImpedanceAcceptances.RemoveAll(x => string.Equals(x.Key, a.Key, StringComparison.OrdinalIgnoreCase));
            Model.ImpedanceAcceptances.Add(a);
            any = true;
        }
        if (!any) return false;
        IsDirty = true;
        ReapplyImpedanceAcceptances();
        return true;
    }

    /// <summary>Removes the acceptance covering each finding given (Un-accept), and a stale one given
    /// directly (R-imp5-3c) — both are one removal by key.</summary>
    public void RemoveImpedanceAcceptances(IEnumerable<TraceImpedanceAcceptance> acceptances)
    {
        var keys = acceptances.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Model.ImpedanceAcceptances.RemoveAll(a => keys.Contains(a.Key)) == 0) return;
        IsDirty = true;
        ReapplyImpedanceAcceptances();
    }

    /// <summary>Re-marks the report in hand rather than re-running it — an acceptance is a statement about
    /// a finding ALREADY found (LVS's <c>ReapplyLvsWaivers</c>, for the same reason).</summary>
    private void ReapplyImpedanceAcceptances()
    {
        if (ImpedanceReport is { } r) ImpedanceReport = TraceImpedanceAcceptance.Apply(r, Model.ImpedanceAcceptances);
    }

    /// <summary>The held report as Export PDF writes it: stale when the layout has changed since.</summary>
    public TraceImpedanceReport? ImpedanceReportForExport =>
        ImpedanceReport is { } r ? r with { Stale = IsImpedanceStale } : null;

    /// <summary>
    /// Writes the PDF of the report already held — no second run (R-imp3-4a). A stale report is written
    /// with its stale sentence on the summary page. Returns whether the file was written.
    /// </summary>
    public async Task<bool> ExportTraceImpedancePdfAsync(string pdfPath)
    {
        if (ImpedanceReportForExport is not { } report) return false;
        try
        {
            byte[] pdf = await Task.Run(() => TraceImpedanceReportDocument.Pdf(report));
            await File.WriteAllBytesAsync(pdfPath, pdf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError($"Impedance Analysis: '{pdfPath}' was not written: {ex.Message}");
            return false;
        }
        PostImpedanceVerdict(report, pdfPath);
        return true;
    }

    /// <summary>ONE line: the counts, against what, and — after an export — a link to the PDF.</summary>
    private void PostImpedanceVerdict(TraceImpedanceReport report, string? pdfPath)
    {
        string verdict = $"{report.TraceCount} trace{(report.TraceCount == 1 ? "" : "s")} on {report.Layers.Count} " +
                         $"layer{(report.Layers.Count == 1 ? "" : "s")} against {report.TargetOhms:0.##} Ω ± " +
                         $"{report.TolerancePercent:0.##} %: {report.PassCount} pass, {report.WarningCount} warning, {report.FailCount} fail" +
                         (report.AcceptedCount > 0 ? $", {report.AcceptedCount} finding{(report.AcceptedCount == 1 ? "" : "s")} accepted" : "") +
                         (report.Cancelled ? $" (cancelled after {report.Layers.Count} of {report.LayersRequested.Count} layers)" : "") +
                         (report.Stale ? $" — stale: {TraceImpedanceReport.StaleText.ToLowerInvariant()}" : "");
        string text = pdfPath is null ? $"Impedance Analysis: {verdict}." : $"Impedance Analysis exported: {verdict}.";
        if (report.FailCount > 0 || report.Cancelled || report.Stale) _messageSink?.Warning(text, pdfPath);
        else ReportMessage(text, pdfPath);
    }

    // ── cross-probing (R-imp3-2d, R-imp3-3b) ─────────────────────────────────────────────────

    /// <summary>The panel's selected row, emphasised on the canvas: a trace, or one of its findings.</summary>
    public void SelectImpedance(TraceRun? trace, TraceIssue? issue)
    {
        if (ReferenceEquals(trace, _impedanceSelectedTrace) && ReferenceEquals(issue, _impedanceSelectedIssue)) return;
        _impedanceSelectedTrace = trace;
        _impedanceSelectedIssue = issue;
        RebuildOverlay();
    }

    /// <summary>
    /// Brings a trace, or one finding's stretch of it, on screen — through the seam a DRC violation
    /// uses (<see cref="RequestZoomToRegion"/>). Padded by the trace's width so a zero-length finding
    /// still frames something.
    /// </summary>
    public void ZoomToImpedance(TraceRun trace, TraceIssue? issue)
    {
        long pad = (long)Math.Ceiling(Math.Max(trace.WidthMax, Model.DbuPerMicron));
        Bbox box;
        if (issue is { } i)
            box = new Bbox(Math.Min(i.X0, i.X1) - 2 * pad, Math.Min(i.Y0, i.Y1) - 2 * pad,
                           Math.Max(i.X0, i.X1) + 2 * pad, Math.Max(i.Y0, i.Y1) + 2 * pad);
        else if (trace.Pieces.Count > 0)
            box = new Bbox(trace.Pieces.Min(p => Math.Min(p.X0, p.X1)) - pad, trace.Pieces.Min(p => Math.Min(p.Y0, p.Y1)) - pad,
                           trace.Pieces.Max(p => Math.Max(p.X0, p.X1)) + pad, trace.Pieces.Max(p => Math.Max(p.Y0, p.Y1)) + pad);
        else
            box = new Bbox(Math.Min(trace.StartX, trace.EndX) - pad, Math.Min(trace.StartY, trace.EndY) - pad,
                           Math.Max(trace.StartX, trace.EndX) + pad, Math.Max(trace.StartY, trace.EndY) + pad);
        RequestZoomToRegion(box);
    }

    /// <summary>
    /// The results overlay (R-imp3-3a): each reviewed trace's centre line, cut by cut, and a marker at
    /// each finding — or null with no results or Show on canvas off. Stale results still draw, as a
    /// stale LVS result does: the banner says so, and the rows are what the reviewer is fixing.
    /// </summary>
    private ImpedanceOverlay? BuildImpedanceOverlay()
    {
        if (!ShowImpedanceOverlay || ImpedanceReport is not { } r) return null;

        var traces = new List<ImpedanceTraceMarker>();
        var findings = new List<ImpedanceFindingMarker>();
        foreach (var t in r.AllTraces)
        {
            var stretches = new List<ImpedanceStretch>(t.Stations.Count);
            foreach (var st in t.Stations)
            {
                double dx = st.Uy, dy = -st.Ux;   // along the trace; (Ux, Uy) is the cut's normal
                stretches.Add(new ImpedanceStretch(
                    (long)Math.Round(st.X - 0.5 * st.Length * dx), (long)Math.Round(st.Y - 0.5 * st.Length * dy),
                    (long)Math.Round(st.X + 0.5 * st.Length * dx), (long)Math.Round(st.Y + 0.5 * st.Length * dy), st.Z0));
            }
            traces.Add(new ImpedanceTraceMarker(stretches,
                ReferenceEquals(t, _impedanceSelectedTrace) && _impedanceSelectedIssue is null));
            foreach (var i in t.Issues)
                findings.Add(new ImpedanceFindingMarker(i.X, i.Y, i.Kind != TraceIssueKind.OutOfTolerance, i.Fails,
                    ReferenceEquals(i, _impedanceSelectedIssue)) { Accepted = i.Accepted is not null });
        }
        return new ImpedanceOverlay(r.TargetOhms, r.TolerancePercent, traces, findings);
    }
}
