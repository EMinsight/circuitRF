// Accepted findings (brief-impedance-5): a finding the designer knows about and has decided is fine —
// a connector trace 0.4 Ω past the band edge, a reference that steps under a via fence on purpose —
// recorded on the layout with its reason, so the board reaches a clean report WITHOUT widening the
// tolerance for every trace. DRC's waivers solved the same problem (layout-view.md §9A.1: per-violation,
// persisted, and visible); this is that, called Accept: the finding stays true and stays in every
// reader, marked ACCEPTED with the reason. It only stops counting against its trace's verdict.

using System.Globalization;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// One accepted finding, saved in <see cref="LayoutView.ImpedanceAcceptances"/> beside the DRC waivers
/// and on their terms: a statement about this artwork, dirty but not undoable.
/// </summary>
public sealed class TraceImpedanceAcceptance
{
    /// <summary>Which trace and which kind of finding — see <see cref="KeyOf"/>.</summary>
    public string Key { get; set; } = "";

    public TraceIssueKind Kind { get; set; }

    /// <summary>Why. Required, unlike a DRC waiver's: this is the sentence a reader of a passing report
    /// relies on.</summary>
    public string Reason { get; set; } = "";

    public DateTime AcceptedUtc { get; set; }

    public string LayerName { get; set; } = "";

    /// <summary>The finding's text when it was accepted, so an acceptance that no longer matches can
    /// still be recognised and removed — <c>DrcWaiver.RuleName</c>'s role.</summary>
    public string Summary { get; set; } = "";

    /// <summary>For <see cref="TraceIssueKind.OutOfTolerance"/>, the trace's worst Z0 when it was
    /// accepted: a later run further from target shows the finding again. Null for every other kind.</summary>
    public double? WorstOhms { get; set; }

    /// <summary>
    /// The key of <paramref name="kind"/> on the trace from (<paramref name="x0"/>, <paramref name="y0"/>)
    /// to (<paramref name="x1"/>, <paramref name="y1"/>), DBU, on <paramref name="layerName"/>:
    /// <c>layer|x,y|x,y|Kind</c>, the end points in whole µm, the lower one (by x, then y) first.
    ///
    /// <para><b>It names the TRACE and the KIND, never the stretch.</b> Trace ids (T1…) renumber with the
    /// scope, and a finding's stretch moves whenever the tolerance does, so neither can key a decision
    /// meant to outlive both. The trace's two end points do not change with either — and they DO change
    /// when the trace is moved or re-routed, which is right: a moved trace has not been reviewed, so
    /// its acceptance stops matching and is listed as stale.</para>
    /// </summary>
    public static string KeyOf(string layerName, long x0, long y0, long x1, long y1, TraceIssueKind kind, int dbuPerMicron) =>
        $"{TraceKeyOf(layerName, x0, y0, x1, y1, dbuPerMicron)}|{kind}";

    /// <summary>The key of the finding <paramref name="issue"/> on <paramref name="trace"/>.</summary>
    public static string KeyOf(TraceRun trace, TraceIssue issue, int dbuPerMicron) =>
        KeyOf(trace.LayerName, trace.StartX, trace.StartY, trace.EndX, trace.EndY, issue.Kind, dbuPerMicron);

    /// <summary><see cref="KeyOf(string, long, long, long, long, TraceIssueKind, int)"/> without its kind:
    /// the trace alone.</summary>
    public static string TraceKeyOf(string layerName, long x0, long y0, long x1, long y1, int dbuPerMicron)
    {
        if (dbuPerMicron <= 0) dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long Um(long dbu) => (long)Math.Round((double)dbu / dbuPerMicron, MidpointRounding.AwayFromZero);
        long ax = Um(x0), ay = Um(y0), bx = Um(x1), by = Um(y1);
        if (bx < ax || (bx == ax && by < ay)) (ax, ay, bx, by) = (bx, by, ax, ay);
        return string.Create(CultureInfo.InvariantCulture, $"{layerName}|{ax},{ay}|{bx},{by}");
    }

    /// <summary>The trace part of a stored <see cref="Key"/>.</summary>
    private static string TracePart(string key) => key.LastIndexOf('|') is > 0 and var bar ? key[..bar] : key;

