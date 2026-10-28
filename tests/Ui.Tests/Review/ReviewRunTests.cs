// brief-review-2 §2 — the review run's frame: layer roles found by ROLE (the same roles on a technology
// numbered differently), a check whose role is missing reported not run with the sentence that fixes it,
// the one check this brief ships (edge-copper) firing and staying silent, acceptances by key with a stale
// one listed, and the settings round-tripping through the .clay writing only what differs from the default.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;

namespace CircuitRF.Ui.Tests.Review;

public sealed class ReviewRunTests
{
    internal const string FourLayer = "pcb-4layer_FR-4_62mil_1oz";
    internal const long Mm = 1_000_000;   // DBU at the default 1000 DBU/µm

    internal static Technology Tech() => ShippedTechnologies.Load(FourLayer);

    internal static LayerKey Key(Technology tech, string name) => tech.Layers.First(l => l.Name == name).Key;

    internal static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>A 20 mm square board with a 5 mm square of top copper <paramref name="inset"/> from its left edge.</summary>
    internal static LayoutView Board(Technology tech, long inset)
    {
        var view = new LayoutView { DisplayUnit = LayoutUnit.Mm };
        view.Shapes.Add(Rect(Key(tech, "Outline"), 0, 0, 20 * Mm, 20 * Mm));
        view.Shapes.Add(Rect(Key(tech, "Top Copper"), inset, 5 * Mm, inset + 5 * Mm, 10 * Mm));
        return view;
    }

