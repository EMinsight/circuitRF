// Which part of the artwork a recognition reads — brief-artsch-3-board-graph.md R-as3-7, overview D11.
//
// The whole layout, the GUI selection's outline, or the CLI's --region rectangle. Copper outside it is
// not read; a signal island the boundary cuts gets a port at each crossing. GROUND IS STILL READ FROM
// THE WHOLE BOARD — a clipped region's ground is the board's ground, and a pour the selection happens to
// cut in two is still one ground, not two islands.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>A recognition's scope: everything, or a set of closed rings in DBU.</summary>
public sealed record RecognitionScope
{
    private RecognitionScope(IReadOnlyList<long[]> rings) => Rings = rings;

    /// <summary>The whole layout.</summary>
    public static RecognitionScope Whole { get; } = new([]);

    /// <summary>The scope's rings, each a flat x0,y0,x1,y1,… list in DBU — the spelling
    /// <see cref="PolygonShape.Xy"/> uses. Empty for <see cref="Whole"/>.</summary>
    public IReadOnlyList<long[]> Rings { get; }

    /// <summary>True for <see cref="Whole"/>, and for a set of rings that encloses nothing.</summary>
    public bool IsWhole => Rings.Count == 0;

    /// <summary>A rectangle, corners in any order, DBU.</summary>
    public static RecognitionScope Rectangle(long x0, long y0, long x1, long y1)
    {
        long ax = Math.Min(x0, x1), bx = Math.Max(x0, x1), ay = Math.Min(y0, y1), by = Math.Max(y0, y1);
        return new RecognitionScope([[ax, ay, bx, ay, bx, by, ax, by]]);
    }

    /// <summary>A set of polygons — the GUI selection's outline. Rings of fewer than three points are
    /// ignored; none left is <see cref="Whole"/>.</summary>
    public static RecognitionScope Polygons(IEnumerable<long[]> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var kept = rings.Where(r => r is { Length: >= 6 } && r.Length % 2 == 0).Select(r => (long[])r.Clone()).ToList();
        return kept.Count == 0 ? Whole : new RecognitionScope(kept);
    }

    /// <summary>The rings unioned, Clipper form — overlapping selection outlines are one region.</summary>
    internal Paths64 Paths()
    {
        var paths = LayoutClipper.RingsToClipperPaths(Rings);
        for (int i = 0; i < paths.Count; i++)
            if (Clipper.Area(paths[i]) < 0) paths[i].Reverse();   // a selection's winding is not ours to read
        return Clipper.Union(paths, LayoutClipper.Rule);
    }
}
