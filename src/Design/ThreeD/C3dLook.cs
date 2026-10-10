// brief-em3d-106 R-em3d106-5 — the scene's LOOK in the realistic view: the environment that lights it, its rotation, intensity and
// exposure, the background, and which of the CAD chrome the realistic view's suppression lets back in. Document state like
// AirBoxHidden (overview D4): saved with the file and undoable, so a picture reproduces and `render` reads it. Display only —
// C3dPersistence.SerializeForRun clears it (overview rule 1), so no edit here ever makes a result out of date.
//
// Every key is optional, and an omitted key is its default: a document that states nothing draws the realistic view exactly as
// one stating every default does. Nothing is written unless it is stated, so a document opened and saved again is unchanged.
// Brief 107 added Shadows, AmbientOcclusion and Ground; brief 108 added Camera; brief 109 added FieldStyle and FieldOpacity.
//
// brief-em3d-108 R-em3d108-3d (overview D17) — Camera is the ONE place a camera is document state, and only by opt-in: written by
// "Set Camera" in the Look panel, never by orbiting, so a GUI framing reproduces in `render` and in a glTF export. See C3dDocument.Look.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.ThreeD;

/// <summary>The three procedural studios (overview D8): a preset is data (StudioEnvironment's records), never code.</summary>
public enum C3dStudio { Studio, HighKey, Dark }

/// <summary>brief-em3d-109 R-em3d109-4a — how a field plot is drawn in the realistic view. Exact (the default) is the colour map's
/// colour and nothing else; Lit adds a clear-coat sheen that only ever lightens a colour; Glow keeps the field exact and dims the scene
/// around it.</summary>
public enum C3dFieldStyle { Exact, Lit, Glow }

/// <summary>R-em3d106-5 — the <c>Look</c> block. Null on a key is the default.</summary>
public sealed class C3dLook
{
    /// <summary><c>Studio</c>, <c>HighKey</c>, <c>Dark</c>, or a path to a Radiance <c>.hdr</c> relative to the <c>.c3d</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>Degrees about +z; taken mod 360.</summary>
    public double? Rotation { get; set; }

    /// <summary>The environment's brightness multiplier, 0 to 10.</summary>
    public double? Intensity { get; set; }

    /// <summary>Exposure in EV (a factor of 2^EV), −10 to +10.</summary>
    public double? Exposure { get; set; }

    /// <summary><c>Theme</c>, <c>#rrggbb</c>, <c>#rrggbb,#rrggbb</c> (a vertical gradient, top first) or <c>Environment</c>.</summary>
    public string? Background { get; set; }

    // ── R-em3d106-2b — each item the realistic view hides, shown again (overview D11) ─────────────────────────────────

    public bool? ShowEdges { get; set; }
    public bool? ShowGrid { get; set; }
    public bool? ShowOverlays { get; set; }
    public bool? ShowAirBox { get; set; }
    public bool? ShowPorts { get; set; }
    public bool? ShowBoundaries { get; set; }
    public bool? ShowImages { get; set; }

    // ── brief-em3d-107 R-em3d107-4 — how objects sit on each other (each on unless stated false) ──────────────────────

    /// <summary>The key light's shadows (default true).</summary>
    public bool? Shadows { get; set; }

    /// <summary>Contact shading: screen-space ambient occlusion on the environment's light (default true).</summary>
    public bool? AmbientOcclusion { get; set; }

    /// <summary>The shadow catcher under the model: a disc at its lowest z that draws only the darkening (default true).</summary>
    public bool? Ground { get; set; }

    // ── brief-em3d-108 R-em3d108-3d — the camera pictures are taken from (overview D17) ─────────────────────────────────

    /// <summary>The camera a picture is taken from, written only by "Set Camera"; null (the ordinary case) is the
    /// live view's camera, whatever it is.</summary>
    public C3dLookCamera? Camera { get; set; }

    // ── brief-em3d-109 R-em3d109-4a — how field plots are drawn in it ───────────────────────────────────────────────────

    /// <summary><c>Exact</c> (omitted), <c>Lit</c> or <c>Glow</c>.</summary>
    public string? FieldStyle { get; set; }

    /// <summary>A field plot's opacity over the material it lies on, 0 to 100 percent (omitted: 100).</summary>
    public double? FieldOpacity { get; set; }

    /// <summary>Keys this build does not read (a later brief's), kept and written back.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    // ── the defaults (the brief's JSON) ──────────────────────────────────────────────────────────────────────────────

    public const C3dStudio DefaultStudio = C3dStudio.Studio;
    public const double DefaultRotation = 30, DefaultIntensity = 1, DefaultExposure = 0;
    public const string DefaultBackground = BackgroundTheme;
    public const string BackgroundTheme = "Theme", BackgroundEnvironment = "Environment";
    public const double ExposureMin = -10, ExposureMax = 10, IntensityMin = 0, IntensityMax = 10;
    public const double FieldOpacityMin = 0, FieldOpacityMax = 100, DefaultFieldOpacity = 100;

