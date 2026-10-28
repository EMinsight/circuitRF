// The Layout Review as a PDF a reviewer can circulate (brief-review-7) — ONE composition, called by the Review
// panel's Export PDF and by `circuitrf review -o report.pdf` alike, from the one LayoutReviewResult every other
// surface reads. Nothing here computes a finding, counts one differently, or re-runs a check.
//
// ══ WHAT IS ON IT ═════════════════════════════════════════════════════════════════════════════
//
//   * Page 1, the SUMMARY: the layout, its technology, the date, and one row per check — its title, the
//     thresholds it ran with (those that differ from the default marked), its findings and accepted counts, or
//     not run and why. A clean check reads "No findings", never a blank cell. A cancelled run says so here.
//   * Per check with findings, ONE MAP page: per layer one map — the board outline and that layer's copper in
//     its technology colour, faded, every finding's marker on top, numbered as the findings page lists it; markers
//     that would land on one another are one counted badge, seated by MarkerSeats (the Impedance report's and the
//     canvas's clustering). A check on one layer has the page to itself; one on several shares it, a panel per
//     layer, up to PanelsPerPage — a page per layer made a large board's edge-copper, two findings on each of ten
//     layers, eleven pages. A finding between two conductors (stacked-traces) is drawn on the upper layer with
//     the lower one's copper outlined.
//   * Per check, ONE FINDINGS page: the first 20 findings in the result's order — number, location, sentence —
//     then "… and N more"; the accepted ones after, each with its reason. Never more than the one page.
//
// There is one style and it is the summary, on brief-impedance-9's lesson: a report a large board turns into
// hundreds of pages is not a review. Its length is therefore known before anything is drawn — one page, plus two
// for each check with findings (three past PanelsPerPage layers) — and PageCount lays it out on a canvas that
// draws nothing.
//
// Copper is drawn at the page's resolution: a vertex within a fifth of a point of the last, or a ring smaller than
// a third of a point, is not drawn. A 10-layer board's planes and pours are millions of vertices nobody can see,
// and every map page carried them.

using CircuitRF.Design.Layout.Review;
using SkiaSharp;

namespace CircuitRF.Render;

public static class LayoutReviewReportDocument
{
    /// <summary>How many findings a check's findings page lists, of each kind — open, then accepted.</summary>
    public const int Listed = 20;

    /// <summary>A review finding's marker: the canvas's own review colour (the light theme's), so a marker on
    /// screen and the same marker on paper are one colour.</summary>
    internal static readonly SKColor ReviewMark = new(0xa5, 0x3c, 0xc8);

