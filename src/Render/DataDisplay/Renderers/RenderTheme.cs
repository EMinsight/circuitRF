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
        /// Returns <paramref name="c"/> with its alpha replaced by an opacity in [0, 1].
        /// A negative opacity keeps the colour's own alpha.
        /// </summary>
        public static SKColor ToSKColor(SKColor c, double opacity = -1)
        {
            byte a = opacity >= 0 ? (byte)(opacity * 255) : c.Alpha;
            return new SKColor(c.Red, c.Green, c.Blue, a);
        }

        /// <summary>Returns <paramref name="color"/> with alpha set from [0,1] opacity.</summary>
        public static SKColor WithOpacity(SKColor color, double opacity) =>
            color.WithAlpha((byte)(opacity * 255));
    }
}