    /// <summary>The keys, in file order — what the schema and the gates iterate.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        nameof(Environment), nameof(Rotation), nameof(Intensity), nameof(Exposure), nameof(Background),
        nameof(ShowEdges), nameof(ShowGrid), nameof(ShowOverlays), nameof(ShowAirBox), nameof(ShowPorts), nameof(ShowBoundaries),
        nameof(ShowImages), nameof(Shadows), nameof(AmbientOcclusion), nameof(Ground), nameof(Camera), nameof(FieldStyle),
        nameof(FieldOpacity),
    ];

    /// <summary>True when it states nothing (and keeps no unread key): such a block is the same as none.</summary>
    [JsonIgnore]
    public bool IsEmpty => Environment is null && Rotation is null && Intensity is null && Exposure is null && Background is null
                        && ShowEdges is null && ShowGrid is null && ShowOverlays is null && ShowAirBox is null && ShowPorts is null
                        && ShowBoundaries is null && ShowImages is null && Shadows is null && AmbientOcclusion is null && Ground is null
                        && Camera is null && FieldStyle is null && FieldOpacity is null && Unread is not { Count: > 0 };

    /// <summary>The Look a NEW <c>.c3d</c> is written with: every chrome row but the air box shown. A key's default (null, hidden)
    /// is unchanged, so a document written before this — or one whose Look is cleared — still draws as it always did.</summary>
    public static C3dLook ForNewDocument() => new()
    {
        ShowEdges = true, ShowGrid = true, ShowOverlays = true, ShowPorts = true, ShowBoundaries = true, ShowImages = true,
    };

    public C3dLook Clone() => new()
    {
        Environment = Environment, Rotation = Rotation, Intensity = Intensity, Exposure = Exposure, Background = Background,
        ShowEdges = ShowEdges, ShowGrid = ShowGrid, ShowOverlays = ShowOverlays, ShowAirBox = ShowAirBox, ShowPorts = ShowPorts,
        ShowBoundaries = ShowBoundaries, ShowImages = ShowImages, Shadows = Shadows, AmbientOcclusion = AmbientOcclusion, Ground = Ground,
        Camera = Camera?.Clone(), FieldStyle = FieldStyle, FieldOpacity = FieldOpacity,
        Unread = Unread is null ? null : new Dictionary<string, JsonElement>(Unread),
    };

    // ── reading the values (one place: the view, `render` and `check` all ask here) ─────────────────────────────────────

    /// <summary>The rotation in [0, 360).</summary>
    [JsonIgnore]
    public double RotationDegrees
    {
        get
        {
            double r = Rotation is { } v && double.IsFinite(v) ? v % 360 : DefaultRotation;
            return r < 0 ? r + 360 : r;
        }
    }

    /// <summary>The intensity, clamped into its range (a value out of range is <c>check</c>'s error; the view draws the nearest).</summary>
    [JsonIgnore]
    public double IntensityValue => Clamp(Intensity, IntensityMin, IntensityMax, DefaultIntensity);

    [JsonIgnore]
    public double ExposureValue => Clamp(Exposure, ExposureMin, ExposureMax, DefaultExposure);

    /// <summary>R-em3d109-3b — the field opacity in percent, clamped into its range.</summary>
    [JsonIgnore]
    public double FieldOpacityValue => Clamp(FieldOpacity, FieldOpacityMin, FieldOpacityMax, DefaultFieldOpacity);

    /// <summary>R-em3d109-4a — what <see cref="FieldStyle"/> names; an unknown word is <see cref="C3dFieldStyle.Exact"/> with
    /// <paramref name="known"/> false (check's error; the view draws the field exactly).</summary>
    public C3dFieldStyle FieldStyleOf(out bool known)
    {
        known = true;
        if (FieldStyle is not { } f || f.Trim().Length == 0) return C3dFieldStyle.Exact;
        f = f.Trim();
        // as EnvironmentOf: Enum.TryParse also takes a number and a comma list, neither of which is a style's name
        if (Enum.TryParse<C3dFieldStyle>(f, ignoreCase: true, out var s) && Enum.IsDefined(s) && !char.IsDigit(f[0]) && !f.Contains(','))
            return s;
        known = false;
        return C3dFieldStyle.Exact;
    }

    private static double Clamp(double? v, double lo, double hi, double fallback)
        => v is { } x && double.IsFinite(x) ? Math.Clamp(x, lo, hi) : fallback;

    /// <summary>What <see cref="Environment"/> names: a studio, or (Path non-null) a <c>.hdr</c> path as written. An unknown word is
    /// <see cref="C3dStudio.Studio"/> with <paramref name="known"/> false.</summary>
    public (C3dStudio Studio, string? Path) EnvironmentOf(out bool known)
    {
        known = true;
        if (Environment is not { } e || e.Trim().Length == 0) return (DefaultStudio, null);
        e = e.Trim();
        // Enum.TryParse also takes a number and a comma list ("Studio,Dark" is Dark): neither is a studio's name.
        if (Enum.TryParse<C3dStudio>(e, ignoreCase: true, out var s) && Enum.IsDefined(s) && !char.IsDigit(e[0]) && !e.Contains(','))
            return (s, null);
        if (e.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase)) return (DefaultStudio, e);
        known = false;
        return (DefaultStudio, null);
    }

    /// <summary>The background: <paramref name="kind"/> Theme, Solid (Top), Gradient (Top, Bottom) or Environment. False for a
    /// spelling no form takes (check's error; the view draws the theme).</summary>
    public bool TryBackground(out C3dBackgroundKind kind, out (byte R, byte G, byte B) top, out (byte R, byte G, byte B) bottom)
    {
        top = bottom = default;
        kind = C3dBackgroundKind.Theme;
        var b = Background?.Trim();
        if (b is null || b.Length == 0 || b.Equals(BackgroundTheme, StringComparison.OrdinalIgnoreCase)) return true;
        if (b.Equals(BackgroundEnvironment, StringComparison.OrdinalIgnoreCase)) { kind = C3dBackgroundKind.Environment; return true; }
        var parts = b.Split(',');
        if (parts.Length == 1 && MaterialValidation.ParseColour(parts[0].Trim()) is { } c) { kind = C3dBackgroundKind.Solid; top = bottom = c; return true; }
        if (parts.Length == 2 && MaterialValidation.ParseColour(parts[0].Trim()) is { } t && MaterialValidation.ParseColour(parts[1].Trim()) is { } u)
        {
            kind = C3dBackgroundKind.Gradient; top = t; bottom = u;
            return true;
        }
        return false;
    }

    /// <summary>A short name for the status line and a picture's notes: the studio's, or the <c>.hdr</c>'s file name.</summary>
    [JsonIgnore]
    public string EnvironmentLabel
    {
        get
        {
            var (studio, path) = EnvironmentOf(out _);
            return path is null ? StudioLabel(studio) : System.IO.Path.GetFileName(path);
        }
    }

    public static string StudioLabel(C3dStudio s) => s switch { C3dStudio.HighKey => "High key", _ => s.ToString() };

    /// <summary>The exposure as the status line spells it: <c>+0.5</c>, <c>0</c>, <c>-1</c>.</summary>
    public static string FormatEv(double ev)
        => ev == 0 ? "0" : (ev > 0 ? "+" : "") + ev.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>R-em3d106-4d — a <c>.hdr</c> path resolved against the <c>.c3d</c> at <paramref name="documentPath"/>.</summary>
    public static string ResolvePath(string path, string? documentPath)
        => System.IO.Path.IsPathRooted(path) || documentPath is null
            ? path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(documentPath) ?? "", path));
}

