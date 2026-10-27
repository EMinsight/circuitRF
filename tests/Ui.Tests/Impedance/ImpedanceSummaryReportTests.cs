// The Summary report (brief-impedance-9) — one test per gate in §4, 2 to 5: Full is the report it was (a map
// of a few findings draws no badge, and the style-less call is Full); repeated findings group into one row
// per kind × width class with the right count, and the worst list holds the worst; fifty findings in one
// small area are one badge reading 50 while three apart stay three markers; and the CLI's --report summary
// and the panel's Summary export write the Summary.

using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Impedance;

[Collection(LvsCliConsoleCollection.Name)]
public class ImpedanceSummaryReportTests
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static Technology Tech() => new()
    {
        Name = "summary",
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = Gnd, Name = "Plane", Purpose = "conductor" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(500), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    /// <summary>A straight trace along x at <paramref name="y"/>, µm, one station, one piece.</summary>
    private static TraceRun Trace(string id, double x0, double x1, double y, double width, params TraceIssue[] issues) => new()
    {
        Id = id, Layer = Top, LayerName = "Top", Issues = issues,
        Verdict = TraceRun.VerdictOf(true, issues),
        Length = Um(x1 - x0), WidthMin = Um(width), WidthMax = Um(width), Z0Min = 48, Z0Max = 52, InTolerance = 1,
        StartX = Um(x0), StartY = Um(y), EndX = Um(x1), EndY = Um(y),
        Pieces = [(Um(x0), Um(y), Um(x1), Um(y), Um(width))],
        Stations = [new TraceStation { X = Um(0.5 * (x0 + x1)), Y = Um(y), Ux = 0, Uy = 1, Length = Um(x1 - x0), Width = Um(width), Z0 = 50 }],
    };

    private static TraceIssue Gap(double x, double y) =>
        new(TraceIssueKind.ReturnBroken, IssueSeverity.Fail, Um(x - 100), Um(y), Um(x + 100), Um(y), "gap") { SpanLength = Um(200) };

    private static TraceIssue Z0(double x, double y, double ohms) =>
        new(TraceIssueKind.OutOfTolerance, IssueSeverity.Fail, Um(x - 50), Um(y), Um(x + 50), Um(y), $"Z0 {ohms} Ω") { WorstOhms = ohms };

    private static TraceImpedanceReport Report(Bbox extent, params TraceRun[] traces) => new()
    {
        TargetOhms = 50, TolerancePercent = 10, WarningPercent = 20, Extent = extent,
        Layers = [new TraceLayerResult { Layer = Top, Name = "Top", Traces = traces }],
    };

    private static int Pages(byte[] pdf) =>
        Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type /Page\b(?!s)").Count;

    /// <summary>Gate 2 — Full is what it was: the call with no style is Full, and a map of a few findings
    /// apart places them all, so no badge is drawn and nothing on the page changes.</summary>
    [Fact]
    public void Full_IsTheStylelessReport_AndAFewFindingsDrawNoBadge()
    {
        var r = Report(new Bbox(Um(-10000), Um(-10000), Um(10000), Um(10000)),
                       Trace("T1", -5000, 5000, -4000, 200, Gap(-2000, -4000)),
                       Trace("T2", -5000, 5000, 0, 200, Gap(0, 0), Z0(3000, 0, 70)),
                       Trace("T3", -5000, 5000, 4000, 200, Gap(2000, 4000)));
        Assert.Equal(TraceImpedanceReportDocument.Pdf(r, TraceImpedanceReportStyle.Full), TraceImpedanceReportDocument.Pdf(r));
        var marks = TraceImpedanceReportDocument.MapMarks(r, r.Layers[0], TraceImpedanceReportStyle.Full);
        Assert.Equal([1, 2, 3, 4], marks.SelectMany(m => m.Numbers).Order());
        Assert.DoesNotContain(marks, m => m.Badge);
    }

    /// <summary>Gate 3 — thirty traces in two widths with the same return-path gap under each, and four Z0
    /// findings: one row per kind × width class with the right counts, every finding in exactly one row,
    /// and the worst-first list led by the fails with the largest excursion.</summary>
    [Fact]
    public void RepeatedFindings_GroupByKindAndWidthClass_AndTheWorstLead()
    {
        var traces = new List<TraceRun>();
        for (int i = 0; i < 30; i++)
        {
            double y = i * 1000, w = i < 20 ? 100 : 300;
            var issues = new List<TraceIssue> { Gap(0, y) };
            if (i % 10 == 0) issues.Add(Z0(2000, y, 50 + 3 * (i + 10)));   // 80, 110, 140 Ω
            traces.Add(Trace($"T{i + 1}", -5000, 5000, y, w, [.. issues]));
        }
        var r = Report(new Bbox(Um(-6000), Um(-1000), Um(6000), Um(30000)), [.. traces]);
        var layer = r.Layers[0];

        var groups = TraceImpedanceReportDocument.FindingGroups(r, layer);
        var gaps = groups.Where(g => g.Kind == TraceIssueKind.ReturnBroken).ToList();
        Assert.Equal(2, gaps.Count);
        Assert.Equal((100.0, 20), (gaps[0].Class.NominalMicrons, gaps[0].Open));
        Assert.Equal((300.0, 10), (gaps[1].Class.NominalMicrons, gaps[1].Open));
        Assert.Equal(layer.Traces.Sum(t => t.Issues.Count), groups.Sum(g => g.Open + g.Accepted));

        var worst = TraceImpedanceReportDocument.WorstFindings(r, layer);
        Assert.Equal(20, worst.Count);
        Assert.Equal([140.0, 110.0, 80.0], worst.Take(3).Select(f => f.Issue.WorstOhms!.Value));
    }

    /// <summary>Gate 4 — fifty findings in one small area of a large board draw one badge reading 50; three
    /// findings far apart draw three markers.</summary>
    [Fact]
    public void FiftyFindingsTogether_AreOneBadge_ThreeApartStayThree()
    {
        var board = new Bbox(Um(-50000), Um(-50000), Um(50000), Um(50000));
        // Ten short traces 20 µm apart, five findings each, all within 0.2 mm, plus a long trace far off so
        // the map frames the board rather than the cluster.
        var dense = Enumerable.Range(0, 10).Select(i =>
            Trace($"T{i + 1}", 0, 200, i * 20, 10, [.. Enumerable.Range(0, 5).Select(k => Gap(40 * k + 20, i * 20))])).ToList();
        dense.Add(Trace("T11", -45000, -40000, -45000, 200));
        var together = Report(board, [.. dense]);
        var badge = Assert.Single(TraceImpedanceReportDocument.MapMarks(together, together.Layers[0], TraceImpedanceReportStyle.Summary));
        Assert.True(badge.Badge);
        Assert.Equal("50", badge.Label(TraceImpedanceReportStyle.Summary));

        var apart = Report(board,
                           Trace("T1", -40000, -30000, -40000, 200, Gap(-35000, -40000)),
                           Trace("T2", -5000, 5000, 0, 200, Gap(0, 0)),
                           Trace("T3", 30000, 40000, 40000, 200, Gap(35000, 40000)));
        var marks = TraceImpedanceReportDocument.MapMarks(apart, apart.Layers[0], TraceImpedanceReportStyle.Summary);
        Assert.Equal(3, marks.Count);
        Assert.All(marks, m => Assert.False(m.Badge));
    }

    /// <summary>Gate 5 — one layout, twenty lines over a slotted plane: the CLI's --report summary writes the
    /// Summary (its page count, not Full's), and the panel's Summary export is byte for byte the Summary of
    /// the same analysis — the pattern the Full export is held to.</summary>
    [Fact]
    public async Task TheCliAndThePanel_WriteTheSummary()
    {
        var shapes = new List<LayoutShape>();
        for (int i = 0; i < 20; i++) shapes.Add(Rect(Top, -6000, i * 2000, 6000, i * 2000 + 500));
        shapes.Add(Rect(Gnd, -8000, -2000, -300, 42000));
        shapes.Add(Rect(Gnd, 300, -2000, 8000, 42000));
        var options = new TraceImpedanceOptions { Layers = [Top] };

        string dir = Path.Combine(Path.GetTempPath(), "crf-imp-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.AddRange(shapes);
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);
            string cliPdf = Path.Combine(dir, "cli.pdf");
            InProcess("impedance", clay, "--layers", "Top", "-o", cliPdf, "--report", "summary", "--json");

            var vm = new LayoutEditorViewModel(view) { Technology = Tech() };
            var held = await vm.RunTraceImpedanceAsync(options, new RunControl());
            Assert.NotNull(held);
            int full = TraceImpedanceReportDocument.PageCount(held!, TraceImpedanceReportStyle.Full);
            int summary = TraceImpedanceReportDocument.PageCount(held!, TraceImpedanceReportStyle.Summary);
            Assert.True(full > summary, $"Full {full} pages, Summary {summary}");
            Assert.Equal(summary, Pages(File.ReadAllBytes(cliPdf)));

            string panelPdf = Path.Combine(dir, "panel.pdf");
            Assert.True(await vm.ExportTraceImpedancePdfAsync(panelPdf, TraceImpedanceReportStyle.Summary));
            var oneShot = TraceImpedanceAnalysis.Analyze(vm.Model.Shapes, vm.Technology!, vm.Model.DbuPerMicron,
                              options with { DisplayUnit = vm.DisplayUnit }, new RunControl())
                          with { Title = "Untitled layout", SourcePath = null, TechnologyPath = null,
                                 CreatedUtc = held!.CreatedUtc, Elapsed = held.Elapsed };
            byte[] expected = TraceImpedanceReportDocument.Pdf(oneShot, TraceImpedanceReportStyle.Summary);
            Assert.Equal(expected, await File.ReadAllBytesAsync(panelPdf));
            Assert.Equal(summary, Pages(expected));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static int InProcess(params string[] args)
    {
        var real = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            JsonRun.Reset();
            return CliEntry.Run(args);
        }
        finally { Console.SetOut(real); }
    }
}
