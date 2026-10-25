// ================================================================
//  OutOfRangeLineTests.cs — one far-out sample must not erase a trace's line.
//
//  A power sweep with one unconverged point stored DE = −9.9e11 there. In the window the trace's
//  markers drew (each is its own small shape) and its line did not draw at all; the CPU raster the
//  render verb uses drew it. The GPU canvas gives up on a path with a coordinate that far out, so
//  the renderer clips each segment to a box a few canvas sizes wide before it reaches the path —
//  which is what this holds: no coordinate beyond the box reaches Skia, and the in-range segments
//  are still there.
//
//  A stability circle near the singular point is the same failure as a CIRCLE: a radius of ~1,800 in
//  Γ is a million pixels at 1600 px, and with the sweep's other circles in one path the CPU raster
//  itself dropped them there while drawing them at 400 px, so the circles changed with the zoom.
// ================================================================

using CircuitRF.Render.DataDisplay;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class OutOfRangeLineTests
{
    [Fact]
    public void AHugeValueReachesThePathClippedAndTheRestOfTheLineIsKept()
    {
        const double W = 200, H = 100;
        using var path = new SKPath();
        var line = new TraceRenderer.ClippedPolyline(path, (W, H));
        foreach (var p in new SKPoint[] { new(10, 50), new(60, 50), new(100, -9.9e11f), new(140, 50), new(190, 50) })
            line.Add(p);

        var b = path.Bounds;
        Assert.InRange(b.Top, -4 * W - 1, 0);
        Assert.InRange(b.Bottom, 50, 51);
        Assert.Equal(10, b.Left, 3);
        Assert.Equal(190, b.Right, 3);
        // The flat ends are unchanged: the first and last points are the samples themselves.
        Assert.Equal(new SKPoint(10, 50), path.Points[0]);
        Assert.Equal(new SKPoint(190, 50), path.Points[^1]);
    }

    [Fact]
    public void AStabilityCircleAMillionPixelsWideStaysInTheBoxAndTheOthersStillDraw()
    {
        // Two load stability circles of one FET sweep, (centre, radius) in Γ: the near-singular one,
        // and the 10 GHz one, which crosses Γx = 0.9 at Γy = 0.594.
        (double X, double Y, double R)[] circles =
            [(19.678312674877308, 1832.8355337978496, 1832.9151724512972),
             (-1.8964660591689488, 6.270847740656866, 6.32830289553086)];
        const int W = 1600;
        double sc = W * 0.42, o = W / 2.0;

        using var path = new SKPath();
        foreach (var c in circles)
            TraceRenderer.AppendClippedCircle(path, o + c.X * sc, o - c.Y * sc, c.R * sc, (W, W));

        var b = path.Bounds;
        Assert.True(b.Left >= -4 * W && b.Top >= -4 * W && b.Right <= 5 * W && b.Bottom <= 5 * W, $"bounds {b}");

        using var bmp = new SKBitmap(W, W);
        using (var cv = new SKCanvas(bmp))
        using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true, Color = SKColors.Black })
        {
            cv.Clear(SKColors.White);
            cv.DrawPath(path, paint);
        }
        int px = (int)(o + 0.9 * sc), py = (int)(o - 0.594 * sc);
        Assert.True(Enumerable.Range(-3, 7).Any(d => bmp.GetPixel(px, py + d).Red < 128),
                    "the 10 GHz circle is missing where it crosses Γx = 0.9");
    }
}
