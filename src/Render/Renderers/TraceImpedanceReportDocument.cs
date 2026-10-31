// The Trace Impedance Analysis report as a PDF — ONE composition, called by the layout editor's
// Export and by `circuitrf impedance -o report.pdf` alike, for the reason RailReportPage gives: two
// compositions agree on the day they are written and drift the first time either is touched.
//
// ══ WHAT IS ON IT ═════════════════════════════════════════════════════════════════════════════
//
//   * A SUMMARY page: what was analysed, against what, by which method, and the verdict per layer —
//     the page a reviewer files with the fabrication package.
//   * Per layer, ONE MAP page (the board designer's own ask: one page per layer): the layer's copper
//     in grey, every trace found drawn over it coloured by its Z0 station by station, green inside
//     target ± tolerance, blue below, red above, with each trace's id and a numbered marker at every
//     finding — FILLED for a fail, HOLLOW for a warning, so the kind colours (orange = Z0, purple =
//     return path) keep their meaning across both tiers. Ids and markers sit BESIDE the traces with a
//     leader back (MapLabelPlacer), never on them, and the map frames the traces reviewed rather than
//     the whole board (round-10 report: the labels hid the copper). The colour scale, the pass band and the
//     warning band are on the page. A trace outside the review's scope (brief-impedance-2) is plain
//     copper — no colour, no label, no marker — because it is not in the report at all; the summary
//     counts it.
//   * Per layer, the TABLE of its traces and the numbered FINDINGS, continued over as many pages as
//     they need. A marker's number on the map is the finding's number in this list.
//   * ACCEPTED FINDINGS (brief-impedance-5), when there are any: each one's trace, finding, reason and
//     date, and the saved acceptances that matched nothing in this run. An accepted finding stays on
//     the map and in the lists — grey, with a check — because a report that passes by hiding findings
//     is not one anybody can rely on.
//
// Every number and sentence arrives from TraceImpedanceReport; nothing is recomputed here.
//
// ══ TWO STYLES (brief-impedance-9) ════════════════════════════════════════════════════════════
//
// FULL is the above, every trace and finding listed. A whole-board review of a large board is thousands
// of pages of it, most of them one finding written out once per occurrence, so SUMMARY keeps the same
// summary page and, per layer, the map (one marker per trace, carrying its count; ids on failing traces
// only) and ONE findings page: the findings grouped by kind × severity × width class with their count,
// the traces they hit and the worst value, then the 20 worst written out as Full writes them, with
// passing traces and junctions counted rather than listed. Every finding is counted somewhere — an
// accepted one in its group's Accepted column, never dropped. A layer with no trace reviewed has no pages
// in either style (the summary page's By layer row says so); on a multilayer board those are the planes,
// and a Full report of a 10-layer board reviewed on its two outer layers was 16 pages of blank inner maps.
//
// On either map, markers that would land on one another are one BADGE (a pill, not a disc): its count in
// Summary, and in Full the numbers it stands for, which the list after the table spells out. A map where
// no two markers meet draws exactly what it drew before badges existed.

using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>Which composition <see cref="TraceImpedanceReportDocument.Pdf(TraceImpedanceReport, TraceImpedanceReportStyle)"/> writes.</summary>
public enum TraceImpedanceReportStyle
{
    /// <summary>Every trace, junction and finding listed, per layer.</summary>
    Full,
    /// <summary>Per layer the map and one page of grouped findings, the worst written out (brief-impedance-9).</summary>
    Summary,
}

public static class TraceImpedanceReportDocument
{
    // A4 landscape, in points — the page every review report shares (ReportFurniture).
    public const int PageW = ReportFurniture.PageW;
    public const int PageH = ReportFurniture.PageH;
    private const float Margin = ReportFurniture.Margin;

    private static readonly SKColor Ink        = ReportFurniture.Ink;
    private static readonly SKColor Muted      = ReportFurniture.Muted;
    private static readonly SKColor Rule       = ReportFurniture.Rule;
    private static readonly SKColor Band       = ReportFurniture.Band;
    private static readonly SKColor Accent     = ReportFurniture.Accent;
    private static readonly SKColor CopperFill = ReportFurniture.CopperFill;
    private static readonly SKColor CopperEdge = ReportFurniture.CopperEdge;
    private static readonly SKColor PassInk    = ReportFurniture.PassInk;
    private static readonly SKColor FailInk    = ReportFurniture.FailInk;
    private static readonly SKColor WarnInk    = ReportFurniture.WarnInk;
    // The layout canvas's results overlay draws in these too (LayoutRenderer.Impedance.cs), so a marker
    // on screen and the same marker on the map page are one colour.
    internal static readonly SKColor Unsolved   = new(0x9a, 0xa0, 0xa6);
    internal static readonly SKColor GroundMark = new(0x7b, 0x2c, 0xbf);
    internal static readonly SKColor ZMark      = new(0xd9, 0x6c, 0x06);
    /// <summary>An accepted finding's marker (brief-impedance-5): grey, with a check — kept, not removed.</summary>
    internal static readonly SKColor AcceptedMark = ReportFurniture.AcceptedMark;

    /// <summary>A Full report longer than this many pages is too long to read: the panel pre-selects the Summary
    /// and the CLI, which keeps Full, warns (owner decision D1 (a), brief-impedance-9).</summary>
    public const int SummaryAbovePages = 50;

    /// <summary>The whole report as PDF bytes, <see cref="TraceImpedanceReportStyle.Full"/>.</summary>
    public static byte[] Pdf(TraceImpedanceReport report) => Pdf(report, TraceImpedanceReportStyle.Full);

    /// <summary>The whole report as PDF bytes, in <paramref name="style"/>. Complete before the caller writes
    /// anything, so a failed or cancelled export leaves no half-written file.</summary>
    public static byte[] Pdf(TraceImpedanceReport report, TraceImpedanceReportStyle style)
    {
        ArgumentNullException.ThrowIfNull(report);
        var metadata = new SKDocumentPdfMetadata
        {
            Creator = "circuitRF",
            Title = $"Trace Impedance Analysis — {report.Title}",
            Subject = "Trace impedance review",
            // The REPORT's time, not the export's: left unset, Skia stamps the wall clock, so exporting
            // one report twice gave two files, and the export gate failed whenever a second ticked over.
            Creation = report.CreatedUtc,
            Modified = report.CreatedUtc,
        };
        using var stream = new SKDynamicMemoryWStream();
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            new Writer(document, report, style).Compose();
            document.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    /// <summary>
    /// How many pages <see cref="Pdf(TraceImpedanceReport, TraceImpedanceReportStyle)"/> would write — laid out
    /// on a canvas that draws nothing, with the maps and the stackup (one page each, whatever is on them) not
    /// drawn at all, so the export can offer both styles with their lengths without writing either.
    /// </summary>
    public static int PageCount(TraceImpedanceReport report, TraceImpedanceReportStyle style)
    {
        ArgumentNullException.ThrowIfNull(report);
        var writer = new Writer(null, report, style);
        writer.Compose();
        return writer.Pages;
    }

    /// <summary>The markers <paramref name="layer"/>'s map draws in <paramref name="style"/>, after clustering —
    /// what the map tests read.</summary>
    internal static IReadOnlyList<MapMark> MapMarks(TraceImpedanceReport report, TraceLayerResult layer,
                                                     TraceImpedanceReportStyle style)
    {
        var writer = new Writer(null, report, style);
        return writer.PlaceMarks(layer, writer.MapGeometry(layer, Writer.MapBox)).Select(k => k.Mark).ToList();
    }

    /// <summary>A Summary's grouped findings on <paramref name="layer"/> — what its findings page lists.</summary>
    internal static IReadOnlyList<FindingGroup> FindingGroups(TraceImpedanceReport report, TraceLayerResult layer) =>
        new Writer(null, report, TraceImpedanceReportStyle.Summary).Groups(layer);

    /// <summary>The findings a Summary writes out in full on <paramref name="layer"/>, worst first.</summary>
    internal static IReadOnlyList<(int N, TraceRun Trace, TraceIssue Issue)> WorstFindings(TraceImpedanceReport report, TraceLayerResult layer) =>
        new Writer(null, report, TraceImpedanceReportStyle.Summary).Worst(layer);

    /// <summary>One row of a layer's grouped findings: a kind, a severity and a width class, with how many
    /// findings of it are open and how many accepted, the traces they are on, and the worst of them.</summary>
    internal sealed record FindingGroup(TraceIssueKind Kind, IssueSeverity Severity, TraceWidthClass Class,
                                        int Open, int Accepted, IReadOnlyList<string> TraceIds, string Worst);

    /// <summary>
    /// One marker on a map: a finding (Full), a trace's findings (Summary), or a BADGE standing for several
    /// that would have landed on one another. <see cref="Numbers"/> are the findings' numbers in the layer's
    /// list; <see cref="Count"/> how many findings it stands for.
    /// </summary>
    internal sealed record MapMark(SKPoint Anchor, SKPoint Along, IReadOnlyList<int> Numbers, int Count,
                                   bool Fails, SKColor Color, bool Accepted, bool Badge)
    {
        /// <summary>What the marker reads: the finding's number (Full), the count (Summary, and a Summary
        /// badge), or the numbers a Full badge stands for, as ranges.</summary>
        public string Label(TraceImpedanceReportStyle style) =>
            style == TraceImpedanceReportStyle.Summary ? Count.ToString()
            : Badge ? Ranges(Numbers) : Numbers[0].ToString();

        /// <summary>"5–16, 20" — at most three runs, then "+n".</summary>
        internal static string Ranges(IReadOnlyList<int> numbers)
        {
            var runs = new List<(int A, int B)>();
            foreach (int n in numbers.Order())
                if (runs.Count > 0 && n == runs[^1].B + 1) runs[^1] = (runs[^1].A, n);
                else runs.Add((n, n));
            string text = string.Join(", ", runs.Take(3).Select(r => r.A == r.B ? $"{r.A}" : $"{r.A}–{r.B}"));
            int rest = runs.Skip(3).Sum(r => r.B - r.A + 1);
            return rest > 0 ? $"{text} +{rest}" : text;
        }
    }

    /// <summary>The colour a Z0 is drawn in: green inside the band, blue below it, red above,
    /// saturating at a third outside the band.</summary>
    public static SKColor Z0Color(double z0, double target, double tolerancePercent)
    {
        double lo = target * (1 - tolerancePercent / 100), hi = target * (1 + tolerancePercent / 100);
        if (z0 >= lo && z0 <= hi)
        {
            // Inside: from a mid green at the edges of the band to a deeper green at the target.
            double t = 1 - Math.Abs(z0 - target) / Math.Max(1e-9, hi - target);
            return Lerp(new SKColor(0x6c, 0xc0, 0x6a), new SKColor(0x1e, 0x8e, 0x3e), t);
        }
        if (z0 < lo)
        {
            double t = Math.Clamp((lo - z0) / (0.33 * target), 0, 1);
            return Lerp(new SKColor(0x6f, 0xa8, 0xdc), new SKColor(0x1b, 0x3f, 0x9b), t);
        }
        double u = Math.Clamp((z0 - hi) / (0.33 * target), 0, 1);
        return Lerp(new SKColor(0xf0, 0x9a, 0x4c), new SKColor(0xb4, 0x1f, 0x1f), u);
    }

    private static SKColor Lerp(SKColor a, SKColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
                           (byte)(a.Blue + (b.Blue - a.Blue) * t));
    }

