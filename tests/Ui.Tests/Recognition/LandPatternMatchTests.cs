// brief-artsch-4-parts-and-parts-table.md §4 — LandPatternMatch: generated pads are matched to their
// case, a pair 30 % too big is not, and overlapping candidates resolve to the better fit.

using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class LandPatternMatchTests
{
    private static PadOutline[] Copper(System.Collections.Generic.IEnumerable<RectShape> shapes) =>
        [.. shapes.Where(s => s.Layer == Top)
                  .Select(s => new PadOutline(new Bbox(s.X1, s.Y1, s.X2, s.Y2), Top, PadOutlineSource.Copper))];

    [Fact]
    public void GeneratedPadsAtEachDensityAndBothOrientationsMatchTheirCase()
    {
        foreach (string code in new[] { "0402", "0603", "0805" })
            foreach (var density in new[] { DensityLevel.Most, DensityLevel.Nominal, DensityLevel.Least })
                foreach (bool vertical in new[] { false, true })
                {
                    var found = LandPatternMatch.Find(Copper(Land(code, density, 5_000, 5_000, vertical)), LayoutUnits.DefaultDbuPerMicron);

                    var c = Assert.Single(found);
                    Assert.Equal((code, density, vertical), (c.Best.Case.Code, c.Best.Density, c.Vertical));
                }
    }

    /// <summary>An 0805 grown by 30 % fits no case. (Not every case: an 0603 grown by 30 % is within
    /// tolerance of the two-pad crystal's least-density land, which is a correct reading.)</summary>
    [Fact]
    public void APairThirtyPercentTooBigIsNotMatched()
    {
        var found = LandPatternMatch.Find(Copper(Land("0805", DensityLevel.Nominal, 0, 0, scale: 1.3)), LayoutUnits.DefaultDbuPerMicron);

        Assert.Empty(found);
    }

    /// <summary>Three pads in a line: the left two are an exact 0402, the right two the same lands with a
    /// gap 15 % wide. The middle pad goes to the exact fit.</summary>
    [Fact]
    public void OverlappingCandidatesResolveToTheBetterFit()
    {
        var pads = Copper(Land("0402", DensityLevel.Nominal, 0, 0));
        var left = pads.OrderBy(p => p.CentreX).First();
        var right = pads.OrderBy(p => p.CentreX).Last();
        long gap = right.Bounds.MinX - left.Bounds.MaxX;
        long shift = right.Bounds.MaxX - left.Bounds.MinX + (long)(1.15 * gap);   // a third land, 1.15 gaps beyond
        var third = right with { Bounds = new Bbox(left.Bounds.MinX + shift, left.Bounds.MinY, left.Bounds.MaxX + shift, left.Bounds.MaxY) };

        var found = LandPatternMatch.Find([left, right, third], LayoutUnits.DefaultDbuPerMicron);

        var c = Assert.Single(found);
        Assert.Equal((0, 1), (c.A, c.B));
        Assert.Equal(0, c.Best.Error, 6);
    }
}
