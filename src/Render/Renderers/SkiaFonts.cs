using System;
using System.Reflection;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// Lazy-loaded SKTypeface instances sourced from the embedded font assets.
/// Use these in all custom Skia renderers so that plot text is consistent and
/// independent of the host OS font stack.
///
/// Standard Avalonia controls are unaffected — they resolve fonts through the
/// FontFamily resources registered in App.axaml (DejaVuSans, IBMPlexSans), not
/// through this class.
///
/// Embedded in THIS assembly (src/Render/CircuitRF.Render.csproj), read through
/// <see cref="Assembly.GetManifestResourceStream(string)"/>:
///   DejaVu Sans   — Assets/Fonts/DejaVuSans*.ttf
///   IBM Plex Sans — Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-*.ttf
/// The same files are ALSO linked into src/Ui as AvaloniaResource, so Avalonia's own
/// `avares://CircuitRF.Ui/Assets/Fonts/…` FontFamily resources are unchanged — one set of files,
/// reached two ways.
/// </summary>
internal static class SkiaFonts
{
    /// <summary>
    /// Test-only override (set only by <c>CircuitRF.Ui.Tests</c>, via <c>InternalsVisibleTo</c>) —
    /// the SAME seam <see cref="LayoutTextOutline.TestOverrideTypeface"/> already established, applied
    /// one level lower so it covers EVERY renderer rather than only the label-flatten path.
    ///
    /// <para><b>It no longer exists to make headless rendering POSSIBLE</b> (R-rnd1-4, 2026-09-07).
    /// <see cref="Load"/> used to go through <c>Avalonia.Platform.AssetLoader</c>, which needs a
    /// live app host and threw <c>InvalidOperationException: Unable to locate
    /// 'Avalonia.Platform.IAssetLoader'</c> without one — so every headless render silently drew in
    /// <c>SKTypeface.Default</c>. It now reads the embedded face out of this assembly, which needs
    /// no platform, so <b>the application and <c>src/Cli</c> both take the REAL face</b> and a
    /// headless picture can be compared byte for byte against the application's own.</para>
    ///
    /// <para><b>What it is still for</b> is a test that wants a face it can reason about, or one
    /// deliberately measuring what a substitution does. Production never sets it. Note the
    /// consequence for any COST measurement taken through it: glyph metrics and rasterization cost
    /// belong to the substituted face, not to IBM Plex — close enough for a frame-budget number, and
    /// it must be said rather than implied.</para>
    /// </summary>
    internal static SKTypeface? TestOverrideTypeface;

    // ── DejaVu Sans ───────────────────────────────────────────────────────────
    // Preferred renderer font: covers a wide Unicode range, battle-tested in
    // scientific/engineering plots.
    private static readonly Lazy<SKTypeface> _dejaVuRegular =
        new(() => Load("Assets/Fonts/DejaVuSans.ttf"));

    private static readonly Lazy<SKTypeface> _dejaVuBold =
        new(() => Load("Assets/Fonts/DejaVuSans-Bold.ttf"));

    private static readonly Lazy<SKTypeface> _dejaVuOblique =
        new(() => Load("Assets/Fonts/DejaVuSans-Oblique.ttf"));

    private static readonly Lazy<SKTypeface> _dejaVuBoldOblique =
        new(() => Load("Assets/Fonts/DejaVuSans-BoldOblique.ttf"));

    public static SKTypeface DejaVuRegular     => TestOverrideTypeface ?? _dejaVuRegular.Value;
    public static SKTypeface DejaVuBold        => TestOverrideTypeface ?? _dejaVuBold.Value;
    public static SKTypeface DejaVuOblique     => TestOverrideTypeface ?? _dejaVuOblique.Value;
    public static SKTypeface DejaVuBoldOblique => TestOverrideTypeface ?? _dejaVuBoldOblique.Value;

    // ── IBM Plex Sans (static — SkiaSharp does not support variable fonts) ────
    // Clean, modern typeface; use for UI-adjacent overlay text and data labels.
    private static readonly Lazy<SKTypeface> _plexRegular =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Regular.ttf"));

    private static readonly Lazy<SKTypeface> _plexBold =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Bold.ttf"));

    private static readonly Lazy<SKTypeface> _plexSemiBold =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-SemiBold.ttf"));

    private static readonly Lazy<SKTypeface> _plexItalic =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Italic.ttf"));

    private static readonly Lazy<SKTypeface> _plexLight =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Light.ttf"));

