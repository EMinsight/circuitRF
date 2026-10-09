// ================================================================
//  ReviewCliDisplayFixesTests.cs — brief-yield-16: the review fixes to the yield verbs, MCP and the Data
//  Display. One test per requirement, table-driven where the requirement lists flags.
// ================================================================

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Tests.Examples;
using CircuitRF.Ui.Tests.Optimization;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>The bandpass example's estimate, run once by the CLI into a scratch folder (never beside the shipped
/// design), with its text <c>--contributions</c> output and its result file.</summary>
public sealed class BandpassReviewRun
{
    public string StdOut { get; }
    public DataSet Data { get; }

    public BandpassReviewRun()
    {
        string npy = Path.Combine(OptCli.Dir(), "bp.yield.npy");
        var (exit, stdout, stderr) = OptCli.Run("yield", "estimate", YieldExampleTests.Csch("BandpassYield"), "-o", npy, "--contributions", "-q");
        Assert.True(exit == 0, stderr);
        StdOut = stdout;
        Data = DataSetImporter.Import(npy).DataSet;
    }
}

public sealed class ReviewCliDisplayFixesTests(BandpassReviewRun bandpass) : IClassFixture<BandpassReviewRun>
{
    // ── fixtures ─────────────────────────────────────────────────────────────────

    private static string Divider(string dir, string statistics = "trials=200", string extra = "", string name = "div.cnl")
        => OptCli.Write(dir, name, YieldCircuits.Divider(statistics, extra: extra));

    private static string Schematic(string cnl)
    {
        string csch = Path.ChangeExtension(cnl, ".csch");
        Assert.Equal(0, OptCli.Run("netlist", cnl, "--to-schematic", "-o", csch).Exit);
        return csch;
    }

    private static JsonObject Result(string stdout, string key) => JsonNode.Parse(stdout)!["result"]![key]!.AsObject();

    /// <summary>Every command line at once — each is a refusal that costs a process start and nothing else.</summary>
    private static (int Exit, string StdOut, string StdErr)[] RunAll(IEnumerable<string[]> commands)
        => [.. commands.AsParallel().AsOrdered().Select(a => OptCli.Run(a))];

    // ── R-ya16-1 ─────────────────────────────────────────────────────────────────

    /// <summary>A saved corner replays exactly the trial it names; a flag that changed the draws, which the corner
    /// cannot carry, is refused by name and the schematic is left as it was.</summary>
    [Fact]
    public void SaveCorner_ReplaysTheTrialItNames_AndRefusesEveryFlagItCannotCarry()
    {
        string csch = Schematic(Divider(OptCli.Dir()));
        string before = File.ReadAllText(csch);
        string[][] uncarried = [["--vars", "R1.R"], ["--sigma-scale", "2"], ["--process", "0"], ["--mismatch", "0"], ["--set", "k=1"]];
        var refused = RunAll(uncarried.Select(f => (string[])["yield", "mc", csch, "--trial", "5", .. f, "--save-corner", "VC"]));
        for (int i = 0; i < uncarried.Length; i++)
        {
            Assert.Equal(1, refused[i].Exit);
            Assert.Contains($"--save-corner cannot be used with {uncarried[i][0]}", refused[i].StdErr);
        }
        Assert.Equal(before, File.ReadAllText(csch));

        var (exit, stdout, stderr) = OptCli.Run("yield", "mc", csch, "--trial", "5", "--save-corner", "VC", "--json");
        Assert.True(exit == 0, stderr);
        var drawn = Result(stdout, "yield")["trial"]!["values"]!.AsObject();
        var corners = Result(OptCli.Run("yield", "corners", csch, "--corners", "VC", "--json").StdOut, "corners");
        var replayed = corners["corners"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "VC")!["values"]!.AsObject();
        Assert.Equal(drawn.ToJsonString(), replayed.ToJsonString());
    }

    // ── R-ya16-2 ─────────────────────────────────────────────────────────────────