    /// <summary>The acceptance of <paramref name="issue"/> on <paramref name="trace"/>, with
    /// <paramref name="reason"/>. The reason is required — an empty one throws; the panel refuses it
    /// before it gets here.</summary>
    public static TraceImpedanceAcceptance For(TraceImpedanceReport report, TraceRun trace, TraceIssue issue,
                                               string reason, DateTime acceptedUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(issue);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An acceptance needs a reason.", nameof(reason));
        return new TraceImpedanceAcceptance
        {
            Key = KeyOf(trace, issue, report.DbuPerMicron),
            Kind = issue.Kind,
            Reason = reason.Trim(),
            AcceptedUtc = acceptedUtc,
            LayerName = trace.LayerName,
            Summary = issue.FindingText,
            WorstOhms = issue.Kind == TraceIssueKind.OutOfTolerance ? WorstOhmsOf(trace, report.TargetOhms) : null,
        };
    }

    /// <summary>The trace's Z0 furthest from <paramref name="target"/>, or null when nothing was solved.</summary>
    public static double? WorstOhmsOf(TraceRun trace, double target) =>
        new[] { trace.Z0Min, trace.Z0Max }.Where(z => z is not null).Select(z => z!.Value)
            .OrderByDescending(z => Math.Abs(z - target)).Cast<double?>().FirstOrDefault();

    /// <summary>
    /// <paramref name="report"/> with <paramref name="acceptances"/> applied (R-imp5-2): each finding they
    /// match marked <see cref="TraceIssue.Accepted"/>, each trace's verdict taken from its UN-accepted
    /// findings, and the acceptances that matched nothing listed as
    /// <see cref="TraceImpedanceReport.StaleAcceptances"/>. Starts from the findings as the analysis made
    /// them, whatever was applied before — so the panel re-applies after every Accept with no re-run,
    /// and null or empty <paramref name="acceptances"/> gives the report as if there were none.
    ///
    /// <para>An <see cref="TraceIssueKind.OutOfTolerance"/> acceptance does not cover a WORSE finding: a
    /// trace now further from target than the accepted worst Z0 shows the finding again, un-accepted,
    /// with both numbers in its text. An acceptance whose trace was out of scope, or on a layer not
    /// analysed, is neither applied nor stale: it is not reported at all.</para>
    /// </summary>
    public static TraceImpedanceReport Apply(TraceImpedanceReport report, IReadOnlyList<TraceImpedanceAcceptance>? acceptances)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.Ok) return report;
        acceptances ??= [];
        var byKey = new Dictionary<string, TraceImpedanceAcceptance>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in acceptances) byKey[a.Key] = a;
        var matched = new HashSet<TraceImpedanceAcceptance>(ReferenceEqualityComparer.Instance);

        TraceRun ApplyTo(TraceRun trace)
        {
            var issues = new List<TraceIssue>(trace.Issues.Count);
            foreach (var found in trace.Issues)
            {
                var issue = found with { Text = found.FindingText, Finding = null, Accepted = null };
                if (!byKey.TryGetValue(KeyOf(trace, issue, report.DbuPerMicron), out var a)) { issues.Add(issue); continue; }
                matched.Add(a);
                if (issue.Kind == TraceIssueKind.OutOfTolerance && a.WorstOhms is { } then
                    && WorstOhmsOf(trace, report.TargetOhms) is { } now
                    && Math.Abs(now - report.TargetOhms) > Math.Abs(then - report.TargetOhms) + 1e-9)
                {
                    issues.Add(issue with
                    {
                        Finding = issue.Text,
                        Text = issue.Text + string.Create(CultureInfo.InvariantCulture, $" Accepted at {then:0.0} Ω, now {now:0.0} Ω."),
                    });
                    continue;
                }
                issues.Add(issue with { Accepted = a });
            }
            return trace with { Issues = issues, Verdict = TraceRun.VerdictOf(trace.Z0Min is not null, issues) };
        }

        var layers = report.Layers.Select(l => l with { Traces = [.. l.Traces.Select(ApplyTo)] }).ToList();

        // Stale: matched no finding, on a layer this run finished, and not a trace the scope left out.
        var stale = new List<TraceImpedanceAcceptance>();
        foreach (var a in acceptances)
        {
            if (matched.Contains(a)) continue;
            var layer = layers.FirstOrDefault(l => string.Equals(l.Name, a.LayerName, StringComparison.OrdinalIgnoreCase));
            if (layer is null || layer.OutOfScopeTraceKeys.Contains(TracePart(a.Key), StringComparer.OrdinalIgnoreCase)) continue;
            stale.Add(a);
        }
        return report with { Layers = layers, StaleAcceptances = stale };
    }
}
