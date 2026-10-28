// The Layout Review run (brief-review-2): one read of the board, its layer roles resolved once, the
// enabled checks run in the overview's table order, and grouped findings back. One run serves every
// surface — the Review panel, the PDF, `circuitrf review` and the MCP tool all read the same
// LayoutReviewResult from this function, and nothing in src/Ui or src/Cli computes a finding.
//
// A review is not DRC: it is a person's aid, beside DRC and separate from it. Its checks are built in,
// with editable thresholds, and a finding is a Warning or — for a check that could not run — a Note.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review.Checks;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Review;

public static class LayoutReview
{
    /// <summary>Every built-in check, in the order a review runs and reports them (the series
    /// overview's table order).</summary>
    private static readonly ReviewCheck[] Registry =
    [
        new ViaInPadCheck(),
        new GroundViaDistanceCheck(),
        new GroundPinViaCheck(),
        new SharedGroundViaCheck(),
        new ThermalViasCheck(),
        new StackedTracesCheck(),
        new CoupledLinesCheck(),
        new EdgeCopperCheck(),
        new EdgeTraceCheck(),
        new EdgeStitchingCheck(),
        new ProbePadsCheck(),
        new ImpedanceCheck(),
    ];

    /// <summary>The checks' definitions — the one table the panel, the CLI and MCP list.</summary>
    public static IReadOnlyList<ReviewCheckDef> Checks { get; } = [.. Registry.Select(c => c.Def)];

    /// <summary>The settings of a check whose settings are not the review's (<see cref="ReviewCheckDef.SettingsOwner"/>),
    /// as <paramref name="view"/> saves them, in one read-only line; null for any other check.</summary>
    public static string? SettingsText(string checkId, LayoutView? view) =>
        checkId == ImpedanceCheck.Id ? ImpedanceCheck.SettingsText(view?.ImpedanceReview) : null;

    /// <summary>The checks a review of <paramref name="target"/> runs, in order.</summary>
    public static IReadOnlyList<ReviewCheckDef> ChecksFor(ReviewTarget target) => [.. Checks.Where(c => c.AppliesTo(target))];

    public static ReviewCheckDef? FindCheck(string id) =>
        Checks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>The check and threshold a <c>check-id.threshold-id</c> key names, or null.</summary>
    public static (ReviewCheckDef Check, ReviewThresholdDef Threshold)? FindThreshold(string key)
    {
        foreach (var c in Checks)
            if (key.StartsWith(c.Id + ".", StringComparison.Ordinal) && c.Threshold(key[(c.Id.Length + 1)..]) is { } t)
                return (c, t);
        return null;
    }

    /// <summary>Whether <paramref name="finding"/> is accepted in the Impedance review rather than the review's own
    /// list (brief-review-5 R-rev5-4): its Accept writes <see cref="TraceImpedanceAcceptance"/>s.</summary>
    public static bool IsAcceptedElsewhere(ReviewFinding finding) => FindCheck(finding.CheckId)?.SettingsOwner is not null;

    /// <summary>
    /// What accepting the impedance finding <paramref name="finding"/> with <paramref name="reason"/> writes
    /// (brief-review-6 R-rev6-6): one <see cref="TraceImpedanceAcceptance"/> for each not-yet-accepted issue on its
    /// trace, so the Impedance panel and the review agree about it. Empty when the trace is not in
    /// <paramref name="result"/>'s Impedance analysis.
    /// </summary>
    public static IReadOnlyList<TraceImpedanceAcceptance> ImpedanceAcceptancesFor(
        LayoutReviewResult result, ReviewFinding finding, string reason, DateTime acceptedUtc)
    {
        if (result.Impedance is not { } report || finding.CheckId != ImpedanceCheck.Id) return [];
        var trace = report.AllTraces.FirstOrDefault(t => ImpedanceCheck.KeyOf(report, t) == finding.Key);
        return trace is null ? []
            : [.. trace.Issues.Where(i => i.Accepted is null).Select(i => TraceImpedanceAcceptance.For(report, trace, i, reason, acceptedUtc))];
    }