    public static SKTypeface PlexRegular  => TestOverrideTypeface ?? _plexRegular.Value;
    public static SKTypeface PlexBold     => TestOverrideTypeface ?? _plexBold.Value;
    public static SKTypeface PlexSemiBold => TestOverrideTypeface ?? _plexSemiBold.Value;
    public static SKTypeface PlexItalic   => TestOverrideTypeface ?? _plexItalic.Value;
    public static SKTypeface PlexLight    => TestOverrideTypeface ?? _plexLight.Value;

    // ── The embedded faces, with the test override deliberately NOT applied ───
    //
    // Two callers need the face this assembly actually ships rather than whatever a test has
    // substituted, and they need it for opposite reasons:
    //
    //   * RenderTypefaceInstaller, which fills LayoutTextOutline.TypefaceSource. Label GEOMETRY is
    //     compared byte for byte between the application's own export and the CLI's
    //     (ConvertCliVerbTests), and the CLI has no override to read — so a label flattened here
    //     against a substituted face is a different set of coordinates and the gate fails. That
    //     seam has its OWN override (LayoutTextOutline.TestOverrideTypeface, serialized by
    //     tests/Ui.Tests' LayoutTextOutlineTypefaceCollection), which is the one label tests mean.
    //   * A test asserting that the embedded faces resolve at all. The public properties cannot
    //     serve that: the override is a shared mutable static, roughly ten test classes set it to
    //     SKTypeface.Default for the duration of their own tests, and xunit runs classes in
    //     PARALLEL — so reading DejaVuRegular reports the suite's scheduling, not the loader.
    //     Measured, not assumed: the assertion passed alone and read `Helvetica` in the full run.

    internal static SKTypeface RealPlexRegular  => _plexRegular.Value;
    internal static SKTypeface RealPlexBold     => _plexBold.Value;
    internal static SKTypeface RealPlexSemiBold => _plexSemiBold.Value;
    internal static SKTypeface RealPlexItalic   => _plexItalic.Value;
    internal static SKTypeface RealPlexLight    => _plexLight.Value;

    // SvgTextOutliner, a third caller: it turns an SVG's text into the glyphs of the face the run names, which is the
    // shipped face whatever a test has substituted — a substituted face writes a different family name, which it leaves as text.
    internal static SKTypeface RealDejaVuRegular     => _dejaVuRegular.Value;
    internal static SKTypeface RealDejaVuBold        => _dejaVuBold.Value;
    internal static SKTypeface RealDejaVuOblique     => _dejaVuOblique.Value;
    internal static SKTypeface RealDejaVuBoldOblique => _dejaVuBoldOblique.Value;

    /// <summary>The face <see cref="Load"/> produces for <paramref name="assetRelativePath"/>,
    /// ignoring <see cref="TestOverrideTypeface"/> — see the note above. Reads the resource each
    /// call; it is for assertions, not for a render path.</summary>
    internal static SKTypeface RealFace(string assetRelativePath) => Load(assetRelativePath);

    // ── Helper ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Reads a face out of THIS assembly's own manifest resources — no platform, no app host, no
    /// asset system (R-rnd1-4).
    ///
    /// <para><b>What this replaced, and why it had to go.</b> The faces used to load through
    /// <c>Avalonia.Platform.AssetLoader</c>, which throws <c>InvalidOperationException: Unable to
    /// locate 'Avalonia.Platform.IAssetLoader'</c> with no live Avalonia app host — measured
    /// directly, not assumed. This method caught that and returned <see cref="SKTypeface.Default"/>,
    /// which kept a headless render from throwing but made it a DIFFERENT PICTURE: different
    /// glyphs, different advances, different line breaks, reported as a success. Now the renderers
    /// take the real embedded face in every process, and <c>circuitrf render</c> can be gated on
    /// byte identity against what the application itself draws.</para>
    ///
    /// <para>A missing or unreadable resource still falls back rather than throwing out of whatever
    /// was drawing — that is a broken build, but losing one typeface is better than losing the
    /// frame, and <c>tests/Ui.Tests</c> asserts the real family names so a broken build is caught
    /// there instead of in a paint.</para>
    /// </summary>
    private static SKTypeface Load(string assetRelativePath)
    {
        // The LogicalName the .csproj assigns is this exact path with '.' for '/' — stated in one
        // place there and derived in one place here, so a new face needs no name to be invented.
        string resource = "CircuitRF.Render." + assetRelativePath.Replace('/', '.');
        using var stream = typeof(SkiaFonts).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return SKTypeface.Default;
        return SKTypeface.FromStream(stream) ?? SKTypeface.Default;
    }
}