    private static SKFont Font(SKTypeface face, float size) => ReportFurniture.Font(face, size);

    private static string Ohms(double? z) => TraceImpedanceReport.OhmsText(z);

    private static string VerdictText(TraceVerdict v) => TraceImpedanceReport.VerdictText(v);

    private static SKColor VerdictInk(TraceVerdict v) => v switch
    {
        TraceVerdict.Pass    => PassInk,
        TraceVerdict.Warning => WarnInk,
        TraceVerdict.Fail    => FailInk,
        _ => Muted,
    };

    private static string SeverityText(TraceIssue i) => TraceImpedanceReport.SeverityText(i);

    /// <summary>The page stream: the current page, a cursor, and the running header and footer. With no
    /// document it lays the pages out on a canvas that draws nothing, to count them.</summary>
    private sealed class Writer(SKDocument? document, TraceImpedanceReport report, TraceImpedanceReportStyle style)
    {
        private SKCanvas? _canvas;
        private SKCanvas? _noDraw;
        private int _page;
        private float _y;
        private string _section = "";

        private readonly SKFont _title = Font(SkiaFonts.PlexSemiBold, 20);
        private readonly SKFont _h1 = Font(SkiaFonts.PlexSemiBold, 14);
        private readonly SKFont _h2 = Font(SkiaFonts.PlexSemiBold, 10.5f);
        private readonly SKFont _body = Font(SkiaFonts.PlexRegular, 8.5f);
        private readonly SKFont _small = Font(SkiaFonts.PlexRegular, 7f);
        private readonly SKFont _bold = Font(SkiaFonts.PlexSemiBold, 8.5f);
        private readonly SKFont _big = Font(SkiaFonts.PlexSemiBold, 22);

        private SKCanvas C => _canvas!;
        private static float Bottom => ReportFurniture.Bottom;
        private bool Counting => document is null;
        private bool IsSummary => style == TraceImpedanceReportStyle.Summary;

        /// <summary>The pages begun so far.</summary>
        public int Pages => _page;

        /// <summary>The whole report: the summary page, each layer's pages in the style's composition, the
        /// accepted findings.</summary>
        public void Compose()
        {
            Summary();
            foreach (var layer in report.Layers)
            {
                // A layer with no trace reviewed has no pages, in either style: the summary page's By layer
                // table already says so, and on a multilayer board the planes are most of the layers.
                if (layer.Traces.Count == 0 && layer.JunctionReviews.Count == 0) continue;
                LayerMap(layer);
                if (IsSummary) LayerFindings(layer);
                else LayerTable(layer);
            }
            Accepted();
            Finish();
            _noDraw?.Dispose();
        }

        private static SKPaint Fill(SKColor c) => ReportFurniture.Fill(c);
        private static SKPaint Stroke(SKColor c, float w) => ReportFurniture.Stroke(c, w);

        private void NewPage(string section)
        {
            EndPage();
            _section = section;
            _canvas = document?.BeginPage(PageW, PageH) ?? (_noDraw ??= new SKNoDrawCanvas(PageW, PageH));
            _page++;
            _y = ReportFurniture.BeginPage(C, $"Trace Impedance Analysis — {report.Title}", section, _small);
        }

        private void EndPage()
        {
            if (_canvas is null) return;
            // The units on EVERY page (owner, 2026-09-25): a page pulled out of the report and
            // filed on its own still says what its numbers are in.
            ReportFurniture.Footer(C, $"Target {report.TargetOhms:0.##} Ω ± {report.TolerancePercent:0.##} % " +
                                      $"(warning ± {report.WarningPercent:0.##} %)  ·  " +
                                      $"Units: coordinates, lengths and widths in {report.Unit}; Z0 in Ω  ·  " +
                                      $"{report.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  circuitRF", _page, _small);
            document?.EndPage();
            _canvas = null;
        }

        public void Finish() => EndPage();

        // ── text helpers ────────────────────────────────────────────────────────────────────────

        private static List<string> Wrap(string text, SKFont font, float width) => ReportFurniture.Wrap(text, font, width);

        /// <summary>Wrapped text from <paramref name="y"/> down; a line that would pass
        /// <paramref name="maxY"/> is not drawn, and the last one drawn ends in an ellipsis.</summary>
        private float Paragraph(string text, float x, float y, float width, SKFont font, SKColor color,
                                float lead = 1.4f, float maxY = float.MaxValue) =>
            ReportFurniture.Paragraph(C, text, x, y, width, font, color, lead, maxY);

        private static string Clip(string text, SKFont font, float width) => ReportFurniture.Clip(text, font, width);