    /// <summary>Under colour-by-pass a pass is drawn in the theme's neutral colour, never the trace's — whose first
    /// colour is red — and it differs clearly from the fail colour in both themes.</summary>
    [Fact]
    public void PassAndFail_AreClearlyDifferentColours_InBothThemes_ForTheFirstTraceColour()
    {
        static int Distance(SkiaSharp.SKColor a, SkiaSharp.SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
        var t = TrialFixtures.Family();
        t.Properties.LineColorIndex = TraceProperties.LineColorOrder[0];
        DisplayFixtures.Resolved(TrialFixtures.Result(), t);
        int pass = Array.IndexOf(t.MemberCategories!, TrialCategory.Pass), fail = Array.IndexOf(t.MemberCategories!, TrialCategory.Fail);
        foreach (var theme in new[] { RenderTheme.Light, RenderTheme.Dark })
        {
            var p = TrialRenderer.ElementPaint(t, pass, theme).Color;
            var f = TrialRenderer.ElementPaint(t, fail, theme).Color;
            Assert.Equal(theme.PassColor, p);
            Assert.Equal(theme.FailColor, f);
            Assert.True(Distance(p, f) >= 150, $"pass {p} and fail {f} are too alike");
            Assert.True(Distance(p, t.Properties.LineColor) >= 150, $"pass {p} reads as the trace colour {t.Properties.LineColor}");
        }
    }

    // ── R-ya16-3 ─────────────────────────────────────────────────────────────────

    /// <summary>A Monte Carlo at each corner: the histogram with spec lines is one trace per corner, each pinned;
    /// the yield display, the statistics table, the ranking and a free-corner statistic each refuse by name.</summary>
    [Fact]
    public void ARunAtEachCorner_PlotsOneHistogramPerCorner_AndEveryOneRunDisplayRefusesByName()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "div.cnl", CornerCircuits.Divider + "\ntune R2.R dist=gauss sd=2%\nstatistics trials=40\n" + CornerCircuits.Corners);
        var (run, _, runErr) = OptCli.Run("yield", "corners", cnl, "--mc", "-q");
        Assert.True(run is 0 or 3, runErr);
        string npy = Path.Combine(dir, "div.yield.npy");
        string cdd = Path.Combine(dir, "h.cdd");
        var (exit, _, stderr) = OptCli.Run("plot", npy, "-o", Path.Combine(dir, "h.svg"), "--spec-lines", "--write-cdd", cdd,
                                           "--trace", "cube=trials.goal:Vout:worst,stat=histogram");
        Assert.True(exit == 0, stderr);
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var expressions = config.Tabs.Single().Plots.Single().Traces.Select(t => t.Expression!).ToList();
        string[] names = ["nominal", "cold", "hot", "hiR2"];
        Assert.Equal(names.Select(n => $"[\"{n}\", :]"), expressions.Select(e => e[e.IndexOf('[')..(e.IndexOf(']') + 1)]));

