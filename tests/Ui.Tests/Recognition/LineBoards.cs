// Synthetic boards for brief-artsch-5-traces-to-line-elements.md's gates, on RecognitionBoards' two-layer and
// four-layer technologies. Coordinates in µm; every line runs to the board's edge or to a part, so it ends on
// a port or a pad.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

internal static class LineBoards
{
    public const double W = 950;   // ≈ 50 Ω on the two-layer board's 500 µm core

    public static PolygonShape Poly(LayerKey layer, params double[] xyUm) =>
        new() { Layer = layer, Xy = [.. xyUm.Select(Um)] };

    /// <summary>A 30 × 30 mm board: a line in from the left edge at y = 10 mm, turning 90° at x = 15 mm and
    /// running up to the top edge — with its outer corner chamfered by <paramref name="legUm"/> when given.</summary>
    public static LayoutView Corner(double legUm = 0)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 30_000, 30_000));
        double h = W / 2;
        view.Shapes.Add(Poly(Top,
            0, 10_000 - h, 15_000 + h - legUm, 10_000 - h, 15_000 + h, 10_000 - h + legUm,
            15_000 + h, 30_000, 15_000 - h, 30_000, 15_000 - h, 10_000 + h, 0, 10_000 + h));
        return view;
    }

    /// <summary>A 16 × 10 mm board: 5 mm of 500 µm line from the left edge, a 1 mm linear ramp to 1.5 mm, and
    /// 10 mm of 1.5 mm line to the right edge.</summary>
    public static LayoutView Taper()
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 16_000, 10_000));
        view.Shapes.Add(Poly(Top, 0, 4_750, 5_000, 4_750, 6_000, 4_250, 16_000, 4_250,
                                  16_000, 5_750, 6_000, 5_750, 5_000, 5_250, 0, 5_250));
        return view;
    }

    /// <summary>A 60 µm line edge to edge across 6.05 mm with a 50 µm long, 90 µm wide sliver in its middle.</summary>
    public static LayoutView Sliver()
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 6_050, 4_000));
        view.Shapes.Add(Rect(Top, 0, 1_970, 3_000, 2_030));
        view.Shapes.Add(Rect(Top, 3_000, 1_955, 3_050, 2_045));
        view.Shapes.Add(Rect(Top, 3_050, 1_970, 6_050, 2_030));
        return view;
    }

    /// <summary>A 30 × 20 mm board: a line across at y = 10 mm and a branch from its middle down to the
    /// bottom edge — or, with <paramref name="cross"/>, on up to the top edge too.</summary>
    public static LayoutView Junction(bool cross)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 30_000, 20_000));
        view.Shapes.Add(Line(Top, 0, 30_000, 10_000));
        view.Shapes.Add(Rect(Top, 15_000 - W / 2, 0, 15_000 + W / 2, cross ? 20_000 : 10_000));
        return view;
    }

    /// <summary><see cref="RecognitionBoards.Stitched"/> with the top pour <paramref name="gapUm"/> from the
    /// line on each side.</summary>
    public static LayoutView Coplanar(double gapUm)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 30_000));
        view.Shapes.Add(Line(Top, 0, 40_000, 15_000));
        view.Shapes.Add(Rect(Top, 0, 0, 40_000, 15_000 - W / 2 - gapUm));
        view.Shapes.Add(Rect(Top, 0, 15_000 + W / 2 + gapUm, 40_000, 30_000));
        foreach (double y in new double[] { 2_000, 6_000, 24_000, 28_000 })
            for (int i = 0; i < 10; i++)
                view.Shapes.Add(ViaAt(2_000 + 4_000 * i, y));
        return view;
    }

    /// <summary><see cref="RecognitionBoards.FourLayer"/> with Bottom a ground reference too, so the inner
    /// layer is a stripline the stackup can bind.</summary>
    public static Technology FourLayerStripline()
    {
        var tech = FourLayer();
        tech.Stackup.Layers[^1].IsGroundReference = true;
        return tech;
    }

    /// <summary>A 30 × 10 mm four-layer board: the Plane and Bottom planes tied by vias, and a 150 µm line on
    /// the inner layer edge to edge at y = 5 mm — with inner-layer ground <paramref name="sideGapUm"/> either
    /// side of it when given, tied down by vias.</summary>
    public static LayoutView InnerLine(double? sideGapUm = null)
    {
        const double w = 150;
        var view = new LayoutView();
        view.Shapes.Add(Rect(Plane, 0, 0, 30_000, 10_000));
        view.Shapes.Add(Rect(Bottom, 0, 0, 30_000, 10_000));
        view.Shapes.Add(Rect(Inner, 0, 5_000 - w / 2, 30_000, 5_000 + w / 2));
        if (sideGapUm is { } g)
        {
            view.Shapes.Add(Rect(Inner, 0, 0, 30_000, 5_000 - w / 2 - g));
            view.Shapes.Add(Rect(Inner, 0, 5_000 + w / 2 + g, 30_000, 10_000));
        }
        foreach (double y in new double[] { 1_000, 9_000 })
            for (int i = 0; i < 6; i++)
                view.Shapes.Add(ViaAt(2_500 + 5_000 * i, y, drill: 200));
        return view;
    }

    /// <summary>
    /// A 24 × 8 mm four-layer board: two 0402 parts in series on a 380 µm line, the line between them drawn
    /// pad edge to pad edge and 10 mm long, and lines from each outer pad to the board's edge.
    /// </summary>
    public static (LayoutView View, double PadEdgeToPadEdgeUm) TwoPartsInSeries()
    {
        const double w = 380, y = 4_000;
        var view = new LayoutView();
        view.Shapes.Add(Rect(Plane, 0, 0, 24_000, 8_000));
        var r1 = Land("0402", DensityLevel.Nominal, 6_000, y);
        double r1Right = r1.Where(s => s.Layer == Top).Max(s => s.X2) / 1000.0;
        double r1Left = r1.Where(s => s.Layer == Top).Min(s => s.X1) / 1000.0;
        double half = (r1Right - r1Left) / 2;
        double r2Centre = r1Right + 10_000 + half;
        var r2 = Land("0402", DensityLevel.Nominal, r2Centre, y);
        double r2Left = r2.Where(s => s.Layer == Top).Min(s => s.X1) / 1000.0;
        double r2Right = r2.Where(s => s.Layer == Top).Max(s => s.X2) / 1000.0;
        view.Shapes.AddRange(r1.Where(s => s.Layer == Top));
        view.Shapes.AddRange(r2.Where(s => s.Layer == Top));
        view.Shapes.Add(Rect(Top, 0, y - w / 2, r1Left, y + w / 2));
        view.Shapes.Add(Rect(Top, r1Right, y - w / 2, r2Left, y + w / 2));
        view.Shapes.Add(Rect(Top, r2Right, y - w / 2, 24_000, y + w / 2));
        return (view, r2Left - r1Right);
    }

    /// <summary>The line elements (MLIN, CPWG, SLIN, TLIN) of <paramref name="result"/>.</summary>
    public static IEnumerable<LineElement> Lines(RecognitionResult result) =>
        result.Lines.Elements.Where(e => e.Type is LineElementType.MLIN or LineElementType.CPWG or LineElementType.SLIN or LineElementType.TLIN);

    /// <summary>What a node is, by its name.</summary>
    public static LineNode? NodeOf(RecognitionResult result, string node) => result.Lines.Node(node);

    public static double Micro(this LineElement e, string parameter) => e.Parameters[parameter] * 1e6;
}
