using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Tests.Lvs;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-dfr29-2 — a test pad hanging off a line is a FINDING when it loads the line at the highest frequency asked:
/// judged by its |S11| on the line and its via's resonance (owner decision D1), still a note with no frequency to judge
/// it at, reported even where the via keep-out leaves the trace unchecked, and carried by the CLI's --json.
/// </summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class TestPadShuntFindingTests
{
    private const double SixGHz = 6e9;
    private static readonly LayerKey Top = new(1, 0);

    // The line is on Bottom Copper and its plane, the conductor above it, is drawn: Inner 8 (21,0) on the ten-layer board,
    // 71 µm away; Inner 2 (3,0) on the four-layer one, 0.2 mm. The pad on Top faces Inner 1, undrawn and so taken as the
    // solid ground plane it is marked as: 71 µm under Top on ten layers, 0.2 mm on four.
    private static readonly Board TenLayer = new("pcb-10layer_FR-4_1.7mm_1oz", new(2, 0), new(21, 0), new(7, 0), 130_000);
    private static readonly Board FourLayer = new("pcb-4layer_FR-4_62mil_1oz", new(4, 0), new(3, 0), new(9, 0), 360_000);

    private sealed record Board(string Tech, LayerKey Bottom, LayerKey Plane, LayerKey Drill, long Width);

    /// <summary>The line's plane over the board, cleared round each via at <paramref name="vias"/>.</summary>
    private static PolygonShape Plane(Board b, params (long X, long Y)[] vias) => new()
    {
        Layer = b.Plane, Xy = [-5_000_000, -5_000_000, 25_000_000, -5_000_000, 25_000_000, 5_000_000, -5_000_000, 5_000_000],
        Holes = [.. vias.Select(v => Disc(v.X, v.Y, 500_000))],
    };

    /// <summary>A line on Bottom Copper into a through via at (20 mm, 0), and a 1 mm pad on Top over it.</summary>
    private static List<LayoutShape> LineIntoPad(Board b)
    {
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = b.Bottom, X1 = 0, Y1 = -b.Width / 2, X2 = 20_000_000, Y2 = b.Width / 2 },
            new CircleShape { Layer = b.Bottom, Cx = 20_000_000, Cy = 0, R = 300_000 },
            new ViaShape { Layer = b.Drill, X = 20_000_000, Y = 0, PadSize = 600_000, DrillSize = 300_000 },
            new CircleShape { Layer = Top, Cx = 20_000_000, Cy = 0, R = 500_000 },
            Plane(b, (20_000_000, 0)),
        };
        return shapes;
    }

    private static long[] Disc(long cx, long cy, long r) => [.. Enumerable.Range(0, 64).SelectMany(k => new[]
    {
        cx + (long)Math.Round(r * Math.Cos(-k * Math.PI / 32)), cy + (long)Math.Round(r * Math.Sin(-k * Math.PI / 32)),
    })];

    private static TraceImpedanceReport Analyze(List<LayoutShape> shapes, string tech, LayerKey layer, double? fMax)
    {
        var report = TraceImpedanceAnalysis.Analyze(shapes, ShippedTechnologies.Load(tech), LayoutUnits.DefaultDbuPerMicron,
                                                    new TraceImpedanceOptions { Layers = [layer], MaxFrequencyHz = fMax });
        Assert.True(report.Ok, report.Refusal);
        return report;
    }

    /// <summary>Gate 1: a 1 mm pad through a via fails at 6 GHz on the ten-layer board, where its plane is 71 µm away; on
    /// the four-layer one, 0.2 mm away, it loads the line less. The finding's box is the pad's bounds and its sentence the
    /// note's with the verdict.</summary>
    [Fact]
    public void APadOnTheTenLayerBoard_FailsAtSixGigahertz_AndTheSamePadOnFourLayers_LoadsTheLineLess()
    {
        var ten = Analyze(LineIntoPad(TenLayer), TenLayer.Tech, TenLayer.Bottom, SixGHz);
        var trace = Assert.Single(ten.AllTraces);
        var issue = Assert.Single(trace.Issues, i => i.Kind == TraceIssueKind.PadShunt);
        Assert.Equal(IssueSeverity.Fail, issue.Severity);
        Assert.Equal(TraceVerdict.Fail, trace.Verdict);
        Assert.Equal((19_500_000L, -500_000L, 20_500_000L, 500_000L), (issue.X0, issue.Y0, issue.X1, issue.Y1));
        Assert.Contains("'Inner 1'", issue.Text);
        Assert.Matches(@"\|S11\| -?\d+\.\d dB at 6 GHz", issue.Text);
        Assert.DoesNotContain(trace.Notes, n => n.Contains("round pad"));

        // The four-layer pad's plane is further away, so the pad alone is under half the capacitance and a warning at
        // 6 GHz; its 1.6 mm through via adds a barrel to Inner 1 and resonates near 10 GHz, which fails it there under D1.
        var four = Analyze(LineIntoPad(FourLayer), FourLayer.Tech, FourLayer.Bottom, SixGHz);
        var shunt = Assert.Single(Assert.Single(four.AllTraces).PadShunts);
        Assert.True(shunt.Capacitance < 0.5 * trace.PadShunts[0].Capacitance);
        Assert.True(shunt.S11Db < trace.PadShunts[0].S11Db);
    }

    /// <summary>Gate 2: with no highest frequency there is nothing to judge it at — a note that says so, no finding.</summary>
    [Fact]
    public void WithNoHighestFrequency_ThePadIsANote_ThatSaysWhy()
    {
        var trace = Assert.Single(Analyze(LineIntoPad(TenLayer), TenLayer.Tech, TenLayer.Bottom, null).AllTraces);
        Assert.DoesNotContain(trace.Issues, i => i.Kind == TraceIssueKind.PadShunt);
        Assert.Null(Assert.Single(trace.PadShunts).Severity);
        Assert.Contains(trace.Notes, n => n.Contains("round pad") && n.Contains("Not judged: no highest frequency"));
    }

    /// <summary>Gate 3 (R-dfr29-2-4): a line running past, a 300 µm stub teed off it to a via and a 1 mm pad on the far
    /// side. The whole stub is inside the via keep-out and too short to be a trace of its own; the pad is still reported.</summary>
    [Fact]
    public void AStubInsideTheViaKeepOut_StillReportsThePad()
    {
        long w = TenLayer.Width, vy = w / 2 + 300_000;
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = TenLayer.Bottom, X1 = 0, Y1 = -w / 2, X2 = 20_000_000, Y2 = w / 2 },
            new RectShape { Layer = TenLayer.Bottom, X1 = 10_000_000 - w / 2, Y1 = 0, X2 = 10_000_000 + w / 2, Y2 = vy },
            new CircleShape { Layer = TenLayer.Bottom, Cx = 10_000_000, Cy = vy, R = 300_000 },
            new ViaShape { Layer = TenLayer.Drill, X = 10_000_000, Y = vy, PadSize = 600_000, DrillSize = 300_000 },
            new CircleShape { Layer = Top, Cx = 10_000_000, Cy = vy, R = 500_000 },
            Plane(TenLayer, (10_000_000, vy)),
        };
        var report = Analyze(shapes, TenLayer.Tech, TenLayer.Bottom, SixGHz);
        var issue = Assert.Single(report.AllTraces.SelectMany(t => t.Issues), i => i.Kind == TraceIssueKind.PadShunt);
        Assert.Equal(IssueSeverity.Fail, issue.Severity);
        Assert.Equal((10_000_000L, vy), (issue.X, issue.Y));
    }

    /// <summary>Gate 6: the CLI's --json carries the finding as kind pad-shunt with its C, L, resonance and |S11|. The MCP
    /// impedance tool is this verb.</summary>
    [Fact]
    public void TheVerbsJson_CarriesThePadShunt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-dfr29-2-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var real = Console.Out;
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), ShippedTechnologies.Load(TenLayer.Tech));
            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.AddRange(LineIntoPad(TenLayer));
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);

            var buffer = new StringWriter();
            Console.SetOut(buffer);
            JsonRun.Reset();
            Assert.Equal(1, CliEntry.Run(["impedance", clay, "--layers", "Bottom Copper", "--max-freq", "6GHz", "--json"]));
            Console.SetOut(real);

            var issue = JsonDocument.Parse(buffer.ToString()).RootElement.GetProperty("result").GetProperty("impedance")
                                    .GetProperty("layers")[0].GetProperty("traces")[0].GetProperty("issues").EnumerateArray()
                                    .Single(i => i.GetProperty("kind").GetString() == "pad-shunt");
            Assert.Equal("fail", issue.GetProperty("severity").GetString());
            var shunt = issue.GetProperty("shunt");
            Assert.True(shunt.GetProperty("capacitance").GetDouble() > 0);
            Assert.True(shunt.GetProperty("inductance").GetDouble() > 0);
            Assert.True(shunt.GetProperty("resonanceHz").GetDouble() > 0);
            Assert.Equal(6e9, shunt.GetProperty("atHz").GetDouble());
            Assert.True(shunt.GetProperty("s11Db").GetDouble() > TraceImpedanceAnalysis.PadShuntFailDb);
        }
        finally
        {
            Console.SetOut(real);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
