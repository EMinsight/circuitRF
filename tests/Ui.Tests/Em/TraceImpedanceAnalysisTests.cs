// Trace Impedance Analysis — every trace on a layout, end to end (round 8). One test per claim: a
// trace is found and answered with the probe's own number; a bend does not split it or flag it; a
// slot in the plane under it is flagged where it is; a cancelled run keeps the layers it finished;
// and the headless verb writes the report and decides its exit code by the verdicts. The warning tier
// (brief-impedance-1): a stretch inside the warning band warns, an electrically short one warns, a
// broken return never does, a reference step alone warns, and the verb exits 0 on warnings.

using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Em;

[Collection(LvsCliConsoleCollection.Name)]
public class TraceImpedanceAnalysisTests
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static Technology Tech() => new()
    {
        Name = "analysis",
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

    private static TraceImpedanceReport Analyze(IReadOnlyList<LayoutShape> shapes, double target = 50, double tol = 10,
                                                IReadOnlyList<LayerKey>? layers = null, RunControl? control = null,
                                                double warn = 20, double? maxFrequencyHz = null, Technology? tech = null) =>
        TraceImpedanceAnalysis.Analyze(shapes, tech ?? Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions
            {
                TargetOhms = target, TolerancePercent = tol, WarningPercent = warn, MaxFrequencyHz = maxFrequencyHz,
                Layers = layers ?? [Top],
            }, control);

    /// <summary>One straight microstrip: found as ONE trace, and its Z0 is the probe's, because both
    /// cut it through the same cross-section solve.</summary>
    [Fact]
    public void AStraightMicrostrip_IsOneTrace_AtTheProbesZ0()
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, 0, 0, Top);

        var report = Analyze(shapes);

        var trace = Assert.Single(Assert.Single(report.Layers).Traces);
        Assert.True(probe.Ok, probe.Refusal);
        Assert.Equal(probe.Z0Ohms, trace.Z0Mean!.Value, probe.Z0Ohms * 0.005);
        Assert.Equal(12000, trace.Length / LayoutUnits.DefaultDbuPerMicron, 12000 * 0.01);
        Assert.Equal("microstrip", Assert.Single(trace.Configurations).Name);
    }

    /// <summary>Two microstrips of different widths on one layer each read the probe's own Z0. The cut's
    /// key once held the width only in quanta of 1.5 % of itself, so every trace wider than ~67 µm
    /// keyed alike and a 300 µm line took a 1000 µm line's 47.7 Ω (the probe's is 85.0 Ω).</summary>
    [Fact]
    public void TwoWidthsOnOneLayer_EachReadTheirOwnZ0()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, 1500, 6000, 2500), Rect(Top, -6000, -2150, 6000, -1850),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var traces = Assert.Single(Analyze(shapes).Layers).Traces;

        Assert.Equal(2, traces.Count);
        foreach (var t in traces)
        {
            var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron,
                                                  0, (t.StartY + t.EndY) / 2, Top);
            Assert.Equal(probe.Z0Ohms, t.Z0Mean!.Value, probe.Z0Ohms * 0.005);
        }
    }

    /// <summary>A layout that draws only its top copper and lets the technology's ground-flagged layer
    /// stand for the plane reads as if the plane were drawn — not against the stackup's bottom
    /// boundary a copper thickness deeper, which read a 50 Ω line as 52.3 Ω (round 8).</summary>
    [Fact]
    public void AnUndrawnGroundFlaggedLayer_IsTakenAsThePlane()
    {
        LayoutShape[] drawn = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        LayoutShape[] implied = [Rect(Top, -6000, -500, 6000, 500)];
        var tech = Tech();
        tech.Stackup.Layers[2].IsGroundReference = true;
        tech.Stackup.Bottom = BoundaryCondition.Ground;
        var options = new TraceImpedanceOptions { Layers = [Top] };

        var withPlane = Assert.Single(TraceImpedanceAnalysis.Analyze(drawn, tech, LayoutUnits.DefaultDbuPerMicron, options).Layers[0].Traces);
        var withFlag  = Assert.Single(TraceImpedanceAnalysis.Analyze(implied, tech, LayoutUnits.DefaultDbuPerMicron, options).Layers[0].Traces);

        Assert.Equal(withPlane.Z0Mean!.Value, withFlag.Z0Mean!.Value, withPlane.Z0Mean.Value * 0.002);
        Assert.Contains(withFlag.Notes, n => n.StartsWith("Nothing is drawn on 'Plane'", StringComparison.Ordinal));
    }

    /// <summary>A trace that runs onto a narrower component pad does not take the pad in as its last
    /// few mils: an end piece shorter than it is wide is the land, not the line (round 8's 0201 pads
    /// read 57.6 Ω at the end of a 50 Ω trace).</summary>
    [Fact]
    public void APadAtATracesEnd_IsNotPartOfTheTrace()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, 6000, 500),
            Rect(Top, 5800, -420, 6600, 420),      // the pad: narrower than the trace, overlapping its end
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        Assert.Equal(1000, trace.WidthMin / LayoutUnits.DefaultDbuPerMicron, 1.0);
    }

    /// <summary>A trace with a 90° bend is one trace: the bend's corner is not cut, and not flagged.</summary>
    [Fact]
    public void ABentTrace_IsOneTrace_AndItsCornerIsNotFlagged()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, 500, 500),     // along x, ending in the corner square
            Rect(Top, -500, -500, 500, 6000),     // up y from the same square
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];
        var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, Um(-3000), 0, Top);

        var report = Analyze(shapes, target: probe.Z0Ohms, tol: 5);

        var trace = Assert.Single(Assert.Single(report.Layers).Traces);
        Assert.Equal(2, trace.Pieces.Count);
        Assert.Empty(trace.Issues);
        Assert.Equal(TraceVerdict.Pass, trace.Verdict);
    }

    /// <summary>A window cut in the plane across the trace is a return-path break, placed at the window.</summary>
    [Fact]
    public void ASlotInThePlane_IsFlaggedWhereItIs()
    {
        var plane = new PolygonShape
        {
            Layer = Gnd,
            Xy = [Um(-8000), Um(-8000), Um(8000), Um(-8000), Um(8000), Um(8000), Um(-8000), Um(8000)],
            Holes = [[Um(2500), Um(-1500), Um(2500), Um(1500), Um(3500), Um(1500), Um(3500), Um(-1500)]],
        };
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), plane];

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        var broken = Assert.Single(trace.Issues, i => i.Kind == TraceIssueKind.ReturnBroken);
        Assert.InRange(broken.X, Um(2500), Um(3500));
        Assert.Equal(TraceVerdict.Fail, trace.Verdict);

        // A broken return is never softened: not by a wide warning band, not by being electrically short.
        var soft = Assert.Single(Assert.Single(Analyze(shapes, warn: 90, maxFrequencyHz: 1e6).Layers).Traces);
        Assert.Equal(IssueSeverity.Fail, Assert.Single(soft.Issues, i => i.Kind == TraceIssueKind.ReturnBroken).Severity);
        Assert.Equal(TraceVerdict.Fail, soft.Verdict);
    }

    /// <summary>Z0 ~1 % above the pass band warns while the warning band holds it (board A's 55.4 Ω
    /// against a 55.0 Ω edge), and fails once the warning band is set below it.</summary>
    [Theory]
    [InlineData(20.0, IssueSeverity.Warning, TraceVerdict.Warning)]
    [InlineData(10.5, IssueSeverity.Fail, TraceVerdict.Fail)]
    public void JustOutsideThePassBand_WarnsInsideTheWarningBand(double warn, IssueSeverity severity, TraceVerdict verdict)
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        double z0 = Assert.Single(Analyze(shapes).Layers[0].Traces).Z0Min!.Value;
        double target = z0 / (1.01 * 1.10);     // the pass band's top edge 1 % below the trace

        var trace = Assert.Single(Analyze(shapes, target: target, tol: 10, warn: warn).Layers[0].Traces);

        var issue = Assert.Single(trace.Issues);
        Assert.Equal(TraceIssueKind.OutOfTolerance, issue.Kind);
        Assert.Equal(severity, issue.Severity);
        Assert.Equal(verdict, trace.Verdict);
    }

    /// <summary>A 2 mm neck in an in-band trace, far outside the warning band: a fail with no frequency,
    /// a warning at a frequency where it is under λ/20, and a fail again where it is not.</summary>
    [Theory]
    [InlineData(null, IssueSeverity.Fail)]
    [InlineData(1e9, IssueSeverity.Warning)]
    [InlineData(20e9, IssueSeverity.Fail)]
    public void AShortNeck_IsAWarningOnlyWhenElectricallyShort(double? maxFrequencyHz, IssueSeverity severity)
    {
        LayoutShape[] wide = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        double z0 = Assert.Single(Analyze(wide).Layers[0].Traces).Z0Min!.Value;
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, -1000, 500),
            Rect(Top, -1000, -150, 1000, 150),     // the neck: 300 µm wide, 2 mm long
            Rect(Top, 1000, -500, 6000, 500),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var trace = Assert.Single(Analyze(shapes, target: z0, maxFrequencyHz: maxFrequencyHz).Layers[0].Traces);

        var issue = Assert.Single(trace.Issues);
        Assert.Equal(TraceIssueKind.OutOfTolerance, issue.Kind);
        Assert.Equal(severity, issue.Severity);
        Assert.Equal(maxFrequencyHz is not null && severity == IssueSeverity.Warning,
                     issue.Text.Contains("electrically short", StringComparison.Ordinal));
    }

    /// <summary>The reference stepping from one layer to another along a trace, with nothing else
    /// wrong, is a warning: often a designed transition, and the reviewer decides.</summary>
    [Fact]
    public void AReferenceStepAlone_IsAWarning()
    {
        var l2 = new LayerKey(3, 0); var l3 = new LayerKey(4, 0); var l4 = new LayerKey(5, 0);
        StackupLayer Cu(string name, LayerKey key) => new()
            { Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [key] };
        StackupLayer Core() => new() { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(200), Epsr = 4.4 };
        var tech = new Technology
        {
            Name = "four",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = l2, Name = "L2", Purpose = "conductor" },
                new LayerDef { Key = l3, Name = "L3", Purpose = "conductor" },
                new LayerDef { Key = l4, Name = "L4", Purpose = "conductor" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Open,
                Layers = [Cu("Top", Top), Core(), Cu("L2", l2), Core(), Cu("L3", l3), Core(), Cu("L4", l4)],
            },
        };
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, 6000, 500),
            Rect(l2, -8000, 6000, 8000, 8000),      // L2 is cleared under the whole trace
            Rect(l3, -8000, -8000, 0, 8000),        // L3 under the first half only…
            Rect(l4, -8000, -8000, 8000, 8000),     // …and L4 under all of it
        ];

        var trace = Assert.Single(Analyze(shapes, target: 55, tol: 45, warn: 90, tech: tech).Layers[0].Traces);

        var step = Assert.Single(trace.Issues);
        Assert.Equal(TraceIssueKind.ReferenceStep, step.Kind);
        Assert.Equal(IssueSeverity.Warning, step.Severity);
        Assert.Equal(TraceVerdict.Warning, trace.Verdict);
    }

    /// <summary>A warning band no wider than the tolerance is refused, naming both numbers.</summary>
    [Fact]
    public void AWarningBandNoWiderThanTheTolerance_IsRefused()
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];

        var report = Analyze(shapes, tol: 15, warn: 15);

        Assert.False(report.Ok);
        Assert.Contains("± 15 %", report.Refusal, StringComparison.Ordinal);
    }

    /// <summary>Cancel stops the run between layers and the report keeps every layer that finished
    /// (owner, 2026-09-25) — and says it was cancelled.</summary>
    [Fact]
    public void ACancelledRun_KeepsTheLayersItFinished()
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token = cts.Token,
            MinReportIntervalMs = 0,
            Progress = new Inline(p => { if (p.Stage.Contains("(2 of 2)", StringComparison.Ordinal)) cts.Cancel(); }),
        };

        var report = Analyze(shapes, layers: [Top, Gnd], control: control);

        Assert.True(report.Cancelled);
        Assert.Equal("Top", Assert.Single(report.Layers).Name);
        Assert.Equal(["Top", "Plane"], report.LayersRequested);
    }

    private sealed class Inline(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }

    /// <summary>
    /// The verb on a layout on disk: it writes the PDF the editor writes (summary, then a map and a
    /// table per layer), exits 0 when every trace passes and 1 when one fails, and refuses a layer
    /// name that is not copper with the names that are.
    /// </summary>
    [Fact]
    public void TheVerb_WritesTheReport_AndExitsByTheVerdicts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-impedance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.Add(Rect(Top, -6000, -500, 6000, 500));
            view.Shapes.Add(Rect(Gnd, -8000, -8000, 8000, 8000));
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);
            string pdf = Path.Combine(dir, "report.pdf");
            double z0 = TraceImpedanceProbe.Probe(view.Shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, 0, 0, Top).Z0Ohms;

            Assert.Equal(0, InProcess("impedance", clay, "--layers", "Top",
                                      "--target", z0.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                                      "-o", pdf, "--json"));
            string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, @"/Type /Page\b(?!s)").Count);

            // Only a warning: reported, exit 0 — and 1 when --severity warning says warnings count.
            string warnTarget = (z0 / 1.15).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, InProcess("impedance", clay, "--layers", "Top", "--target", warnTarget));
            Assert.Contains(" WARN ", _last, StringComparison.Ordinal);
            Assert.Contains("0 pass, 1 warning, 0 fail", _last, StringComparison.Ordinal);
            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--target", warnTarget, "--severity", "warning"));

            // A frequency without its unit is refused.
            Assert.Equal(1, InProcess("impedance", clay, "--max-freq", "6", "--json"));
            Assert.Contains("impedance.args.bad-number", _last, StringComparison.Ordinal);

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--target", "10", "--json"));
            var trace = JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedance")
                                    .GetProperty("layers")[0].GetProperty("traces")[0];
            Assert.Equal("fail", trace.GetProperty("verdict").GetString());

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Silk", "--json"));
            Assert.Contains("impedance.layers.unknown", _last, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private string _last = "";

    private int InProcess(params string[] args)
    {
        var real = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            JsonRun.Reset();
            int exit = CliEntry.Run(args);
            _last = buffer.ToString();
            return exit;
        }
        finally { Console.SetOut(real); }
    }
}