    /// <summary>Reviews with edge-copper alone unless <paramref name="settings"/> says otherwise — the frame's
    /// one check; the pad checks have their own class.</summary>
    private static LayoutReviewResult Run(LayoutView view, Technology tech, ReviewSettings? settings = null)
    {
        if (settings is null)
        {
            settings = new ReviewSettings();
            foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, c.Id == "edge-copper");
        }
        return LayoutReview.Run(new TraceImpedanceAnalysis.LayoutSource("board.clay", view, tech, null, [.. view.Shapes], null), settings);
    }

    [Fact]
    public void Roles_AreTheSame_OnAShippedTechnology_AndOneNumberedDifferently()
    {
        var shipped = Tech();
        var renumbered = Tech();
        foreach (var l in renumbered.Layers) l.Key = new LayerKey(l.Key.Layer + 100, l.Key.Datatype + 3);
        foreach (var row in renumbered.Stackup.Layers)
            row.DrawingLayers = [.. row.DrawingLayers.Select(k => new LayerKey(k.Layer + 100, k.Datatype + 3))];

        var a = ReviewLayers.For(shipped);
        var b = ReviewLayers.For(renumbered);

        static object Roles(ReviewLayers r) => new
        {
            Copper = string.Join("|", r.Copper.Select(c => c.Name)),
            Adjacent = string.Join("|", r.AdjacentCopper.Select(p => $"{p.Upper.Name}/{p.Lower.Name}")),
            Surfaces = string.Join("|", new[] { r.TopCopper, r.BottomCopper, r.TopMask, r.BottomMask }
                                        .Select(k => k is { } key ? r.NameOf(key) : "-")),
            Drills = string.Join("|", r.Drills.Select(d => $"{d.Entry.Name}:{d.Top?.Name}-{d.Bottom?.Name}")),
            Outline = string.Join("|", r.Outline.Select(r.NameOf)),
            Ground = r.GroundReference?.Name,
        };

        Assert.Equal(Roles(a), Roles(b));
        Assert.Equal(4, a.Copper.Count);
        Assert.Equal(3, a.AdjacentCopper.Count);
        Assert.Equal("Top Copper|Bottom Copper|Soldermask Top|Soldermask Bottom",
                     string.Join("|", new[] { a.TopCopper, a.BottomCopper, a.TopMask, a.BottomMask }.Select(k => a.NameOf(k!.Value))));
        Assert.Equal("Outline", Assert.Single(a.Outline.Select(a.NameOf)));
        Assert.Equal("Inner 1 (Ground Plane)", a.GroundReference?.Name);
        Assert.NotEqual(a.TopCopper, b.TopCopper);   // the same role, found on a different number
    }

    [Fact]
    public void WithNoOutlineLayer_EdgeCopperIsNotRun_WithTheSentenceThatFixesIt()
    {
        var tech = Tech();
        tech.Layers.First(l => l.Name == "Outline").Interchange = null;

        var result = Run(Board(Tech(), 100_000), tech);

        var row = Assert.Single(result.Checks);
        Assert.Equal(ReviewCheckState.NotRun, row.State);
        Assert.Contains("Gerber X2 Function to Profile", row.NotRunReason);
        var note = Assert.Single(result.Findings);
        Assert.Equal(ReviewSeverity.Note, note.Severity);
        Assert.Equal(row.NotRunReason, note.Sentence);
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void CopperInsideThePullback_IsOneFinding_AndOutsideItNone()
    {
        var tech = Tech();

        var near = Run(Board(tech, 100_000), tech);
        var finding = Assert.Single(near.Findings);
        Assert.Equal("edge-copper", finding.CheckId);
        Assert.Equal(Key(tech, "Top Copper"), finding.Layer);
        Assert.Equal("Copper 0.1 mm from the board edge; the review asks for 0.25 mm", finding.Sentence);
        Assert.Equal(new Bbox(100_000, 5 * Mm, 250_000, 10 * Mm), finding.Marker);
        Assert.Equal(ReviewCheckState.Ran, Assert.Single(near.Checks).State);

        Assert.Empty(Run(Board(tech, 300_000), tech).Findings);
    }

    [Fact]
    public void AnAcceptance_MatchesByKey_AndOneMatchingNothingIsListedStale()
    {
        var tech = Tech();
        var result = Run(Board(tech, 100_000), tech);
        var finding = Assert.Single(result.Findings);
        var accepted = ReviewAcceptance.For(finding, "Top Copper", "Edge-launch connector", DateTime.UnixEpoch);
        var gone = new ReviewAcceptance { Key = "edge-copper|1/0|0,0,1,1", CheckId = "edge-copper", Reason = "old" };

        var applied = ReviewAcceptance.Apply(result, [accepted, gone]);

        Assert.Same(accepted, Assert.Single(applied.Findings).Accepted);
        Assert.Equal(0, applied.WarningCount);
        Assert.Equal(1, Assert.Single(applied.Checks).AcceptedCount);
        Assert.Same(gone, Assert.Single(applied.StaleAcceptances));
    }

    [Fact]
    public void Settings_RoundTripThroughTheClay_WritingOnlyWhatDiffers_AndKeepingWhatItDoesNotKnow()
    {
        var tech = Tech();
        var view = Board(tech, 100_000);
        var check = LayoutReview.FindCheck("edge-copper")!;
        var pullback = check.Threshold("pullback")!;

        view.Review = new ReviewSettings();
        view.Review.Set(check, pullback, 400_000, view.DbuPerMicron);
        view.Review.Thresholds!["a-later-check.reach"] = 2 * Mm;   // a later build's check
        view.Review.SetEnabled("via-in-pad", false);

        string json = LayoutPersistence.Serialize(view);
        var back = LayoutPersistence.Deserialize(json);

        Assert.Equal(400_000, back.Review!.Value(check, pullback, back.DbuPerMicron));
        Assert.Equal(2 * Mm, back.Review.Thresholds!["a-later-check.reach"]);
        Assert.False(back.Review.IsEnabled("via-in-pad"));
        Assert.Equal(json, LayoutPersistence.Serialize(back));

        // Back at its default, the threshold is not written — and a layout at the defaults writes no Review.
        back.Review.Set(check, pullback, pullback.DefaultStored(back.DbuPerMicron), back.DbuPerMicron);
        Assert.DoesNotContain("edge-copper.pullback", LayoutPersistence.Serialize(back));
        var plain = Board(tech, 100_000);
        plain.Review = new ReviewSettings { Thresholds = new() { ["edge-copper.pullback"] = 250_000 } };
        Assert.DoesNotContain("\"Review\"", LayoutPersistence.Serialize(plain));
    }
}
