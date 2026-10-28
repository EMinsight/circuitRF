// brief-review-4 §4 — stacked traces and the three edge checks, one small layout per claim on the shipped
// four-layer technology: a 20 mm square board, its Inner 1 the flagged ground reference. Where ground is
// needed a plane is drawn there, and a via is a plated through-hole (the Drill layer) landing on top copper.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using static CircuitRF.Ui.Tests.Review.ReviewRunTests;

namespace CircuitRF.Ui.Tests.Review;

public sealed class CopperEdgeChecksTests
{
    private const long Um = 1_000;
    private const long Side = 20 * Mm;

    private static readonly string[] EdgeChecks = ["edge-copper", "edge-trace", "edge-stitching"];

    private static (LayoutView View, Technology Tech) Board(bool plane = false, bool outline = true)
    {
        var tech = Tech();
        var view = new LayoutView { DisplayUnit = LayoutUnit.Mm };
        if (outline) view.Shapes.Add(new PolygonShape { Layer = Key(tech, "Outline"), Xy = [0, 0, Side, 0, Side, Side, 0, Side] });
        if (plane) view.Shapes.Add(Rect(Key(tech, "Inner 1"), 0, 0, Side, Side));
        return (view, tech);
    }

    private static void Via(LayoutView view, Technology tech, long x, long y) =>
        view.Shapes.Add(new ViaShape
        {
            Layer = Key(tech, "Drill"), X = x, Y = y, DrillSize = 300 * Um, PadSize = 600 * Um, LandingLayer = Key(tech, "Top Copper"),
        });

