// brief-review-6 follow-up — the RF review checks, one small layout per claim: coupled-lines on the shipped four-layer
// board (top copper 0.2 mm over its ground plane, so 2 h = 0.41 mm and 5 h = 1.02 mm), probe-pads on placed GSG
// pads on the shipped GaAs die, and ground-pin-via on the shipped Bias tee example's grounded capacitor.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.Review;
using static CircuitRF.Ui.Tests.Review.ReviewRunTests;

namespace CircuitRF.Ui.Tests.Review;

public sealed class RfReviewChecksTests
{
    private const long Um = 1_000;

    private static LayoutReviewResult Review(LayoutView view, Technology tech, IReadOnlyList<LayoutShape> shapes,
                                             string check, ReviewTarget target = ReviewTarget.Pcb)
    {
        var settings = new ReviewSettings { Target = target };
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == check);
        return LayoutReview.Run(new TraceImpedanceAnalysis.LayoutSource("x.clay", view, tech, null, shapes, null), settings);
    }

    // ── coupled-lines ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Two 0.3 mm traces 5 mm long on top copper, <paramref name="gapUm"/> apart edge to edge, over a plane.</summary>
    private static (LayoutView, Technology) TwoTraces(long gapUm)
    {
        var tech = Tech();
        var view = new LayoutView { DisplayUnit = LayoutUnit.Mm };
        view.Shapes.Add(Rect(Key(tech, "Inner 1"), 0, 0, 20 * Mm, 20 * Mm));
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), 5 * Mm, 10 * Mm, 10 * Mm, 10 * Mm + 300 * Um));
        long y = 10 * Mm + 300 * Um + gapUm * Um;
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), 5 * Mm, y, 10 * Mm, y + 300 * Um));
        return (view, tech);
    }

    [Fact]
    public void CoupledLines_TwoNetsCloserThanTheSpacing_AreOneFinding_AndFurtherApart_None()
    {
        var (near, tech) = TwoTraces(200);
        var finding = Assert.Single(Review(near, tech, [.. near.Shapes], "coupled-lines").Findings);
        Assert.Equal("5 mm", finding.MeasuredText);
        Assert.StartsWith("Top Copper traces of two nets run side by side for 5 mm, 0.2 mm apart", finding.Sentence);

        var (far, _) = TwoTraces(600);
        Assert.Empty(Review(far, tech, [.. far.Shapes], "coupled-lines").Findings);
    }

    // ── probe-pads ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A GSG pad placed at (<paramref name="x"/>, 0) on the GaAs die: its snapshot, its instance and its artwork.</summary>
    private static void Place(LayoutView view, List<LayoutShape> shapes, Technology tech, string name, long x,
                              double pitch = 150e-6, int nVia = 1)
    {
        var p = new Dictionary<string, PCellValue>
        {
            ["W"] = 72e-6, ["L"] = 100e-6, ["Pitch"] = pitch, ["PadW"] = 80e-6, ["PadL"] = 80e-6, ["NVia"] = (double)nVia,
        };
        view.PCellSnapshots[name] = new PCellSnapshot(GsgPadPCell.GeneratorId, p, null, null, null);
        view.Instances.Add(new LayoutInstance { CellRef = $"cells/{name}", X = x, Y = 0, SchematicId = name });
        foreach (var s in GsgPadPCell.Generate(p, tech, PCellLayerSelection.Default).Shapes)
        {
            LayoutGeometry.TranslateBy(s, x, 0);
            shapes.Add(s);
        }
    }

    [Fact]
    public void ProbePads_APadNotAtTheProbesPitch_IsAFinding()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var view = new LayoutView { DisplayUnit = LayoutUnit.Um };
        var shapes = new List<LayoutShape>();
        Place(view, shapes, tech, "P1", 0);
        Place(view, shapes, tech, "P2", 1000 * Um);
        Place(view, shapes, tech, "P3", 2000 * Um, pitch: 100e-6);

        // The default probe, 150 µm: the one pad laid out at 100 µm.
        var finding = Assert.Single(Review(view, tech, shapes, "probe-pads", ReviewTarget.Mmic).Findings);
        Assert.Equal("P3 has a 100 µm pitch and the probe 150 µm: the probe will not land on it", finding.Sentence);

        // A 100 µm probe: the other two.
        var settings = new ReviewSettings { Target = ReviewTarget.Mmic };
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == "probe-pads");
        var def = LayoutReview.FindCheck("probe-pads")!;
        settings.Set(def, def.Threshold("probe-pitch")!, 100 * Um, view.DbuPerMicron);
        var source = new TraceImpedanceAnalysis.LayoutSource("x.clay", view, tech, null, shapes, null);
        Assert.Equal(["P1", "P2"], LayoutReview.Run(source, settings).Findings.Select(f => f.Sentence[..2]));
    }

    [Fact]
    public void ProbePads_GroundPadsWithNoVia_AndMetalWhereTheProbeComesIn_AreFindings()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var view = new LayoutView { DisplayUnit = LayoutUnit.Um };
        var shapes = new List<LayoutShape>();
        Place(view, shapes, tech, "P1", 0, nVia: 0);
        Place(view, shapes, tech, "P2", 1000 * Um);   // grounded through its vias, so the die has ground
        shapes.Add(Rect(Key(tech, "Metal2"), -100 * Um, -20 * Um, -70 * Um, 20 * Um));   // 30 µm in front of the pads

        var findings = Review(view, tech, shapes, "probe-pads", ReviewTarget.Mmic).Findings;
        Assert.Equal(2, findings.Count(f => f.Sentence.StartsWith("A ground pad of P1 is not on ground", StringComparison.Ordinal)));
        var front = Assert.Single(findings, f => f.Sentence.StartsWith("Metal", StringComparison.Ordinal));
        Assert.Equal("30 µm", front.MeasuredText);
    }

    // ── ground-pin-via ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GroundPinVia_OnTheBiasTee_JudgesTheGroundedCapacitorPin()
    {
        string clay = Path.Combine(RepoRoot(), "examples", "LVS", "Bias tee", "layout", "Bias tee.clay");
        var source = TraceImpedanceAnalysis.LoadLayout(clay);
        var settings = new ReviewSettings { Target = ReviewTarget.Mmic };
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == "ground-pin-via");
        Assert.Empty(LayoutReview.Run(source, settings).Findings);

        settings.Set(LayoutReview.FindCheck("ground-pin-via")!, LayoutReview.FindCheck("ground-pin-via")!.Thresholds[0], 1 * Um,
                     source.View.DbuPerMicron);
        var finding = Assert.Single(LayoutReview.Run(source, settings).Findings);
        Assert.Contains("C2 pin 2", finding.Sentence);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