    /// <summary>Whether <paramref name="acceptance"/> accepts one of the issues of the impedance finding
    /// <paramref name="finding"/>'s trace — what its Un-accept removes.</summary>
    public static bool CoversImpedanceFinding(TraceImpedanceAcceptance acceptance, ReviewFinding finding) =>
        finding.CheckId == ImpedanceCheck.Id
        && acceptance.Key.StartsWith(finding.Key + "|", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="result"/> with the Impedance review's <paramref name="acceptances"/> re-applied to the
    /// analysis it holds and its impedance findings re-marked from it — no re-run, as
    /// <see cref="ReviewAcceptance.Apply"/> re-applies the review's own. Apply that after this one.
    /// </summary>
    public static LayoutReviewResult ReapplyImpedance(LayoutReviewResult result, IReadOnlyList<TraceImpedanceAcceptance>? acceptances)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Impedance is not { } held) return result;
        var report = TraceImpedanceAcceptance.Apply(held, acceptances);
        var byKey = new Dictionary<string, TraceRun>(StringComparer.Ordinal);
        foreach (var t in report.AllTraces) byKey.TryAdd(ImpedanceCheck.KeyOf(report, t), t);
        var findings = result.Findings.Select(f => f.CheckId == ImpedanceCheck.Id && byKey.TryGetValue(f.Key, out var t)
            ? f with { RunAccepted = ImpedanceCheck.AcceptedOf(report, t) } : f).ToList();
        return result with { Findings = findings, Impedance = report };
    }

    /// <summary>
    /// Reviews the layout <paramref name="source"/> read — its view, resolved technology and flattened
    /// shapes, read once by <see cref="TraceImpedanceAnalysis.LoadLayout"/> and not re-read here.
    /// Acceptances are not applied: <see cref="ReviewAcceptance.Apply"/> does that, so a caller can
    /// re-apply after an Accept with no re-run.
    /// </summary>
    public static LayoutReviewResult Run(TraceImpedanceAnalysis.LayoutSource source, ReviewSettings? settings = null,
                                         RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new ReviewSettings();
        if (source.Technology is not { } tech)
            return LayoutReviewResult.Refused(
                $"'{Path.GetFileName(source.Path)}' resolves no technology, so the review cannot tell copper from "
              + "the board outline. Give the layout a technology, or open it in a workspace that has one.");

        var ctx = new ReviewContext(source, tech, settings, control?.Token ?? CancellationToken.None) { Control = control };
        var target = settings.TargetOrDefault;
        var enabled = Registry.Where(c => c.Def.AppliesTo(target) && settings.IsEnabled(c.Def.Id)).ToList();
        var rows = new List<ReviewCheckRow>();
        var findings = new List<(int Check, ReviewFinding Finding)>();
        var notes = new List<string>();
        bool cancelled = false;

        for (int i = 0; i < enabled.Count; i++)
        {
            var check = enabled[i];
            var def = check.Def;
            if (cancelled || control?.Token.IsCancellationRequested == true)
            {
                cancelled = true;
                rows.Add(new ReviewCheckRow(def.Id, def.Title, ReviewCheckState.Cancelled, null, 0, 0));
                continue;
            }
            if (control is not null) control.Stage = $"Reviewing: {def.Id} ({i + 1} of {enabled.Count})";

            var run = new ReviewCheckRun(def, settings, ctx);
            try
            {
                check.Run(ctx, run);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                rows.Add(new ReviewCheckRow(def.Id, def.Title, ReviewCheckState.Cancelled, null, 0, 0));
                continue;
            }
            catch (Exception ex)
            {
                // One check that throws must not lose the others' findings.
                run.NotRun($"The check failed: {ex.Message}");
            }

            int order = Array.IndexOf(Registry, check);
            if (run.Cancelled)
            {
                // A check that keeps what it finished before the cancel (impedance: the layers it solved).
                cancelled = true;
                rows.Add(new ReviewCheckRow(def.Id, def.Title, ReviewCheckState.Cancelled, null, run.Findings.Count, 0) { Summary = run.Summary });
                findings.AddRange(run.Findings.Select(f => (order, f)));
                continue;
            }
            if (run.NotRunReason is { } why)
            {
                rows.Add(new ReviewCheckRow(def.Id, def.Title, ReviewCheckState.NotRun, why, 0, 0));
                // ONE Note per fix, however many checks it stopped (R-rev2-5).
                if (!notes.Contains(why))
                {
                    notes.Add(why);
                    findings.Add((order, new ReviewFinding(def.Id, null, ReviewSeverity.Note, why, null, [], Bbox.Empty,
                                                           $"{def.Id}|note")));
                }
                continue;
            }
            rows.Add(new ReviewCheckRow(def.Id, def.Title, ReviewCheckState.Ran, null, run.Findings.Count, 0) { Summary = run.Summary });
            findings.AddRange(run.Findings.Select(f => (order, f)));
        }

        var layers = ctx.Layers;
        var ordered = findings
            .OrderBy(f => f.Check)
            .ThenBy(f => layers.OrderOf(f.Finding.Layer))
            .ThenBy(f => f.Finding.Marker.MinX).ThenBy(f => f.Finding.Marker.MinY)
            .ThenBy(f => f.Finding.Marker.MaxX).ThenBy(f => f.Finding.Marker.MaxY)
            .Select(f => f.Finding).ToList();

        var diagnostics = new List<string>(ctx.Diagnostics);
        if (cancelled)
            diagnostics.Add($"The review was cancelled: {rows.Count(r => r.State != ReviewCheckState.Cancelled)} of "
                          + $"{rows.Count} check(s) finished, and only their findings are reported.");

        return new LayoutReviewResult(rows, ordered, tech.Name, diagnostics, cancelled)
        {
            DisplayUnit = source.View.DisplayUnit,
            DbuPerMicron = source.View.DbuPerMicron,
            Impedance = ctx.Impedance,
            Target = target,
            Settings = settings.Clone(),
            SourcePath = source.Path,
            CreatedUtc = DateTime.UtcNow,
            Artwork = ReviewArtwork.From(ctx),
        };
    }
}