    /// <summary>The whole report as PDF bytes — complete before the caller writes anything, so a failed export
    /// leaves no half-written file.</summary>
    public static byte[] Pdf(LayoutReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var metadata = new SKDocumentPdfMetadata
        {
            Creator = "circuitRF",
            Title = $"Layout Review — {Title(result)}",
            Subject = "Layout review",
            // The RUN's time, not the export's: exporting one result twice gives one file.
            Creation = result.CreatedUtc,
            Modified = result.CreatedUtc,
        };
        using var stream = new SKDynamicMemoryWStream();
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            new Writer(document, result).Compose();
            document.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    /// <summary>How many pages <see cref="Pdf"/> writes — laid out on a canvas that draws nothing, with the maps
    /// (one page each, whatever is on them) not drawn at all, so the panel can say "Export PDF (14 pages)".</summary>
    public static int PageCount(LayoutReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var writer = new Writer(null, result);
        writer.Compose();
        return writer.Pages;
    }

    /// <summary>What the report calls the layout: its cell's name, or the file's.</summary>
    public static string Title(LayoutReviewResult result) =>
        result.SourcePath is { Length: > 0 } p ? CircuitRF.Design.Layout.Em.TraceImpedanceAnalysis.CellTitle(p) : "Untitled layout";

    /// <summary>The checks the report gives pages to: those with a warning finding, open or accepted.</summary>
    internal static IReadOnlyList<ReviewCheckRow> ChecksWithFindings(LayoutReviewResult result) =>
        [.. result.Checks.Where(c => result.Findings.Any(f => f.CheckId == c.Id && f.Severity == ReviewSeverity.Warning))];

    /// <summary>A check's maps: its findings' layers, in the result's order (stackup order).</summary>
    internal static IReadOnlyList<LayerKey?> MapLayers(LayoutReviewResult result, string checkId) =>
        [.. result.Findings.Where(f => f.CheckId == checkId && f.Severity == ReviewSeverity.Warning)
                           .Select(f => f.Layer).Distinct()];

    private sealed class Writer(SKDocument? document, LayoutReviewResult result)
    {
        private const float Margin = ReportFurniture.Margin;
        private const float Bottom = ReportFurniture.Bottom;
        private const int PageW = ReportFurniture.PageW;

        private SKCanvas? _canvas;
        private SKCanvas? _noDraw;
        private int _page;
        private float _y;

        private readonly SKFont _title = ReportFurniture.Font(SkiaFonts.PlexSemiBold, 20);
        private readonly SKFont _h1 = ReportFurniture.Font(SkiaFonts.PlexSemiBold, 14);
        private readonly SKFont _h2 = ReportFurniture.Font(SkiaFonts.PlexSemiBold, 10.5f);
        private readonly SKFont _body = ReportFurniture.Font(SkiaFonts.PlexRegular, 8.5f);
        private readonly SKFont _small = ReportFurniture.Font(SkiaFonts.PlexRegular, 7f);
        private readonly SKFont _list = ReportFurniture.Font(SkiaFonts.PlexRegular, 7.5f);
        private readonly SKFont _bold = ReportFurniture.Font(SkiaFonts.PlexSemiBold, 8.5f);
        private readonly SKFont _big = ReportFurniture.Font(SkiaFonts.PlexSemiBold, 22);

        private SKCanvas C => _canvas!;
        private bool Counting => document is null;
        private readonly string _name = Title(result);
        private string Unit => LayoutUnits.Suffix(result.DisplayUnit);
        private string Date => result.CreatedUtc == default ? "" : result.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd");

        public int Pages => _page;

        public void Compose()
        {
            Summary();
            foreach (var check in ChecksWithFindings(result))
            {
                var plan = Plan(check.Id);
                var layers = MapLayers(result, check.Id);
                int pages = (layers.Count + PanelsPerPage - 1) / PanelsPerPage;
                for (int p = 0; p < pages; p++)
                    MapPage(check, [.. layers.Skip(p * PanelsPerPage).Take(PanelsPerPage)], p + 1, pages, plan);
                FindingsPage(check, plan);
            }
            EndPage();
            _noDraw?.Dispose();
        }

        private void NewPage(string section)
        {
            EndPage();
            _canvas = document?.BeginPage(ReportFurniture.PageW, ReportFurniture.PageH)
                      ?? (_noDraw ??= new SKNoDrawCanvas(ReportFurniture.PageW, ReportFurniture.PageH));
            _page++;
            _y = ReportFurniture.BeginPage(C, $"Layout Review — {_name}", section, _small);
        }

        private void EndPage()
        {
            if (_canvas is null) return;
            // The unit on EVERY page: a page pulled out and filed on its own still says what its numbers are in.
            ReportFurniture.Footer(C, $"Units: coordinates and lengths in {Unit}  ·  {(Date.Length > 0 ? Date + "  ·  " : "")}circuitRF",
                                   _page, _small);
            document?.EndPage();
            _canvas = null;
        }

        private float Paragraph(string text, float x, float y, float width, SKFont font, SKColor color,
                                float lead = 1.4f, float maxY = float.MaxValue) =>
            ReportFurniture.Paragraph(C, text, x, y, width, font, color, lead, maxY);

        private static string Clip(string text, SKFont font, float width) => ReportFurniture.Clip(text, font, width);

        // ── page 1: the summary (R-rev7-1) ──────────────────────────────────────────────────────

        private static readonly float[] SummaryCols =
            [Margin, Margin + 130, Margin + 400, Margin + 452, Margin + 504];

        private void Summary()
        {
            NewPage("Summary");
            using var ink = ReportFurniture.Fill(ReportFurniture.Ink);
            using var muted = ReportFurniture.Fill(ReportFurniture.Muted);
            using var accent = ReportFurniture.Fill(ReportFurniture.Accent);
            float x0 = Margin, width = PageW - 2 * Margin;

            _y += 16;
            C.DrawText("Layout Review", x0, _y, SKTextAlign.Left, _title, ink);
            _y += 18;
            C.DrawText(Clip(_name, _h1, width), x0, _y, SKTextAlign.Left, _h1, accent);

            var facts = new List<(string, string)>
            {
                ("Layout", result.SourcePath is { Length: > 0 } p ? Path.GetFileName(p) : _name),
                ("Technology", result.TechnologyName ?? "—"),
                ("Reviewed as", result.Target == ReviewTarget.Mmic ? "a MMIC die" : "a PCB"),
                ("Date", Date.Length > 0 ? Date : "—"),
            };
            float y = _y + 4;
            foreach (var (k, v) in facts)
            {
                y += 13;
                C.DrawText(k, x0, y, SKTextAlign.Left, _bold, muted);
                C.DrawText(Clip(v, _body, width - 80), x0 + 80, y, SKTextAlign.Left, _body, ink);
            }
            _y = y + 12;

            if (result.Stale)
                _y = ReportFurniture.Notice(C, "The layout has changed since this review: these findings are of the artwork as it was.",
                                            x0, _y, width, _bold);

            if (result.Cancelled)
                _y = ReportFurniture.Notice(C,
                    $"Cancelled — the {result.Checks.Count(c => c.State != ReviewCheckState.Cancelled)} of {result.Checks.Count} " +
                    "checks that finished before the run was stopped, and only their findings.", x0, _y, width, _bold);

            // Tiles.
            int notRun = result.Checks.Count(c => c.State == ReviewCheckState.NotRun);
            var tiles = new (string Label, string Value, SKColor Color)[]
            {
                ("Findings", result.WarningCount.ToString("N0"), result.WarningCount > 0 ? ReviewMark : ReportFurniture.PassInk),
                ("Accepted", result.AcceptedCount.ToString("N0"), ReportFurniture.Muted),
                ("Checks with findings", result.Checks.Count(c => c.FindingCount > c.AcceptedCount).ToString(), ReportFurniture.Ink),
                ("Checks not run", notRun.ToString(), notRun > 0 ? ReportFurniture.WarnInk : ReportFurniture.Ink),
            };
            float tileW = (408f - 3 * 10) / 4f;
            for (int i = 0; i < tiles.Length; i++)
            {
                float x = x0 + i * (tileW + 10);
                using var band = ReportFurniture.Fill(ReportFurniture.Band);
                using var valueInk = ReportFurniture.Fill(tiles[i].Color);
                C.DrawRoundRect(new SKRect(x, _y, x + tileW, _y + 46), 4, 4, band);
                C.DrawText(tiles[i].Value, x + 10, _y + 27, SKTextAlign.Left, _big, valueInk);
                C.DrawText(tiles[i].Label, x + 10, _y + 40, SKTextAlign.Left, _small, muted);
            }
            _y += 60;

            // One row per check, every one, each cell wrapped in its column.
            C.DrawText("Checks", x0, _y, SKTextAlign.Left, _h2, ink);
            _y += 5;
            _y = ReportFurniture.TableHeader(C, SummaryCols, ["Check", $"Thresholds (* differs from the default)", "Findings", "Accepted", "Result"], _y);
            int row = 0;
            foreach (var check in result.Checks)
            {
                var (findings, accepted, verdict, verdictInk) = Cells(check);
                SummaryRow([check.Title, ThresholdsText(check.Id), findings, accepted, Printable(verdict)],
                           [ReportFurniture.Ink, ReportFurniture.Ink, ReportFurniture.Ink, ReportFurniture.Muted, verdictInk],
                           row++ % 2 == 1 ? ReportFurniture.Stripe : null);
            }
            _y += 12;

            var notes = new List<string>
            {
                "A review finding is a place a reviewer would want to look at, never a design-rule error. An accepted finding " +
                "is still listed, with its reason, on its check's findings page, and does not count.",
                $"Each check with findings has a map of each layer it found something on and one findings page listing the first {Listed}; the full " +
                "list is circuitrf review --all, or --json.",
            };
            if (result.StaleAcceptances.Count > 0)
                notes.Add($"{result.StaleAcceptances.Count:N0} acceptance{(result.StaleAcceptances.Count == 1 ? "" : "s")} saved on the " +
                          "layout matched nothing in this run: the place was edited, or the finding no longer occurs.");
            notes.AddRange(result.Diagnostics.Select(Printable));
            foreach (var note in notes)
            {
                if (_y + 12 > Bottom) break;
                _y = Paragraph("• " + note, x0, _y, width, _small, ReportFurniture.Muted, 1.35f, Bottom) + 2;
            }
        }

        /// <summary>A check's summary cells: its open findings, its accepted ones, and what it came to.</summary>
        private (string Findings, string Accepted, string Verdict, SKColor Ink) Cells(ReviewCheckRow check)
        {
            int open = check.FindingCount - check.AcceptedCount;
            string extra = check.Summary is { Length: > 0 } s ? $" {s}" : "";
            return check.State switch
            {
                ReviewCheckState.NotRun => ("—", "—", $"Not run: {check.NotRunReason}", ReportFurniture.WarnInk),
                ReviewCheckState.Cancelled when check.FindingCount == 0 =>
                    ("—", "—", "Cancelled before it finished.", ReportFurniture.WarnInk),
                ReviewCheckState.Cancelled =>
                    (open.ToString("N0"), check.AcceptedCount.ToString("N0"),
                     "Cancelled part-way: the findings it had made." + extra, ReportFurniture.WarnInk),
                _ when check.FindingCount == 0 => ("0", "0", "No findings." + extra, ReportFurniture.PassInk),
                _ when open == 0 => ("0", check.AcceptedCount.ToString("N0"), "Every finding accepted." + extra, ReportFurniture.PassInk),
                _ => (open.ToString("N0"), check.AcceptedCount.ToString("N0"), Layers(check.Id) + extra, ReportFurniture.Ink),
            };
        }

        /// <summary>"On Top (12), Bottom (3)." — where a check's findings are.</summary>
        private string Layers(string checkId) =>
            "On " + string.Join(", ", result.Findings
                .Where(f => f.CheckId == checkId && f.Severity == ReviewSeverity.Warning)
                .GroupBy(f => f.Layer)
                .Select(g => $"{LayerTitle(g.Key, g.First().LayerName)} ({g.Count():N0})")) + ".";

        private string LayerTitle(LayerKey? key, string? name) =>
            key is null ? "the board" : name ?? result.Artwork?.NameOf(key.Value) ?? $"{key.Value.Layer}/{key.Value.Datatype}";

        /// <summary>The thresholds a check ran with, as the reviewer reads them; one that differs from its default is
        /// starred and says the default. A check whose settings are another review's says whose.</summary>
        private string ThresholdsText(string checkId)
        {
            if (LayoutReview.FindCheck(checkId) is not { } def) return "—";
            if (def.SettingsOwner is { } owner) return $"The Impedance review's, set in {owner}.";
            if (def.Thresholds.Count == 0) return "None.";
            var settings = result.Settings ?? new ReviewSettings();
            int dpm = result.DbuPerMicron;
            return string.Join("; ", def.Thresholds.Select(t =>
            {
                double value = settings.Value(def, t, dpm), dflt = t.DefaultStored(dpm);
                string text = $"{t.Title} {ReviewSettings.Display(t, value, result.DisplayUnit, dpm)}";
                return value == dflt ? text : $"{text}* (default {ReviewSettings.Display(t, dflt, result.DisplayUnit, dpm)})";
            }));
        }

        /// <summary>A summary row whose cells wrap in their columns; as tall as its tallest cell.</summary>
        private void SummaryRow(string[] cells, SKColor[] colors, SKColor? background)
        {
            const float lead = 1.3f;
            var lines = cells.Select((text, i) =>
                ReportFurniture.Wrap(text, _small, (i + 1 < SummaryCols.Length ? SummaryCols[i + 1] : PageW - Margin) - SummaryCols[i] - 6)).ToList();
            // Never past the page: the summary is ONE page, so a very long reason is cut, not continued.
            int most = Math.Max(1, (int)((Bottom - 30 - _y - 4) / (_small.Size * lead)));
            int rows = Math.Clamp(lines.Max(l => l.Count), 1, Math.Min(4, most));
            float h = rows * _small.Size * lead + 4;
            if (background is { } bg) using (var p = ReportFurniture.Fill(bg)) C.DrawRect(Margin, _y, PageW - 2 * Margin, h, p);
            for (int i = 0; i < cells.Length; i++)
            {
                float w = (i + 1 < SummaryCols.Length ? SummaryCols[i + 1] : PageW - Margin) - SummaryCols[i] - 6;
                using var paint = ReportFurniture.Fill(colors[i]);
                for (int k = 0; k < Math.Min(rows, lines[i].Count); k++)
                {
                    string text = k == rows - 1 && lines[i].Count > rows ? Clip(lines[i][k] + " …", _small, w) : lines[i][k];
                    C.DrawText(text, SummaryCols[i] + 3, _y + 2 + (k + 1) * _small.Size * lead - 1.5f, SKTextAlign.Left, _small, paint);
                }
            }
            _y += h;
            using var rule = ReportFurniture.Stroke(ReportFurniture.Rule, 0.4f);
            C.DrawLine(Margin, _y, PageW - Margin, _y, rule);
        }

        // ── what a check's pages list (R-rev7-3) ────────────────────────────────────────────────

        /// <summary>A check's findings as its pages show them: the open ones in the result's order, the first
        /// <see cref="Listed"/> numbered 1…, and the accepted ones. The map numbers what the list lists.</summary>
        /// <param name="ListedOpen">How many of <paramref name="Open"/> the page lists: <see cref="Listed"/>, or
        /// fewer when their sentences are too long for one page — decided here, before the map numbers any.</param>
        private sealed record CheckPlan(IReadOnlyList<ReviewFinding> Open, IReadOnlyList<ReviewFinding> Accepted, int ListedOpen)
        {
            /// <summary>The number a finding carries on the map and in the list, or 0 when it is not listed.</summary>
            public int NumberOf(ReviewFinding f)
            {
                for (int i = 0; i < ListedOpen; i++) if (ReferenceEquals(Open[i], f)) return i + 1;
                return 0;
            }
        }

        /// <summary>Where a findings page's list starts, whatever its header says: the header is clipped to fit.</summary>
        private const float ListTop = Margin + 12 + 14 + 4 + 40 + 12;

        /// <summary>Where a findings page's list must end.</summary>
        private const float ListFloor = Bottom - 12;

        private const float EntryLead = 1.35f;

        private float EntryHeight(string line) =>
            ReportFurniture.Wrap(line, _list, PageW - 2 * Margin - 14).Count * _list.Size * EntryLead + 2.5f;

        private string OpenLine(ReviewFinding f) => $"{LayerTitle(f.Layer, f.LayerName)} {Where(f.Marker)}: {Printable(f.Sentence)}";

        /// <summary>A sentence in glyphs the report's face has: the review writes a drill's diameter with ⌀, which
        /// the face lacks, so it is written Ø, as a drawing writes it.</summary>
        private static string Printable(string text) => text.Replace('⌀', 'Ø');

        private CheckPlan Plan(string checkId)
        {
            var all = result.Findings.Where(f => f.CheckId == checkId && f.Severity == ReviewSeverity.Warning).ToList();
            List<ReviewFinding> open = [.. all.Where(f => !f.IsAccepted)];
            int listed = 0;
            float y = ListTop;
            for (; listed < Math.Min(Listed, open.Count); listed++)
            {
                // Room kept for the "… and N more" line under the list when one will follow.
                float tail = listed + 1 < open.Count ? 14 : 0;
                float h = EntryHeight(OpenLine(open[listed]));
                if (y + h + tail > ListFloor) break;
                y += h;
            }
            return new CheckPlan(open, [.. all.Where(f => f.IsAccepted)], listed);
        }

        private string Where(Bbox b) =>
            $"({LayoutUnits.Format((b.MinX + b.MaxX) / 2, result.DisplayUnit, result.DbuPerMicron, 3)}, " +
            $"{LayoutUnits.Format((b.MinY + b.MaxY) / 2, result.DisplayUnit, result.DbuPerMicron, 3)})";

        // ── a map page (R-rev7-2) ───────────────────────────────────────────────────────────────

        private const float SideW = 170;

        /// <summary>How many layers' maps share a map page, at most (R-rev7-4: about two pages per check, however
        /// many layers its findings are on). A board with more copper layers than this gets a second map page.</summary>
        internal const int PanelsPerPage = 12;

        /// <summary>The grid a map page lays <paramref name="n"/> panels out on: one map fills the page; more share it.</summary>
        private static (int Cols, int Rows) Grid(int n) => n switch
        {
            <= 1 => (1, 1),
            2 => (2, 1),
            <= 4 => (2, 2),
            <= 6 => (3, 2),
            <= 9 => (3, 3),
            _ => (4, 3),
        };

        /// <summary>One map page of a check: a panel per layer in <paramref name="layers"/>, each that layer's copper
        /// and its findings, and one key for the page.</summary>
        private void MapPage(ReviewCheckRow check, IReadOnlyList<LayerKey?> layers, int page, int pages, CheckPlan plan)
        {
            var onPage = result.Findings.Where(f => f.CheckId == check.Id && f.Severity == ReviewSeverity.Warning
                                                    && layers.Contains(f.Layer)).ToList();
            string where = layers.Count == 1 ? LayerTitle(layers[0], onPage.FirstOrDefault()?.LayerName) : "map";
            NewPage($"{check.Title} — {where}" + (pages > 1 ? $" ({page} of {pages})" : ""));
            using var ink = ReportFurniture.Fill(ReportFurniture.Ink);
            using var muted = ReportFurniture.Fill(ReportFurniture.Muted);
            _y += 14;
            string head = layers.Count == 1 ? $"{check.Title} — {where}" : check.Title;
            C.DrawText(head, Margin, _y, SKTextAlign.Left, _h1, ink);
            C.DrawText(Counts(onPage) + (layers.Count > 1 ? $" on {layers.Count} layers" : ""),
                       Margin + _h1.MeasureText(head) + 12, _y, SKTextAlign.Left, _body, muted);
            _y += 8;

            // One page whatever is on it: counting pages needs none of the drawing.
            if (Counting) return;
            var area = new SKRect(Margin, _y, PageW - Margin - SideW - 14, Bottom);
            var def = LayoutReview.FindCheck(check.Id);
            var (cols, rows) = Grid(layers.Count);
            const float gap = 10, titleH = 12;
            float w = (area.Width - (cols - 1) * gap) / cols, h = (area.Height - (rows - 1) * gap) / rows;
            bool badges = false, unnumbered = false, anyLower = false;
            var drawnLayers = new List<LayerKey>();
            for (int k = 0; k < layers.Count; k++)
            {
                var layer = layers[k];
                var findings = onPage.Where(f => f.Layer == layer).ToList();
                float x = area.Left + k % cols * (w + gap), y = area.Top + k / cols * (h + gap);
                var box = new SKRect(x, y, x + w, y + h);
                if (layers.Count > 1)
                {
                    C.DrawText(Clip($"{LayerTitle(layer, findings.FirstOrDefault()?.LayerName)}  ·  {Counts(findings)}", _bold, w),
                               x, y + 9, SKTextAlign.Left, _bold, ink);
                    box = new SKRect(x, y + titleH, x + w, y + h);
                }
                LayerKey? drawn = layer ?? result.Artwork?.TopCopper;
                if (drawn is { } d) drawnLayers.Add(d);
                IReadOnlyList<LayerKey> lower = def?.SpansAdjacentCopper == true && layer is { } up && result.Artwork is { } art
                    ? art.LowerNeighbour(up) : [];
                anyLower |= lower.Count > 0;
                var (b, u) = DrawMap(box, findings, plan, drawn, lower);
                badges |= b;
                unnumbered |= u;
            }
            Legend(new SKRect(area.Right + 14, area.Top, PageW - Margin, Bottom), check, drawnLayers, anyLower, onPage,
                   badges, unnumbered, layers.Contains(null));
        }

        /// <summary>"12 findings  ·  3 accepted".</summary>
        private static string Counts(IReadOnlyList<ReviewFinding> findings)
        {
            int accepted = findings.Count(f => f.IsAccepted), open = findings.Count - accepted;
            return $"{open:N0} finding{(open == 1 ? "" : "s")}" + (accepted > 0 ? $"  ·  {accepted:N0} accepted" : "");
        }

        /// <summary>The faded copper colour of a layer: its technology colour mixed with white.</summary>
        private SKColor LayerColor(LayerKey key, double keep)
        {
            var c = result.Artwork?.ColorOf(key) is { } rgba ? new SKColor(rgba.R, rgba.G, rgba.B) : ReportFurniture.CopperEdge;
            byte Mix(byte v) => (byte)Math.Round(255 + (v - 255) * keep);
            return new SKColor(Mix(c.Red), Mix(c.Green), Mix(c.Blue));
        }

        /// <summary>Draws the map; whether a badge stood in for markers too close to draw apart, and whether any open
        /// finding went unnumbered (beyond the list).</summary>
        private (bool Badges, bool Unnumbered) DrawMap(SKRect box, List<ReviewFinding> findings, CheckPlan plan,
                                                       LayerKey? drawn, IReadOnlyList<LayerKey> lower)
        {
            using (var frame = ReportFurniture.Stroke(ReportFurniture.Rule, 0.6f)) C.DrawRect(box, frame);
            var art = result.Artwork;
            var board = art?.Board ?? [];
            var copper = drawn is { } d && art is not null ? art.Copper(d) : [];
            var below = lower.SelectMany(k => art!.Copper(k)).ToList();

            // The frame: the board; with no board drawn, the copper and the findings.
            var ext = Bounds(board);
            if (ext.IsEmpty) ext = Bounds(copper).Union(Bounds(below));
            foreach (var f in findings) if (!f.Marker.IsEmpty) ext = ext.Union(f.Marker);
            double extW = ext.MaxX - (double)ext.MinX, extH = ext.MaxY - (double)ext.MinY;
            if (ext.IsEmpty || extW <= 0 && extH <= 0) return (false, false);
            extW = Math.Max(extW, 1); extH = Math.Max(extH, 1);
            // The scale bar has a strip of its own under the map, so it is never drawn over copper.
            const float pad = 8, barStrip = 14;
            double s = Math.Min((box.Width - 2 * pad) / extW, (box.Height - 2 * pad - barStrip) / extH);
            double ox = box.Left + pad + 0.5 * (box.Width - 2 * pad - extW * s);
            double oy = box.Top + pad + 0.5 * (box.Height - 2 * pad - barStrip - extH * s);
            SKPoint P(double x, double y) => new((float)(ox + (x - ext.MinX) * s), (float)(oy + (ext.MaxY - y) * s));

            SKPath PathOf(IEnumerable<long[]> rings, bool decimate = false)
            {
                var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var ring in rings)
                {
                    if (ring.Length < 6) continue;
                    if (decimate && !Visible(ring)) continue;
                    var last = P(ring[0], ring[1]);
                    path.MoveTo(last);
                    for (int i = 2; i < ring.Length; i += 2)
                    {
                        var q = P(ring[i], ring[i + 1]);
                        // At the page's resolution: a vertex within a fifth of a point of the last one drawn is not one
                        // anybody can see, and a large board's pours and antipads are millions of them.
                        if (decimate && i + 2 < ring.Length && Math.Abs(q.X - last.X) < 0.2f && Math.Abs(q.Y - last.Y) < 0.2f) continue;
                        path.LineTo(q);
                        last = q;
                    }
                    path.Close();
                }
                return path;
            }

            // A ring smaller than a third of a point both ways is not drawn: invisible, and a hole that small left
            // filled is invisible too.
            bool Visible(long[] ring)
            {
                long x0 = long.MaxValue, y0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue;
                for (int i = 0; i + 1 < ring.Length; i += 2)
                {
                    x0 = Math.Min(x0, ring[i]); x1 = Math.Max(x1, ring[i]);
                    y0 = Math.Min(y0, ring[i + 1]); y1 = Math.Max(y1, ring[i + 1]);
                }
                return (x1 - x0) * s >= 0.33 || (y1 - y0) * s >= 0.33;
            }

            C.Save();
            C.ClipRect(box);
            if (board.Count > 0)
                using (var path = PathOf(board))
                using (var fill = ReportFurniture.Fill(new SKColor(0xfb, 0xfb, 0xf8)))
                    C.DrawPath(path, fill);
            if (drawn is { } key)
                using (var path = PathOf(copper, decimate: true))
                using (var fill = ReportFurniture.Fill(LayerColor(key, 0.28)))
                using (var edge = ReportFurniture.Stroke(LayerColor(key, 0.5), 0.3f))
                {
                    C.DrawPath(path, fill);
                    C.DrawPath(path, edge);
                }
            if (lower.Count > 0)
                using (var path = PathOf(below, decimate: true))
                using (var edge = ReportFurniture.Stroke(ReportFurniture.AcceptedMark, 0.3f))
                {
                    edge.PathEffect = SKPathEffect.CreateDash([2f, 1.2f], 0);
                    C.DrawPath(path, edge);
                }
            if (board.Count > 0)
                using (var path = PathOf(board))
                using (var outline = ReportFurniture.Stroke(ReportFurniture.Ink, 0.7f))
                    C.DrawPath(path, outline);

            // The marked regions, outlined; a two-point ring is a line (a pad to its nearest via).
            foreach (var f in findings)
            {
                var colour = f.IsAccepted ? ReportFurniture.AcceptedMark : ReviewMark;
                using var stroke = ReportFurniture.Stroke(colour, 0.8f);
                stroke.StrokeCap = SKStrokeCap.Round;
                foreach (var ring in f.MarkerRings)
                    if (ring.Length == 4) C.DrawLine(P(ring[0], ring[1]), P(ring[2], ring[3]), stroke);
                using var path = PathOf(f.MarkerRings);
                C.DrawPath(path, stroke);
            }

            // The markers, at each marked region's centre: seated by MarkerSeats, a listed finding leading first
            // so a badge sits where the list can name something, then an open one before an accepted one.
            var anchors = findings.Select(f => f.Marker.IsEmpty ? new SKPoint(box.MidX, box.MidY) : AnchorOf(f, P)).ToList();
            var order = Enumerable.Range(0, findings.Count)
                .OrderBy(i => plan.NumberOf(findings[i]) is var n && n > 0 ? n : int.MaxValue)
                .ThenBy(i => findings[i].IsAccepted ? 1 : 0).ThenBy(i => i);
            bool badges = false, unnumbered = false;
            const float r = 4.3f;
            foreach (var (lead, members) in MarkerSeats.Group(anchors, order, 2 * r + 1.5f))
            {
                var at = anchors[lead];
                if (members.Count == 1)
                {
                    var f = findings[lead];
                    int n = plan.NumberOf(f);
                    if (f.IsAccepted) ReportFurniture.Check(C, at, r);
                    else ReportFurniture.Marker(C, at, r, n > 0 ? n.ToString() : "", ReviewMark, false, n >= 10 ? 4.1f : 4.6f);
                    unnumbered |= !f.IsAccepted && n == 0;
                    continue;
                }
                badges = true;
                string label = members.Count.ToString("N0");
                float w = ReportFurniture.BadgeWidth(label);
                bool allAccepted = members.All(i => findings[i].IsAccepted);
                ReportFurniture.Badge(C, new SKRect(at.X - w / 2, at.Y - 4.3f, at.X + w / 2, at.Y + 4.3f), label,
                                      allAccepted ? ReportFurniture.AcceptedMark : ReviewMark, false, allAccepted);
            }
            C.Restore();

            var (barW, nice) = ReportFurniture.ScaleBar(extW, s);
            ReportFurniture.DrawScaleBar(C, box, barW,
                ReviewText.Length((long)Math.Round(nice), result.DisplayUnit, result.DbuPerMicron), _small);
            return (badges, unnumbered);
        }

        /// <summary>Where a finding's marker sits: its region's centre, or — when the centre is not in the region, as
        /// with a band round the board's edge — the region's corner nearest it, so the marker is on what it marks.</summary>
        private static SKPoint AnchorOf(ReviewFinding f, Func<double, double, SKPoint> P)
        {
            double cx = 0.5 * (f.Marker.MinX + (double)f.Marker.MaxX), cy = 0.5 * (f.Marker.MinY + (double)f.Marker.MaxY);
            bool inside = false;
            double best = double.PositiveInfinity, bx = cx, by = cy;
            foreach (var ring in f.MarkerRings)
            {
                int n = ring.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = ring[2 * i], yi = ring[2 * i + 1], xj = ring[2 * j], yj = ring[2 * j + 1];
                    if ((yi > cy) != (yj > cy) && cx < (xj - xi) * (cy - yi) / (yj - yi) + xi) inside = !inside;
                    double d = (xi - cx) * (xi - cx) + (yi - cy) * (yi - cy);
                    if (d < best) { best = d; bx = xi; by = yi; }
                }
            }
            // A line (a pad to its via) has no inside: its middle is on it.
            return inside || f.MarkerRings.All(r => r.Length <= 4) ? P(cx, cy) : P(bx, by);
        }

