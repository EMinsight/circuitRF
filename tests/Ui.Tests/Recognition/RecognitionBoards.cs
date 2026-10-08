// Synthetic boards for brief-artsch-3-board-graph.md's gates, built in memory: a two-layer and a
// four-layer technology, and the handful of boards the claims are about. Coordinates in µm.

using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Ui.Tests.Recognition;

internal static class RecognitionBoards
{
    public static readonly LayerKey Top = new(1, 0), Bottom = new(2, 0), Plane = new(3, 0), Inner = new(4, 0), Via = new(9, 0);

    public static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static StackupLayer Cu(string name, LayerKey key) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [key],
    };

    private static StackupLayer Core(string name, double um) =>
        new() { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Um(um), Epsr = 4.3 };

    /// <summary>Top and Bottom on 500 µm, Bottom the ground reference, a through via.</summary>
    public static Technology TwoLayer()
    {
        var tech = new Technology
        {
            Name = "two-layer",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = Bottom, Name = "Bottom", Purpose = "conductor" },
                new LayerDef { Key = Via, Name = "Drill", Purpose = "via" },
            ],
        };
        tech.Stackup.Layers =
        [
            Cu("Top", Top),
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Via], SpanFromLayer = "Top", SpanToLayer = "Bottom" },
            Core("Core", 500),
            Cu("Bottom", Bottom),
        ];
        tech.Stackup.Layers[3].IsGroundReference = true;
        return tech;
    }

    /// <summary>Top, an inner ground plane (the reference), an inner signal layer and Bottom.</summary>
    public static Technology FourLayer()
    {
        var tech = new Technology
        {
            Name = "four-layer",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = Plane, Name = "Plane", Purpose = "conductor" },
                new LayerDef { Key = Inner, Name = "Inner", Purpose = "conductor" },
                new LayerDef { Key = Bottom, Name = "Bottom", Purpose = "conductor" },
                new LayerDef { Key = Via, Name = "Drill", Purpose = "via" },
            ],
        };
        tech.Stackup.Layers =
        [
            Cu("Top", Top),
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Via], SpanFromLayer = "Top", SpanToLayer = "Bottom" },
            Core("PP1", 200),
            Cu("Plane", Plane),
            Core("Core", 800),
            Cu("Inner", Inner),
            Core("PP2", 200),
            Cu("Bottom", Bottom),
        ];
        tech.Stackup.Layers[3].IsGroundReference = true;
        return tech;
    }

    public static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2, string? net = null) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2), Net = net };

    public static ViaShape ViaAt(double x, double y, double drill = 300) =>
        new() { Layer = Via, X = Um(x), Y = Um(y), DrillSize = Um(drill), PadSize = Um(2 * drill) };

    /// <summary>A 50 Ω-ish line across the board at <paramref name="y"/>, 950 µm wide.</summary>
    public static RectShape Line(LayerKey layer, double x1, double x2, double y) => Rect(layer, x1, y - 475, x2, y + 475);

    /// <summary>
    /// A 40 × 30 mm board: a Bottom plane, a top pour split by a channel for a line running edge to
    /// edge at y = 15 mm, and 200 vias stitching the two pour halves to the plane.
    /// </summary>
    public static LayoutView Stitched(string? groundNamedAt = null)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 30_000));
        view.Shapes.Add(Line(Top, 0, 40_000, 15_000));
        view.Shapes.Add(Rect(Top, 0, 0, 40_000, 14_025));
        view.Shapes.Add(Rect(Top, 0, 15_975, 40_000, 30_000));
        foreach (double y in new double[] { 2_000, 5_000, 8_000, 11_000, 19_000, 22_000, 25_000, 28_000 })
            for (int i = 0; i < 25; i++)
                view.Shapes.Add(ViaAt(1_000 + 1_500 * i, y));
        return view;
    }

    /// <summary>
    /// A 20 × 16 mm board: a Bottom plane, a line edge to edge at y = 5 mm, and a 1.5 × 1 mm shunt pad
    /// beside it carrying six vias to the plane — on its own island, or inside a top pour.
    /// </summary>
    public static LayoutView Shunt(bool padOnPour)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 20_000, 16_000));
        view.Shapes.Add(Line(Top, 0, 20_000, 5_000));
        view.Shapes.Add(Rect(Top, 9_000, 6_000, 10_500, 7_000));
        if (padOnPour) view.Shapes.Add(Rect(Top, 2_000, 6_000, 18_000, 15_000));
        foreach (double y in new double[] { 6_250, 6_750 })
            foreach (double x in new double[] { 9_250, 9_750, 10_250 })
                view.Shapes.Add(ViaAt(x, y, drill: 200));
        return view;
    }

    /// <summary>A Bottom plane 40 × 10 mm and a line on Top from <paramref name="x1"/> to <paramref name="x2"/> at y = 5 mm.</summary>
    public static LayoutView PlaneAndLine(double x1 = 0, double x2 = 40_000)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 10_000));
        view.Shapes.Add(Line(Top, x1, x2, 5_000));
        return view;
    }

    public static RecognitionResult Recognize(
        LayoutView view, Technology tech, RecognitionOptions? options = null, RecognitionScope? scope = null,
        CircuitRF.Design.Layout.Em.EmSetup? setup = null) =>
        ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = tech, Shapes = view.Shapes, EmSetup = setup,
            Options = options ?? new RecognitionOptions(), Scope = scope ?? RecognitionScope.Whole,
        });
}