        // ── the summary page ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The summary — ALWAYS one page (owner, 2026-09-25). Two columns: what was analysed and what
        /// was found on the left, the stackup it was analysed against on the right, drawn by the
        /// renderer the Technology editor's Stackup tab paints with, labels and all. Every block on the
        /// left is written against the page's bottom and clipped there rather than continued, in
        /// order of what a reviewer needs first; the method text is last because it is the same on
        /// every report.
        /// </summary>
        public void Summary()
        {
            NewPage("Summary");
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            using var accent = Fill(Accent);

            const float leftW = 408f, gap = 20f;
            float x0 = Margin, rightX = Margin + leftW + gap, rightW = PageW - Margin - rightX;

            _y += 16;
            C.DrawText("Trace Impedance Analysis", x0, _y, SKTextAlign.Left, _title, ink);
            _y += 18;
            C.DrawText(Clip(report.Title, _h1, leftW), x0, _y, SKTextAlign.Left, _h1, accent);

            // The verdict leads (R-imp5-4a), then the scope: what a reviewer filing the page needs first is
            // whether it passed, and then what "it" was.
            _y += 18;
            using (var verdictInk = Fill(report.FailCount > 0 || report.AllTraces.Any(t => t.Verdict == TraceVerdict.Unsolved)
                                         || report.AllJunctions.Any(j => j.Verdict is TraceVerdict.Fail or TraceVerdict.Unsolved) ? FailInk
                                         : report.WarningCount > 0 || report.AllJunctions.Any(j => j.Verdict == TraceVerdict.Warning)
                                             ? WarnInk : PassInk))
                C.DrawText(Clip(report.VerdictSentence, _h2, leftW), x0, _y, SKTextAlign.Left, _h2, verdictInk);
            float columnsTop = _y + 4;

            // ── left: the facts ─────────────────────────────────────────────────────────────────
            var facts = new List<(string, string)>
            {
                ("Target", $"{report.TargetOhms:0.##} Ω ± {report.TolerancePercent:0.##} %  " +
                           $"(pass {report.LowOhms:0.0}–{report.HighOhms:0.0} Ω, " +
                           $"warning {report.WarnLowOhms:0.0}–{report.WarnHighOhms:0.0} Ω)"),
                ("Technology", report.TechnologyPath is { Length: > 0 } tp
                    ? $"{report.TechnologyName}  ({Path.GetFileName(tp)})" : report.TechnologyName),
                ("Layers", report.LayersRequested.Count > 0 ? string.Join(", ", report.LayersRequested)
                                                            : string.Join(", ", report.Layers.Select(l => l.Name))),
                ("Source", report.SourcePath is { Length: > 0 } sp ? Path.GetFileName(sp) : report.Title),
                ("Created", $"{report.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {report.StationCount:N0} cross-sections, " +
                            $"{report.SolveCount:N0} solved, {report.Elapsed.TotalSeconds:0.#} s"),
            };
            // The scope in words, under Target (brief-impedance-2): what was reviewed, and how many traces
            // were left out. The one fact allowed more than a line, because it is the sentence that says
            // what a report of "1 trace, 1 pass" is a report OF.
            if (report.ScopeText.Length > 0) facts.Insert(0, ("Scope", report.ScopeText));
            float y = columnsTop;
            foreach (var (k, v) in facts)
            {
                y += 13;
                C.DrawText(k, x0, y, SKTextAlign.Left, _bold, muted);
                var lines = k == "Scope" ? Wrap(v, _body, leftW - 72) : [Clip(v, _body, leftW - 72)];
                for (int i = 0; i < Math.Min(lines.Count, 3); i++)
                {
                    if (i > 0) y += 11;
                    C.DrawText(i == 2 && lines.Count > 3 ? Clip(lines[i] + " …", _body, leftW - 72) : lines[i],
                               x0 + 72, y, SKTextAlign.Left, _body, ink);
                }
            }
            _y = y + 12;

            // A report exported after the layout was edited (brief-impedance-3 R-imp3-4a) says so first:
            // a stale PDF must not pass as a review of the artwork as it is now.
            if (report.Stale)
                _y = ReportFurniture.Notice(C, $"{TraceImpedanceReport.StaleText}: these results are of the artwork as it was.",
                                            x0, _y, leftW, _bold);

            if (report.Cancelled)
                _y = ReportFurniture.Notice(C, $"Cancelled — the {report.Layers.Count} of {report.LayersRequested.Count} layers that " +
                                               "finished before the run was stopped.", x0, _y, leftW, _bold);

            // Tiles.
            var tiles = new (string Label, string Value, SKColor Color)[]
            {
                ("Traces analysed", report.TraceCount.ToString(), Ink),
                ("Pass", report.PassCount.ToString(), PassInk),
                ("Warning", report.WarningCount.ToString(), WarnInk),
                ("Fail", report.FailCount.ToString(), FailInk),
            };
            float tileW = (leftW - 3 * 10) / 4f;
            for (int i = 0; i < tiles.Length; i++)
            {
                float x = x0 + i * (tileW + 10);
                using var band = Fill(Band);
                using var valueInk = Fill(tiles[i].Color);
                C.DrawRoundRect(new SKRect(x, _y, x + tileW, _y + 46), 4, 4, band);
                C.DrawText(tiles[i].Value, x + 10, _y + 27, SKTextAlign.Left, _big, valueInk);
                C.DrawText(tiles[i].Label, x + 10, _y + 40, SKTextAlign.Left, _small, muted);
            }
            _y += 60;

            // Per-layer table — every layer, however many; a layer is one short row.
            C.DrawText("By layer", x0, _y, SKTextAlign.Left, _h2, ink);
            _y += 5;
            float[] cols = [x0, x0 + 100, x0 + 138, x0 + 168, x0 + 210, x0 + 240, x0 + 274, x0 + 330];
            string[] heads = ["Layer", "Traces", "Pass", "Warning", "Fail", "Pours", "Out of scope", "Worst Z0 excursion"];
            Header(cols, heads, x0 + leftW);
            foreach (var l in report.Layers)
            {
                double? worst = l.Traces.SelectMany(t => new[] { t.Z0Min, t.Z0Max })
                                        .Where(z => z is not null)
                                        .Select(z => z!.Value)
                                        .OrderByDescending(z => Math.Abs(z - report.TargetOhms))
                                        .Cast<double?>().FirstOrDefault();
                Row(cols, [l.Name, l.Traces.Count.ToString(),
                           l.Traces.Count(t => t.Verdict == TraceVerdict.Pass).ToString(),
                           l.Traces.Count(t => t.Verdict == TraceVerdict.Warning).ToString(),
                           l.Traces.Count(t => t.Verdict == TraceVerdict.Fail).ToString(),
                           l.PoursSkipped.ToString(),
                           l.OutOfScope.ToString(),
                           worst is { } w ? $"{w:0.0} Ω ({(w - report.TargetOhms) / report.TargetOhms:+0%;-0%})" : "—"],
                    [Ink, Ink, PassInk, WarnInk, FailInk, Muted, Muted, Ink], right: x0 + leftW);
            }
            _y += 16;

            // Line types.
            if (_y + 20 < Bottom)
            {
                C.DrawText("Line types", x0, _y, SKTextAlign.Left, _h2, ink);
                using var smallBold = Font(SkiaFonts.PlexSemiBold, _small.Size);
                _y += 1;
                foreach (var (shortName, meaning) in TraceImpedanceReport.TypeLegend)
                {
                    if (_y + _small.Size * 1.5f > Bottom) break;
                    _y += _small.Size * 1.5f;
                    C.DrawText(shortName, x0, _y, SKTextAlign.Left, smallBold, ink);
                    C.DrawText(Clip(meaning, _small, leftW - 72), x0 + 72, _y, SKTextAlign.Left, _small, muted);
                }
                _y += 12;
            }

            foreach (var note in report.Notes)
                _y = Paragraph("• " + note, x0, _y, leftW, _small, Ink, 1.35f, Bottom) + 2;

            // Method — last, and clipped to the page.
            if (_y + 24 < Bottom)
            {
                _y += 8;
                C.DrawText("How it was measured", x0, _y, SKTextAlign.Left, _h2, ink);
                string method =
                    "Traces are found in the copper as drawn: two long parallel edges facing each other are a trace, and " +
                    "stretches meeting within about a width — through a jog, a bend, a mitre or a width step — are one " +
                    "trace. A junction, a via, a pad and the end of the copper end a trace; what continues on another layer " +
                    "is that layer's trace. Copper shorter than four of its widths is a pad unless a region, pick or net " +
                    "chooses it; then two widths is enough. Pours and planes are not analysed. Each trace is cut once per width, and each cut " +
                    "is a quasi-static cross-section solve over the stackup at right: the reference below (and above) is " +
                    "the nearest layer whose copper covers the trace's whole width there, and every other conductor near " +
                    "it is held at ground. Bend corners are not cut and not flagged. A trace FAILS when any of it is " +
                    "outside the warning band, when the nearest layer under (or over) it stops covering it part of the " +
                    "way, or when it has no return at all. It WARNS when it leaves the pass band but stays inside the " +
                    "warning band, when a plane edge runs under it, when its reference steps to another layer, or when " +
                    "part of it could not be solved." +
                    (report.MaxFrequencyHz is { } f
                        ? $" A stretch outside the warning band that is shorter than λ/{TraceImpedanceAnalysis.ShortFraction:0} " +
                          $"at {TraceImpedanceReport.Hz(f)} (λ from each cut's own effective permittivity) is electrically " +
                          "short, and warns rather than fails. A round pad hanging off a trace — a test point, directly or " +
                          $"through a via — is judged at {TraceImpedanceReport.Hz(f)} as the shunt it puts on its line: it WARNS " +
                          $"when its |S11| there is above {TraceImpedanceAnalysis.PadShuntWarnDb:0} dB or its via resonates below " +
                          $"twice that frequency, and FAILS above {TraceImpedanceAnalysis.PadShuntFailDb:0} dB or with the " +
                          "resonance inside the band."
                        : "") +
                    (report.ViaTransitionMicrons > 0
                        ? $" Trace within {report.Len(report.ViaTransitionMicrons * report.DbuPerMicron)} of the land of a via " +
                          "on it is not checked, because a plane is normally cleared round a via; every such stretch is " +
                          "named in its trace's notes, for an EM run to verify."
                        : "") +
                    " Where the nearest layer is missing only from a trace's end to the edge of the clearance hole round " +
                    "the via that trace ends on — no further than the hole's radius plus " +
                    $"{report.Len(TraceImpedanceAnalysis.ViaClearanceSlackMicrons * report.DbuPerMicron)} — the hole is the " +
                    "via's design, not a broken return: it is an Info note, outlined on the map without a number, and left " +
                    "out of the trace's result. Missing further than that is a slot, and missing away from the end a gap; " +
                    "both are still findings." +
                    " The solve is lossless and frequency-independent: a review of the geometry, not a replacement for an EM run.";
                _y = Paragraph(method, x0, _y + 1, leftW, _small, Muted, 1.35f, Bottom);
            }

            // ── right: the stackup ──────────────────────────────────────────────────────────────
            if (!Counting) DrawStackup(new SKRect(rightX, columnsTop, rightX + rightW, Bottom));
        }

        /// <summary>The stackup the analysis cut through, as the Technology editor's Stackup tab
        /// draws it — bands, vias and the tab's own text labels — scaled uniformly into
        /// <paramref name="box"/>, with the technology's name and <c>.ctech</c> file above it.</summary>
        private void DrawStackup(SKRect box)
        {
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            float y = box.Top + 13;
            C.DrawText("Stackup", box.Left, y, SKTextAlign.Left, _h2, ink);
            y += 12;
            C.DrawText(Clip(report.TechnologyName, _body, box.Width), box.Left, y, SKTextAlign.Left, _body, ink);
            if (report.TechnologyPath is { Length: > 0 } path)
            {
                y += 11;
                C.DrawText(Clip(Path.GetFileName(path), _small, box.Width), box.Left, y, SKTextAlign.Left, _small, muted);
            }
            y += 8;

            if (report.Technology is not { } tech)
            {
                C.DrawText("No stackup.", box.Left, y + 12, SKTextAlign.Left, _body, muted);
                return;
            }

            // Laid out wide enough that no spec wraps — the width the tab's own copy-as-picture uses —
            // then scaled down to the column, uniformly: a stackup stretched on one axis misstates
            // every thickness in it.
            // The narrowest layout that keeps the tab's label column BESIDE the bands (a narrower one
            // stacks the specs beneath the drawing, which the page has no height for), widened until no
            // spec wraps — so the scale-down to the column is as small as it can be.
            float w = 400f;
            while (w < 1600f && StackupScene.Build(tech, w).LabelColumnDropped) w += 20f;
            var scene = StackupScene.Build(tech, StackupScene.WidthThatFitsLabels(tech, w, 1600f));
            var area = new SKRect(box.Left, y, box.Right, box.Bottom);
            float scale = Math.Min(area.Width / Math.Max(1f, scene.Width), area.Height / Math.Max(1f, scene.Height));
            scale = Math.Min(scale, 1f);

            using (var frame = Stroke(Rule, 0.6f))
                C.DrawRoundRect(new SKRect(area.Left, area.Top, area.Left + scene.Width * scale, area.Top + scene.Height * scale), 3, 3, frame);
            C.Save();
            C.Translate(area.Left, area.Top);
            C.Scale(scale);
            StackupRenderer.Draw(C, scene, StackupRenderTheme.Light, transparentBackground: true);
            C.Restore();
        }

        private void Header(float[] cols, string[] heads, float right = PageW - Margin) =>
            _y = ReportFurniture.TableHeader(C, cols, heads, _y, right);

        private void Row(float[] cols, string[] cells, SKColor[] colors, SKColor? background = null,
                         float right = PageW - Margin) =>
            _y = ReportFurniture.TableRow(C, cols, cells, colors, _body, _y, background, right);

        // ── the map page ────────────────────────────────────────────────────────────────────────

        /// <summary>Every finding on the layer, numbered in the order the table lists them.</summary>
        private static List<(int N, TraceRun Trace, TraceIssue Issue)> Findings(TraceLayerResult layer)
        {
            var list = new List<(int, TraceRun, TraceIssue)>();
            int n = 0;
            foreach (var t in layer.Traces)
                foreach (var i in t.Issues) list.Add((++n, t, i));
            return list;
        }

        /// <summary>Where a layer's map is drawn: under its title line, left of the legend column.</summary>
        internal static readonly SKRect MapBox = new(Margin, Margin + 12 + 14 + 8, PageW - Margin - 170 - 14, PageH - Margin - 14);

        public void LayerMap(TraceLayerResult layer)
        {
            NewPage($"{layer.Name} — map");
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);

            _y += 14;
            C.DrawText(layer.Name, Margin, _y, SKTextAlign.Left, _h1, ink);
            int pass = layer.Traces.Count(t => t.Verdict == TraceVerdict.Pass);
            int warn = layer.Traces.Count(t => t.Verdict == TraceVerdict.Warning);
            int fail = layer.Traces.Count(t => t.Verdict == TraceVerdict.Fail);
            C.DrawText($"{layer.Traces.Count} traces  ·  {pass} pass  ·  {warn} warning  ·  {fail} fail" +
                       (layer.PoursSkipped > 0 ? $"  ·  {layer.PoursSkipped} pour{(layer.PoursSkipped == 1 ? "" : "s")} not analysed" : "") +
                       (layer.OutOfScope > 0 ? $"  ·  {layer.OutOfScope} outside the scope" : ""),
                       Margin + _h1.MeasureText(layer.Name) + 12, _y, SKTextAlign.Left, _body, muted);
            _y += 8;

