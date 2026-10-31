// Trace Impedance — a trace entering its own via's clearance hole (brief-dfr32-5). One test per claim, on a
// synthetic stripline between two planes, into a through via with a round clearance hole in each plane: the
// holes are Info lines and the trace passes; a slot in a plane running on past the hole still fails; a gap in a
// plane mid-trace still fails.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class TraceImpedanceViaClearanceTests
{
    private static readonly LayerKey Upper = new(1, 0);
    private static readonly LayerKey Inner = new(2, 0);
    private static readonly LayerKey Lower = new(3, 0);
    private static readonly LayerKey Drill = new(4, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    /// <summary>A plane, a stripline layer and a plane, with a through via.</summary>
    private static Technology Tech() => new()
    {
        Name = "stripline",
        Layers =
        [
            new LayerDef { Key = Upper, Name = "Upper", Purpose = "conductor" },
            new LayerDef { Key = Inner, Name = "Inner", Purpose = "conductor" },
            new LayerDef { Key = Lower, Name = "Lower", Purpose = "conductor" },
            new LayerDef { Key = Drill, Name = "Via" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Upper", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Upper] },
                new StackupLayer { Kind = StackupKind.Via, Name = "V1", DrawingLayers = [Drill] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core 1", ThicknessDbu = Um(300), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Inner", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Inner] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core 2", ThicknessDbu = Um(300), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Lower", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Lower] },
            ],
        },
    };

    /// <summary>A round hole of 1 mm radius round the via at the origin, as a ring of 48 points: wider than the via's
    /// land plus the 400 µm the via-transition skip reaches, as a generous clearance is.</summary>
    private const double HoleUm = 1000;

    private static long[] RoundHole()
    {
        var xy = new long[96];
        for (int k = 0; k < 48; k++)
        {
            double a = 2 * Math.PI * k / 48;
            xy[2 * k] = Um(HoleUm * Math.Cos(a)); xy[2 * k + 1] = Um(HoleUm * Math.Sin(a));
        }
        return xy;
    }

    /// <summary>The round hole with a 600 µm slot running on from it along the trace to x = 2.5 mm.</summary>
    private static long[] HoleWithSlot()
    {
        double start = Math.Asin(300.0 / HoleUm);
        var xy = new List<long>();
        for (int k = 0; k <= 40; k++)
        {
            double a = start + (2 * Math.PI - 2 * start) * k / 40;
            xy.Add(Um(HoleUm * Math.Cos(a))); xy.Add(Um(HoleUm * Math.Sin(a)));
        }
        xy.AddRange([Um(2500), Um(-300), Um(2500), Um(300)]);
        return [.. xy];
    }

    private static long[] Box(double x0, double y0, double x1, double y1) => [Um(x0), Um(y0), Um(x1), Um(y0), Um(x1), Um(y1), Um(x0), Um(y1)];

    private static PolygonShape Plane(LayerKey layer, params long[][] holes) =>
        new() { Layer = layer, Xy = Box(-8000, -8000, 8000, 8000), Holes = [.. holes] };

    /// <summary>A 220 µm stripline on Inner from the via at the origin out 6.2 mm to an open end.</summary>
    private static TraceRun Run(PolygonShape upper, PolygonShape lower)
    {
        List<LayoutShape> board =
        [
            upper, lower,
            new ViaShape { Layer = Drill, X = 0, Y = 0, DrillSize = Um(250), PadSize = Um(500), LandingLayer = Inner },
            new RectShape { Layer = Inner, X1 = 0, Y1 = Um(-110), X2 = Um(6200), Y2 = Um(110) },
        ];
        return Assert.Single(TraceImpedanceAnalysis.Analyze(board, Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions { Layers = [Inner] }).AllTraces);
    }

    private static string Said(TraceRun t) =>
        $"Z0 {t.Z0Min:0.0}–{t.Z0Max:0.0} Ω; issues: {string.Join(" | ", t.Issues.Select(i => i.Text))}; notes: {string.Join(" | ", t.Notes)}";

    [Fact]
    public void AStriplineIntoItsViaThroughClearanceHoles_Passes_WithTwoInfoLines()
    {
        var t = Run(Plane(Upper, RoundHole()), Plane(Lower, RoundHole()));

        Assert.True(t.Verdict == TraceVerdict.Pass, Said(t));
        Assert.Equal(2, t.ViaClearances.Count);
        Assert.Equal(["above", "below"], t.ViaClearances.Select(c => c.Where).Order());
        Assert.Equal(2, t.Notes.Count(n => n.StartsWith("Info: via clearance at the start", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASlotInThePlaneLongerThanTheHole_StillFails()
    {
        var t = Run(Plane(Upper, HoleWithSlot()), Plane(Lower, RoundHole()));

        Assert.True(t.Verdict == TraceVerdict.Fail, Said(t));
        // From the via transition's edge to the slot's end: the round hole in the OTHER plane excuses nothing above.
        Assert.Contains(t.Issues, i => i.Kind == TraceIssueKind.ReturnBroken && i.Fails && i.SpanLength > Um(1800)
                                       && i.Text.Contains("'Upper' is missing above", StringComparison.Ordinal));
        Assert.DoesNotContain(t.ViaClearances, c => c.Where == "above");
    }

    [Fact]
    public void AGapInThePlaneMidTrace_StillFails()
    {
        var t = Run(Plane(Upper, RoundHole(), Box(3000, -1500, 3400, 1500)), Plane(Lower, RoundHole()));

        Assert.True(t.Verdict == TraceVerdict.Fail, Said(t));
        Assert.Contains(t.Issues, i => i.Kind == TraceIssueKind.ReturnBroken && i.Fails && i.X0 > Um(2500) && i.X1 < Um(3900));
    }
}
