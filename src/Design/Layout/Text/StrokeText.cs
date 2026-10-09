// A stroke label as geometry — brief-silkscreen-stroke-font.md R-ssf-2, R-ssf-5, R-ssf-6, R-ssf-9.
//
// THE ONE PLACE a LabelShape in the stroke font becomes strokes. The canvas draws these strokes, the selection box
// and hit test are their bounds, a Gerber export writes them as D01 strokes, and Flatten turns them into paths or
// outlines — all from For(), so what is drawn, what is picked and what is fabricated cannot disagree.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Text;

/// <summary>A stroke label's geometry, in DBU in the layout's own Y-up frame.</summary>
/// <param name="Strokes">Centre-line polylines as flat x,y pairs.</param>
/// <param name="PenWidth">The pen's width (<see cref="StrokeText.PenWidth"/>).</param>
/// <param name="Unknown">Characters drawn as boxes because the font lacks them (D5).</param>
public sealed record StrokeLabelGeometry(IReadOnlyList<double[]> Strokes, double PenWidth, int Unknown);

/// <summary>Stroke labels as geometry.</summary>
public static class StrokeText
{
    /// <summary>D4: an unset pen is the cap height over this.</summary>
    public const double PenDivisor = 6.5;

    /// <summary>D6: a Bold label's pen is this many times its width.</summary>
    public const double BoldPenFactor = 1.6;

    /// <summary>
    /// The pen an unset <see cref="LabelShape.StrokeWidth"/> stands for: <see cref="LabelShape.Height"/> /
    /// <see cref="PenDivisor"/>, never below a minimum-width rule on the label's own layer when
    /// <paramref name="tech"/> has one. Computed at use and never stored (R-ssf-4).
    /// </summary>
    public static double DefaultPenWidth(LabelShape label, Technology? tech = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Math.Max(label.Height / PenDivisor, MinWidthRule(tech, label.Layer));
    }

    /// <summary>The pen a stroke label draws with: its stated width, else <see cref="DefaultPenWidth"/>; times
    /// <see cref="BoldPenFactor"/> for a Bold label.</summary>
    public static double PenWidth(LabelShape label, Technology? tech = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        double pen = label.StrokeWidth is { } w and > 0 ? w : DefaultPenWidth(label, tech);
        return label.Style == LabelFontStyle.Bold ? pen * BoldPenFactor : pen;
    }

    /// <summary>
    /// <paramref name="label"/>'s strokes, placed: its alignment applied in the text's own frame, then its rotation
    /// (any angle, counter-clockwise), then its anchor. <see cref="LabelShape.Height"/> is the cap height (D2).
    /// <paramref name="centred"/> is the port override the renderer applies — a port's name is centred on its anchor
    /// whatever the label says.
    /// <list type="bullet">
    ///   <item>HAlign: Left starts at the anchor, Center straddles it, Right ends at it — over the ADVANCE, the
    ///   offset rounded to whole DBU so a string's box is the same size however it is aligned.</item>
    ///   <item>VAlign: Baseline on the anchor; Top hangs the INKED cap line from it (the cap line plus half the pen);
    ///   Middle centres the cap height on it; Bottom stands the inked descender line on it. Inked, because a top- or
    ///   bottom-anchored label is placed against something — a part body, the row above — and what has to clear it
    ///   is the ink.</item>
    /// </list>
    /// </summary>
    public static StrokeLabelGeometry For(LabelShape label, bool centred = false, Technology? tech = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        double pen = PenWidth(label, tech);
        if (string.IsNullOrEmpty(label.Text) || label.Height <= 0) return new StrokeLabelGeometry([], pen, 0);

        double cap = label.Height;
        var laid = StrokeFont.Layout(label.Text, label.Style, cap);
        var h = centred ? LabelHAlign.Center : label.HAlign ?? LabelHAlign.Left;
        var v = centred ? LabelVAlign.Middle : label.VAlign ?? LabelVAlign.Baseline;
        double dx = Math.Round(h switch
        {
            LabelHAlign.Center => -laid.Advance / 2,
            LabelHAlign.Right  => -laid.Advance,
            _                  => 0,
        });
        double dy = v switch
        {
            LabelVAlign.Top    => -cap - pen / 2,
            LabelVAlign.Middle => -cap / 2,
            LabelVAlign.Bottom => cap * StrokeFont.DescentOverCap + pen / 2,
            _                  => 0,
        };

        double rad = label.RotationDegrees * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var placed = new List<double[]>(laid.Strokes.Count);
        foreach (var st in laid.Strokes)
        {
            var xy = new double[st.Length];
            for (int i = 0; i + 1 < st.Length; i += 2)
            {
                double x = st[i] + dx, y = st[i + 1] + dy;
                xy[i] = label.X + x * cos - y * sin;
                xy[i + 1] = label.Y + x * sin + y * cos;
            }
            placed.Add(xy);
        }
        return new StrokeLabelGeometry(placed, pen, laid.Unknown);
    }