            const float sideW = 170;
            var box = new SKRect(Margin, _y, PageW - Margin - sideW - 14, Bottom);
            // One page whatever is on it: counting pages needs none of the drawing.
            if (Counting) return;
            bool badges = DrawMap(layer, box);
            DrawLegend(new SKRect(box.Right + 14, _y, PageW - Margin, Bottom), layer, badges);
        }

        /// <summary>A map's frame on the page: the board rectangle <see cref="MapFrame"/> chooses, scaled
        /// uniformly into the box and centred.</summary>
        internal sealed class MapView(SKRect box, Bbox ext, double s, double ox, double oy)
        {
            public SKRect Box => box;
            public double S => s;
            public double ExtW => ext.MaxX - (double)ext.MinX;
            public SKPoint P(double x, double y) => new((float)(ox + (x - ext.MinX) * s), (float)(oy + (ext.MaxY - y) * s));
        }

        /// <summary>The map's frame in <paramref name="box"/>, or null when there is nothing to frame.</summary>
        internal MapView? MapGeometry(TraceLayerResult layer, SKRect box)
        {
            var ext = MapFrame(layer);
            double extW = ext.MaxX - (double)ext.MinX, extH = ext.MaxY - (double)ext.MinY;
            if (ext.IsEmpty || extW <= 0 || extH <= 0) return null;
            float pad = 8;
            double sx = (box.Width - 2 * pad) / extW, sy = (box.Height - 2 * pad) / extH;
            double s = Math.Min(sx, sy);
            double ox = box.Left + pad + 0.5 * (box.Width - 2 * pad - extW * s);
            double oy = box.Top + pad + 0.5 * (box.Height - 2 * pad - extH * s);
            return new MapView(box, ext, s, ox, oy);
        }

        /// <summary>Draws the map; true when a badge stood in for markers that would have overlapped.</summary>
        private bool DrawMap(TraceLayerResult layer, SKRect box)
        {
            using (var frame = Stroke(Rule, 0.6f)) C.DrawRect(box, frame);

            if (MapGeometry(layer, box) is not { } v) return false;
            double s = v.S;
            SKPoint P(double x, double y) => v.P(x, y);

            C.Save();
            C.ClipRect(box);

            // The copper.
            using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                foreach (var ring in layer.Copper)
                {
                    if (ring.Length < 6) continue;
                    path.MoveTo(P(ring[0], ring[1]));
                    for (int i = 2; i < ring.Length; i += 2) path.LineTo(P(ring[i], ring[i + 1]));
                    path.Close();
                }
                using var fill = Fill(CopperFill);
                using var edge = Stroke(CopperEdge, 0.3f);
                C.DrawPath(path, fill);
                C.DrawPath(path, edge);
            }

            // The scope's regions (brief-impedance-4 R-imp4-4a), dashed, so a reader sees what was
            // reviewed. A region is on its own layer's map, or every layer's when it names none
            // (brief-impedance-6).
            foreach (var region in report.Scope?.Regions ?? [])
            {
                if (region.VertexCount < 3 || !region.IsOn(layer.Name)) continue;
                using var outline = new SKPath();
                outline.MoveTo(P(region.Xy[0], region.Xy[1]));
                for (int i = 2; i < region.Xy.Length; i += 2) outline.LineTo(P(region.Xy[i], region.Xy[i + 1]));
                outline.Close();
                using var dash = Stroke(Ink, 0.7f);
                dash.PathEffect = SKPathEffect.CreateDash([3f, 2f], 0);
                C.DrawPath(outline, dash);
            }

            // The traces, station by station.
            foreach (var t in layer.Traces)
                foreach (var st in t.Stations)
                {
                    double dx = st.Uy, dy = -st.Ux;   // along the trace
                    var a = P(st.X - 0.5 * st.Length * dx, st.Y - 0.5 * st.Length * dy);
                    var b = P(st.X + 0.5 * st.Length * dx, st.Y + 0.5 * st.Length * dy);
                    float w = (float)Math.Max(st.Width * s, 1.4);
                    using var paint = Stroke(st.Z0 is { } z ? Z0Color(z, report.TargetOhms, report.TolerancePercent) : Unsolved, w);
                    paint.StrokeCap = SKStrokeCap.Butt;
                    C.DrawLine(a, b, paint);
                }

