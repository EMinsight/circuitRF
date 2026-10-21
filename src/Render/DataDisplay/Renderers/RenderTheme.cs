// ================================================================
//  RenderTheme.cs  —  Color and style constants for Skia renderers
//
//  Ported from splotRF/src/Renderers/RenderTheme.cs — namespace
//  renamed to CircuitRF.Ui.DataDisplay.
//
//  TODO 7.x: wire RenderTheme to circuitRF ColorTheme/.ccolor
//  For now pick RenderTheme.Light vs .Dark from ActualThemeVariant.
//
//  RND-4: the SYSTEM-ACCENT lookup that used to live here went the other
//  way — it read Application.Current's resources on the UI thread, which
//  is the window's business and not the drawing's, and no renderer ever
//  called it (only PlotControl, DragSelectOverlay and MarkerInfoBoxView
//  did). It is src/Ui/DataDisplay/PlotAccentColor.cs now. What is left is
//  two palettes and two pure conversions.
// ================================================================

using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public record RenderTheme(
        SKColor GridColor,
        SKColor MinorGridColor,
        SKColor TickColor,
        SKColor TextColor,
        SKColor BackgroundColor,
        SKColor BorderColor,
        bool    DarkMode)
    {
        // ---- Preset themes ----------------------------------------------

        public static RenderTheme Light { get; } = new(
            GridColor       : new SKColor(160, 160, 160),
            MinorGridColor  : new SKColor(200, 200, 200),
            TickColor       : new SKColor( 60,  79,  79),
            TextColor       : new SKColor( 60,  79,  79),
            BackgroundColor : SKColors.White,
            BorderColor     : new SKColor( 60,  79,  79),
            DarkMode        : false);

        public static RenderTheme Dark { get; } = new(
            GridColor       : new SKColor(100, 100, 100),
            MinorGridColor  : new SKColor( 70,  70,  70),
            TickColor       : new SKColor(200, 200, 200),
            TextColor       : new SKColor(210, 210, 210),
            BackgroundColor : new SKColor( 30,  30,  30),
            BorderColor     : new SKColor(200, 200, 200),
            DarkMode        : true);

        // ---- Helpers ----------------------------------------------------

        public static SKColor SelectionColorFallback = new SKColor(33, 150, 175);
        public static byte SelectionAlpha = 175;

        /// <summary>The colour a goal's limit is drawn in — a spec line (brief-yield-8 R-ya8-3).</summary>
        public SKColor LimitColor => DarkMode ? new SKColor(240, 110, 90) : new SKColor(200, 40, 40);

        /// <summary>The colour a failing trial is drawn in (brief-yield-9 R-ya9-1) — the limit it failed against.</summary>
        public SKColor FailColor => LimitColor;

        /// <summary>
        /// The colour a passing trial is drawn in under colour-by-pass: a neutral grey, whatever the trace's own colour
        /// (brief-yield-16 R-ya16-2). In the trace's colour, a first trace — red — drew its passes and its fails as one
        /// red band.
        /// </summary>
        public SKColor PassColor => DarkMode ? new SKColor(150, 158, 166) : new SKColor(110, 118, 128);

        /// <summary>The opacity an area is filled at under its own outline — a histogram's bars (R-ya8-1).</summary>
        public double FillOpacity => 0.35;

        /// <summary>
        /// Returns <paramref name="c"/> as a TRACE is drawn on this theme, with its alpha replaced by
        /// an opacity in [0, 1]. A negative opacity keeps the colour's own alpha.
        ///
        /// <para><b>Instance, not static, on purpose.</b> Every trace colour in the renderers comes
        /// through here, so naming the theme at each call site is what makes a colour that needs the
        /// canvas it is drawn on impossible to pass through unmapped. A static version compiled
        /// quietly at a site nobody had thought about.</para>
        /// </summary>
        public SKColor ToSKColor(SKColor c, double opacity = -1)
        {
            SKColor drawn = TraceColor(c);
            byte a = opacity >= 0 ? (byte)(opacity * 255) : drawn.Alpha;
            return new SKColor(drawn.Red, drawn.Green, drawn.Blue, a);
        }

        /// <summary>
        /// The colour a trace is DRAWN in on this canvas.
        ///
        /// <para><b>On a dark canvas only, a colour darker than mid is mirrored about mid lightness</b>
        /// (<c>l -> 100 - l</c>, SkiaSharp's HSL triple being degrees and 0..100) with its hue and
        /// saturation kept: Black draws as White, Indigo and Purple as their light selves, while
        /// Red, Blue, Gray, Yellow, White and the accent sit at or above mid and are untouched.
        /// Channel inversion was the other candidate and was rejected:
        /// it turns Red into Cyan, so a trace would change its identity with the theme. Blending toward
        /// white until a luminance floor clears desaturates the same Red to pink.</para>
        ///
        /// <para>The stored colour and its palette INDEX are untouched. A `.cdd` means what it meant,
        /// and the LIGHT theme, which is the reference, renders bit for bit as it always did. The
        /// colour picker keeps showing the stored colour for the same reason the Layers panel swatch
        /// does (<c>LayerCanvasContrast</c>): a swatch that changed with the theme would disagree with
        /// the file it edits.</para>
        ///
        /// <para>Dark is decided from the BACKGROUND's own Rec. 601 luminance, not from a flag, so a
        /// custom background gets the right treatment too (the test `LayoutRenderer`'s snap marker
        /// uses). Alpha 0 is returned as is: the palette's "Clear" is #00FFFFFF and would otherwise
        /// mirror white to black.</para>
        /// </summary>
        public SKColor TraceColor(SKColor c)
        {
            if (c.Alpha == 0) return c;
            if (!DarkCanvas) return c;
            c.ToHsl(out float h, out float s, out float l);
            return l < 50f ? SKColor.FromHsl(h, s, 100f - l, c.Alpha) : c;
        }

        private bool DarkCanvas =>
            (0.299 * BackgroundColor.Red + 0.587 * BackgroundColor.Green + 0.114 * BackgroundColor.Blue) < 127.5;

        /// <summary>Returns <paramref name="color"/> with alpha set from [0,1] opacity.</summary>
        public static SKColor WithOpacity(SKColor color, double opacity) =>
            color.WithAlpha((byte)(opacity * 255));
    }
}
