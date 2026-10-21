// ================================================================
//  DarkTraceColourTests.cs — a trace colour is DRAWN by the canvas it lands on.
//
//  A `.cdd` stores an INDEX into TraceProperties.ColorLUT, and black-on-dark was the
//  reported symptom: a dark palette entry that reads on the light canvas disappears on
//  the dark one. RenderTheme mirrors a dark colour's lightness on a dark canvas and
//  leaves everything else alone; the LIGHT theme is the reference and must not move at
//  all, which is what the first gate below holds shut.
// ================================================================

using System;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Tests.Statistics;
using RfCore.Data;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class DarkTraceColourTests
{
    /// <summary>The light canvas is the reference: every stored colour comes back exactly as stored.</summary>
    [Fact]
    public void TheLightTheme_ChangesNoPaletteColour()
    {
        foreach (var (index, stored) in TraceProperties.ColorLUT)
            Assert.Equal(stored, RenderTheme.Light.TraceColor(stored));
    }

    /// <summary>On a dark canvas the dark half of the palette is mirrored about mid lightness, hue kept.</summary>
    [Fact]
    public void OnADarkCanvas_TheDarkHalfOfThePalette_IsMirroredAboutMidLightness()
    {
        var dark = RenderTheme.Dark;
        Assert.Equal(SKColors.White, dark.TraceColor(TraceProperties.ColorLUT[0]));   // Black

        foreach (int index in new[] { 6, 7, 11, 13 })                                 // Green, Indigo, Purple, Teal
        {
            var stored = TraceProperties.ColorLUT[index];
            var drawn  = dark.TraceColor(stored);
            stored.ToHsl(out float hs, out _, out float ls);
            drawn.ToHsl(out float hd, out _, out float ld);

            Assert.True(ld > ls + 30f, $"index {index} {stored} drew as {drawn}");
            Assert.InRange(Math.Abs(hs - hd), 0f, 1f);
        }
    }

    /// <summary>Colours that already read on a dark canvas are untouched, and opacity still lands on the alpha.</summary>
    [Fact]
    public void OnADarkCanvas_ColoursThatAlreadyRead_AreUntouched()
    {
        var dark = RenderTheme.Dark;
        foreach (int index in new[] { 5, 12, 14, 15, 16 })                            // Gray, Red, White, Yellow, Accent
            Assert.Equal(TraceProperties.ColorLUT[index], dark.TraceColor(TraceProperties.ColorLUT[index]));

        // "Clear" is #00FFFFFF and must not be mirrored to black.
        Assert.Equal(TraceProperties.ColorLUT[3], dark.TraceColor(TraceProperties.ColorLUT[3]));

        // The theme's own pass/fail colours are already chosen for the canvas.
        Assert.Equal(dark.PassColor, dark.ToSKColor(dark.PassColor, 1.0));
        Assert.Equal(dark.FailColor, dark.ToSKColor(dark.FailColor, 1.0));

        Assert.Equal(127, dark.ToSKColor(TraceProperties.ColorLUT[0], 0.5).Alpha);
    }

    /// <summary>
    /// Every palette colour reads on the dark canvas, including pure Blue, which sits at HSL mid lightness and so
    /// escapes the mirror: it is lightened, hue kept, until it clears the contrast floor.
    /// </summary>
    [Fact]
    public void OnADarkCanvas_EveryPaletteColour_ClearsTheContrastFloor()
    {
        var dark = RenderTheme.Dark;
        foreach (var (index, stored) in TraceProperties.ColorLUT)
        {
            if (stored.Alpha == 0) continue;                                           // Clear
            var drawn = dark.TraceColor(stored);
            Assert.True(RenderTheme.Contrast(drawn, dark.BackgroundColor) >= RenderTheme.MinTraceContrast,
                        $"index {index} {stored} drew as {drawn}");
        }

        TraceProperties.ColorLUT[1].ToHsl(out float hs, out _, out _);                 // Blue stays blue
        dark.TraceColor(TraceProperties.ColorLUT[1]).ToHsl(out float hd, out _, out _);
        Assert.InRange(Math.Abs(hs - hd), 0f, 1f);
    }

    /// <summary>A colour that is not a palette entry was chosen by whoever set it, and draws exactly on either canvas.</summary>
    [Fact]
    public void AColourOutsideThePalette_IsDrawnExactly()
    {
        var navy = new SKColor(20, 30, 90);
        Assert.Equal(navy, RenderTheme.Dark.TraceColor(navy));
        Assert.Equal(navy, RenderTheme.Light.TraceColor(navy));
    }

    /// <summary>
    /// The renderers, not just the helper: a black trace on the dark canvas is drawn LIGHT, and the same
    /// plot on the light canvas is drawn black. This is the gate a call site skipped in the sweep fails.
    /// </summary>
    [Fact]
    public void ABlackTrace_IsDrawnLightOnTheDarkCanvas_AndBlackOnTheLightOne()
    {
        var ds = new DataSet();
        ds.Add("x", new DataCube([new Axis("trial", [1, 2, 3])], [1, 2, 3]));
        var t = DisplayFixtures.ExpressionTrace("x");
        t.Properties.LineColorIndex   = 0;
        t.Properties.MarkerColorIndex = 0;
        t.Properties.LineWidth        = 3;
        var plot = DisplayFixtures.Resolved(ds, t);

        static bool HasPixel(Plot plot, RenderTheme theme, Func<SKColor, bool> want)
        {
            const int w = 400, h = 300;
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            surface.Canvas.Clear(theme.BackgroundColor);
            PlotRenderer.Draw(surface.Canvas, (w, h), plot, PlotDetail.Full, theme);
            using var image = SKBitmap.FromImage(surface.Snapshot());
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (want(image.GetPixel(x, y))) return true;
            return false;
        }

        Assert.True(HasPixel(plot, RenderTheme.Dark,  c => c.Red > 250 && c.Green > 250 && c.Blue > 250),
                    "a black trace on the dark canvas left no light ink");
        Assert.True(HasPixel(plot, RenderTheme.Light, c => c.Red < 12 && c.Green < 12 && c.Blue < 12),
                    "a black trace on the light canvas left no black ink");
    }
}
