// brief-review-5 §2 — the `impedance` check is the saved Impedance review's verdicts read in, not a rule of
// its own. One small board: a 1000 µm and a 300 µm microstrip over a plane (47.7 Ω and 85.0 Ω on this
// stackup), both inside a saved scope region. The narrow one is the one finding, in the Impedance report's
// own words; with no saved scope nothing is solved; an Impedance acceptance is the review's acceptance;
// and the finding's key is the Impedance review's trace key, as `circuitrf impedance` reports the trace.

using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Review;

[Collection(LvsCliConsoleCollection.Name)]
public sealed class ImpedanceCheckTests
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static Technology Tech() => new()
    {
        Name = "impedance-check",
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
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(500), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    /// <summary>The board, with a saved scope region round both traces unless <paramref name="scoped"/> is false.</summary>
    private static LayoutView Board(bool scoped = true)
    {
        var view = new LayoutView { TechRef = "board.ctech" };
        view.Shapes.Add(Rect(Top, -6000, 2000, 6000, 3000));     // 1000 µm: inside 50 Ω ± 10 %
        view.Shapes.Add(Rect(Top, -6000, -2150, 6000, -1850));   // 300 µm: well above the band
        view.Shapes.Add(Rect(Gnd, -8000, -8000, 8000, 8000));
        if (scoped)
            view.ImpedanceReview = new TraceImpedanceReview
            {
                Layers = ["Top"],
                Scope = new TraceImpedanceScope { Regions = [TraceScopeRegion.Rectangle(null, Um(-7000), Um(-4000), Um(7000), Um(4000))] },
            };
        return view;
    }

    private static TraceImpedanceAnalysis.LayoutSource Source(LayoutView view) =>
        new("board.clay", view, Tech(), null, [.. view.Shapes], null);

    private static LayoutReviewResult Review(LayoutView view, RunControl? control = null)
    {
        var settings = new ReviewSettings();
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == "impedance");
        return ReviewAcceptance.Apply(LayoutReview.Run(Source(view), settings, control), view.ReviewAcceptances);
    }

    /// <summary>The Impedance analysis of the board as its saved review describes it — what the panel and the verb run.</summary>
    private static TraceImpedanceReport Analyze(LayoutView view) =>
        TraceImpedanceAnalysis.Analyze(Source(view), TraceImpedanceReview.OptionsFor(view.ImpedanceReview, Tech()));

    private static string KeyOf(TraceRun t) =>
        TraceImpedanceAcceptance.TraceKeyOf(t.LayerName, t.StartX, t.StartY, t.EndX, t.EndY, LayoutUnits.DefaultDbuPerMicron);

    [Fact]
    public void TheNarrowTrace_IsTheOneFinding_InTheReportsOwnWords()
    {
        var view = Board();
        var narrow = Analyze(view).AllTraces.Single(t => t.Verdict == TraceVerdict.Fail);

        var result = Review(view);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(Top, finding.Layer);
        Assert.Equal(KeyOf(narrow), finding.Key);
        string worst = TraceImpedanceReport.OhmsText(TraceImpedanceAcceptance.WorstOhmsOf(narrow, 50));
        Assert.Contains($"worst Z0 {worst} Ω against 50 Ω ± 10 %", finding.Sentence);
        Assert.StartsWith($"FAIL {narrow.Id}:", finding.Sentence);
        Assert.Contains(TraceImpedanceReport.IssueKindText(TraceIssueKind.OutOfTolerance), finding.Sentence);
        Assert.StartsWith("1 of 2 RF traces pass, within 50 Ω ± 10 %.", Assert.Single(result.Checks).Summary);
    }

    [Fact]
    public void NoSavedScope_IsNotRun_AndSolvesNothing()
    {
        foreach (var view in new[] { Board(scoped: false), WithEmptyScope() })
        {
            var stages = new List<string>();
            var result = Review(view, new RunControl { Progress = new Inline(p => stages.Add(p.Stage)) });

            var row = Assert.Single(result.Checks);
            Assert.Equal(ReviewCheckState.NotRun, row.State);
            Assert.Equal("No impedance scope is saved on this layout. Choose the RF traces in the Impedance panel.", row.NotRunReason);
            Assert.Null(result.Impedance);
            Assert.DoesNotContain(stages, s => s.Contains("solving", StringComparison.Ordinal));
        }

        static LayoutView WithEmptyScope()
        {
            var view = Board(scoped: false);
            view.ImpedanceReview = new TraceImpedanceReview { Scope = new TraceImpedanceScope() };
            return view;
        }
    }

    [Fact]
    public void AnImpedanceAcceptance_IsTheReviewsAcceptance_AndAReviewAcceptanceIsNot()
    {
        var view = Board();
        var report = Analyze(view);
        var narrow = report.AllTraces.Single(t => t.Verdict == TraceVerdict.Fail);

        // A review acceptance under the same key covers nothing: impedance findings are accepted in the Impedance review.
        view.ReviewAcceptances.Add(new ReviewAcceptance { Key = KeyOf(narrow), CheckId = "impedance", Reason = "not here" });
        Assert.False(Assert.Single(Review(view).Findings).IsAccepted);

        view.ImpedanceAcceptances.Add(TraceImpedanceAcceptance.For(report, narrow, Assert.Single(narrow.Issues),
                                                                   "a 75 Ω test line", new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc)));
        var result = Review(view);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("a 75 Ω test line", finding.Accepted?.Reason);
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void TheFindingsKey_IsTheTraceKey_OfTheVerbsFailingTrace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-review-imp-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, Board());

            var real = Console.Out;
            var buffer = new StringWriter();
            try
            {
                Console.SetOut(buffer);
                JsonRun.Reset();
                CliEntry.Run(["impedance", clay, "--json"]);
            }
            finally { Console.SetOut(real); }

            var failing = JsonDocument.Parse(buffer.ToString()).RootElement.GetProperty("result").GetProperty("impedance")
                .GetProperty("layers").EnumerateArray()
                .SelectMany(l => l.GetProperty("traces").EnumerateArray().Select(t => (Layer: l.GetProperty("layer").GetString()!, Trace: t)))
                .Single(x => x.Trace.GetProperty("verdict").GetString() == "fail");
            long At(string end, int i) => (long)Math.Round(failing.Trace.GetProperty(end)[i].GetDouble());
            string verbKey = TraceImpedanceAcceptance.TraceKeyOf(failing.Layer, At("start", 0), At("start", 1), At("end", 0), At("end", 1), 1);

            var settings = new ReviewSettings();
            foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == "impedance");
            var finding = Assert.Single(LayoutReview.Run(TraceImpedanceAnalysis.LoadLayout(clay), settings).Findings);
            Assert.Equal(verbKey, finding.Key);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class Inline(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }
}
