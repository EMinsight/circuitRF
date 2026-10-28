// brief-review-3 §4 — the four pad and via checks, one small layout per claim on the shipped four-layer
// technology: its Inner 1 is the flagged ground reference and is DRAWN, so every board here draws a plane
// there and ground is that plane's net. A via is a plated through-hole (the Drill layer) landing on top
// copper; a pad is top copper under a top mask opening.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using static CircuitRF.Ui.Tests.Review.ReviewRunTests;

namespace CircuitRF.Ui.Tests.Review;

public sealed class PadViaChecksTests
{
    private const long Um = 1_000;

    private static readonly string[] PadChecks = ["via-in-pad", "ground-via-distance", "shared-ground-via", "thermal-vias"];

    /// <summary>A 20 mm board with a ground plane drawn on Inner 1 under all of it.</summary>
    private static (LayoutView View, Technology Tech) Board()
    {
        var tech = Tech();
        var view = new LayoutView { DisplayUnit = LayoutUnit.Mm };
        view.Shapes.Add(Rect(Key(tech, "Inner 1"), -10 * Mm, -10 * Mm, 10 * Mm, 10 * Mm));
        return (view, tech);
    }

    /// <summary>A pad: top copper and the mask opening over it.</summary>
    private static void Pad(LayoutView view, Technology tech, long x1, long y1, long x2, long y2)
    {
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), x1, y1, x2, y2));
        view.Shapes.Add(Rect(Key(tech, "Soldermask Top"), x1, y1, x2, y2));
    }

    private static void Via(LayoutView view, Technology tech, long x, long y, long drill = 200 * Um, long pad = 400 * Um) =>
        view.Shapes.Add(new ViaShape
        {
            Layer = Key(tech, "Drill"), X = x, Y = y, DrillSize = drill, PadSize = pad, LandingLayer = Key(tech, "Top Copper"),
        });

    private static void Trace(LayoutView view, Technology tech, long x1, long x2, long y) =>
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), x1, y - 50 * Um, x2, y + 50 * Um));

    private static LayoutReviewResult Review(LayoutView view, Technology tech, params string[] checks)
    {
        var settings = new ReviewSettings();
        foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, checks.Contains(c.Id));
        return LayoutReview.Run(new TraceImpedanceAnalysis.LayoutSource("board.clay", view, tech, null, [.. view.Shapes], null), settings);
    }

    [Fact]
    public void AViaInsideA0603Pad_IsOneFinding_AndThe_SameVia_OutsideIt_None()
    {
        var (inside, tech) = Board();
        Pad(inside, tech, 0, 0, 600 * Um, 500 * Um);
        Via(inside, tech, 450 * Um, 250 * Um);   // off the pad's centre, so not a pin's hole

        var finding = Assert.Single(Review(inside, tech, "via-in-pad").Findings);
        Assert.Equal("Via in pad (drill ⌀0.2 mm inside a 0.6 × 0.5 mm pad)", finding.Sentence);
        Assert.Equal(Key(tech, "Top Copper"), finding.Layer);

        var (outside, _) = Board();
        Pad(outside, tech, 0, 0, 600 * Um, 500 * Um);
        Via(outside, tech, 900 * Um, 250 * Um);   // barrel edge 0.2 mm from the pad

        Assert.Empty(Review(outside, tech, "via-in-pad").Findings);
    }

    [Fact]
    public void AThroughHolePin_PadCentredOnItsDrill_IsNoViaInPad()
    {
        var (view, tech) = Board();
        Pad(view, tech, -800 * Um, -800 * Um, 800 * Um, 800 * Um);
        Via(view, tech, 0, 0, drill: 1000 * Um, pad: 1600 * Um);

        var result = Review(view, tech, "via-in-pad");

        Assert.Equal(ReviewCheckState.Ran, Assert.Single(result.Checks).State);
        Assert.Empty(result.Findings);
    }

    /// <summary>The pad is joined to the via's land by a top-copper trace; the gap is pad edge to barrel edge.</summary>
    private static (LayoutView, Technology) GroundPadWithViaAt(long gap)
    {
        var (view, tech) = Board();
        Pad(view, tech, 0, 0, 600 * Um, 500 * Um);
        long x = 600 * Um + gap + 100 * Um;
        Trace(view, tech, 500 * Um, x, 250 * Um);
        Via(view, tech, x, 250 * Um);
        return (view, tech);
    }

    [Fact]
    public void AGroundPad_WithItsViaAt0p3mm_IsQuiet_AndAt0p8mm_IsOneFinding_StatingTheDistance()
    {
        var (near, tech) = GroundPadWithViaAt(300 * Um);
        Assert.Empty(Review(near, tech, "ground-via-distance").Findings);

        var (far, _) = GroundPadWithViaAt(800 * Um);
        var finding = Assert.Single(Review(far, tech, "ground-via-distance").Findings);
        Assert.Equal("Nearest ground via 0.8 mm from this ground pad; the review asks for 0.5 mm", finding.Sentence);
        Assert.Equal("0.8 mm", finding.MeasuredText);
    }

    [Fact]
    public void TwoGroundPads_WhoseOnlyViaIsTheSame_AreOneFinding_AndGivingOneItsOwnViaClearsIt()
    {
        static (LayoutView, Technology) Two(bool ownVia)
        {
            var (view, tech) = Board();
            Pad(view, tech, 0, 0, 600 * Um, 500 * Um);
            Pad(view, tech, 1400 * Um, 0, 2000 * Um, 500 * Um);
            Trace(view, tech, 500 * Um, 1500 * Um, 250 * Um);
            Via(view, tech, 1000 * Um, 250 * Um);   // 0.3 mm from each pad
            if (ownVia) Via(view, tech, 2400 * Um, 250 * Um);
            return (view, tech);
        }

        var (shared, tech) = Two(ownVia: false);
        var finding = Assert.Single(Review(shared, tech, "shared-ground-via").Findings);
        Assert.StartsWith("2 ground pads depend on this one ground via", finding.Sentence);

        var (own, _) = Two(ownVia: true);
        Assert.Empty(Review(own, tech, "shared-ground-via").Findings);
    }

    /// <summary>A 3 × 3 mm ground paddle with a grid of <paramref name="perSide"/>² vias inside it.</summary>
    private static (LayoutView, Technology) Paddle(int perSide)
    {
        var (view, tech) = Board();
        Pad(view, tech, 0, 0, 3 * Mm, 3 * Mm);
        long pitch = 3 * Mm / perSide;
        for (int i = 0; i < perSide; i++)
            for (int j = 0; j < perSide; j++)
                Via(view, tech, pitch / 2 + i * pitch, pitch / 2 + j * pitch, pad: 0);
        return (view, tech);
    }

    [Fact]
    public void A9mm2ThermalPad_WithFourVias_IsOneFinding_WithNineNone_AndItsViasAreNotViaInPad()
    {
        var (four, tech) = Paddle(2);
        var result = Review(four, tech, "via-in-pad", "thermal-vias");
        var finding = Assert.Single(result.Findings);
        Assert.Equal("thermal-vias", finding.CheckId);
        Assert.Equal("4 vias under a 9 mm² thermal pad, one per 1.5 mm pitch; the review asks for one per 1 mm pitch", finding.Sentence);

        // Said in the layout's own unit, squared for the area.
        four.DisplayUnit = LayoutUnit.Um;
        Assert.Equal("4 vias under a 9,000,000 µm² thermal pad, one per 1500 µm pitch; the review asks for one per 1000 µm pitch",
                     Assert.Single(Review(four, tech, "via-in-pad", "thermal-vias").Findings).Sentence);

        var (nine, _) = Paddle(3);
        Assert.Empty(Review(nine, tech, "via-in-pad", "thermal-vias").Findings);
    }

    [Fact]
    public void WithNoGroundReferenceFlagged_AllFourAreNotRun_WithOneNote()
    {
        var (view, tech) = GroundPadWithViaAt(800 * Um);
        foreach (var row in tech.Stackup.Layers) row.IsGroundReference = false;

        var result = Review(view, tech, PadChecks);

        Assert.Equal(PadChecks, result.Checks.Select(c => c.Id));
        Assert.All(result.Checks, c => Assert.Equal(ReviewCheckState.NotRun, c.State));
        var note = Assert.Single(result.Findings);
        Assert.Equal(ReviewSeverity.Note, note.Severity);
        Assert.Contains("flag the ground plane in the Technology Editor", note.Sentence);
    }
}
