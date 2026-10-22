// Which part of the artwork a recognition reads — brief-artsch-3-board-graph.md R-as3-7, overview D11.
//
// The whole layout, the GUI selection's outline, or the CLI's --region rectangle. Copper outside it is
// not read; a signal island the boundary cuts gets a port at each crossing. GROUND IS STILL READ FROM
// THE WHOLE BOARD — a clipped region's ground is the board's ground, and a pour the selection happens to
// cut in two is still one ground, not two islands.
//
// A SELECTION also says which LAYERS it is on (designer report, round 25). The outline of a trace selected on
// the top layer said nothing about the inner layers under it, so every inner-layer trace and plane crossing
// that outline came into the circuit, each cut into ports. Copper on another layer is read only where a via
// joins it to copper on the selection's own layers; an outline with no layers (a rectangle) reads them all.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>A recognition's scope: everything, or a set of closed rings in DBU.</summary>
public sealed record RecognitionScope
{
    private RecognitionScope(IReadOnlyList<long[]> rings, IReadOnlyList<LayerKey>? layers = null)
    {
        Rings = rings;
        Layers = layers ?? [];
    }

    /// <summary>The whole layout.</summary>
    public static RecognitionScope Whole { get; } = new([]);

    /// <summary>The scope's rings, each a flat x0,y0,x1,y1,… list in DBU — the spelling
    /// <see cref="PolygonShape.Xy"/> uses. Empty for <see cref="Whole"/>.</summary>
    public IReadOnlyList<long[]> Rings { get; }

    /// <summary>The layers the selection was made on; copper on any other layer is read only where a via joins
    /// it to copper on one of these. Empty — a rectangle, the whole layout — reads every layer.</summary>
    public IReadOnlyList<LayerKey> Layers { get; }

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
    public static RecognitionScope Polygons(IEnumerable<long[]> rings) => Polygons(rings, []);

    /// <summary>A set of polygons drawn on <paramref name="layers"/> — the GUI selection's outline and the
    /// layers its shapes and parts are on.</summary>
    public static RecognitionScope Polygons(IEnumerable<long[]> rings, IEnumerable<LayerKey> layers)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(layers);
        var kept = rings.Where(r => r is { Length: >= 6 } && r.Length % 2 == 0).Select(r => (long[])r.Clone()).ToList();
        return kept.Count == 0 ? Whole : new RecognitionScope(kept, [.. layers.Distinct().OrderBy(l => l.Layer).ThenBy(l => l.Datatype)]);
    }

    /// <summary>Two scopes are equal when their rings are, point for point — so a recognition input rebuilt from the same
    /// selection equals the one a preview was made from (R-dfr22-2-8).</summary>
    public bool Equals(RecognitionScope? other) =>
        other is not null && Rings.Count == other.Rings.Count && Rings.Zip(other.Rings).All(p => p.First.AsSpan().SequenceEqual(p.Second))
        && Layers.SequenceEqual(other.Layers);

    public override int GetHashCode() => Rings.Aggregate(Rings.Count, (h, r) => System.HashCode.Combine(h, r.Length, r.Length > 0 ? r[0] : 0));

    /// <summary>The rings unioned, Clipper form — overlapping selection outlines are one region.</summary>
    internal Paths64 Paths()
    {
        var paths = LayoutClipper.RingsToClipperPaths(Rings);
        for (int i = 0; i < paths.Count; i++)
            if (Clipper.Area(paths[i]) < 0) paths[i].Reverse();   // a selection's winding is not ours to read
        return Clipper.Union(paths, LayoutClipper.Rule);
    }
}
