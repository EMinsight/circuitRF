// `impedance` — the saved Impedance review's verdicts as review findings (brief-review-5). Which traces are
// RF is the Impedance review's question and the designer has already answered it there, in its saved
// scope, so this check grows no impedance rule: it runs the analysis the layout already describes, with
// the options `circuitrf impedance` builds from the same saved review (TraceImpedanceReview.OptionsFor),
// and lists the traces that do not pass. With no saved scope it does not run — a whole-board impedance
// run is minutes on a large board, and the Impedance panel is where that cost is stated.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class ImpedanceCheck : ReviewCheck
{
    public const string Id = "impedance";

    /// <summary>Where this check's settings and acceptances are changed.</summary>
    public const string Owner = "the Impedance panel";

    internal const string NoScope =
        "No impedance scope is saved on this layout. Choose the RF traces in the Impedance panel.";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Impedance of the RF traces",
        "The traces the saved Impedance review chose that do not pass it: FAIL, WARN or unsolved. Its target, "
      + "bands and scope are the Impedance review's, set in the Impedance panel.",
        [])
    {
        SettingsOwner = Owner,
        // A die's RF lines are reviewed as a board's are: the Impedance review is the same on both.
        Targets = [ReviewTarget.Pcb, ReviewTarget.Mmic],
    };

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var saved = ctx.Source.View.ImpedanceReview;
        if (saved?.Scope is null or { IsEmpty: true })
        {
            run.NotRun(NoScope);
            return;
        }

        var options = TraceImpedanceReview.OptionsFor(saved, ctx.Tech, name => ctx.Diagnostics.Add(
            $"impedance: the saved Impedance review names '{name}', which is not a copper layer of this technology now; it was skipped."));
        var report = TraceImpedanceAnalysis.Analyze(ctx.Source, options, Forwarding(ctx));
        if (report.Refusal is { } why) { run.NotRun(why); return; }
        foreach (string note in report.Notes) ctx.Diagnostics.Add($"impedance: {note}");

        report = TraceImpedanceAcceptance.Apply(report, ctx.Source.View.ImpedanceAcceptances);
        ctx.Impedance = report;
        run.Cancelled = report.Cancelled;

        string band = string.Create(CultureInfo.InvariantCulture, $"{report.TargetOhms:0.##} Ω ± {report.TolerancePercent:0.##} %");
        int passed = 0;
        foreach (var layer in report.Layers)
            foreach (var t in layer.Traces)
            {
                if (t.Z0Min is not null && t.Issues.Count == 0) { passed++; continue; }
                Add(ctx, run, report, t, band);
            }

        int n = report.TraceCount;
        string counted = n == 0 ? "No RF traces were found in the saved scope."
                       : passed == n ? $"{n} RF trace{(n == 1 ? "" : "s")}, all within {band}."
                       : $"{passed} of {n} RF traces pass, within {band}.";
        run.Summary = report.ScopeText.Length > 0 ? $"{counted} {report.ScopeText}" : counted;
    }

    /// <summary>One finding for a trace that does not pass: its verdict as the analysis found it (before
    /// any acceptance), worst Z0 against the band, line type, and its finding kinds, in the Impedance
    /// report's own words. Accepted when every one of its findings is.</summary>
    private static void Add(ReviewContext ctx, ReviewCheckRun run, TraceImpedanceReport report, TraceRun t, string band)
    {
        var found = TraceRun.VerdictOf(t.Z0Min is not null, t.Issues.Select(i => i with { Accepted = null }));
        string verdict = found == TraceVerdict.Unsolved ? "UNSOLVED" : TraceImpedanceReport.VerdictText(found);
        double? worst = TraceImpedanceAcceptance.WorstOhmsOf(t, report.TargetOhms);
        string worstText = worst is null ? "no Z0 solved" : $"worst Z0 {TraceImpedanceReport.OhmsText(worst)} Ω";
        var kinds = t.Issues.Select(i => i.Kind).Distinct().Select(TraceImpedanceReport.IssueKindText).ToList();
        string sentence = $"{verdict} {t.Id}: {worstText} against {band} ({t.TypeSummary})"
                        + (kinds.Count > 0 ? $"; {string.Join(", ", kinds)}" : "");

        run.Add(t.Layer, sentence, worst is null ? null : $"{TraceImpedanceReport.OhmsText(worst)} Ω", Marker(t),
                KeyOf(report, t), AcceptedOf(report, t));
    }

    /// <summary>The key of the finding for trace <paramref name="t"/>: the trace part of the Impedance review's key.</summary>
    internal static string KeyOf(TraceImpedanceReport report, TraceRun t) =>
        TraceImpedanceAcceptance.TraceKeyOf(t.LayerName, t.StartX, t.StartY, t.EndX, t.EndY, report.DbuPerMicron);

    /// <summary>The trace's finding is accepted when every one of its issues is (R-rev5-4), with their reasons.</summary>
    internal static ReviewAcceptance? AcceptedOf(TraceImpedanceReport report, TraceRun t) =>
        t.Issues.Count > 0 && t.Issues.All(i => i.Accepted is not null)
            ? new ReviewAcceptance
            {
                Key = KeyOf(report, t),
                CheckId = Id,
                Reason = string.Join("; ", t.Issues.Select(i => i.Accepted!.Reason).Distinct(StringComparer.Ordinal)),
                AcceptedUtc = t.Issues.Max(i => i.Accepted!.AcceptedUtc),
                LayerName = t.LayerName,
                Summary = string.Join(" ", t.Issues.Select(i => i.Accepted!.Summary)),
            }
            : null;

    /// <summary>The trace's issue stretches, each as wide as the trace (a pad shunt's stretch is the pad's
    /// bounds, so its box); the whole trace when it has none — an unsolved trace.</summary>
    private static Paths64 Marker(TraceRun t)
    {
        double half = Math.Max(t.WidthMax / 2, 1);
        var paths = new Paths64();
        if (t.Issues.Count == 0)
            foreach (var (x0, y0, x1, y1, w) in t.Pieces) paths.Add(Strip(x0, y0, x1, y1, Math.Max(w / 2, 1)));
        foreach (var i in t.Issues)
            paths.Add(i.Kind == TraceIssueKind.PadShunt
                ? Clipper.MakePath(new long[] {Math.Min(i.X0, i.X1), Math.Min(i.Y0, i.Y1), Math.Max(i.X0, i.X1), Math.Min(i.Y0, i.Y1),
                                    Math.Max(i.X0, i.X1), Math.Max(i.Y0, i.Y1), Math.Min(i.X0, i.X1), Math.Max(i.Y0, i.Y1)})
                : Strip(i.X0, i.Y0, i.X1, i.Y1, half));
        return Clipper.Union(paths, LayoutClipper.Rule);
    }

    /// <summary>The segment widened by <paramref name="half"/> either side and at both ends; a point is a square.</summary>
    private static Path64 Strip(long x0, long y0, long x1, long y1, double half)
    {
        double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
        (dx, dy) = len > 0 ? (dx / len * half, dy / len * half) : (half, 0);
        double nx = -dy, ny = dx;
        long R(double v) => (long)Math.Round(v);
        return Clipper.MakePath(new long[] {
            R(x0 - dx + nx), R(y0 - dy + ny), R(x1 + dx + nx), R(y1 + dy + ny),
            R(x1 + dx - nx), R(y1 + dy - ny), R(x0 - dx - nx), R(y0 - dy - ny)});
    }

    /// <summary>The analysis's own control: the review's token, and its layer-by-layer stages forwarded
    /// as this check's progress (R-rev5-6).</summary>
    private static RunControl Forwarding(ReviewContext ctx)
    {
        var parent = ctx.Control;
        return new RunControl
        {
            Token = ctx.Token,
            MinReportIntervalMs = parent?.MinReportIntervalMs ?? 40,
            Progress = parent?.Progress is { } p ? new Prefixed(p) : null,
            Notes = parent?.Notes,
        };
    }

    private sealed class Prefixed(IProgress<RunProgress> to) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => to.Report(value with { Stage = $"Reviewing: {Id} — {value.Stage}" });
    }

    /// <summary>
    /// The Impedance review's settings as this check reads them, in one line for a list of the checks
    /// (R-rev5-5): read-only here, and where to change them.
    /// </summary>
    public static string SettingsText(TraceImpedanceReview? saved)
    {
        string where = $"set in {Owner}";
        if (saved?.Scope is null or { IsEmpty: true }) return $"No impedance scope is saved, so this check does not run ({where}).";
        var s = saved.Scope;
        var chosen = new List<string>();
        static string N(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
        if (s.Regions.Count > 0) chosen.Add(N(s.Regions.Count, "region"));
        if (s.Picks.Count > 0) chosen.Add(N(s.Picks.Count, "pick"));
        if (s.Nets.Count > 0) chosen.Add(N(s.Nets.Count, "net"));
        if (s.WholeLayers.Count > 0) chosen.Add($"{string.Join(", ", s.WholeLayers)} whole");
        if (s.Widths.Count > 0) chosen.Add(N(s.Widths.Count, "width class"));
        return string.Create(CultureInfo.InvariantCulture,
            $"target {saved.TargetOhms:0.##} Ω ± {saved.TolerancePercent:0.##} %, warning ± {saved.WarningPercent:0.##} %")
             + (saved.Layers is { Count: > 0 } layers ? $", layers {string.Join(", ", layers)}" : ", every copper layer")
             + $", scope {string.Join(", ", chosen)} ({where}).";
    }
}
