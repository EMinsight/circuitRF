// brief-review-7 §2 — the review report. Its length is the summary page plus, per check with findings, a map per
// layer and one findings page, whatever the finding counts; page 1 names every check; a findings page lists 20 and
// counts the rest; and `circuitrf review -o` run as a PROCESS writes the bytes the in-process call writes, every
// byte but the creation timestamp the PDF carries by design.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Render;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Review;

[Collection(LvsCliConsoleCollection.Name)]
public sealed class ReviewReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-review-report-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp folder that outlives the run is not a failure */ }
    }

    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bottom = new(4, 0);

    private static ReviewFinding Finding(string check, LayerKey layer, int i, ReviewAcceptance? accepted = null)
    {
        var box = new Bbox(i * 1000, 0, i * 1000 + 500, 500);
        return new ReviewFinding(check, layer, ReviewSeverity.Warning, $"Finding {i} of {check}", null,
                                 [[box.MinX, box.MinY, box.MaxX, box.MinY, box.MaxX, box.MaxY, box.MinX, box.MaxY]], box,
                                 ReviewFinding.KeyOf(check, layer, box))
        { LayerName = layer == Top ? "Top Copper" : "Bottom Copper", Accepted = accepted };
    }

    private static ReviewCheckRow Row(string id, ReviewCheckState state, int findings, string? why = null) =>
        new(id, LayoutReview.FindCheck(id)!.Title, state, why, findings, 0);

    private static LayoutReviewResult Seeded(IReadOnlyList<ReviewCheckRow> rows, IReadOnlyList<ReviewFinding> findings) =>
        new(rows, findings, "seeded", [], false)
        {
            DisplayUnit = LayoutUnit.Mm,
            CreatedUtc = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc),
            Settings = new ReviewSettings(),
        };

    [Fact]
    public void TwoChecksWithFindings_OneClean_OneNotRun_AreOnePlusTwoPlusTwoPages_AndPageOneNamesAllFour()
    {
        var findings = new List<ReviewFinding> { Finding("via-in-pad", Top, 1), Finding("via-in-pad", Top, 2), Finding("edge-copper", Top, 3) };
        var result = Seeded(
            [
                Row("via-in-pad", ReviewCheckState.Ran, 2),
                Row("thermal-vias", ReviewCheckState.Ran, 0),
                Row("edge-copper", ReviewCheckState.Ran, 1),
                Row("edge-stitching", ReviewCheckState.NotRun, 0, "Flag the ground plane in the Technology Editor."),
            ],
            findings);

        Assert.Equal(1 + 2 + 2, LayoutReviewReportDocument.PageCount(result));
        byte[] pdf = LayoutReviewReportDocument.Pdf(result);
        var pages = PdfText.Pages(pdf);
        Assert.Equal(5, pages.Count);

        string first = pages[0];
        foreach (var row in result.Checks) Assert.Contains(row.Title, first, StringComparison.Ordinal);
        Assert.Contains("No findings.", first, StringComparison.Ordinal);
        Assert.Contains("Not run: Flag the ground plane", first, StringComparison.Ordinal);
    }

    [Fact]
    public void FiveHundredFindingsInOneCheck_AreStillTwoPages_ListingTwentyAndCountingTheRest()
    {
        var findings = Enumerable.Range(1, 500).Select(i => Finding("via-in-pad", Top, i)).ToList();
        var result = Seeded([Row("via-in-pad", ReviewCheckState.Ran, 500)], findings);

        Assert.Equal(1 + 2, LayoutReviewReportDocument.PageCount(result));
        var pages = PdfText.Pages(LayoutReviewReportDocument.Pdf(result));
        Assert.Equal(3, pages.Count);
        Assert.Contains("Finding 20 of via-in-pad", pages[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Finding 21 of via-in-pad", pages[2], StringComparison.Ordinal);
        Assert.Contains("… and 480 more", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbRunAsAProcess_WritesTheBytesTheInProcessCallWrites_ButTheTimestamp()
    {
        Directory.CreateDirectory(_dir);
        var tech = ReviewRunTests.Tech();
        TechPersistence.SaveToFile(Path.Combine(_dir, "board.ctech"), tech);
        var view = ReviewRunTests.Board(tech, 100_000);
        view.TechRef = "board.ctech";
        string clay = Path.Combine(_dir, "board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        string outPath = Path.Combine(_dir, "cli.pdf");

        var (exit, _, stderr) = RunCli("review", clay, "--checks", "edge-copper", "-o", outPath);
        Assert.True(exit == 0, stderr);

        var source = TraceImpedanceAnalysis.LoadLayout(clay);
        var settings = source.View.Review?.Clone() ?? new ReviewSettings();
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == "edge-copper");
        var result = ReviewAcceptance.Apply(LayoutReview.Run(source, settings), source.View.ReviewAcceptances);
        Assert.Equal(1, result.WarningCount);
        byte[] inProcess = LayoutReviewReportDocument.Pdf(result);
        byte[] fromCli = File.ReadAllBytes(outPath);

        Assert.Equal(3, PdfText.Pages(fromCli).Count);
        Assert.Equal(StripPdfDates(inProcess), StripPdfDates(fromCli));
    }

    private static string StripPdfDates(byte[] pdf) =>
        Regex.Replace(Encoding.Latin1.GetString(pdf), @"D:\d{14}[^)]*", "D:<stamp>");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ReviewReportTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}

/// <summary>
/// The text of a PDF Skia wrote, page by page — enough of the format to read what a report SAYS rather than how
/// it looks: each page's content stream (Flate) is read for its text-showing operators, and each glyph id mapped
/// back to Unicode through its font's ToUnicode CMap. One line per text object (one draw).
/// </summary>
internal static class PdfText
{
    private static readonly Regex Obj = new(@"(\d+) 0 obj\s*(.*?)endobj", RegexOptions.Singleline);

    public static List<string> Pages(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        var objects = new Dictionary<int, string>();
        foreach (System.Text.RegularExpressions.Match m in Obj.Matches(text)) objects[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;

        string Body(int id) => objects.TryGetValue(id, out var b) ? b : "";
        string Stream(int id)
        {
            string body = Body(id);
            int start = body.IndexOf("stream", StringComparison.Ordinal);
            if (start < 0) return "";
            start += "stream".Length;
            if (body[start] == '\r') start++;
            if (body[start] == '\n') start++;
            int end = body.LastIndexOf("endstream", StringComparison.Ordinal);
            byte[] raw = Encoding.Latin1.GetBytes(body[start..end]);
            if (!body[..start].Contains("/FlateDecode", StringComparison.Ordinal)) return Encoding.Latin1.GetString(raw);
            using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var outStream = new MemoryStream();
            try { z.CopyTo(outStream); } catch (InvalidDataException) { /* a trailing newline before endstream */ }
            return Encoding.Latin1.GetString(outStream.ToArray());
        }
        string Resolve(string dictOrRef) =>
            Regex.Match(dictOrRef, @"^\s*(\d+) 0 R") is { Success: true } r ? Body(int.Parse(r.Groups[1].Value)) : dictOrRef;

        var cmaps = new Dictionary<int, Dictionary<int, string>>();
        Dictionary<int, string> CMap(int fontId)
        {
            if (cmaps.TryGetValue(fontId, out var map)) return map;
            map = [];
            if (Regex.Match(Body(fontId), @"/ToUnicode (\d+) 0 R") is { Success: true } tu)
            {
                string cmap = Stream(int.Parse(tu.Groups[1].Value));
                foreach (System.Text.RegularExpressions.Match block in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                    foreach (System.Text.RegularExpressions.Match p in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                        map[Convert.ToInt32(p.Groups[1].Value, 16)] = Utf16(p.Groups[2].Value);
                foreach (System.Text.RegularExpressions.Match block in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
                    foreach (System.Text.RegularExpressions.Match p in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(<[0-9A-Fa-f]+>|\[[^\]]*\])"))
                    {
                        int lo = Convert.ToInt32(p.Groups[1].Value, 16), hi = Convert.ToInt32(p.Groups[2].Value, 16);
                        string dst = p.Groups[3].Value;
                        if (dst.StartsWith('['))
                        {
                            var each = Regex.Matches(dst, @"<([0-9A-Fa-f]+)>").Select(x => Utf16(x.Groups[1].Value)).ToList();
                            for (int g = lo; g <= hi && g - lo < each.Count; g++) map[g] = each[g - lo];
                        }
                        else
                        {
                            int first = Convert.ToInt32(dst.Trim('<', '>'), 16);
                            for (int g = lo; g <= hi; g++) map[g] = char.ConvertFromUtf32(first + g - lo);
                        }
                    }
            }
            return cmaps[fontId] = map;
        }

        // The pages in the page tree's order, which need not be the objects' order.
        var order = new List<int>();
        void Walk(int node)
        {
            string b = Body(node);
            if (Regex.IsMatch(b, @"/Type\s*/Page\b(?!s)")) { order.Add(node); return; }
            if (Regex.Match(b, @"/Kids\s*\[([^\]]*)\]") is { Success: true } kids)
                foreach (System.Text.RegularExpressions.Match k in Regex.Matches(kids.Groups[1].Value, @"(\d+) 0 R")) Walk(int.Parse(k.Groups[1].Value));
        }
        int root = objects.First(kv => Regex.IsMatch(kv.Value, @"/Type\s*/Catalog")).Key;
        Walk(int.Parse(Regex.Match(Body(root), @"/Pages\s+(\d+) 0 R").Groups[1].Value));

        var pages = new List<string>();
        foreach (int id in order)
        {
            string body = Body(id);
            var fonts = new Dictionary<string, int>();
            var resources = Regex.Match(body, @"/Resources\s*(\d+ 0 R|<<.*)", RegexOptions.Singleline) is { Success: true } res
                ? Resolve(res.Groups[1].Value) : "";
            if (Regex.Match(resources, @"/Font\s*(\d+ 0 R|<<[^>]*>>)") is { Success: true } fd)
                foreach (System.Text.RegularExpressions.Match f in Regex.Matches(Resolve(fd.Groups[1].Value), @"/(\w+)\s+(\d+) 0 R"))
                    fonts[f.Groups[1].Value] = int.Parse(f.Groups[2].Value);
            var contents = Regex.Match(body, @"/Contents\s+(\d+) 0 R");
            if (!contents.Success) { pages.Add(""); continue; }
            string content = Stream(int.Parse(contents.Groups[1].Value));

            var sb = new StringBuilder();
            Dictionary<int, string> current = [];
            foreach (System.Text.RegularExpressions.Match op in Regex.Matches(content,
                         @"/(\w+)\s+[\d.]+\s+Tf|<([0-9A-Fa-f]*)>\s*Tj|\[((?:[^\]])*)\]\s*TJ|\bET\b"))
            {
                if (op.Value == "ET") { sb.Append('\n'); continue; }
                if (op.Groups[1].Success) { current = fonts.TryGetValue(op.Groups[1].Value, out int fid) ? CMap(fid) : []; continue; }
                string hex = op.Groups[2].Success ? op.Groups[2].Value
                    : string.Concat(Regex.Matches(op.Groups[3].Value, @"<([0-9A-Fa-f]*)>").Select(x => x.Groups[1].Value));
                for (int i = 0; i + 3 < hex.Length; i += 4)
                    sb.Append(current.TryGetValue(Convert.ToInt32(hex.Substring(i, 4), 16), out var u) ? u : "?");
            }
            pages.Add(sb.ToString());
        }
        return pages;
    }

    private static string Utf16(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }
}
