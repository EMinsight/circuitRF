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
    /// overview's table order). Briefs 3–5 add theirs in their place.</summary>
    private static readonly ReviewCheck[] Registry =
    [
        new ViaInPadCheck(),
        new GroundViaDistanceCheck(),
        new SharedGroundViaCheck(),
        new ThermalViasCheck(),
        new EdgeCopperCheck(),
    ];

    /// <summary>The checks' definitions — the one table the panel, the CLI and MCP list.</summary>
    public static IReadOnlyList<ReviewCheckDef> Checks { get; } = [.. Registry.Select(c => c.Def)];

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

        var ctx = new ReviewContext(source, tech, settings);
        var enabled = Registry.Where(c => settings.IsEnabled(c.Def.Id)).ToList();
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
            rows.Add(new ReviewCheckRow(def.Id, def.Title, ReviewCheckState.Ran, null, run.Findings.Count, 0));
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
    public List<string> Diagnostics { get; } = [];

    private Dictionary<LayerKey, Paths64>? _regions;
    private ConnectivityPartition? _partition;
    private DrcRegionEval? _eval;
    private ReviewBoard? _board;
    private ReviewPads? _pads;

    public ReviewContext(TraceImpedanceAnalysis.LayoutSource source, Technology tech, ReviewSettings settings)
    {
        Source = source;
        Tech = tech;
        Settings = settings;
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

    /// <summary>The pads, vias and ground the four pad checks share (brief-review-3).</summary>
    public ReviewPads Pads => _pads ??= ReviewPads.Build(this);

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
    public void Add(LayerKey? layer, string sentence, string? measured, Paths64 region)
    {
        var bounds = DrcRegions.BoundsOf(region);
        Findings.Add(new ReviewFinding(def.Id, layer, ReviewSeverity.Warning, sentence, measured,
                                       DrcRegions.ToRings(region), bounds, ReviewFinding.KeyOf(def.Id, layer, bounds))
        {
            LayerName = layer is { } l ? ctx.Layers.NameOf(l) : null,
        });
    }
}