    /// <summary>The box the label inks: every stroke's points, grown by half the pen (round caps reach exactly
    /// that far past an end). Null for a label that draws nothing.</summary>
    public static Bbox? Bounds(LabelShape label, bool centred = false, Technology? tech = null)
    {
        var g = For(label, centred, tech);
        if (g.Strokes.Count == 0) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var st in g.Strokes)
            for (int i = 0; i + 1 < st.Length; i += 2)
            {
                minX = Math.Min(minX, st[i]); maxX = Math.Max(maxX, st[i]);
                minY = Math.Min(minY, st[i + 1]); maxY = Math.Max(maxY, st[i + 1]);
            }
        // Outward to whole DBU, past floating-point noise: an edge that is exactly on a DBU stays on it.
        const double noise = 1e-6;
        double r = g.PenWidth / 2;
        return new Bbox((long)Math.Floor(minX - r + noise), (long)Math.Floor(minY - r + noise),
                        (long)Math.Ceiling(maxX + r - noise), (long)Math.Ceiling(maxY + r - noise));
    }

    /// <summary>
    /// One round-ended <see cref="PathShape"/> per pen stroke, at the label's pen width, on its layer and net — what
    /// a Gerber export writes as D01 strokes and what Flatten gives by default (D7). Vertices round to DBU; a stroke
    /// that collapses to one point stays a two-point path, which a round pen draws as a dot.
    /// </summary>
    public static IReadOnlyList<PathShape> ToPaths(LabelShape label, Technology? tech = null) =>
        ToPaths(label, For(label, tech: tech));

    /// <summary><see cref="ToPaths(LabelShape, Technology?)"/> from geometry already computed.</summary>
    public static IReadOnlyList<PathShape> ToPaths(LabelShape label, StrokeLabelGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(geometry);
        long width = Math.Max(1, (long)Math.Round(geometry.PenWidth));
        var paths = new List<PathShape>(geometry.Strokes.Count);
        foreach (var st in geometry.Strokes)
        {
            var xy = new List<long>(st.Length);
            for (int i = 0; i + 1 < st.Length; i += 2)
            {
                long x = (long)Math.Round(st[i]), y = (long)Math.Round(st[i + 1]);
                if (xy.Count >= 2 && xy[^2] == x && xy[^1] == y) continue;
                xy.Add(x); xy.Add(y);
            }
            if (xy.Count == 0) continue;
            if (xy.Count == 2) { xy.Add(xy[0]); xy.Add(xy[1]); }
            paths.Add(new PathShape { Layer = label.Layer, Net = label.Net, Xy = [.. xy], Width = width, End = PathEndStyle.Round });
        }
        return paths;
    }

    /// <summary>
    /// The label's inked outline as filled polygons — every pen stroke's outline (round caps and joins) united, holes
    /// kept (an <c>O</c>'s counter). D7's "Polygons" choice of Flatten; <paramref name="tolDbu"/> bounds the arcs'
    /// deviation.
    /// </summary>
    public static IReadOnlyList<PolygonShape> ToPolygons(LabelShape label, long tolDbu, Technology? tech = null)
    {
        var paths = ToPaths(label, tech);
        if (paths.Count == 0) return [];
        var subject = new Paths64();
        foreach (var p in paths) subject.AddRange(LayoutClipper.ToClipperPaths(p, tolDbu, Math.Max(1, tolDbu)));
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, subject, new Paths64(), tree, LayoutClipper.Rule);
        return [.. LayoutClipper.FromClipperTree(tree, label.Layer, label.Net).OfType<PolygonShape>()];
    }

    /// <summary>The largest plain minimum-width rule on <paramref name="layer"/> (one measuring the drawing layer
    /// itself, not a derived region), or 0.</summary>
    private static double MinWidthRule(Technology? tech, LayerKey layer)
    {
        if (tech?.DrcRules is not { Count: > 0 } rules) return 0;
        long best = 0;
        foreach (var r in rules)
            if (r.Kind == DrcRuleKind.MinWidth && r.RegionA is null && r.Layer == layer && r.ValueDbu > best)
                best = r.ValueDbu;
        return best;
    }
}