            // An out-of-band stretch is already in its colour, so only its marker marks it; a return-path
            // finding has no colour of its own and gets its stretch outlined — every one, in both styles.
            foreach (var (_, trace, issue) in Findings(layer))
            {
                if (issue.Kind == TraceIssueKind.OutOfTolerance) continue;
                var a = P(issue.X0, issue.Y0); var b = P(issue.X1, issue.Y1);
                using var span = Stroke(ColorOf(issue), 1.1f);
                span.StrokeCap = SKStrokeCap.Round;
                span.PathEffect = SKPathEffect.CreateDash([2.5f, 1.5f], 0);
                float half = (float)Math.Max(trace.Stations.FirstOrDefault()?.Width * s ?? 2, 2) / 2 + 2.2f;
                double len = Math.Max(1e-6, Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)));
                float nx = (float)(-(b.Y - a.Y) / len * half), ny = (float)((b.X - a.X) / len * half);
                // A pad shunt's box is the pad's bounds (brief-dfr29-2): ring the pad.
                if (issue.Kind == TraceIssueKind.PadShunt) C.DrawCircleDashSafe(P(issue.X, issue.Y), (float)(len / Math.Sqrt(8)) + 2.2f, span);
                else if (len < 1) { C.DrawCircleDashSafe(a, half + 1, span); }
                else
                {
                    using var path = new SKPath();
                    path.MoveTo(a.X + nx, a.Y + ny); path.LineTo(b.X + nx, b.Y + ny);
                    path.LineTo(b.X - nx, b.Y - ny); path.LineTo(a.X - nx, a.Y - ny); path.Close();
                    C.DrawPath(path, span);
                }
            }

            // A via clearance at a trace's end (brief-dfr32-5 R-dfr32-5-2) is Info, not a finding: its stretch is
            // outlined hollow, solid and grey, with no marker and no number, so the map and the findings agree.
            foreach (var trace in layer.Traces)
                foreach (var cl in trace.ViaClearances)
                {
                    var a = P(cl.X0, cl.Y0); var b = P(cl.X1, cl.Y1);
                    using var hollow = Stroke(AcceptedMark, 0.8f);
                    // Just outside a finding's outline, so one beside it over the same stretch stays readable.
                    float half = (float)Math.Max(trace.Stations.FirstOrDefault()?.Width * s ?? 2, 2) / 2 + 3.6f;
                    double len = Math.Max(1e-6, Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)));
                    if (len < 1) { C.DrawCircle(a, half + 1, hollow); continue; }
                    float nx = (float)(-(b.Y - a.Y) / len * half), ny = (float)((b.X - a.X) / len * half);
                    using var path = new SKPath();
                    path.MoveTo(a.X + nx, a.Y + ny); path.LineTo(b.X + nx, b.Y + ny);
                    path.LineTo(b.X - nx, b.Y - ny); path.LineTo(a.X - nx, a.Y - ny); path.Close();
                    C.DrawPath(path, hollow);
                }

            // Finding markers: the number in a disc BESIDE the trace with a leader to the finding — never on
            // it, where the disc hid the copper it was about (round-10 report). Every station stroke is an
            // obstacle; so is the scale bar.
            var marks = PlaceMarks(layer, v, out var placer);

            // Trace ids beside their longest piece — or the next longest where that one is crowded. A
            // Summary names only the traces that fail: on a large board every id is a label over copper.
            using var idFont = Font(SkiaFonts.PlexSemiBold, 5.5f);
            var ids = new List<(MapLabelPlacer.Placement At, string Id)>();
            foreach (var t in layer.Traces)
            {
                if (IsSummary && t.Verdict != TraceVerdict.Fail) continue;
                var anchors = t.Pieces
                    .OrderByDescending(q => Math.Pow(q.X1 - q.X0, 2) + Math.Pow(q.Y1 - q.Y0, 2)).Take(4)
                    .Select(q => (P(0.5 * (q.X0 + q.X1), 0.5 * (q.Y0 + q.Y1)),
                                  new SKPoint(P(q.X1, q.Y1).X - P(q.X0, q.Y0).X, P(q.X1, q.Y1).Y - P(q.X0, q.Y0).Y)))
                    .ToList();
                if (anchors.Count == 0) continue;
                ids.Add((placer.Place(anchors, idFont.MeasureText(t.Id) + 1.5f, 6.2f), t.Id));
            }

            // Leaders first, so every disc and id sits over them.
            using (var leader = Stroke(Muted, 0.35f))
                foreach (var at in marks.Select(k => k.At).Concat(ids.Select(k => k.At)))
                    if (at.NeedsLeader)
                    {
                        C.DrawLine(at.Anchor, MapLabelPlacer.Nearest(at.Box, at.Anchor), leader);
                        C.DrawCircle(at.Anchor, 0.55f, leader);
                    }
            foreach (var (mark, at) in marks)
            {
                string label = mark.Label(style);
                if (mark.Badge) Badge(at.Box, label, mark.Color, mark.Fails, mark.Accepted);
                else
                {
                    var c = new SKPoint(at.Box.MidX, at.Box.MidY);
                    if (mark.Accepted) Check(c, 4.3f);
                    else Marker(c, 4.3f, label, mark.Color, mark.Fails, label.Length >= 3 ? 3.4f : 4.6f);
                }
            }
            using (var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.White })
            using (var inkId = Fill(Ink))
                foreach (var (at, id) in ids)
                {
                    float x = at.Box.Left + 0.75f, y = at.Box.Bottom - 1.3f;
                    C.DrawText(id, x, y, SKTextAlign.Left, idFont, halo);
                    C.DrawText(id, x, y, SKTextAlign.Left, idFont, inkId);
                }
            C.Restore();

            // Scale bar.
            var (barW, nice) = ScaleBar(v.ExtW, s);
            ReportFurniture.DrawScaleBar(C, box, barW, report.Len(nice), _small);
            return marks.Any(k => k.Mark.Badge);
        }

        /// <summary>A finding's colour on the map: grey when accepted, purple for a return-path finding,
        /// orange for Z0.</summary>
        private static SKColor ColorOf(TraceIssue issue) =>
            issue.Accepted is not null ? AcceptedMark : issue.Kind != TraceIssueKind.OutOfTolerance ? GroundMark : ZMark;

        /// <summary>The direction along the trace at a finding, in page space: its stretch's, or the
        /// trace's at the nearest station for a finding at a point.</summary>
        private static SKPoint AlongOf(TraceRun trace, TraceIssue issue, MapView v) =>
            issue.X0 == issue.X1 && issue.Y0 == issue.Y1
                ? NearestAlong(trace, issue.X, issue.Y)
                : new SKPoint(v.P(issue.X1, issue.Y1).X - v.P(issue.X0, issue.Y0).X, v.P(issue.X1, issue.Y1).Y - v.P(issue.X0, issue.Y0).Y);

        /// <summary>A fresh placer holding what no label may cover: every station stroke and the scale bar.</summary>
        private MapLabelPlacer NewPlacer(TraceLayerResult layer, MapView v)
        {
            var box = v.Box;
            var placer = new MapLabelPlacer(new SKRect(box.Left + 2, box.Top + 2, box.Right - 2, box.Bottom - 2));
            foreach (var t in layer.Traces)
                foreach (var st in t.Stations)
                {
                    double dx = st.Uy, dy = -st.Ux;
                    placer.AddSegment(v.P(st.X - 0.5 * st.Length * dx, st.Y - 0.5 * st.Length * dy),
                                      v.P(st.X + 0.5 * st.Length * dx, st.Y + 0.5 * st.Length * dy),
                                      (float)Math.Max(st.Width * v.S, 1.4) / 2 + 0.6f);
                }
            var (barW, nice) = ScaleBar(v.ExtW, v.S);
            placer.AddRect(new SKRect(box.Left + 6, box.Bottom - 18, box.Left + 16 + barW + _small.MeasureText(report.Len(nice)), box.Bottom - 4));
            return placer;
        }

        /// <summary>The markers before any would land on another: one per finding in Full; in Summary one per
        /// trace, at its worst finding and carrying its count, those of traces close together already one badge.</summary>
        private List<MapMark> Marks(TraceLayerResult layer, MapView v)
        {
            var marks = new List<MapMark>();
            if (!IsSummary)
            {
                foreach (var (n, trace, issue) in Findings(layer))
                    marks.Add(new MapMark(v.P(issue.X, issue.Y), AlongOf(trace, issue, v), [n], 1, issue.Fails,
                                          ColorOf(issue), issue.Accepted is not null, Badge: false));
                return marks;
            }
            int first = 1;
            foreach (var t in layer.Traces)
            {
                if (t.Issues.Count == 0) continue;
                var open = t.Issues.Where(i => i.Accepted is null).ToList();
                var worst = open.Count > 0 ? ByWorst(open, i => i).First() : t.Issues[0];
                marks.Add(new MapMark(v.P(worst.X, worst.Y), AlongOf(t, worst, v), [.. Enumerable.Range(first, t.Issues.Count)],
                                      t.Issues.Count, open.Any(i => i.Fails), ColorOf(worst), open.Count == 0, Badge: false));
                first += t.Issues.Count;
            }

            // Seats (MarkerSeats, shared with the canvas's Review markers): a failing marker, then the one with
            // most findings, takes a seat; a marker anchored within a marker's width or so of a seat joins it.
            const float reach = 12f;
            var order = Enumerable.Range(0, marks.Count)
                .OrderByDescending(i => marks[i].Fails && !marks[i].Accepted).ThenByDescending(i => marks[i].Count).ToList();
            return [.. MarkerSeats.Group([.. marks.Select(m => m.Anchor)], order, reach)
                            .Select(q => q.Members.Count == 1 ? marks[q.Lead] : Merge(marks, q.Members, q.Lead))];
        }

        /// <summary>
        /// The markers placed, beside their anchors (<see cref="MapLabelPlacer"/>). Where two placed markers
        /// overlap — the placer found no free spot for one — they become one badge, and the lot is placed
        /// again, until none overlaps. A map whose markers all found room is placed exactly once, as it was
        /// before badges existed. <paramref name="placer"/> is the placer the last round filled, for the ids.
        /// </summary>
        private List<(MapMark Mark, MapLabelPlacer.Placement At)> PlaceMarks(TraceLayerResult layer, MapView v, out MapLabelPlacer placer)
        {
            var marks = Marks(layer, v);
            for (int round = 0; ; round++)
            {
                var p = NewPlacer(layer, v);
                var placed = marks.Select(m => p.Place([(m.Anchor, m.Along)], MarkWidth(m), 8.6f)).ToList();
                placer = p;
                var groups = Overlapping(placed);
                if (round == 8 || groups.Count == marks.Count) return [.. marks.Zip(placed)];
                marks = [.. groups.Select(g => g.Count == 1 ? marks[g[0]] : Merge(marks, g, Central(marks, g)))];
            }
        }

        internal List<(MapMark Mark, MapLabelPlacer.Placement At)> PlaceMarks(TraceLayerResult layer, MapView? v) =>
            v is null ? [] : PlaceMarks(layer, v, out _);

        private float MarkWidth(MapMark m) => m.Badge ? ReportFurniture.BadgeWidth(m.Label(style)) : 8.6f;

        /// <summary>The placed boxes that overlap, as groups of indices joined through any overlap — each
        /// group in order, the groups in the order of their first member.</summary>
        private static List<List<int>> Overlapping(List<MapLabelPlacer.Placement> placed)
        {
            var parent = Enumerable.Range(0, placed.Count).ToArray();
            int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            const float cell = 12f;
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < placed.Count; i++)
            {
                var b = placed[i].Box;
                for (int gx = (int)MathF.Floor(b.Left / cell); gx <= (int)MathF.Floor(b.Right / cell); gx++)
                    for (int gy = (int)MathF.Floor(b.Top / cell); gy <= (int)MathF.Floor(b.Bottom / cell); gy++)
                    {
                        var key = (gx, gy);
                        if (!grid.TryGetValue(key, out var l)) grid[key] = l = [];
                        foreach (int j in l)
                            if (placed[j].Box.IntersectsWith(b)) parent[Root(i)] = Root(j);
                        l.Add(i);
                    }
            }
            return [.. Enumerable.Range(0, placed.Count).GroupBy(Root).Select(g => g.ToList()).OrderBy(g => g[0])];
        }

        /// <summary>The member whose anchor is nearest the group's centre — where its badge is anchored.</summary>
        private static int Central(List<MapMark> marks, List<int> group)
        {
            float x = group.Average(i => marks[i].Anchor.X), y = group.Average(i => marks[i].Anchor.Y);
            return group.MinBy(i => SKPoint.Distance(marks[i].Anchor, new SKPoint(x, y)));
        }

        /// <summary>One badge for <paramref name="group"/>, anchored where <paramref name="lead"/> is: every
        /// number, the count summed, filled when any fails, in the colour of its worst member.</summary>
        private static MapMark Merge(List<MapMark> marks, List<int> group, int lead)
        {
            var members = group.Select(i => marks[i]).ToList();
            var worst = members.OrderByDescending(m => m.Fails && !m.Accepted).ThenByDescending(m => !m.Accepted).First();
            bool accepted = members.All(m => m.Accepted);
            return new MapMark(marks[lead].Anchor, marks[lead].Along, [.. members.SelectMany(m => m.Numbers).Order()],
                               members.Sum(m => m.Count), members.Any(m => m.Fails && !m.Accepted),
                               accepted ? AcceptedMark : worst.Color, accepted, Badge: true);
        }

        /// <summary>A badge: a pill in place of markers that would overlap (<see cref="ReportFurniture.Badge"/>).</summary>
        private void Badge(SKRect box, string label, SKColor color, bool fails, bool accepted) =>
            ReportFurniture.Badge(C, box, label, color, fails, accepted);

        /// <summary>Findings worst first: failing ones first, then by Z0 excursion, then by the length of trace
        /// they cover; otherwise in their order on the layer.</summary>
        private IEnumerable<T> ByWorst<T>(IEnumerable<T> items, Func<T, TraceIssue> of) =>
            items.OrderByDescending(x => of(x).Fails)
                 .ThenByDescending(x => of(x).WorstOhms is { } z ? Math.Abs(z - report.TargetOhms) : -1)
                 .ThenByDescending(x => of(x).SpanLength ?? 0);

        /// <summary>
        /// What a layer's map frames: the traces it reviewed, with room round them — not the whole
        /// board. A review scoped to one corner drew that corner a few points across on a page-wide board,
        /// every label on top of every other (round-10 report). Never smaller than 15 % of the board's
        /// larger side, so a single trace keeps the copper round it; the whole board when nothing was
        /// reviewed.
        /// </summary>
        internal Bbox MapFrame(TraceLayerResult layer)
        {
            var board = report.Extent;
            if (board.IsEmpty) return board;
            var t = Bbox.Empty;
            foreach (var tr in layer.Traces)
                foreach (var st in tr.Stations)
                {
                    long h = (long)Math.Ceiling(0.5 * Math.Max(st.Width, st.Length));
                    t = t.Union(new Bbox(st.X - h, st.Y - h, st.X + h, st.Y + h));
                }
            if (t.IsEmpty) return board;

            double boardSide = Math.Max(board.MaxX - (double)board.MinX, board.MaxY - (double)board.MinY);
            double w = t.MaxX - (double)t.MinX, hgt = t.MaxY - (double)t.MinY;
            double margin = 0.12 * Math.Max(w, hgt);
            double minSide = 0.15 * boardSide;
            double padX = Math.Max(margin, 0.5 * (minSide - w)), padY = Math.Max(margin, 0.5 * (minSide - hgt));
            var framed = new Bbox((long)(t.MinX - padX), (long)(t.MinY - padY), (long)(t.MaxX + padX), (long)(t.MaxY + padY));
            // Never beyond the artwork: there is nothing out there to show.
            return new Bbox(Math.Max(framed.MinX, board.MinX), Math.Max(framed.MinY, board.MinY),
                            Math.Min(framed.MaxX, board.MaxX), Math.Min(framed.MaxY, board.MaxY));
        }

        private static (float Width, double Length) ScaleBar(double extW, double s) => ReportFurniture.ScaleBar(extW, s);

        /// <summary>The trace's direction at the station nearest a point, in page space (Y down).</summary>
        private static SKPoint NearestAlong(TraceRun trace, long x, long y)
        {
            var st = trace.Stations.MinBy(q => Math.Pow(q.X - x, 2) + Math.Pow(q.Y - y, 2));
            return st is null ? new SKPoint(1, 0) : new SKPoint((float)st.Uy, (float)st.Ux);
        }

        /// <summary>A numbered finding marker (<see cref="ReportFurniture.Marker"/>).</summary>
        private void Marker(SKPoint c, float radius, string n, SKColor color, bool fails, float textSize) =>
            ReportFurniture.Marker(C, c, radius, n, color, fails, textSize);

        /// <summary>An accepted finding's marker (<see cref="ReportFurniture.Check"/>) — kept on the map, not removed
        /// (R-imp5-4a).</summary>
        private void Check(SKPoint c, float radius) => ReportFurniture.Check(C, c, radius);

        private void DrawLegend(SKRect r, TraceLayerResult layer, bool badges)
        {
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            float y = r.Top + 12;
            C.DrawText("Z0 along each trace", r.Left, y, SKTextAlign.Left, _h2, ink);
            y += 10;

            // The scale: target ± 45 % (wider when the warning band is), with the pass band marked and the
            // warning band's edges as a second, lighter pair of ticks.
            double span = Math.Max(0.45, report.WarningPercent / 100 + 0.1);
            double t = report.TargetOhms, lo = t * Math.Max(0.02, 1 - span), hi = t * (1 + span);
            float barH = 150, barW = 14;
            var bar = new SKRect(r.Left, y, r.Left + barW, y + barH);
            for (int i = 0; i < 60; i++)
            {
                double z = hi - (hi - lo) * (i + 0.5) / 60;
                using var p = Fill(Z0Color(z, t, report.TolerancePercent));
                C.DrawRect(bar.Left, bar.Top + barH * i / 60f, barW, barH / 60f + 0.5f, p);
            }
            float Yz(double z) => (float)(bar.Top + (hi - z) / (hi - lo) * barH);
            using (var br = Stroke(Ink, 0.8f))
            {
                foreach (var z in new[] { report.HighOhms, report.LowOhms })
                    C.DrawLine(bar.Left - 3, Yz(z), bar.Right + 3, Yz(z), br);
            }
            bool warnOnBar = report.WarnLowOhms > lo && report.WarnHighOhms < hi;
            if (warnOnBar)
                using (var wr = Stroke(Muted, 0.5f))
                {
                    wr.PathEffect = SKPathEffect.CreateDash([1.5f, 1f], 0);
                    foreach (var z in new[] { report.WarnHighOhms, report.WarnLowOhms })
                        C.DrawLine(bar.Left - 3, Yz(z), bar.Right + 3, Yz(z), wr);
                }
            var marks = warnOnBar
                ? new[] { hi, report.WarnHighOhms, report.HighOhms, t, report.LowOhms, report.WarnLowOhms, lo }
                : new[] { hi, report.HighOhms, t, report.LowOhms, lo };
            foreach (var z in marks)
                C.DrawText($"{z:0.#} Ω" + (z == hi ? "+" : z == lo ? "−" : ""), bar.Right + 7, Yz(z) + 3, SKTextAlign.Left, _small, z == t ? ink : muted);
            using (var passInk = Fill(PassInk))
                C.DrawText("pass band", bar.Right + 50, Yz(t) + 3, SKTextAlign.Left, _small, passInk);
            if (warnOnBar)
                using (var warnInk = Fill(WarnInk))
                    C.DrawText("warning band", bar.Right + 50, Yz(0.5 * (report.HighOhms + report.WarnHighOhms)) + 3,
                               SKTextAlign.Left, _small, warnInk);
            y = bar.Bottom + 12;

            void Key(SKColor c, string text, bool disc)
            {
                using var p = Fill(c);
                float top = y;
                if (disc) C.DrawCircle(r.Left + 5, top + 5.5f, 4.5f, p);
                else C.DrawRect(r.Left, top + 3.5f, 12, 4, p);
                y = Paragraph(text, r.Left + 16, top - 1.5f, r.Width - 16, _small, Muted, 1.35f) + 6;
            }
            Key(Unsolved, "not solved (no return path, or the cut left the copper)", false);
            // Pale copper reads as "not evaluated" beside the darker grey above (designer feedback round 11), and
            // most of it is deliberately not a trace: say what it is.
            Key(CopperEdge, "pale copper: not a trace (a pad or land, a taper, a pour, or a run too short for its width), so not reviewed", false);
            if (IsSummary)
            {
                Key(ZMark, "Z0 outside the pass band", true);
                Key(GroundMark, "with the stretch outlined: return path broken or partial, or the reference steps to another layer; with the pad ringed: a test pad loading the line", true);
                float top = y;
                Marker(new SKPoint(r.Left + 5, top + 5.5f), 4.5f, "3", Muted, true, 4.6f);
                Marker(new SKPoint(r.Left + 16, top + 5.5f), 4.5f, "1", Muted, false, 4.6f);
                y = Paragraph("one marker per trace, at its worst finding: the number is how many findings it has; " +
                              "filled when one fails, hollow when they only warn", r.Left + 27, top - 1.5f, r.Width - 27, _small, Muted, 1.35f) + 6;
            }
            else
            {
                Key(ZMark, "numbered: Z0 outside the pass band", true);
                Key(GroundMark, "numbered, with the stretch outlined: return path broken or partial, or the reference steps to another layer; with the pad ringed: a test pad loading the line", true);
                float top = y;
                Marker(new SKPoint(r.Left + 5, top + 5.5f), 4.5f, "1", Muted, true, 4.6f);
                Marker(new SKPoint(r.Left + 16, top + 5.5f), 4.5f, "2", Muted, false, 4.6f);
                y = Paragraph("filled: a fail; hollow: a warning", r.Left + 27, top - 1.5f, r.Width - 27, _small, Muted, 1.35f) + 6;
            }
            if (badges || IsSummary)
            {
                float top = y;
                string sample = IsSummary ? "12" : "4–9";
                float w = ReportFurniture.BadgeWidth(sample);
                Badge(new SKRect(r.Left + 0.7f, top + 1.2f, r.Left + 0.7f + w, top + 9.8f), sample, Muted, true, false);
                y = Paragraph(IsSummary
                                  ? "a badge: the markers of traces too close to draw apart, with their findings counted"
                                  : "a badge: markers too close to draw apart, with the numbers of the findings it stands for",
                              r.Left + w + 6, top - 1.5f, r.Width - w - 6, _small, Muted, 1.35f) + 6;
            }
            if (layer.Traces.Any(t => t.AcceptedCount > 0))
            {
                float top = y;
                Check(new SKPoint(r.Left + 5, top + 5.5f), 4.5f);
                y = Paragraph("grey, checked: an accepted finding — listed, with its reason, under Accepted findings",
                              r.Left + 16, top - 1.5f, r.Width - 16, _small, Muted, 1.35f) + 6;
            }
            if (layer.Traces.Any(t => t.ViaClearances.Count > 0))
            {
                float top = y;
                using (var hollow = Stroke(AcceptedMark, 0.8f))
                    C.DrawRect(r.Left, top + 2.5f, 12, 6, hollow);
                y = Paragraph("outlined grey, no number: a plane cleared round the via a trace ends on — Info, not a finding",
                              r.Left + 16, top - 1.5f, r.Width - 16, _small, Muted, 1.35f) + 6;
            }
            if (IsSummary)
                y = Paragraph("Ids are on the traces that fail.", r.Left, y - 1.5f, r.Width, _small, Muted, 1.35f) + 6;
            y += 10;

            // A Summary's findings are counted here and grouped on the next page.
            if (IsSummary)
            {
                var all = layer.Traces.SelectMany(t => t.Issues).ToList();
                if (all.Count == 0)
                {
                    C.DrawText(layer.Traces.Count == 0 ? "No traces on this layer." : "No findings on this layer.",
                               r.Left, y + 2, SKTextAlign.Left, _body, layer.Traces.Count == 0 ? muted : Fill(PassInk));
                    return;
                }
                C.DrawText("Findings", r.Left, y, SKTextAlign.Left, _h2, ink);
                int accepted = all.Count(i => i.Accepted is not null);
                Paragraph($"{all.Count:N0} on {layer.Traces.Count(t => t.Issues.Count > 0):N0} traces: " +
                          $"{all.Count(i => i.Accepted is null && i.Fails):N0} fail, {all.Count(i => i.Accepted is null && !i.Fails):N0} warning" +
                          (accepted > 0 ? $", {accepted:N0} accepted" : "") + ". Grouped, with the worst in full, on the next page.",
                          r.Left, y + 1, r.Width, _small, Ink, 1.35f);
                return;
            }

            // The worst findings on this page, briefly; the full list follows the table.
            var findings = Findings(layer);
            if (findings.Count > 0)
            {
                C.DrawText("Findings", r.Left, y, SKTextAlign.Left, _h2, ink);
                y += 3;
                foreach (var (n, trace, issue) in findings)
                {
                    string text = $"{n}. {trace.Id} {Tier(issue)} — {Brief(issue)}";
                    var lines = Wrap(text, _small, r.Width);
                    if (y + lines.Count * 9.5f > r.Bottom - 12)
                    {
                        C.DrawText($"… {findings.Count - n + 1} more, listed after the table", r.Left, y + 9, SKTextAlign.Left, _small, muted);
                        break;
                    }
                    y = Paragraph(text, r.Left, y, r.Width, _small, Ink, 1.35f) + 1.5f;
                }
            }
            else
            {
                C.DrawText(layer.Traces.Count == 0 ? "No traces on this layer." : "No findings on this layer.",
                           r.Left, y + 2, SKTextAlign.Left, _body, layer.Traces.Count == 0 ? muted : Fill(PassInk));
            }
        }

        /// <summary>A finding's tier as its line prints it: "ACCEPTED" for an accepted one, whose severity
        /// no longer counts, else WARN or FAIL.</summary>
        private static string Tier(TraceIssue i) => i.Accepted is not null ? "ACCEPTED" : SeverityText(i);

        private static string Brief(TraceIssue i) => i.Kind switch
        {
            TraceIssueKind.OutOfTolerance => i.Text.Split(" from ")[0].Replace("Z0 ", "Z0 "),
            TraceIssueKind.ReturnBroken   => "return broken: " + i.Text.Split(" is missing")[0] + " missing",
            TraceIssueKind.PartialReference => "partial reference: " + i.Text.Split(" for ")[0],
            TraceIssueKind.ReferenceStep  => i.Text.Split(" at (")[0],
            TraceIssueKind.NoReference    => "no return path",
            TraceIssueKind.PadShunt       => "pad loads the line: " + (i.Text.Split(": |S11| ") is [_, var tail] ? "|S11| " + tail.Split(" on the ")[0] : "a shunt"),
            _                             => "not solved",
        };

        // ── the Summary's findings page (brief-impedance-9) ─────────────────────────────────────

        /// <summary>How many findings a Summary writes out in full per layer.</summary>
        internal const int WorstInFull = 20;

        /// <summary>A finding kind as a group's row names it.</summary>
        internal static string KindText(TraceIssueKind kind) => TraceImpedanceReport.IssueKindText(kind);

        /// <summary>
        /// The layer's findings grouped by kind × severity × width class (owner decision D2 (a)) — the class the
        /// survey would put the trace in, so the rows name the widths the reviewer chose the scope from. Fails
        /// first, then by kind, narrowest class first. Every finding is in exactly one row.
        /// </summary>
        internal List<FindingGroup> Groups(TraceLayerResult layer)
        {
            // A class by its index, narrowest first.
            var classes = TraceImpedanceAnalysis.WidthClasses(layer.Traces, report.DbuPerMicron);
            var classOf = new Dictionary<TraceRun, int>(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < classes.Count; k++)
                foreach (var t in classes[k].Traces) classOf[t] = k;

            return [.. layer.Traces.SelectMany(t => t.Issues.Select(i => (Trace: t, Issue: i)))
                .GroupBy(f => (f.Issue.Kind, f.Issue.Severity, Class: classOf[f.Trace]))
                .OrderByDescending(g => g.Key.Severity == IssueSeverity.Fail)
                .ThenBy(g => g.Key.Kind)
                .ThenBy(g => g.Key.Class)
                .Select(g => new FindingGroup(g.Key.Kind, g.Key.Severity, classes[g.Key.Class].Class,
                    g.Count(f => f.Issue.Accepted is null), g.Count(f => f.Issue.Accepted is not null),
                    [.. g.Select(f => f.Trace.Id).Distinct()], WorstText(g.Key.Kind, [.. g.Select(f => f.Issue)])))];
        }

        /// <summary>The worst value in a group: the largest Z0 excursion, the longest stretch, the smallest
        /// coverage, the largest |S11| — each as the analysis measured it.</summary>
        private string WorstText(TraceIssueKind kind, IReadOnlyList<TraceIssue> issues)
        {
            switch (kind)
            {
                case TraceIssueKind.OutOfTolerance when issues.Where(i => i.WorstOhms is not null)
                                                              .MaxBy(i => Math.Abs(i.WorstOhms!.Value - report.TargetOhms)) is { WorstOhms: { } z }:
                    return $"{z:0.0} Ω ({(z - report.TargetOhms) / report.TargetOhms:+0%;-0%})";
                case TraceIssueKind.PartialReference when issues.Min(i => i.Coverage) is { } cov:
                    return $"{cov:P0} covered";
                case TraceIssueKind.PadShunt when issues.Max(i => i.S11Db) is { } db:
                    return $"|S11| {db:0.0} dB";
                case TraceIssueKind.ReturnBroken or TraceIssueKind.NoReference or TraceIssueKind.Unsolved
                    when issues.Max(i => i.SpanLength) is { } len:
                    return $"{report.Len(len)} long";
                default:
                    return "—";
            }
        }

        /// <summary>A class's widths in the report's unit: one width, or the range merged into it.</summary>
        private string ClassText(TraceWidthClass c)
        {
            double um = report.DbuPerMicron;
            return c.MaxMicrons - c.MinMicrons < 0.05
                ? report.Num(c.NominalMicrons * um, 0)
                : $"{report.Num(c.MinMicrons * um, 0)}–{report.Num(c.MaxMicrons * um, 0)}";
        }

        /// <summary>Ids joined while they fit <paramref name="width"/>, then "+ n more".</summary>
        private static string IdsThatFit(IReadOnlyList<string> ids, SKFont font, float width)
        {
            string text = "";
            for (int k = 0; k < ids.Count; k++)
            {
                string next = k == 0 ? ids[k] : text + ", " + ids[k];
                string tail = k + 1 < ids.Count ? $" + {ids.Count - k - 1} more" : "";
                if (font.MeasureText(next + tail) > width && k > 0) return $"{text} + {ids.Count - k} more";
                text = next;
            }
            return text;
        }

        /// <summary>The open findings on the layer, worst first, at most <see cref="WorstInFull"/>.</summary>
        internal List<(int N, TraceRun Trace, TraceIssue Issue)> Worst(TraceLayerResult layer) =>
            [.. ByWorst(Findings(layer).Where(f => f.Issue.Accepted is null), f => f.Issue).Take(WorstInFull)];

        private static readonly float[] GroupCols =
            [Margin, Margin + 150, Margin + 186, Margin + 262, Margin + 300, Margin + 346, Margin + 436];

        /// <summary>
        /// The Summary's page for a layer, after its map: the traces and junctions counted, the findings grouped,
        /// then the worst <see cref="WorstInFull"/> written exactly as Full writes them. Continued only when the
        /// groups or the worst run past the page.
        /// </summary>
        public void LayerFindings(TraceLayerResult layer)
        {
            string section = $"{layer.Name} — findings";
            NewPage(section);
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            _y += 14;
            C.DrawText($"{layer.Name}: findings", Margin, _y, SKTextAlign.Left, _h1, ink);
            _y += 4;

            // Counts, not tables.
            int Of(TraceVerdict v) => layer.Traces.Count(t => t.Verdict == v);
            string traces = $"{layer.Traces.Count:N0} traces: {Of(TraceVerdict.Pass):N0} pass, {Of(TraceVerdict.Warning):N0} warning, " +
                            $"{Of(TraceVerdict.Fail):N0} fail" + (Of(TraceVerdict.Unsolved) > 0 ? $", {Of(TraceVerdict.Unsolved):N0} not solved" : "") +
                            (layer.PoursSkipped > 0 ? $"; {layer.PoursSkipped:N0} pour{(layer.PoursSkipped == 1 ? "" : "s")} not analysed" : "") +
                            (layer.OutOfScope > 0 ? $"; {layer.OutOfScope:N0} outside the scope" : "") + ".";
            if (layer.JunctionReviews.Count > 0)
            {
                var j = layer.JunctionReviews;
                var bad = j.Where(x => x.Verdict != TraceVerdict.Pass).Select(x => x.Id).ToList();
                string head = $" {j.Count:N0} junctions: {j.Count(x => x.Verdict == TraceVerdict.Pass):N0} pass, " +
                              $"{j.Count(x => x.Verdict == TraceVerdict.Warning):N0} warning, " +
                              $"{j.Count(x => x.Verdict is TraceVerdict.Fail or TraceVerdict.Unsolved):N0} fail or not solved";
                traces += head + (bad.Count > 0 ? " (" + IdsThatFit(bad, _body, PageW - 2 * Margin - _body.MeasureText(traces + head) - 20) + ")." : ".");
            }
            int notes = layer.Traces.Sum(t => t.Notes.Count);
            if (notes > 0) traces += $" {notes:N0} note{(notes == 1 ? "" : "s")} on {layer.Traces.Count(t => t.Notes.Count > 0):N0} traces, in the Full report.";
            _y = Paragraph(traces, Margin, _y, PageW - 2 * Margin, _body, Ink) + 18;

            var groups = Groups(layer);
            if (groups.Count == 0)
            {
                C.DrawText(layer.Traces.Count == 0 ? "No traces on this layer." : "No findings on this layer.",
                           Margin, _y + 4, SKTextAlign.Left, _body, layer.Traces.Count == 0 ? muted : Fill(PassInk));
                return;
            }

            // The groups.
            string[] heads = ["Finding", "Result", $"Width ({report.Unit})", "Open", "Accepted", "Worst", "Traces"];
            C.DrawText("Grouped by kind, result and width class", Margin, _y, SKTextAlign.Left, _h2, ink);
            _y += 4;
            Header(GroupCols, heads);
            int row = 0;
            float idsW = PageW - Margin - GroupCols[^1] - 6;
            foreach (var g in groups)
            {
                if (_y + 13 > Bottom) { NewPage(section + " (continued)"); _y += 6; Header(GroupCols, heads); }
                bool fails = g.Severity == IssueSeverity.Fail;
                Row(GroupCols,
                    [KindText(g.Kind), fails ? "FAIL" : "WARN", ClassText(g.Class), g.Open.ToString("N0"),
                     g.Accepted > 0 ? g.Accepted.ToString("N0") : "", g.Worst, IdsThatFit(g.TraceIds, _body, idsW)],
                    [Ink, fails ? FailInk : WarnInk, Ink, Ink, Muted, Ink, Muted],
                    row++ % 2 == 1 ? new SKColor(0xfa, 0xfb, 0xfc) : null);
            }
            if (_y + 13 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
            using (var boldRow = Font(SkiaFonts.PlexSemiBold, _body.Size))
            {
                int sum = groups.Sum(g => g.Open), acc = groups.Sum(g => g.Accepted);
                C.DrawText("Total", GroupCols[0] + 3, _y + 9.5f, SKTextAlign.Left, boldRow, ink);
                C.DrawText(sum.ToString("N0"), GroupCols[3] + 3, _y + 9.5f, SKTextAlign.Left, boldRow, ink);
                if (acc > 0) C.DrawText(acc.ToString("N0"), GroupCols[4] + 3, _y + 9.5f, SKTextAlign.Left, boldRow, muted);
                _y += 13;
            }
            _y += 4;
            C.DrawText($"Width classes as the width survey groups them; \"Open\" counts against the trace, \"Accepted\" does not. " +
                       $"Every finding on the layer is in one row.", Margin, _y + 6, SKTextAlign.Left, _small, muted);
            _y += 12;

            // The worst, in full — the sentences a reviewer quotes.
            int open = layer.Traces.Sum(t => t.Issues.Count(i => i.Accepted is null));
            var worst = Worst(layer);
            if (worst.Count == 0) return;
            if (_y + 40 > Bottom) NewPage(section + " (continued)");
            _y += 10;
            C.DrawText(open <= WorstInFull ? "Every open finding, in full" : $"The {worst.Count} worst of {open:N0}, in full",
                       Margin, _y, SKTextAlign.Left, _h2, ink);
            _y += 2;
            // In the small type, so twenty fit under a dozen groups on one page: the sentence is Full's, word for word.
            foreach (var (n, trace, issue) in worst)
            {
                string line = $"{trace.Id} {Tier(issue)}: {issue.Text}";
                var lines = Wrap(line, _small, PageW - 2 * Margin - 18);
                if (_y + lines.Count * 9.5f + 2 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                var color = issue.Kind == TraceIssueKind.OutOfTolerance ? ZMark : GroundMark;
                Marker(new SKPoint(Margin + 4, _y + 6.6f), 3.8f, "", color, issue.Fails, 6f);
                _y = Paragraph(line, Margin + 12, _y, PageW - 2 * Margin - 18, _small, Ink, 1.35f) + 2.5f;
            }
        }

        // ── the table and the findings ──────────────────────────────────────────────────────────

        private static readonly float[] Cols =
            [Margin, Margin + 26, Margin + 96, Margin + 166, Margin + 254, Margin + 302, Margin + 350, Margin + 474, Margin + 516, Margin + 558, Margin + 600, Margin + 634, Margin + 710, Margin + 742];
        private string[] Heads
        {
            get
            {
                string u = report.Unit;
                return ["ID", $"Start ({u})", $"End ({u})", "Ends", $"Length ({u})", $"Width ({u})", "Type",
                        "Z0 min (Ω)", "Z0 max (Ω)", "Z0 avg (Ω)", "In tol.", "Reference", "Result", "#"];
            }
        }

        public void LayerTable(TraceLayerResult layer)
        {
            string section = $"{layer.Name} — traces";
            NewPage(section);
            using var ink = Fill(Ink);
            _y += 14;
            C.DrawText($"{layer.Name}: traces", Margin, _y, SKTextAlign.Left, _h1, ink);
            _y += 10;

            if (layer.Traces.Count == 0)
            {
                _y = Paragraph("No traces were found on this layer: its copper is a plane or a pour, or nothing on it has two " +
                               "long parallel edges facing each other.", Margin, _y, PageW - 2 * Margin, _body, Muted);
                JunctionTable(layer, section);
                return;
            }

            Header(Cols, Heads);
            int row = 0;
            foreach (var t in layer.Traces)
            {
                if (_y + 13 > Bottom) { NewPage(section + " (continued)"); _y += 6; Header(Cols, Heads); }
                string Pt(long x, long y) =>
                    $"{report.Num(x, 0)}, {report.Num(y, 0)}";
                string width = report.WidthText(t);
                var verdictColor = VerdictInk(t.Verdict);
                Row(Cols,
                    [t.Id, Pt(t.StartX, t.StartY), Pt(t.EndX, t.EndY), $"{t.StartsAt} / {t.EndsAt}",
                     report.Num(t.Length, 0), width,
                     t.TypeSummary, Ohms(t.Z0Min), Ohms(t.Z0Max), Ohms(t.Z0Mean), TraceImpedanceReport.InToleranceText(t),
                     string.Join(", ", t.References), VerdictText(t.Verdict), t.Issues.Count == 0 ? "" : t.Issues.Count.ToString()],
                    [Ink, Muted, Muted, Muted, Ink, Ink, Ink,
                     Zc(t.Z0Min), Zc(t.Z0Max), Zc(t.Z0Mean), Ink, Muted, verdictColor, Ink],
                    row++ % 2 == 1 ? new SKColor(0xfa, 0xfb, 0xfc) : null);
            }
            using var muted = Fill(Muted);
            _y += 12;
            C.DrawText($"Coordinates and lengths in {report.Unit}; Z0 in Ω. " +
                       "\"In tol.\" is the share of the trace's solved length inside the pass band. \"Type\" is the line type " +
                       "along the trace, with each type's share where it is more than one.",
                       Margin, _y, SKTextAlign.Left, _small, muted);
            _y += 10;
            JunctionTable(layer, section);

            // The findings, numbered as on the map.
            var findings = Findings(layer);
            var notes = layer.Traces.SelectMany(t => t.Notes.Select(n => (t.Id, n))).ToList();
            if (findings.Count == 0 && notes.Count == 0) return;
            if (_y + 40 > Bottom) { NewPage(section + " (continued)"); }
            _y += 10;
            C.DrawText("Findings", Margin, _y, SKTextAlign.Left, _h2, ink);
            _y += 2;
            foreach (var (n, trace, issue) in findings)
            {
                string line = $"{trace.Id} {Tier(issue)}: {issue.Text}" +
                              (issue.Accepted is { } a ? $" Accepted: {a.Reason}" : "");
                var lines = Wrap(line, _body, PageW - 2 * Margin - 24);
                if (_y + lines.Count * 12 + 2 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                var color = issue.Kind == TraceIssueKind.OutOfTolerance ? ZMark : GroundMark;
                if (issue.Accepted is not null) Check(new SKPoint(Margin + 6, _y + 8.5f), 5.5f);
                else Marker(new SKPoint(Margin + 6, _y + 8.5f), 5.5f, n.ToString(), color, issue.Fails, n >= 100 ? 4.5f : 6f);
                _y = Paragraph(line, Margin + 18, _y - 1, PageW - 2 * Margin - 24, _body, Ink) + 4;
            }
            if (notes.Count > 0)
            {
                if (_y + 30 > Bottom) NewPage(section + " (continued)");
                _y += 8;
                C.DrawText("Notes", Margin, _y, SKTextAlign.Left, _h2, ink);
                foreach (var (tid, note) in notes)
                {
                    if (_y + 24 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                    _y = Paragraph($"{tid}: {note}", Margin, _y, PageW - 2 * Margin, _body, Muted) + 2;
                }
            }
        }

        // ── junctions (R-dfr22-3-6) ─────────────────────────────────────────────────────────────

        private static readonly float[] JunctionCols = [Margin, Margin + 40, Margin + 90, Margin + 190, Margin + 250, Margin + 320, Margin + 380];

        /// <summary>One row per junction arm, under the trace table: the junction, its kind and centre, then each arm's
        /// trace, width, Z0 and result. Nothing when the layer has no junction.</summary>
        private void JunctionTable(TraceLayerResult layer, string section)
        {
            if (layer.JunctionReviews.Count == 0) return;
            string[] heads = ["ID", "Kind", $"At ({report.Unit})", "Arm", $"Width ({report.Unit})", "Z0 (Ω)", "Result"];
            if (_y + 40 > Bottom) NewPage(section + " (continued)");
            using var ink = Fill(Ink);
            _y += 10;
            C.DrawText("Junctions", Margin, _y, SKTextAlign.Left, _h2, ink);
            _y += 4;
            Header(JunctionCols, heads);
            int row = 0;
            foreach (var j in layer.JunctionReviews)
                foreach (var (a, k) in j.Arms.Select((a, k) => (a, k)))
                {
                    if (_y + 13 > Bottom) { NewPage(section + " (continued)"); _y += 6; Header(JunctionCols, heads); }
                    Row(JunctionCols,
                        [k == 0 ? j.Id : "", k == 0 ? j.Kind : "", k == 0 ? $"{report.Num(j.X, 0)}, {report.Num(j.Y, 0)}" : "",
                         a.TraceId ?? "arm", report.Num(a.Width, 0), Ohms(a.Z0), VerdictText(a.Verdict)],
                        [Ink, Ink, Muted, Ink, Ink, Zc(a.Z0), VerdictInk(a.Verdict)],
                        row++ % 2 == 1 ? new SKColor(0xfa, 0xfb, 0xfc) : null);
                }
            _y += 10;
        }

        // ── accepted findings (brief-impedance-5 R-imp5-4a) ─────────────────────────────────────

        /// <summary>Every accepted finding — trace, finding, reason, date — then the saved acceptances that
        /// matched nothing in this run. No page at all when there are neither.</summary>
        public void Accepted()
        {
            var accepted = report.Layers.SelectMany(l => l.Traces.SelectMany(t => t.Issues
                                  .Where(i => i.Accepted is not null).Select(i => (Layer: l.Name, Trace: t, Issue: i)))).ToList();
            if (accepted.Count == 0 && report.StaleAcceptances.Count == 0) return;

            const string section = "Accepted findings";
            NewPage(section);
            using var ink = Fill(Ink);
            _y += 14;
            C.DrawText(section, Margin, _y, SKTextAlign.Left, _h1, ink);
            _y += 4;
            _y = Paragraph("Each finding below is still true, and is still on the map and in its layer's list. The designer " +
                           "accepted it, for the reason given, so it does not count against its trace's result. An acceptance " +
                           "names the trace by its end points: a trace that is moved or re-routed is reviewed again.",
                           Margin, _y, PageW - 2 * Margin, _small, Muted, 1.35f) + 6;

            float width = PageW - 2 * Margin - 24;
            foreach (var (layer, trace, issue) in accepted)
            {
                var a = issue.Accepted!;
                string line = $"{trace.Id} ({layer}, {SeverityText(issue)}): {issue.Text}\nReason: {a.Reason}  ·  " +
                              $"accepted {a.AcceptedUtc.ToLocalTime():yyyy-MM-dd}";
                var lines = Wrap(line, _body, width);
                if (_y + lines.Count * 12 + 4 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                Check(new SKPoint(Margin + 6, _y + 8.5f), 5.5f);
                _y = Paragraph(line, Margin + 18, _y - 1, width, _body, Ink) + 6;
            }

            if (report.StaleAcceptances.Count == 0) return;
            if (_y + 40 > Bottom) NewPage(section + " (continued)");
            _y += 10;
            C.DrawText("Accepted, but matched nothing in this run", Margin, _y, SKTextAlign.Left, _h2, ink);
            _y = Paragraph("The trace was moved or re-routed, or the finding no longer occurs. Kept on the layout until it is " +
                           "removed.", Margin, _y, PageW - 2 * Margin, _small, Muted, 1.35f) + 4;
            foreach (var a in report.StaleAcceptances)
            {
                string line = $"{a.LayerName}: {a.Summary}\nReason: {a.Reason}  ·  accepted {a.AcceptedUtc.ToLocalTime():yyyy-MM-dd}";
                var lines = Wrap(line, _body, PageW - 2 * Margin);
                if (_y + lines.Count * 12 + 4 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                _y = Paragraph(line, Margin, _y, PageW - 2 * Margin, _body, Muted) + 6;
            }
        }

        private SKColor Zc(double? z) =>
            z is not { } v ? Muted
            : v >= report.LowOhms && v <= report.HighOhms ? PassInk
            : v >= report.WarnLowOhms && v <= report.WarnHighOhms ? WarnInk : FailInk;
    }
}