        var ds = DataSetImporter.Import(npy).DataSet;
        Assert.Equal(names, ResultContributions.StackedCorners(ds));
        Assert.NotEmpty(SpecLineResolve.GoalsOf(ds));
        Assert.Contains("'corner' axis", YieldDisplayPreset.Refusal(ds));
        Assert.Empty(YieldDisplayPreset.Compose(ds, npy));
        Assert.False(StatisticsTablePreset.Available(ds));
        Assert.Equal("yield.contrib.corner-stacked", ResultContributions.Of(ds, "Vout").Refusal?.Id);
        var free = DisplayFixtures.CubeTrace("trials.goal:Vout:worst",
            new AxisSlice("corner", AxisRole.FamilyIterate, 0), new AxisSlice("trial", AxisRole.KeepAsX, 0));
        Assert.Equal(TraceStatistics.CornerRefusal, TraceStatistics.Build(free, ds, TraceStatistic.Histogram).Refusal);
    }

    // ── R-ya16-4 / R-ya16-5 ──────────────────────────────────────────────────────

    /// <summary>Cancelled during the model optimum — after every run of the design finished and the file was written —
    /// the verb exits 130 and the file is gone. The same call uncancelled writes it, so the 130 is the cancellation.</summary>
    [Fact]
    public void ADoeCancelledDuringTheOptimum_Exits130_AndWritesNothing()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "cf.cnl", DoeCircuits.ClosedForm("design=full2"));
        string npy = DoeRun.ResultPathFor(cnl);

        JsonRun.Reset();
        Assert.Equal(0, CliEntry.Run(["yield", "doe", cnl, "--optimum", "-q"]));
        Assert.True(File.Exists(npy));
        File.Delete(npy);

        using var cts = new CancellationTokenSource();
        using (RunHost.Install(cts.Token, p => { if (p.Total > 0 && p.Completed >= p.Total) cts.Cancel(); }))
        {
            JsonRun.Reset();
            Assert.Equal(130, CliEntry.Run(["yield", "doe", cnl, "--optimum", "-q"]));
        }
        Assert.False(File.Exists(npy), "a doe cancelled during its optimum left its result behind");
    }

    /// <summary><c>--optimum</c> with stat factors is refused before anything runs: no simulation, no file.</summary>
    [Fact]
    public void OptimumWithStatFactors_IsRefusedBeforeAnythingRuns()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "cf.cnl", DoeCircuits.ClosedForm("design=full2"));
        var (exit, stdout, stderr) = OptCli.Run("yield", "doe", cnl, "--factors", "stat", "--design", "pb", "--optimum");
        Assert.Equal(1, exit);
        Assert.Contains("--optimum belongs to yield doe --factors opt", stderr);
        Assert.DoesNotContain("design of experiments:", stderr);
        Assert.DoesNotContain("Wrote", stdout);
        Assert.False(File.Exists(DoeRun.ResultPathFor(cnl)));
    }

    // ── R-ya16-6 ─────────────────────────────────────────────────────────────────

    /// <summary>A schematic's generated corners come back in JSON in full — temperature and values — as a netlist's do.</summary>
    [Fact]
    public void GenerateOnASchematic_ReturnsEachCornersTemperatureAndValuesInJson()
    {
        string csch = Schematic(Divider(OptCli.Dir()));
        var (exit, stdout, stderr) = OptCli.Run("yield", "corners", csch, "--generate", "temp=-40,85;R2.R=900,1100", "--json");
        Assert.True(exit == 0, stderr);
        var generated = Result(stdout, "corners")["generated"]!.AsArray();
        Assert.Equal(4, generated.Count);
        var first = generated[0]!;
        Assert.Equal(-40, first["temp"]!.GetValue<double>());
        Assert.Equal("900", first["values"]!["R2.R"]!.GetValue<string>());
        Assert.Null(first["line"]);
    }

    // ── R-ya16-7 ─────────────────────────────────────────────────────────────────

    /// <summary>Every flag a run does not honour is refused by name — and the flag table, which says so, is the table
    /// <c>reference statistics</c> prints.</summary>
    [Fact]
    public void EveryFlagARunIgnores_IsRefusedByName_AndTheReferencePageListsWhoTakesEachFlag()
    {
        string cnl = Divider(OptCli.Dir());
        (string Flag, string[] Args)[] pairs =
        [
            ("--confidence",    ["doe", cnl, "--confidence", "90%"]),
            ("--nonconverged",  ["doe", cnl, "--nonconverged", "warn"]),
            ("--save",          ["doe", cnl, "--save", "all"]),
            ("--process",       ["doe", cnl, "--process", "0"]),
            ("--mismatch",      ["doe", cnl, "--mismatch", "0"]),
            ("--sigma-scale",   ["doe", cnl, "--sigma-scale", "2"]),
            ("--analyses",      ["doe", cnl, "--analyses", "all"]),
            ("--resolution",    ["doe", cnl, "--design", "pb", "--resolution", "4"]),
            ("--target",        ["corners", cnl, "--target", "50%"]),
            ("--trials",        ["corners", cnl, "--trials", "5"]),
            ("--seed",          ["corners", cnl, "--seed", "2"]),
            ("--autostop",      ["corners", cnl, "--autostop"]),
            ("--contributions", ["corners", cnl, "--contributions"]),
            ("--contributions", ["corners", cnl, "--mc", "--contributions"]),
            ("--contributions", ["mc", cnl, "--contributions", "--corners", "x"]),
            ("--corners",       ["mc", cnl, "--trial", "3", "--corners", "x"]),
        ];
        var results = RunAll(pairs.Select(p => (string[])["yield", .. p.Args]));
        for (int i = 0; i < pairs.Length; i++)
        {
            Assert.True(results[i].Exit == 1, $"{string.Join(' ', pairs[i].Args)}: exit {results[i].Exit}");
            Assert.Contains($"yield: {pairs[i].Flag} belongs to", results[i].StdErr);
        }

        string page = Reference.RenderStatistics();
        foreach (var (flag, takes, nouns, _) in CircuitRF.Cli.Yield.Flags)
            Assert.Contains(page.Split('\n'), l => l.TrimStart().StartsWith($"{flag} {takes}".TrimEnd() + " ", StringComparison.Ordinal)
                                                  && l.TrimEnd().EndsWith($"[{string.Join(", ", nouns)}]", StringComparison.Ordinal));
    }

    // ── R-ya16-8 ─────────────────────────────────────────────────────────────────

    /// <summary>A trial or statistic option that would draw nothing is refused; the usage text's own family example
    /// runs.</summary>
    [Fact]
    public void PlotRefusesTrialAndStatisticOptionsThatDoNothing_AndItsUsageExampleRuns()
    {
        string dir = OptCli.Dir();
        var ds = TrialFixtures.Result();
        var trial = ds["SP1.G"].Axes[0];
        var freq = ds["SP1.G"].Axes[1];
        var s = new Complex[trial.Length * freq.Length * 4];
        for (int k = 0; k < s.Length; k++) s[k] = new Complex(0.1 + 0.001 * k, 0.05);
        ds.AddToGroup("SP1", "S", new DataCube([trial, freq, new Axis("i", [1, 2]), new Axis("j", [1, 2])], s));
        string npy = Path.Combine(dir, "t.yield.npy");
        DataSetExporter.Export(ds, npy, ExportFormat.Npy);

        string[][] refused =
        [
            ["--type", "smith", "--trace", "cube=SP1.G,envelope=minmax"],
            ["--trace", "cube=trials.goal:G:worst,envelope=minmax"],
            ["--trace", "cube=nominal.SP1.G,colorby=pass"],
            ["--trace", "cube=trials.goal:G:worst,stat=histogram,colorby=pass"],
            ["--trace", "cube=trials.goal:G:worst,stat=cdf,fit=normal"],
            ["--trace", "cube=trials.goal:G:worst,stat=yieldsens,param=R1.R,percent=1"],
            ["--trace", "cube=trials.goal:G:worst,stat=yieldsens,param=R1.R,over=trial"],
        ];
        var results = RunAll(refused.Select(a => (string[])["plot", npy, "-o", Path.Combine(dir, "x.svg"), .. a]));
        for (int i = 0; i < refused.Length; i++)
        {
            Assert.True(results[i].Exit == 1, $"{string.Join(' ', refused[i])}: exit {results[i].Exit}");
            Assert.Contains("plot: in --trace", results[i].StdErr);
        }

        string usage = OptCli.Run("plot").StdErr;
        var m = System.Text.RegularExpressions.Regex.Match(usage, @"as a family \((cube=[^)]*)\)");
        Assert.True(m.Success, usage);
        var (exit, _, stderr) = OptCli.Run("plot", npy, "-o", Path.Combine(dir, "fam.svg"), "--trace", m.Groups[1].Value + ",colorby=pass");
        Assert.True(exit == 0, stderr);
    }

    // ── R-ya16-9 ─────────────────────────────────────────────────────────────────

    /// <summary>A measurement family (spelled bare) draws its nominal from <c>nominal.measurements.&lt;name&gt;</c>;
    /// a cube that really is top-level still finds <c>nominal.&lt;name&gt;</c>.</summary>
    [Fact]
    public void AMeasurementFamily_DrawsItsNominal_AndATopLevelCubeStillDoes()
    {
        var ds = new DataSet();
        var trial = new Axis("trial", [1, 2, 3]);
        var freq = new Axis("freq", [1e9, 2e9, 3e9], "Hz");
        var values = Enumerable.Range(0, 9).Select(k => (double)k).ToArray();
        ds.AddToGroup(DataSet.MeasurementsGroup, "S21dB", new DataCube([trial, freq], values));
        ds.AddToGroup(TrialResolve.NominalGroup, "measurements.S21dB", new DataCube([freq], [-1.0, -2, -3]));
        ds.AddToGroup(DataSet.DefaultGroup, "G", new DataCube([trial, freq], values));
        ds.AddToGroup(TrialResolve.NominalGroup, "G", new DataCube([freq], [5.0, 6, 7]));

        foreach (var (cube, first) in new[] { ("S21dB", -1f), ("G", 5f) })
        {
            var t = DisplayFixtures.CubeTrace(cube, new AxisSlice("trial", AxisRole.FamilyIterate, 0), new AxisSlice("freq", AxisRole.KeepAsX, 0));
            t.ShowCurves = false;
            DisplayFixtures.Resolved(ds, t);
            Assert.Equal(3, t.NominalPoints.Count);
            Assert.Equal(first, t.NominalPoints[0].Y);
        }
    }

    // ── R-ya16-10 ────────────────────────────────────────────────────────────────

    /// <summary>On the bandpass's PassbandSpec, where β·r passes 100 % for C1.C, every displayed share — the Pareto's
    /// bars and the CLI's text table — is within 0–100 % and the cumulative line never decreases, ending at 100 %;
    /// the raw β·r is still what the source keeps.</summary>
    [Fact]
    public void BandpassContributions_AreDisplayedWithin0To100_AndTheRawShareIsKept()
    {
        var ds = bandpass.Data;
        var (plot, refusal) = ContributionParetoPreset.Build(ds, "PassbandSpec", "bp.yield.npy", out _);
        Assert.Null(refusal);
        double[] Values(string expression) => [.. ds[expression[(expression.IndexOf('*') + 1)..]].RealValues];
        var bars = Values(plot!.Traces[0].Expression!);
        var cumulative = Values(plot.Traces[1].Expression!);
        Assert.All(bars, b => Assert.InRange(b, 0, 1));
        Assert.All(cumulative.Zip(cumulative.Skip(1)), p => Assert.True(p.Second >= p.First));
        Assert.Equal(1, cumulative[^1], 12);
        Assert.Contains(ds[$"{ResultContributions.Group}.{ResultContributions.CubeName("PassbandSpec")}"].RealValues, raw => raw > 1);

        var section = bandpass.StdOut[bandpass.StdOut.IndexOf("Contributions to PassbandSpec", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n\n", StringComparison.Ordinal)];
        var shares = System.Text.RegularExpressions.Regex.Matches(section, @"\s(-?\d+\.\d) %")
            .Select(x => double.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(6, shares.Count);
        Assert.All(shares, v => Assert.InRange(v, 0, 100));
    }

    // ── R-ya16-11 ────────────────────────────────────────────────────────────────

    /// <summary><c>--vars</c> on a design with a correlation: one entry runs, the correlation going with the other;
    /// both keep ρ.</summary>
    [Fact]
    public void Vars_OnACorrelatedDesign_DropsTheCorrelationOfAnEntryLeftOut_AndKeepsOneWhollyInside()
    {
        string dir = OptCli.Dir();
        string cnl = Divider(dir, "trials=300", "correlate R1.R R2.R rho=0.9");
        var (one, _, oneErr) = OptCli.Run("yield", "mc", cnl, "--vars", "R1.R", "-q");
        Assert.True(one == 0, oneErr);

        string npy = Path.Combine(dir, "both.yield.npy");
        var (both, _, bothErr) = OptCli.Run("yield", "mc", cnl, "--vars", "R1.R,R2.R", "-o", npy, "-q");
        Assert.True(both == 0, bothErr);
        var ds = DataSetImporter.Import(npy).DataSet;
        double[] a = ds["trials.stat:R1.R"].RealValues, b = ds["trials.stat:R2.R"].RealValues;
        double ma = a.Average(), mb = b.Average();
        double r = a.Zip(b).Sum(p => (p.First - ma) * (p.Second - mb))
                 / Math.Sqrt(a.Sum(x => (x - ma) * (x - ma)) * b.Sum(x => (x - mb) * (x - mb)));
        Assert.InRange(r, 0.8, 0.97);
    }

    // ── R-ya16-12 ────────────────────────────────────────────────────────────────

    /// <summary>The shipped display's StopHighSpec histogram (values near −31 dB, limit −22 dB) autoscales to show
    /// its limit; an X range the user fixed stays as set.</summary>
    [Fact]
    public void AHistogramsAutoscaledXRange_TakesInItsSpecLimit()
    {
        var ds = bandpass.Data;
        var histogram = YieldDisplayPreset.Compose(ds, "bp.yield.npy")
            .Single(p => p.Kind == YieldDisplayPreset.PlotKind.Histogram && p.Goal == "StopHighSpec").Config.Traces.Single();
        var plot = DisplayFixtures.Resolved(ds, DisplayFixtures.ExpressionTrace(histogram.Expression!));
        var w = plot.Axes.Window;
        Assert.InRange(-22.0, w.Left, w.Right);

        var fixedWindow = new PlotRect(-33, w.Y, 4, w.Height);
        plot.AutoscaleX = false;
        plot.Axes.Window = fixedWindow;
        plot.Autoscale();
        Assert.Equal(fixedWindow, plot.Axes.Window);
    }

    // ── R-ya16-13 ────────────────────────────────────────────────────────────────

    /// <summary>The bandpass's goal cubes read dB; a frequency goal's histogram is scaled to the display unit.</summary>
    [Fact]
    public void PerTrialGoalValues_CarryTheGoalsUnit_AndAFrequencyGoalIsScaled()
    {
        foreach (var g in new[] { "PassbandSpec", "StopHighSpec" })
            foreach (var kind in new[] { "worst", "margin" })
                Assert.Equal("dB", bandpass.Data[$"trials.goal:{g}:{kind}"].Unit);

        Assert.Equal("Hz", GoalResiduals.ValueUnit(new OptimizationGoal { Name = "F", Expression = "Fc", Limit = "2.4 GHz" }));
        Assert.Equal("Hz", GoalResiduals.ValueUnit(new OptimizationGoal { Name = "F", Expression = "Fc", Limit = "2.4e9" },
                                                   new Dictionary<string, string> { ["Fc"] = "GHz" }));
        var ds = new DataSet();
        ds.AddToGroup("trials", "goal:F:worst", new DataCube([new Axis("trial", [1, 2, 3, 4])], [2.39e9, 2.40e9, 2.41e9, 2.42e9]) { Unit = "Hz" });
        var t = DisplayFixtures.ExpressionTrace("histogram(trials.goal:F:worst, 2)");
        DisplayFixtures.Resolved(ds, t);
        Assert.All(t.Points, p => Assert.InRange(p.X, 2.3f, 2.5f));
    }

    // ── R-ya16-14 ────────────────────────────────────────────────────────────────

    /// <summary>On a Monte Carlo design (no yield goal) <c>yield trial 5</c> runs and agrees with <c>yield mc --trial 5</c>.</summary>
    [Fact]
    public void TrialAndMcTrial_AgreeOnAMonteCarloDesign()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "mc.cnl", YieldCircuits.Divider("trials=50").Replace("use=yield", "use=opt"));
        var results = RunAll([["yield", "trial", cnl, "--trial", "5", "--json"], ["yield", "mc", cnl, "--trial", "5", "--json"]]);
        Assert.All(results, r => Assert.True(r.Exit == 0, r.StdErr));
        var a = Result(results[0].StdOut, "yield");
        var b = Result(results[1].StdOut, "yield");
        Assert.Equal("montecarlo", a["mode"]!.GetValue<string>());
        Assert.Equal(b["trial"]!.ToJsonString(), a["trial"]!.ToJsonString());
    }
}