/// <summary>What a background spelling is.</summary>
public enum C3dBackgroundKind { Theme, Solid, Gradient, Environment }

/// <summary>
/// brief-em3d-108 R-em3d108-3d — the camera pictures are taken from: the direction from the target TOWARD THE VIEWER (x, y, z, any
/// length), the target in DBU (as every <c>.c3d</c> coordinate is), the distance from target to eye in DBU, the vertical field of
/// view in degrees, and the projection. Brief 110 renders from it and brief 111 writes it into a glTF.
/// </summary>
public sealed class C3dLookCamera
{
    public double[]? Direction { get; set; }
    public double[]? Target { get; set; }
    public double? Distance { get; set; }
    public double? FovY { get; set; }
    /// <summary><c>Perspective</c> (omitted) or <c>Orthographic</c>.</summary>
    public string? Projection { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    public const string Perspective = "Perspective", Orthographic = "Orthographic";

    public C3dLookCamera Clone() => new()
    {
        Direction = Direction is null ? null : [.. Direction], Target = Target is null ? null : [.. Target], Distance = Distance,
        FovY = FovY, Projection = Projection, Unread = Unread is null ? null : new Dictionary<string, JsonElement>(Unread),
    };

    /// <summary>Whether it is orthographic (anything but that spelling is perspective).</summary>
    [JsonIgnore]
    public bool IsOrthographic => string.Equals(Projection?.Trim(), Orthographic, StringComparison.OrdinalIgnoreCase);

    /// <summary>What is wrong with it, one phrase per fault, for <c>check</c> — empty when a picture can be taken from it.</summary>
    public IReadOnlyList<string> Faults()
    {
        var faults = new List<string>();
        static bool Finite(double[]? v) => v is { Length: 3 } && v.All(double.IsFinite);
        if (!Finite(Direction) || Direction!.All(x => x == 0)) faults.Add("Direction is not three numbers pointing somewhere");
        if (!Finite(Target)) faults.Add("Target is not three numbers (DBU)");
        if (Distance is not { } d || !double.IsFinite(d) || d <= 0) faults.Add("Distance is not a positive number (DBU)");
        if (FovY is { } f && !(double.IsFinite(f) && f > 0 && f < 180)) faults.Add("FovY is not between 0 and 180 degrees");
        if (Projection is { } p && !(p.Trim().Equals(Perspective, StringComparison.OrdinalIgnoreCase) || IsOrthographic))
            faults.Add($"Projection \"{p}\" is neither Perspective nor Orthographic");
        return faults;
    }
}