/// <summary>
/// What every check shares, built ONCE per run and only on first request (R-rev2-1): the per-layer
/// unioned regions DrcEngine builds (by the same code), the connectivity partition through
/// <see cref="ConnectivityCache"/>, a <see cref="DrcRegionEval"/> over both, and the board region. A
/// check that needs none of them does not trigger their construction.
/// </summary>
internal sealed class ReviewContext
{
    public TraceImpedanceAnalysis.LayoutSource Source { get; }
    public Technology Tech { get; }
    public ReviewSettings Settings { get; }
    public ReviewLayers Layers { get; }
    public int DbuPerMicron => Source.View.DbuPerMicron;
    public LayoutUnit Unit => Source.View.DisplayUnit;
    public CancellationToken Token { get; }
    public List<string> Diagnostics { get; } = [];

    /// <summary>The run's control, or null — a check running an analysis of its own forwards that
    /// analysis's progress through it (brief-review-5 R-rev5-6).</summary>
    public RunControl? Control { get; init; }

    /// <summary>The Impedance analysis the impedance check ran, acceptances applied, or null.</summary>
    public TraceImpedanceReport? Impedance { get; set; }

    private Dictionary<LayerKey, Paths64>? _regions;
    private ConnectivityPartition? _partition;
    private DrcRegionEval? _eval;
    private ReviewBoard? _board;
    private ReviewPads? _pads;
    private List<ReviewPads.Via>? _vias;
    private (IReadOnlyDictionary<LayerKey, IReadOnlyList<TraceImpedanceAnalysis.FoundTrace>>, string?)? _traces;

    public ReviewContext(TraceImpedanceAnalysis.LayoutSource source, Technology tech, ReviewSettings settings,
                         CancellationToken token = default)
    {
        Source = source;
        Tech = tech;
        Settings = settings;
        Token = token;
        Layers = ReviewLayers.For(tech);
    }

    public bool RegionsBuilt => _regions is not null;
    public bool PartitionBuilt => _partition is not null;

    /// <summary>Every layer's artwork, unioned.</summary>
    public IReadOnlyDictionary<LayerKey, Paths64> Regions =>
        _regions ??= DrcLayerGeometry.Build(Source.Shapes.Where(DrcRegions.IsCheckable), Tech, _ => long.MaxValue, Diagnostics).Regions;

    /// <summary>Which copper is one net, and which nets reach the ground reference.</summary>
    public ConnectivityPartition Partition => _partition ??= ConnectivityCache.Get(Regions, Tech);

    /// <summary>The DRC layer-expression evaluator over the regions and the partition.</summary>
    public DrcRegionEval Eval =>
        _eval ??= new DrcRegionEval(Regions, Tech.Layers.Select(l => l.Key).ToHashSet(), Partition);

    public ReviewBoard Board => _board ??= ReviewBoard.Build(Source.Shapes, Tech, Layers, Unit, DbuPerMicron);

    /// <summary>The board, when a check has built it, else null.</summary>
    public ReviewBoard? BoardBuilt => _board;

    /// <summary>The pads, vias and ground the four pad checks share (brief-review-3).</summary>
    public ReviewPads Pads => _pads ??= ReviewPads.Build(this);

    /// <summary>Whether any via entry's drawing layer is a plated drill — what a via needs (R-rev3-2).</summary>
    public bool HasPlatedDrills => Layers.Drills.Any(d => d.Entry.Plated != false);

