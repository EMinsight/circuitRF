// The Layout Review, from the editor's side (brief-review-6). The Impedance panel's shape: the result is held
// on the layout reviewed, so switching documents switches results; an edit keeps it and says it is stale,
// because a reviewer is usually mid-fix and still reading the rows; accepting re-marks the result in hand
// rather than re-running.
//
// NOTHING HERE COMPUTES A FINDING. RunReviewAsync calls LayoutReview.Run — the function `circuitrf review`
// calls — on the artwork flattened as the Impedance run flattens it, and every acceptance is applied by
// ReviewAcceptance.Apply / LayoutReview.ReapplyImpedance.

using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Engine;
using CircuitRF.Render;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    // ── settings (R-rev6-2) ──────────────────────────────────────────────────────────────────

    /// <summary>The review's settings as this layout holds them — the defaults when none are saved.</summary>
    public ReviewSettings ReviewSettings => Model.Review ?? new ReviewSettings();

    /// <summary>
    /// Changes the review's settings on the layout: dirty, and deliberately NOT undoable — review state, not
    /// artwork, on <see cref="LayoutView.DrcWaivers"/>' terms. The edit is made on a copy so a half-made
    /// change is never what is saved.
    /// </summary>
    public void EditReviewSettings(Action<ReviewSettings> edit)
    {
        var settings = ReviewSettings.Clone();
        edit(settings);
        var before = Model.Review?.ForSave(Model.DbuPerMicron);
        var after = settings.ForSave(Model.DbuPerMicron);
        if (SameSettings(before, after)) return;
        Model.Review = after;
        IsDirty = true;
    }

    private static bool SameSettings(ReviewSettings? a, ReviewSettings? b) =>
        a?.Target == b?.Target
        && (a?.Disabled ?? []).SequenceEqual(b?.Disabled ?? [])
        && (a?.Thresholds ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SequenceEqual((b?.Thresholds ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal));

    // ── the run and the held result (R-rev6-3) ───────────────────────────────────────────────

    /// <summary>The last review of this layout, its acceptances applied, held until the next run.</summary>
    [ObservableProperty] private LayoutReviewResult? _reviewResult;

    /// <summary>The layout has been edited since <see cref="ReviewResult"/> was run.</summary>
    [ObservableProperty] private bool _isReviewStale;

    /// <summary>Markers draw only while the Review panel is on screen (R-rev6-5) — set by the panel.</summary>
    [ObservableProperty] private bool _showReviewMarkers;

    /// <summary>The finding the panel has selected, drawn heavier and never folded into a badge.</summary>
    [ObservableProperty] private ReviewFinding? _selectedReviewFinding;

    partial void OnReviewResultChanged(LayoutReviewResult? value) => RebuildOverlay();
    partial void OnShowReviewMarkersChanged(bool value) => RebuildOverlay();
    partial void OnSelectedReviewFindingChanged(ReviewFinding? value) => RebuildOverlay();

    /// <summary>Called from the model's own change notification.</summary>
    internal void MarkReviewStaleOnEdit()
    {
        if (ReviewResult is not null && !IsReviewStale) IsReviewStale = true;
    }

    /// <summary>
    /// Reviews the layout on a worker thread with the settings it holds, and holds the result — which
    /// REPLACES the previous one whole, so a run never mixes in the last one's findings. The artwork is
    /// flattened here, on the caller's (UI) thread, because the model is the editor's. A cancelled run is
    /// held with the checks that finished; a refused one is posted and the previous result kept. Posts the
    /// one Messages line (R-rev6-7).
    /// </summary>
    public async Task<LayoutReviewResult?> RunReviewAsync(RunControl control)
    {
        if (Technology is not { } tech) { ReportError("Review: this layout has no technology, so the review cannot tell copper from the board outline."); return null; }

        var flat = LayoutDesignFlatten.Flatten(Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;
        var source = new TraceImpedanceAnalysis.LayoutSource(CurrentLayoutPath ?? "", Model, tech, ResolvedTechPath, shapes, null);
        var settings = ReviewSettings.Clone();

        var result = await Task.Run(() => LayoutReview.Run(source, settings, control));
        if (result.Refusal is { } why) { ReportError($"Review: {why}"); return null; }

        ReviewResult = Reapply(result);
        IsReviewStale = false;
        SelectedReviewFinding = null;
        ReviewRunReport.Post(_messageSink, ReviewResult);
        return ReviewResult;
    }

    /// <summary>Both acceptance lists applied to <paramref name="result"/> — the Impedance review's first, then the review's.</summary>
    private LayoutReviewResult Reapply(LayoutReviewResult result) =>
        ReviewAcceptance.Apply(LayoutReview.ReapplyImpedance(result, Model.ImpedanceAcceptances), Model.ReviewAcceptances);

    /// <summary>Re-marks the held result after an acceptance changed — no re-run. Also called when the Impedance
    /// panel accepts, so the two panels never disagree about an impedance finding.</summary>
    internal void ReapplyReviewAcceptances()
    {
        if (ReviewResult is { } r) ReviewResult = Reapply(r);
    }

    // ── accepting (R-rev6-6) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts each finding with <paramref name="reason"/>: a review finding writes a <see cref="ReviewAcceptance"/>,
    /// an impedance finding a <see cref="TraceImpedanceAcceptance"/> for each open issue on its trace (brief-review-5
    /// R-rev5-4). Replaces an acceptance saved under the same key; dirty, not undoable. Returns false for an empty
    /// reason, which is refused, or nothing accepted.
    /// </summary>
    public bool AcceptReviewFindings(IEnumerable<ReviewFinding> findings, string reason)
    {
        if (ReviewResult is not { } result || string.IsNullOrWhiteSpace(reason)) return false;
        var now = DateTime.UtcNow;
        bool any = false, impedance = false;
        foreach (var f in findings)
        {
            if (f.Severity != ReviewSeverity.Warning || f.IsAccepted) continue;
            if (LayoutReview.IsAcceptedElsewhere(f))
            {
                foreach (var a in LayoutReview.ImpedanceAcceptancesFor(result, f, reason, now))
                {
                    Model.ImpedanceAcceptances.RemoveAll(x => string.Equals(x.Key, a.Key, StringComparison.OrdinalIgnoreCase));
                    Model.ImpedanceAcceptances.Add(a);
                    any = impedance = true;
                }
                continue;
            }
            var acceptance = ReviewAcceptance.For(f, f.LayerName ?? "", reason, now);
            Model.ReviewAcceptances.RemoveAll(x => string.Equals(x.Key, acceptance.Key, StringComparison.Ordinal));
            Model.ReviewAcceptances.Add(acceptance);
            any = true;
        }
        if (!any) return false;
        IsDirty = true;
        if (impedance) ReapplyImpedanceAcceptances();   // re-marks the review's result too
        else ReapplyReviewAcceptances();
        return true;
    }

    /// <summary>Un-accept: removes what accepted each finding — its review acceptance, or the Impedance
    /// acceptances on its trace.</summary>
    public void UnacceptReviewFindings(IEnumerable<ReviewFinding> findings)
    {
        int removed = 0;
        bool impedance = false;
        foreach (var f in findings.Where(f => f.IsAccepted))
        {
            if (LayoutReview.IsAcceptedElsewhere(f))
            {
                int n = Model.ImpedanceAcceptances.RemoveAll(a => LayoutReview.CoversImpedanceFinding(a, f));
                removed += n;
                impedance |= n > 0;
            }
            else removed += Model.ReviewAcceptances.RemoveAll(a => string.Equals(a.Key, f.Key, StringComparison.Ordinal));
        }
        if (removed == 0) return;
        IsDirty = true;
        if (impedance) ReapplyImpedanceAcceptances();
        else ReapplyReviewAcceptances();
    }

    /// <summary>Removes saved review acceptances directly — a stale one's Remove.</summary>
    public void RemoveReviewAcceptances(IEnumerable<ReviewAcceptance> acceptances)
    {
        var keys = acceptances.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        if (Model.ReviewAcceptances.RemoveAll(a => keys.Contains(a.Key)) == 0) return;
        IsDirty = true;
        ReapplyReviewAcceptances();
    }

    // ── cross-probing (R-rev6-5) ─────────────────────────────────────────────────────────────

    /// <summary>The region a finding's zoom frames: its marker with a margin of half its larger side, and at least
    /// 0.1 mm, so a point-sized marker still lands among the copper around it. Empty for a Note.</summary>
    public Bbox ReviewZoomRegion(ReviewFinding finding)
    {
        var m = finding.Marker;
        if (m.IsEmpty) return Bbox.Empty;
        long margin = Math.Max(Math.Max(m.MaxX - m.MinX, m.MaxY - m.MinY) / 2, 100L * Model.DbuPerMicron);
        return new Bbox(m.MinX - margin, m.MinY - margin, m.MaxX + margin, m.MaxY + margin);
    }

    /// <summary>Brings a finding's marker on screen, through the seam a DRC violation uses.</summary>
    public void ZoomToReviewFinding(ReviewFinding finding) => RequestZoomToRegion(ReviewZoomRegion(finding));

    /// <summary>The findings whose markers the selected copper touches — on its layer, or any finding that names
    /// no layer — what the panel selects when copper is selected on the canvas.</summary>
    public IReadOnlyList<ReviewFinding> ReviewFindingsTouchingSelection()
    {
        if (ReviewResult is not { } r) return [];
        var shapes = EffectiveSelectedShapes();
        if (shapes.Count == 0) return [];
        var boxes = shapes.Select(s => (s.Layer, Box: LayoutGeometry.BboxOf(s))).ToList();
        return [.. r.Findings.Where(f => !f.Marker.IsEmpty
                                         && boxes.Any(b => (f.Layer is null || f.Layer == b.Layer) && b.Box.Intersects(f.Marker)))];
    }

    private IReadOnlyList<ReviewFindingMarker> BuildReviewMarkers()
    {
        if (!ShowReviewMarkers || ReviewResult is not { } r) return [];
        var selected = SelectedReviewFinding?.Key;
        var markers = new List<ReviewFindingMarker>(r.Findings.Count);
        foreach (var f in r.Findings)
            if (f.Severity == ReviewSeverity.Warning && f.MarkerRings.Count > 0)
                markers.Add(new ReviewFindingMarker(f.MarkerRings, f.IsAccepted,
                    selected is not null && string.Equals(f.Key, selected, StringComparison.Ordinal)));
        return markers;
    }
}
