// brief-impedance-3 R-imp3-3 — the Impedance panel's results over the artwork.
//
// Each reviewed trace's centre line in its Z0 colour, cut by cut, on the PDF map's own scale
// (TraceImpedanceReportDocument.Z0Color), and a disc at each finding: filled for a fail, hollow for a
// warning, purple for a return-path kind, orange for Z0 — the map page's rule, so a reviewer reads the
// canvas and the page the same way. Every width is in device pixels, constant at any zoom: the line is
// a pointer to the copper, not a drawing of it, and must not hide the trace it is about.
//
// R-em-15's contract, as for every overlay here: never layer geometry, never counted in
// LayoutFrameCounters, never reachable by an exporter (every export path passes Overlay = null).

using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    private const float ImpedanceLineDevicePixels         = 3f;
    private const float ImpedanceSelectedLineDevicePixels = 5f;
    private const float ImpedanceHaloDevicePixels         = 8f;
    private const float ImpedanceMarkerRadiusDevicePixels = 5f;
    private const float ImpedanceSelectedMarkerRadius     = 7f;

    internal static void DrawImpedanceOverlay(SKCanvas canvas, ImpedanceOverlay overlay, PathSpace ps, double scaleUm)
    {
        using var line = new SKPaint { IsStroke = true, IsAntialias = true, StrokeCap = SKStrokeCap.Butt };

        // The selected trace last, over a dark halo, so it stands out of a dense board.
        foreach (var trace in overlay.Traces.OrderBy(t => t.Selected))
        {
            if (trace.Selected)
            {
                line.StrokeWidth = DevicePixelsToPathSpace(scaleUm, ImpedanceHaloDevicePixels);
                line.StrokeCap = SKStrokeCap.Round;
                line.Color = new SKColor(0x10, 0x14, 0x18, 0xb0);
                foreach (var st in trace.Stretches)
                    canvas.DrawLine(ps.X(st.X0), ps.Y(st.Y0), ps.X(st.X1), ps.Y(st.Y1), line);
                line.StrokeCap = SKStrokeCap.Butt;
            }

            line.StrokeWidth = DevicePixelsToPathSpace(scaleUm,
                trace.Selected ? ImpedanceSelectedLineDevicePixels : ImpedanceLineDevicePixels);
            foreach (var st in trace.Stretches)
            {
                line.Color = st.Z0 is { } z
                    ? TraceImpedanceReportDocument.Z0Color(z, overlay.TargetOhms, overlay.TolerancePercent)
                    : TraceImpedanceReportDocument.Unsolved;
                canvas.DrawLine(ps.X(st.X0), ps.Y(st.Y0), ps.X(st.X1), ps.Y(st.Y1), line);
            }
        }

        using var fill = new SKPaint { IsStroke = false, IsAntialias = true };
        using var ring = new SKPaint { IsStroke = true, IsAntialias = true };
        foreach (var f in overlay.Findings.OrderBy(f => f.Selected))
        {
            var colour = f.ReturnPath ? TraceImpedanceReportDocument.GroundMark : TraceImpedanceReportDocument.ZMark;
            float r = DevicePixelsToPathSpace(scaleUm, f.Selected ? ImpedanceSelectedMarkerRadius : ImpedanceMarkerRadiusDevicePixels);
            float cx = ps.X(f.X), cy = ps.Y(f.Y);

            // Filled for a fail; hollow — white inside a ring of the kind colour — for a warning.
            fill.Color = f.Fails ? colour : SKColors.White;
            canvas.DrawCircle(cx, cy, r, fill);
            ring.Color = f.Fails ? SKColors.White : colour;
            ring.StrokeWidth = DevicePixelsToPathSpace(scaleUm, f.Fails ? 1.2 : 2.0);
            canvas.DrawCircle(cx, cy, r, ring);
            if (f.Selected)
            {
                ring.Color = new SKColor(0x10, 0x14, 0x18);
                ring.StrokeWidth = DevicePixelsToPathSpace(scaleUm, 1.5);
                canvas.DrawCircle(cx, cy, r + DevicePixelsToPathSpace(scaleUm, 2.5), ring);
            }
        }
    }

    // brief-impedance-4 R-imp4-1d: the review's scope. A region is dashed over a light tint so the
    // artwork under it stays readable; a pick is a ring with a cross (a double ring when it takes the
    // connected copper, grey when there is no copper under it now). Device-pixel widths, as above.
    private static readonly SKColor ImpedanceScopeInk = new(0x2f, 0x7f, 0xd8);

    internal static void DrawImpedanceScopeOverlay(SKCanvas canvas, ImpedanceScopeOverlay overlay, PathSpace ps, double scaleUm)
    {
        float dash = DevicePixelsToPathSpace(scaleUm, 6);
        using var outline = new SKPaint
        {
            IsStroke = true, IsAntialias = true, Color = ImpedanceScopeInk,
            PathEffect = SKPathEffect.CreateDash([dash, 0.6f * dash], 0),
        };
        using var tint = new SKPaint { IsStroke = false, IsAntialias = true };

        foreach (var (xy, selected) in overlay.Regions)
        {
            if (xy.Length < 6) continue;
            using var path = Polyline(xy, ps, close: true);
            tint.Color = ImpedanceScopeInk.WithAlpha(selected ? (byte)0x38 : (byte)0x18);
            canvas.DrawPath(path, tint);
            outline.StrokeWidth = DevicePixelsToPathSpace(scaleUm, selected ? 2.5 : 1.5);
            canvas.DrawPath(path, outline);
        }

        if (overlay.Drawing is { Length: >= 4 } drawing)
        {
            using var path = Polyline(drawing, ps, close: false);
            outline.StrokeWidth = DevicePixelsToPathSpace(scaleUm, 1.5);
            canvas.DrawPath(path, outline);
        }

        using var ring = new SKPaint { IsStroke = true, IsAntialias = true };
        float r = DevicePixelsToPathSpace(scaleUm, 6);
        foreach (var (x, y, connected, missing) in overlay.Picks)
        {
            float cx = ps.X(x), cy = ps.Y(y);
            ring.Color = missing ? new SKColor(0x80, 0x80, 0x80) : ImpedanceScopeInk;
            ring.StrokeWidth = DevicePixelsToPathSpace(scaleUm, 1.8);
            canvas.DrawCircle(cx, cy, r, ring);
            if (connected) canvas.DrawCircle(cx, cy, r + DevicePixelsToPathSpace(scaleUm, 3), ring);
            canvas.DrawLine(cx - r, cy, cx + r, cy, ring);
            canvas.DrawLine(cx, cy - r, cx, cy + r, ring);
        }
    }

    private static SKPath Polyline(long[] xy, PathSpace ps, bool close)
    {
        var path = new SKPath();
        path.MoveTo(ps.X(xy[0]), ps.Y(xy[1]));
        for (int i = 2; i + 1 < xy.Length; i += 2) path.LineTo(ps.X(xy[i]), ps.Y(xy[i + 1]));
        if (close) path.Close();
        return path;
    }
}
