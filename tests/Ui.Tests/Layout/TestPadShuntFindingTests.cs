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

    // brief-dfr29-3: a board drawn the way an import draws it. A line on Top Copper into a 0.3 mm hole at (20 mm, 0); the
    // pad, 1.5 mm across, on the far side.
    private const double TwoPointFourFourGHz = 2.44e9;
    private const long Hx = 20_000_000, PadR = 750_000, LandR = 300_000, Stub = 310_000, StubW = 300_000;

    /// <summary>Land + 0.31 mm stub + round pad, or + a square <paramref name="square"/>, as ONE polygon on Bottom.</summary>
    private static PolygonShape LandStubPad(bool square = false)
    {
        long px = Hx + LandR + Stub + PadR;
        Clipper2Lib.Paths64 parts =
        [
            Clipper2Lib.Clipper.MakePath(Disc(Hx, 0, LandR)),
            Clipper2Lib.Clipper.MakePath([Hx, -StubW / 2, px, -StubW / 2, px, StubW / 2, Hx, StubW / 2]),
            Clipper2Lib.Clipper.MakePath(square ? [px - PadR, -PadR, px + PadR, -PadR, px + PadR, PadR, px - PadR, PadR]
                                                : Disc(px, 0, PadR)),
        ];
        foreach (var part in parts) if (!Clipper2Lib.Clipper.IsPositive(part)) part.Reverse();
        var union = Assert.Single(Clipper2Lib.Clipper.Union(parts, Clipper2Lib.FillRule.NonZero));
        return new PolygonShape { Layer = TenLayer.Bottom, Xy = [.. union.SelectMany(p => new[] { p.X, p.Y })] };
    }

    private static List<LayoutShape> ImportedBoard(string fixture)
    {
        var drill = new CircleShape { Layer = TenLayer.Drill, Cx = Hx, Cy = 0, R = 150_000 };
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = Top, X1 = 0, Y1 = -TenLayer.Width / 2, X2 = Hx, Y2 = TenLayer.Width / 2 },
            new CircleShape { Layer = Top, Cx = Hx, Cy = 0, R = LandR },
            Plane(TenLayer, (Hx, 0)),
        };
        switch (fixture)
        {
            case "drill circle, disc pad":
                shapes.Add(drill);
                shapes.Add(new CircleShape { Layer = TenLayer.Bottom, Cx = Hx, Cy = 0, R = PadR });
                break;
            case "via, 64-gon pad":
                shapes.Add(new ViaShape { Layer = TenLayer.Drill, X = Hx, Y = 0, PadSize = 2 * LandR, DrillSize = 300_000 });
                shapes.Add(new PolygonShape { Layer = TenLayer.Bottom, Xy = Disc(Hx, 0, PadR) });
                break;
            case "drill circle, land + stub + pad":
                shapes.Add(drill);
                shapes.Add(LandStubPad());
                break;
            case "the Bottom copper continues as a trace":
                shapes.Add(drill);
                shapes.Add(LandStubPad());
                shapes.Add(new RectShape { Layer = TenLayer.Bottom, X1 = Hx - 10_000_000, Y1 = -TenLayer.Width / 2, X2 = Hx, Y2 = TenLayer.Width / 2 });
                break;
            case "a second drill in the island":
                shapes.Add(drill);
                shapes.Add(LandStubPad());
                shapes.Add(new CircleShape { Layer = TenLayer.Drill, Cx = Hx + LandR + Stub + PadR, Cy = 0, R = 150_000 });
                break;
            case "a square pad":
                shapes.Add(drill);
                shapes.Add(LandStubPad(square: true));
                break;
            case "a blind drill's own land at its far end":
                // Laser 1-2 spans Top to Inner 1, so nothing between its ends carries a land to measure.
                shapes[1] = new CircleShape { Layer = Top, Cx = Hx, Cy = 0, R = 260_000 };
                shapes.Add(new CircleShape { Layer = new LayerKey(22, 0), Cx = Hx, Cy = 0, R = 100_000 });
                shapes.Add(new CircleShape { Layer = new LayerKey(10, 0), Cx = Hx, Cy = 0, R = 260_000 });
                break;
        }
        return shapes;
    }

    /// <summary>brief-dfr29-3 gate 1: a hole that is a bare circle on the drill layer (R1), a pad that is a polygon (R2),
    /// and a pad joined to the hole's land by a short stub on its own layer (R1 + R2 + R3) are each found, failing at
    /// 2.44 GHz, and the sentence says what was read rather than drawn (R4).</summary>
    [Theory]
    [InlineData("drill circle, disc pad")]
    [InlineData("via, 64-gon pad")]
    [InlineData("drill circle, land + stub + pad")]
    public void APadOnAnImportedBoard_IsFound(string fixture)
    {
        var trace = Assert.Single(Analyze(ImportedBoard(fixture), TenLayer.Tech, Top, TwoPointFourFourGHz).AllTraces);
        var issue = Assert.Single(trace.Issues, i => i.Kind == TraceIssueKind.PadShunt);
        Assert.Equal(IssueSeverity.Fail, issue.Severity);
        var shunt = Assert.Single(trace.PadShunts);
        Assert.StartsWith("Bottom Copper", shunt.PadLayer);
        Assert.InRange(shunt.Diameter, 2 * PadR - 2 * PadR / 50, 2 * PadR + 2 * PadR / 50);
        Assert.Equal(fixture.StartsWith("drill circle"), issue.Text.Contains("through a 300 µm drill hole from 'Top Copper"));
        Assert.Equal(!fixture.EndsWith("disc pad"), issue.Text.Contains("across, fitted to its outline,"));
        bool stub = fixture.EndsWith("stub + pad");
        Assert.Equal(stub, issue.Text.Contains("µm trace on 'Bottom Copper (1 oz)' the estimate leaves out"));
        long px = stub ? Hx + LandR + Stub + PadR : Hx;
        Assert.InRange(shunt.X, px - 5_000, px + 5_000);
    }

    /// <summary>brief-dfr29-3 gate 2: the same hole is not a test pad where its Bottom copper continues as a trace, where
    /// its island carries a second drill, or where the pad is a rectangle — nor is a blind drill's own land at its far end,
    /// measured at its other end where nothing between them carries one.</summary>
    [Theory]
    [InlineData("the Bottom copper continues as a trace")]
    [InlineData("a second drill in the island")]
    [InlineData("a square pad")]
    [InlineData("a blind drill's own land at its far end")]
    public void NotATestPad(string fixture)
    {
        var report = Analyze(ImportedBoard(fixture), TenLayer.Tech, Top, TwoPointFourFourGHz);
        Assert.Empty(report.AllTraces.SelectMany(t => t.PadShunts));
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