        private static Bbox Bounds(IEnumerable<long[]> rings)
        {
            var b = Bbox.Empty;
            foreach (var ring in rings)
                for (int i = 0; i + 1 < ring.Length; i += 2) b = b.Union(new Bbox(ring[i], ring[i + 1], ring[i], ring[i + 1]));
            return b;
        }

        private void Legend(SKRect r, ReviewCheckRow check, IReadOnlyList<LayerKey> drawn, bool lower,
                            List<ReviewFinding> findings, bool badges, bool unnumbered, bool boardFindings)
        {
            using var ink = ReportFurniture.Fill(ReportFurniture.Ink);
            float y = r.Top + 12;
            C.DrawText("Key", r.Left, y, SKTextAlign.Left, _h2, ink);
            y += 6;

            void Key(Action<SKPoint> swatch, string text)
            {
                float top = y;
                swatch(new SKPoint(r.Left + 5, top + 5.5f));
                y = Paragraph(text, r.Left + 16, top - 1.5f, r.Width - 16, _small, ReportFurniture.Muted, 1.35f) + 6;
            }
            void Square(SKPoint at, SKPaint p) => C.DrawRect(at.X - 5, at.Y - 2.5f, 10, 5, p);

            if (drawn.Count > 0 && result.Artwork is { } art)
            {
                using var fill = ReportFurniture.Fill(LayerColor(drawn[0], 0.28));
                Key(at => Square(at, fill), (drawn.Distinct().Count() == 1 ? $"{art.NameOf(drawn[0])} copper" : "each layer's copper, in its colour")
                                            + (boardFindings ? "; under a finding of the board, the top copper" : ""));
            }
            if (lower)
            {
                using var edge = ReportFurniture.Stroke(ReportFurniture.AcceptedMark, 0.8f);
                edge.PathEffect = SKPathEffect.CreateDash([2f, 1.2f], 0);
                Key(at => Square(at, edge), "the copper of the layer below, outlined");
            }
            if (result.Artwork?.Board.Count > 0)
            {
                using var edge = ReportFurniture.Stroke(ReportFurniture.Ink, 0.8f);
                Key(at => C.DrawLine(at.X - 5, at.Y, at.X + 5, at.Y, edge), "the board outline");
            }
            Key(at => ReportFurniture.Marker(C, at, 4.5f, "1", ReviewMark, false, 4.6f),
                "a finding, its place outlined: the number is its line on the findings page");
            if (unnumbered)
                Key(at => ReportFurniture.Marker(C, at, 4.5f, "", ReviewMark, false, 4.6f),
                    $"a finding beyond the {Listed} the findings page lists");
            if (badges)
            {
                float w = ReportFurniture.BadgeWidth("12");
                Key(at => ReportFurniture.Badge(C, new SKRect(at.X - w / 2, at.Y - 4.3f, at.X + w / 2, at.Y + 4.3f), "12", ReviewMark, false, false),
                    "a badge: findings too close to draw apart, counted");
            }
            if (findings.Any(f => f.IsAccepted))
                Key(at => ReportFurniture.Check(C, at, 4.5f), "grey, checked: an accepted finding, listed with its reason");

            y += 18;
            if (LayoutReview.FindCheck(check.Id) is { } def)
            {
                C.DrawText("What it marks", r.Left, y, SKTextAlign.Left, _h2, ink);
                Paragraph(def.Description, r.Left, y + 1, r.Width, _small, ReportFurniture.Muted, 1.35f, r.Bottom);
            }
        }

