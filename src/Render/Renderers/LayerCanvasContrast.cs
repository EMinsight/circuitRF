using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// The colour a SILKSCREEN layer is DRAWN in, against the canvas it is drawn on.
///
/// <para><b>Why silk alone.</b> A technology stores silk in the colour of the ink — near-white, because
/// that is what a board's legend is — and the layout canvas is white in the light theme, so the stored
/// colour of the shipped technologies painted Silk Top at #F2F2F2 on #FFFFFF: present and unreadable.
/// The Gerber importer stores the opposite (a dark Silk Top), which is unreadable on the dark canvas
/// instead. No stored colour is right for both themes, so the stored colour is left alone and the
/// renderer moves it away from the background when the two are too close. Copper and process layers
/// are NOT touched: their colours are chosen HUES that tell layers apart (a kit can have hundreds), a
/// luminance guard would recolour them by a rule nobody chose, and a copper layer that is pale on a
/// pale canvas is still separated from it by hue and fill — silk is a neutral, and a neutral has
/// nothing but luminance to be seen by.</para>
///
/// <para><b>Identified by structured fields only</b> — the board-format alias (<c>F.SilkS</c>/
/// <c>B.SilkS</c>), the Gerber file function (<c>Legend,…</c>), or the purpose <c>silkscreen</c> —
/// never by the display name, which a user may call anything.</para>
///
/// <para><b>Cost.</b> One luminance comparison per LAYER per frame, done where the frame builds its
/// layer map; a clone is made only for a silk layer that actually needs adjusting. Nothing per shape.
/// The Layers panel swatch keeps showing the STORED colour: it is what the colour field edits, and a
/// swatch that changed with the theme would make the editor disagree with the file.</para>
/// </summary>
internal static class LayerCanvasContrast
{
    /// <summary>Below this Rec. 601 luminance difference (0-255) a silk colour is judged unreadable on
    /// the canvas. #F2F2F2 on white is 13 and #C8C8C8 on white is 55; a dark #2E2E2E on white is 209.</summary>
    internal const double MinimumLuminanceDifference = 96;

    /// <summary>The luminance difference an adjusted colour is moved out to — well clear of the
    /// threshold, so a legend reads as ink rather than as a faint wash.</summary>
    internal const double TargetLuminanceDifference = 170;

    /// <summary>True when <paramref name="def"/> is a silkscreen / legend layer by a structured
    /// field.</summary>
    internal static bool IsSilkscreen(LayerDef def)
    {
        var ix = def.Interchange;
        if (ix?.PcbLayerName is { } pcb &&
            (pcb.Equals("F.SilkS", StringComparison.OrdinalIgnoreCase) ||
             pcb.Equals("B.SilkS", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (ix?.GerberFileFunction is { } ff &&
            ff.TrimStart().StartsWith("Legend", StringComparison.OrdinalIgnoreCase))
            return true;
        return def.Purpose is { } p && p.Trim().Equals("silkscreen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The layer as it is DRAWN on <paramref name="background"/>: <paramref name="def"/> itself when it
    /// is not silk or already reads, otherwise a clone whose colour is darkened (light canvas) or
    /// lightened (dark canvas) until it clears <see cref="TargetLuminanceDifference"/>. Hue is kept —
    /// the blend is toward black or white — and alpha is untouched.
    /// </summary>
    internal static LayerDef Drawn(LayerDef def, SKColor background)
    {
        if (!IsSilkscreen(def)) return def;
        var adjusted = AdjustedColor(def.Color, background);
        if (adjusted.Equals(def.Color)) return def;
        return new LayerDef
        {
            Key = def.Key, Name = def.Name, Color = adjusted, FillOpacity = def.FillOpacity,
            ZOrder = def.ZOrder, Visible = def.Visible, Selectable = def.Selectable,
            Purpose = def.Purpose, FillPattern = def.FillPattern, Interchange = def.Interchange,
        };
    }

    /// <summary>The frame's layer map with every silk layer resolved to its drawn colour.</summary>
    internal static Dictionary<LayerKey, LayerDef>? DrawnLayerMap(Technology? tech, SKColor background)
        => tech?.Layers.ToDictionary(l => l.Key, l => Drawn(l, background));

    internal static Rgba AdjustedColor(Rgba color, SKColor background)
    {
        double lb = Luminance(background.Red, background.Green, background.Blue);
        double lc = Luminance(color.R, color.G, color.B);
        if (Math.Abs(lc - lb) >= MinimumLuminanceDifference) return color;

        // Blending toward black scales luminance by (1 - a); toward white, lc + (255 - lc) a.
        // Solve for the smallest a that reaches the target, clamped to a full blend.
        bool lightCanvas = lb > 127.5;
        double a;
        if (lightCanvas)
        {
            double want = Math.Max(0, lb - TargetLuminanceDifference);
            a = lc <= 0 ? 1 : 1 - want / lc;
        }
        else
        {
            double want = Math.Min(255, lb + TargetLuminanceDifference);
            a = lc >= 255 ? 1 : (want - lc) / (255 - lc);
        }
        a = Math.Clamp(a, 0, 1);
        double toward = lightCanvas ? 0 : 255;
        static byte Mix(byte c, double t, double a) => (byte)Math.Clamp(Math.Round(c + (t - c) * a), 0, 255);
        return new Rgba(Mix(color.R, toward, a), Mix(color.G, toward, a), Mix(color.B, toward, a), color.A);
    }

    private static double Luminance(double r, double g, double b) => 0.299 * r + 0.587 * g + 0.114 * b;
}
