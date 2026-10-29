// ================================================================
//  FamilyPointMarkerTests.cs — a family trace drawn with markers and no line draws its points.
//
//  Brief ENV-MEAS-3 §5: an envelope run's memory cloud is plotted as a versus trace with markers
//  and no line, and under a drive sweep that trace is a family, one cloud per drive level. The
//  family branch of TraceRenderer drew lines only and returned, so the swept cloud was a blank
//  plot whose axes had scaled to data nobody could see. A family with more than
//  TraceRenderer.LargeScatterPoints per curve takes the batched dot path; both sizes are drawn.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class FamilyPointMarkerTests
{
    private const int W = 420, H = 260;

    [Theory]
    [InlineData(50)]
    [InlineData(6000)]
    public void AMarkersOnlyFamily_DrawsEveryCurvesPoints(int n)
    {
        var ds = new DataSet();
        var x = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        ds.Add("G", new DataCube([new Axis("Pin", [10.0, 20.0], "dBm"), new Axis("x", x, "")],
                                 [.. Enumerable.Range(0, 2 * n).Select(i => i < n ? 1.0 : 2.0)]));
        Assert.True(CubeTraceSpecParser.TryParse("G[:, :]", ds, out string cube, out var slice, out var transform, out string error), error);
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { CubeName = cube, Slice = slice, Transform = transform };
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        Assert.True(t.IsFamily);
        Assert.Equal(2, t.FamilyCurves.Count);
        t.Properties.LineEnabled = false;
        t.Properties.MarkerEnabled = true;

        // y = 1 and y = 2 land on two horizontal rows of the canvas
        var tf = new TransformSet
        {
            Primary = (XScale: (W - 40.0) / (n - 1), YScale: -100.0, XOffset: 20.0, YOffset: 280.0),
            Secondary = (XScale: (W - 40.0) / (n - 1), YScale: -100.0, XOffset: 20.0, YOffset: 280.0),
            CanvasSize = (W, H),
        };
        using var bmp = new SKBitmap(W, H);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            TraceRenderer.Draw(canvas, (W, H), t, tf, RenderTheme.Light);
        }
        foreach (var curve in t.FamilyCurves)
        {
            var px = tf.ToCanvas(curve.Points[n / 2].X, curve.Points[n / 2].Y, false);
            Assert.True(bmp.GetPixel((int)px.X, (int)px.Y) != SKColors.White, $"no ink at curve y = {curve.Points[0].Y}");
        }
    }
}
