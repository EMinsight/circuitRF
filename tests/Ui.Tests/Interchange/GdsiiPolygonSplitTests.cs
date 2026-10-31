using Clipper2Lib;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests.Interchange;

/// <summary>brief-gdf-5: a GDSII element holds at most 8191 points. A fine-pitched spiral has more, and the
/// record writer threw on it — `convert` died with an unhandled exception. Such a polygon is now written as
/// abutting pieces; these hold that every piece fits and that together they cover exactly the original.</summary>
public sealed class GdsiiPolygonSplitTests
{
    /// <summary>A 20,000-point wobbly disc with a square hole, keyholed the way lowering keyholes one.</summary>
    private static long[] Big()
    {
        const int n = 20_000;
        var outer = new long[n * 2];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n, r = 1_000_000 + 20_000 * Math.Sin(37 * a);
            outer[2 * i] = (long)Math.Round(r * Math.Cos(a));
            outer[2 * i + 1] = (long)Math.Round(r * Math.Sin(a));
        }
        long[] hole = [-200_000, -200_000, -200_000, 200_000, 200_000, 200_000, 200_000, -200_000];
        return StreamLowering.Keyhole(outer, [hole]);
    }

    private static Paths64 Union(IEnumerable<long[]> rings)
    {
        var paths = new Paths64();
        foreach (var xy in rings)
        {
            var p = new Path64();
            for (int i = 0; i + 1 < xy.Length; i += 2) p.Add(new Point64(xy[i], xy[i + 1]));
            paths.Add(p);
        }
        return Clipper.Union(paths, FillRule.NonZero);
    }

    [Fact]
    public void ABigPolygon_SplitsIntoPiecesThatFit_CoveringExactlyTheOriginal()
    {
        var big = Big();
        var pieces = GdsiiPolygonSplit.Split(big);

        Assert.True(pieces.Count > 2);
        Assert.All(pieces, p => Assert.InRange(p.Length / 2, 3, GdsiiPolygonSplit.MaxPoints));
        var original = Union([big]);
        var together = Union(pieces);
        Assert.Equal(0, Clipper.Area(Clipper.Xor(original, together, FillRule.NonZero)));
        Assert.True(Clipper.Area(original) > 0);
    }

    [Fact]
    public void TheWriter_WritesIt_AndCountsTheSplit()
    {
        var library = new StreamLibrary(
            [new StreamStructure("TOP", [new StreamPolygon(new LayerKey(1, 0), Big())], [])], 0, 0, 0, 0, 0, [], []);
        using var stream = new MemoryStream();
        var summary = GdsiiWriter.Write(stream, library, new GdsiiUnits(1e-6, 1e-9));
        Assert.Equal(1, summary.PolygonsSplit);
    }
}