    /// <summary>Every plated drill, ground marked — the pad checks' vias and edge-stitching's.</summary>
    public List<ReviewPads.Via> Vias => _vias ??= ReviewPads.BuildVias(this);

    /// <summary>The trace finder's traces on every copper layer, or why it found none (brief-review-4
    /// R-rev4-1) — found once and shared by stacked-traces and edge-trace.</summary>
    public (IReadOnlyDictionary<LayerKey, IReadOnlyList<TraceImpedanceAnalysis.FoundTrace>> Traces, string? Refusal) Traces =>
        _traces ??= TraceImpedanceAnalysis.FindTraces(Source.Shapes, Tech, DbuPerMicron, Token);

    /// <summary>The traces found on one conductor, whichever of its drawing layers the finder keyed them by.</summary>
    public IEnumerable<(LayerKey Layer, TraceImpedanceAnalysis.FoundTrace Trace)> TracesOn(ReviewCopperLayer copper) =>
        Traces.Traces.Where(kv => copper.DrawingLayers.Contains(kv.Key)).SelectMany(kv => kv.Value.Select(t => (kv.Key, t)));

    /// <summary>A threshold of ANY check as this run sees it, stored units — for a threshold one check
    /// declares and others read (<c>thermal-vias.thermal-pad-area</c>, <c>ground-via-distance.max-distance</c>).</summary>
    public double Threshold(string checkId, string thresholdId)
    {
        string key = ReviewSettings.KeyOf(checkId, thresholdId);
        var (def, threshold) = LayoutReview.FindThreshold(key) ?? throw new KeyNotFoundException(key);
        return Settings.Value(def, threshold, DbuPerMicron);
    }

    public string Length(long dbu) => ReviewText.Length(dbu, Unit, DbuPerMicron);
}

/// <summary>One built-in check.</summary>
internal abstract class ReviewCheck
{
    public abstract ReviewCheckDef Def { get; }
    public abstract void Run(ReviewContext ctx, ReviewCheckRun run);
}

/// <summary>One check's run: its thresholds as this run sees them, and where its findings go.</summary>
internal sealed class ReviewCheckRun(ReviewCheckDef def, ReviewSettings settings, ReviewContext ctx)
{
    public List<ReviewFinding> Findings { get; } = [];
    public string? NotRunReason { get; private set; }

    /// <summary>The check's line beyond its findings (<see cref="ReviewCheckRow.Summary"/>), or null.</summary>
    public string? Summary { get; set; }

    /// <summary>The check was cancelled part-way and keeps the findings it finished.</summary>
    public bool Cancelled { get; set; }

    /// <summary>A length threshold, DBU.</summary>
    public long Length(string thresholdId) =>
        (long)Math.Round(settings.Value(def, def.Threshold(thresholdId)
            ?? throw new ArgumentException($"'{def.Id}' has no threshold '{thresholdId}'."), ctx.DbuPerMicron));

    /// <summary>A count or density threshold.</summary>
    public double Number(string thresholdId) =>
        settings.Value(def, def.Threshold(thresholdId)
            ?? throw new ArgumentException($"'{def.Id}' has no threshold '{thresholdId}'."), ctx.DbuPerMicron);

    /// <summary>The check could not run; <paramref name="fix"/> is the one sentence that fixes it.</summary>
    public void NotRun(string fix)
    {
        NotRunReason = fix;
        Findings.Clear();
    }

    /// <param name="region">The marker. A two-point path in it is a LINE (a pad to its nearest via).</param>
    public void Add(LayerKey? layer, string sentence, string? measured, Paths64 region) =>
        Add(layer, sentence, measured, region, null, null);

    /// <param name="key">The finding's key, when it is not the review's place key — an impedance finding
    /// is keyed by its trace, as the Impedance review keys it.</param>
    /// <param name="accepted">The acceptance the check's own run applied (<see cref="ReviewFinding.RunAccepted"/>).</param>
    public void Add(LayerKey? layer, string sentence, string? measured, Paths64 region, string? key, ReviewAcceptance? accepted)
    {
        var bounds = DrcRegions.BoundsOf(region);
        Findings.Add(new ReviewFinding(def.Id, layer, ReviewSeverity.Warning, sentence, measured,
                                       DrcRegions.ToRings(region), bounds, key ?? ReviewFinding.KeyOf(def.Id, layer, bounds))
        {
            LayerName = layer is { } l ? ctx.Layers.NameOf(l) : null,
            RunAccepted = accepted,
        });
    }
}