        // ── a findings page (R-rev7-3) ──────────────────────────────────────────────────────────

        private void FindingsPage(ReviewCheckRow check, CheckPlan plan)
        {
            string section = $"{check.Title} — findings";
            NewPage(section);
            using var ink = ReportFurniture.Fill(ReportFurniture.Ink);
            using var muted = ReportFurniture.Fill(ReportFurniture.Muted);
            float x0 = Margin, width = PageW - 2 * Margin, textX = x0 + 14, textW = width - 14;
            _y += 14;
            C.DrawText($"{check.Title}: findings", x0, _y, SKTextAlign.Left, _h1, ink);
            _y += 4;
            string counts = $"{plan.Open.Count:N0} finding{(plan.Open.Count == 1 ? "" : "s")}" +
                            (plan.Accepted.Count > 0 ? $", {plan.Accepted.Count:N0} accepted" : "") +
                            $". Thresholds: {ThresholdsText(check.Id)}" + (check.Summary is { Length: > 0 } s ? $" {s}" : "");
            Paragraph(counts, x0, _y, width, _body, ReportFurniture.Ink, 1.4f, _y + 40);
            const string fullList = "The full list, with every finding's location and sentence: circuitrf review <layout> --all, or --json.";
            C.DrawText(Clip(fullList, _small, width), x0, Bottom, SKTextAlign.Left, _small, muted);
            _y = ListTop;

            // Never more than this page: Plan chose how many entries fit, so the map numbers exactly these.
            float floor = ListFloor;
            if (plan.Open.Count > 0)
            {
                C.DrawText($"Locations in {Unit}, the marked region's centre", x0, _y, SKTextAlign.Left, _small, muted);
                for (int i = 0; i < plan.ListedOpen; i++)
                {
                    string line = OpenLine(plan.Open[i]);
                    ReportFurniture.Marker(C, new SKPoint(x0 + 5, _y + 6.2f), 4.3f, (i + 1).ToString(), ReviewMark, false, i + 1 >= 10 ? 4.1f : 4.6f);
                    _y = Paragraph(line, textX, _y, textW, _list, ReportFurniture.Ink, EntryLead) + 2.5f;
                }
                int more = plan.Open.Count - plan.ListedOpen;
                if (more > 0)
                {
                    _y += 10;
                    C.DrawText($"… and {more:N0} more", textX, _y, SKTextAlign.Left, _body, muted);
                    _y += 4;
                }
            }

            if (plan.Accepted.Count == 0 || _y + 30 > floor) return;
            _y += 16;
            C.DrawText("Accepted", x0, _y, SKTextAlign.Left, _h2, ink);
            _y += 2;
            int written = 0;
            foreach (var f in plan.Accepted.Take(Listed))
            {
                var a = f.Accepted!;
                string line = $"{LayerTitle(f.Layer, f.LayerName)} {Where(f.Marker)}: {Printable(f.Sentence)}\nReason: {a.Reason}  ·  " +
                              $"accepted {a.AcceptedUtc.ToLocalTime():yyyy-MM-dd}";
                var lines = ReportFurniture.Wrap(line, _list, textW);
                if (_y + lines.Count * _list.Size * EntryLead + 14 > floor) break;
                ReportFurniture.Check(C, new SKPoint(x0 + 5, _y + 6.2f), 4.3f);
                _y = Paragraph(line, textX, _y, textW, _list, ReportFurniture.Ink, EntryLead) + 3;
                written++;
            }
            if (plan.Accepted.Count > written)
            {
                _y += 10;
                C.DrawText($"… and {plan.Accepted.Count - written:N0} more accepted", textX, _y, SKTextAlign.Left, _body, muted);
            }
        }
    }
}
