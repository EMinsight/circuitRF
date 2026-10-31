// brief-dfr32-2 R-dfr32-2-4 / R-dfr32-2-5 — where a port's NAME is drawn, and how big.
//
// A port's name is an annotation, as a schematic pin's name is: drawn at a fixed screen size (the size a ruler's
// readout takes by default), beside the port's mark on its OUTWARD side — past the tail of an edge port's arrow, or
// past an internal port's ring — at every zoom. The label's stored Height is not read here; it still governs what an
// export (GDSII, DXF) writes.
//
// Names do not overlap. Ports are placed in document order, and a name whose box meets an earlier name's moves
// outward along its own port's direction, a step at a time, until it is clear; one that moved further than its own
// height gets a thin leader back to its mark. Nothing here reads the pan or the order tiles were gathered in, so the
// same layout at the same zoom always places its names the same way, and `render` (which draws through this same
// pass) makes the picture the canvas shows.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Layout.Text;
using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>A port name's size, in points: a ruler's default readout size (<c>RulerAnnotation.TextSizePt</c>).</summary>
    internal const double PortNameTextPt = 11.0;

    /// <summary>The clear space between a port's mark and its name, and between two names, in device pixels.</summary>
    internal const double PortNameGapDevicePixels = 3.0;

    /// <summary>The most steps a name takes outward looking for clear space; past it the name stays where it got to.</summary>
    internal const int PortNameMaxSteps = 200;

    /// <summary>One port's name as drawn: the label to draw (the stored one moved and resized, never the model's), its
    /// box, and — when it moved more than its own height to clear another name — the leader from the name back to the
    /// port's mark.</summary>
    internal readonly record struct PortNamePlacement(int Index, LabelShape Drawn, Bbox Box,
                                                      (long X0, long Y0, long X1, long Y1)? Leader);

    /// <summary>A port name's height in DBU at <paramref name="devicePxPerDbu"/>: <see cref="PortNameTextPt"/> on screen.</summary>
    internal static long PortNameHeightDbu(double devicePxPerDbu)
        => Math.Max(1, (long)Math.Round(PortNameTextPt * DevicePixelsPerPoint / devicePxPerDbu));

    /// <summary>
    /// Every port name of a frame, placed. <paramref name="devicePxPerDbu"/> zero or less (no viewport) draws each name
    /// where it always was: at its anchor, at its stored height.
    /// </summary>
    internal static List<PortNamePlacement> PlacePortNames(IEnumerable<(int Index, LabelShape Label)> ports,
        LayoutPortDirection.ConductorLookup? conductorAt, double devicePxPerDbu, Technology? tech, int dbuPerMicron)
    {
        var placed = new List<PortNamePlacement>();
        var ordered = ports.Where(p => !string.IsNullOrEmpty(p.Label.Text)).OrderBy(p => p.Index).ToList();
        if (devicePxPerDbu <= 0)
        {
            foreach (var (index, label) in ordered)
                placed.Add(new(index, label, new Bbox(label.X, label.Y, label.X, label.Y), null));
            return placed;
        }

        long height = PortNameHeightDbu(devicePxPerDbu);
        long gap = Math.Max(1, (long)Math.Round(PortNameGapDevicePixels / devicePxPerDbu));
        foreach (var (index, label) in ordered)
        {
            var drawn = NameLabel(label, 0, 0, height);
            var ink = NameInk(drawn, tech);

            // The mark: its box, the point its name is pushed from, and the outward direction (against the current).
            int ox = 0, oy = 1;
            Bbox mark = new(label.X, label.Y, label.X, label.Y);
            if (LayoutPortDirection.Resolve(conductorAt, label) is { } hint)
            {
                var kind = label.PortKind ?? LayoutPortDirection.InferredKind(hint);
                bool atAnchor = LayoutPortDirection.MarkAtAnchor(kind, hint);
                var drawnHint = atAnchor ? hint : EdgePortSheet.Bar(hint, label, tech, dbuPerMicron);
                mark = LayoutPortDirection.MarkerBbox(label, drawnHint, atAnchor, 0);
                var (ux, uy) = LayoutPortDirection.UnitVector(hint.Direction);
                (ox, oy) = (-ux, -uy);
            }

            // Centre of the mark's outward face, then the name's box just beyond it.
            long cx = (mark.MinX + mark.MaxX) / 2, cy = (mark.MinY + mark.MaxY) / 2;
            long faceX = ox > 0 ? mark.MaxX : ox < 0 ? mark.MinX : cx;
            long faceY = oy > 0 ? mark.MaxY : oy < 0 ? mark.MinY : cy;
            long w = ink.MaxX - ink.MinX, h = ink.MaxY - ink.MinY;
            long ax = faceX + ox * (gap + w / 2), ay = faceY + oy * (gap + h / 2);
            long startX = ax, startY = ay;

            Bbox BoxAt(long x, long y) => new(x + ink.MinX, y + ink.MinY, x + ink.MaxX, y + ink.MaxY);
            bool Clear(Bbox b) => placed.All(q => !Grown(q.Box, gap).Intersects(b));
            long step = Math.Max(1, height / 2);
            for (int i = 0; i < PortNameMaxSteps && !Clear(BoxAt(ax, ay)); i++)
            {
                ax += ox * step;
                ay += oy * step;
            }

            var box = BoxAt(ax, ay);
            long moved = Math.Abs(ax - startX) + Math.Abs(ay - startY);
            (long, long, long, long)? leader = moved > h
                ? (ox != 0 ? (ox > 0 ? box.MinX : box.MaxX) : ax, oy != 0 ? (oy > 0 ? box.MinY : box.MaxY) : ay, faceX, faceY)
                : null;
            placed.Add(new(index, NameLabel(label, ax, ay, height), box, leader));
        }
        return placed;

        static Bbox Grown(Bbox b, long by) => new(b.MinX - by, b.MinY - by, b.MaxX + by, b.MaxY + by);
    }

    /// <summary>The label a port's name is drawn as: the port's text and font, upright, at the given anchor and height,
    /// at the font's own pen.</summary>
    private static LabelShape NameLabel(LabelShape port, long x, long y, long height) => new()
    {
        Layer = port.Layer, X = x, Y = y, Text = port.Text, Height = height, IsPort = true,
        Style = port.Style, Font = port.Font,
    };

    /// <summary>The box a name's ink covers about its own anchor (centred, as every port name is).</summary>
    private static Bbox NameInk(LabelShape drawn, Technology? tech)
    {
        if (drawn.Font == LabelFont.Stroke)
            return StrokeText.Bounds(drawn, centred: true, tech) ?? new Bbox(0, 0, 0, 0);
        // Measured at a reference size and scaled: a DBU height can be far outside a font's comfortable range.
        const float reference = 100f;
        double k = drawn.Height / (double)reference;
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(drawn.Style), reference);
        var (align, dy) = LayoutTextOutline.ResolveLabelAnchor(drawn, font, centred: true);
        float width = font.MeasureText(drawn.Text);
        float left = align switch { SKTextAlign.Center => -width / 2f, SKTextAlign.Right => -width, _ => 0f };
        // Path space is y-down: the glyphs sit from dy + ascent (negative) to dy + descent there, so y-up negates.
        var m = font.Metrics;
        return new Bbox((long)Math.Floor(left * k), (long)Math.Floor(-(dy + m.Descent) * k),
                        (long)Math.Ceiling((left + width) * k), (long)Math.Ceiling(-(dy + m.Ascent) * k));
    }
}