    private static LayoutReviewResult Review(LayoutView view, Technology tech, params string[] checks)
    {
        var settings = new ReviewSettings();
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, checks.Contains(c.Id));
        return LayoutReview.Run(new TraceImpedanceAnalysis.LayoutSource("board.clay", view, tech, null, [.. view.Shapes], null), settings);
    }

    private static double Mms(string measured) =>
        double.Parse(measured.Replace(" mm", ""), CultureInfo.InvariantCulture);

    // ── 1. stacked-traces ─────────────────────────────────────────────────────────────────────────

    /// <summary>A 0.15 mm Top Copper trace over a 0.10 mm trace on <paramref name="lower"/>, both 3 mm along x — or,
    /// <paramref name="crossing"/>, under a 1.2 mm trace crossing it at 90°, whose overlap is 1.2 mm by area over width.</summary>
    private static (LayoutView, Technology) Stacked(string lower, bool crossing = false, bool joined = false)
    {
        var (view, tech) = Board();
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), 5 * Mm, 10 * Mm - 75 * Um, 8 * Mm, 10 * Mm + 75 * Um));
        view.Shapes.Add(crossing
            ? Rect(Key(tech, lower), 6500 * Um - 600 * Um, 7 * Mm, 6500 * Um + 600 * Um, 13 * Mm)
            : Rect(Key(tech, lower), 5 * Mm, 10 * Mm - 50 * Um, 8 * Mm, 10 * Mm + 50 * Um));
        if (joined) Via(view, tech, 8 * Mm, 10 * Mm);
        return (view, tech);
    }

    [Fact]
    public void StackedTraces_DifferentNets_IsOneFinding_ButCrossing_OneNet_OrAConductorBetween_None()
    {
        var (view, tech) = Stacked("Inner 1");
        var finding = Assert.Single(Review(view, tech, "stacked-traces").Findings);
        Assert.Equal(Key(tech, "Top Copper"), finding.Layer);
        Assert.Equal("Top Copper and Inner 1 traces run on top of each other for 3 mm (0.15 mm and 0.1 mm wide, different nets)",
                     finding.Sentence);

        var (crossing, _) = Stacked("Inner 1", crossing: true);
        var (found, _) = TraceImpedanceAnalysis.FindTraces([.. crossing.Shapes], tech, crossing.DbuPerMicron, default);
        Assert.Single(found[Key(tech, "Inner 1")]);   // the wide one is a trace, so it is compared
        Assert.Empty(Review(crossing, tech, "stacked-traces").Findings);

        var (joined, _) = Stacked("Inner 1", joined: true);
        Assert.Empty(Review(joined, tech, "stacked-traces").Findings);

        var (apart, _) = Stacked("Inner 2");
        Assert.Empty(Review(apart, tech, "stacked-traces").Findings);
    }

    // ── 2. edge-copper ────────────────────────────────────────────────────────────────────────────

    private static (LayoutView, Technology) PourAt(long inset)
    {
        var (view, tech) = Board();
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), inset, 5 * Mm, inset + 5 * Mm, 10 * Mm));
        return (view, tech);
    }

    [Fact]
    public void APour0p1mmFromTheEdge_IsOneEdgeCopper_And0p3mmNone()
    {
        var (near, tech) = PourAt(100 * Um);
        var finding = Assert.Single(Review(near, tech, "edge-copper").Findings);
        Assert.Equal("Copper 0.1 mm from the board edge; the review asks for 0.25 mm", finding.Sentence);

        var (far, _) = PourAt(300 * Um);
        Assert.Empty(Review(far, tech, "edge-copper").Findings);
    }

    // ── 3. edge-trace ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATrace0p5mmFromTheEdge_IsOneEdgeTrace_AndAPourThere_IsNeither()
    {
        var (view, tech) = Board();
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), 500 * Um, 5 * Mm, 650 * Um, 10 * Mm));
        var finding = Assert.Single(Review(view, tech, EdgeChecks[..2]).Findings);
        Assert.Equal("edge-trace", finding.CheckId);
        Assert.Equal("Trace 0.5 mm from the board edge (0.15 mm wide); the review asks for 1 mm", finding.Sentence);

        var (pour, _) = PourAt(500 * Um);
        Assert.Empty(Review(pour, tech, EdgeChecks[..2]).Findings);

        var (close, _) = PourAt(100 * Um);
        Assert.Equal("edge-copper", Assert.Single(Review(close, tech, EdgeChecks[..2]).Findings).CheckId);
    }

    // ── 4. edge-stitching: the vias ───────────────────────────────────────────────────────────────

    /// <summary>A ring of ground vias on the band's middle line, 13 to a side (1.46 mm apart), less <paramref name="omit"/>.</summary>
    private static (LayoutView, Technology) Ring(params int[] omit)
    {
        var (view, tech) = Board(plane: true);
        long lo = 500 * Um, hi = Side - 500 * Um;
        var at = new List<(long, long)>();
        for (int k = 0; k < 13; k++)
        {
            long s = lo + (hi - lo) * k / 13;
            at.Add((s, lo));
            at.Add((hi, s));
            at.Add((hi - (s - lo), hi));
            at.Add((lo, hi - (s - lo)));
        }
        // The bottom side's vias are at.Where(index % 4 == 0); omit counts along it.
        for (int i = 0; i < at.Count; i++)
            if (i % 4 != 0 || !omit.Contains(i / 4)) Via(view, tech, at[i].Item1, at[i].Item2);
        return (view, tech);
    }

    private static IEnumerable<ReviewFinding> StitchingGaps(LayoutReviewResult result) =>
        result.Findings.Where(f => f.CheckId == "edge-stitching" && f.Layer is null);

    [Fact]
    public void ARingOfGroundViasEvery1p5mm_HasNoStitchingGap_AndThreeRemoved_IsOneGap_WithinOnePitch()
    {
        var (full, tech) = Ring();
        Assert.Empty(StitchingGaps(Review(full, tech, "edge-stitching")));

        var (missing, _) = Ring(5, 6, 7);
        var gap = Assert.Single(StitchingGaps(Review(missing, tech, "edge-stitching")));
        double pitch = 19.0 / 13;
        Assert.InRange(Mms(gap.MeasuredText!), 4 * pitch - pitch, 4 * pitch + pitch);
        Assert.EndsWith("of board edge with no ground stitching via; the review asks for one every 2 mm", gap.Sentence);
    }

    // ── 5. edge-stitching: the ground copper ──────────────────────────────────────────────────────

    [Fact]
    public void AnOuterGroundPourOnThreeSidesOfTheBand_IsOneNoGroundCopperFinding_OnTheFourth()
    {
        var (view, tech) = Board(plane: true);
        var top = Key(tech, "Top Copper");
        view.Shapes.Add(Rect(top, 0, 0, 2 * Mm, Side));
        view.Shapes.Add(Rect(top, 0, 0, Side, 2 * Mm));
        view.Shapes.Add(Rect(top, Side - 2 * Mm, 0, Side, Side));
        Via(view, tech, 10 * Mm, 1 * Mm);   // joins the pour to the plane

        var onTop = Review(view, tech, "edge-stitching").Findings.Where(f => f.Layer == top);

        var finding = Assert.Single(onTop);
        Assert.Equal("16 mm of board edge on Top Copper with no ground copper", finding.Sentence);
        Assert.True(finding.Marker.MinY >= Side - 1 * Mm, "the finding is on the fourth (top) side");
    }

    // ── 6. the board region ───────────────────────────────────────────────────────────────────────

    /// <summary>Test 5's pour, a trace near the left edge and a pour inside the pullback, on any outline.</summary>
    private static (LayoutView, Technology) EdgeArtwork(bool outline)
    {
        var (view, tech) = Board(plane: true, outline: outline);
        var top = Key(tech, "Top Copper");
        view.Shapes.Add(Rect(top, 0, 0, Side, 2 * Mm));
        view.Shapes.Add(Rect(top, 3 * Mm, 5 * Mm, 3 * Mm + 150 * Um, 12 * Mm));
        view.Shapes.Add(Rect(top, Side - 100 * Um - 3 * Mm, 8 * Mm, Side - 100 * Um, 11 * Mm));
        view.Shapes.Add(Rect(top, 500 * Um, 14 * Mm, 650 * Um, 19 * Mm));
        Via(view, tech, 10 * Mm, 1 * Mm);
        return (view, tech);
    }

    private static void Stroke(LayoutView view, Technology tech, long x1, long y1, long x2, long y2) =>
        view.Shapes.Add(new PathShape { Layer = Key(tech, "Outline"), Xy = [x1, y1, x2, y2], Width = 100 * Um });

    [Fact]
    public void AnOutlineOfFourStrokes_FindsWhatTheClosedPolygonFinds_AndOneWithAGap_IsNotRun_SayingWhere()
    {
        var (polygon, tech) = EdgeArtwork(outline: true);
        var (strokes, _) = EdgeArtwork(outline: false);
        Stroke(strokes, tech, 0, 0, Side, 0);
        Stroke(strokes, tech, Side, 0, Side, Side);
        Stroke(strokes, tech, Side, Side, 0, Side);
        Stroke(strokes, tech, 0, Side, 0, 0);

        static string Seen(LayoutReviewResult r) =>
            string.Join("\n", r.Findings.Select(f => $"{f.CheckId}|{f.LayerName}|{f.Sentence}|{f.Marker}"));
        var expected = Review(polygon, tech, EdgeChecks);
        Assert.Contains(expected.Findings, f => f.CheckId == "edge-copper");
        Assert.Contains(expected.Findings, f => f.CheckId == "edge-trace");
        Assert.Contains(expected.Findings, f => f.CheckId == "edge-stitching");
        Assert.Equal(Seen(expected), Seen(Review(strokes, tech, EdgeChecks)));

        var (open, _) = EdgeArtwork(outline: false);
        Stroke(open, tech, 0, 0, Side, 0);
        Stroke(open, tech, Side, 0, Side, Side);
        Stroke(open, tech, Side, Side, 14 * Mm, Side);
        Stroke(open, tech, 12 * Mm, Side, 0, Side);
        Stroke(open, tech, 0, Side, 0, 0);
        var result = Review(open, tech, EdgeChecks);
        Assert.All(result.Checks, c => Assert.Equal(ReviewCheckState.NotRun, c.State));
        var note = Assert.Single(result.Findings);
        Assert.Contains("does not close: its largest gap, 2 mm, is at (13, 20) mm", note.Sentence);
    }
}
