// Framework-free symbol primitive model.
// No SKColor / SKPath / Avalonia — colors are SymbolColorRole enum values.
// Coordinates are component-LOCAL (100 units = 1 connection-grid square P).
// +x right, +y down (screen convention).

using System.Text.Json.Serialization;
using CircuitRF.Design.Schematic;
namespace CircuitRF.Design.Symbol;

// ── Color / style roles ───────────────────────────────────────────────────────

public enum SymbolColorRole  { SymbolLine, SymbolText, SymbolPlus }
public enum SymbolFontStyle  { Regular, Bold, Italic, Condensed }
/// <summary>Named stroke-width tiers in local units: Thick≈9 / Normal≈6 / Thin≈3.</summary>
public enum SymbolStrokeTier { Normal, Thin, Thick }
public enum SineAxis         { Horizontal, Vertical }
public enum SymbolTextAlign  { Left, Center, Right }

/// <summary>
/// Which EDGE of a symbol's body a pin sits on, which is the same thing as which way its name runs:
/// inward, toward the body it names a terminal of.
///
/// <para><see cref="Left"/> — a pin on the LEFT edge — draws its name to the RIGHT of the pin, and is
/// the default and the no-change case. <see cref="Right"/> ends the name at the pin and runs it
/// leftward, which is what a right-edge pin needs; drawing it rightward instead runs the whole
/// right-hand column of names outward into empty space. <see cref="Top"/> and <see cref="Bottom"/> are
/// the same statement about a vertical lead, and their names are drawn ALONG it, turned a quarter
/// turn — a horizontal name on a top edge whose pins are one grid apart overlaps its neighbours.</para>
///
/// <para><b>Stated as a SIDE, not as a screen direction</b>, which is what makes it survive the Y flip
/// between a source file's Y-up symbol coordinates and circuitRF's Y-down local ones: the pin is on
/// the same edge of the same picture either way, so nothing has to remember to swap it.</para>
/// </summary>
public enum SymbolPinNameAlign { Left, Center, Right, Top, Bottom }
public enum SymbolTextVAlign { Baseline, Top, Middle, Bottom }

/// <summary>Tri-state snap mode for symbol-editor art.  Pins ALWAYS snap to P=100 regardless.</summary>
public enum SnapMode { ConnectionGrid, FineGrid, None }

/// <summary>Pin layout template for an SnP symbol.</summary>
public enum SnpPinConfig { Standard, SplitLR, DualRow }
/// <summary>Pitch between same-side pins for SnP symbols with N ≥ 4.</summary>
public enum SnpPitch { Tight, Loose }

// ── Per-instance glyph variants for the system blocks (brief-sys-1) ───────────
// The same mechanism SnpPinConfig/SnpPitch serve above and Match's NetworkForm serves for the
// match glyph: a value the user sets on the INSTANCE that changes what is drawn. Each is read off
// a parameter expression by name, so a schematic saved before these existed reads as the default.

/// <summary>Which way an ideal circulator circulates. <see cref="CW"/> is 1→2→3→1.</summary>
public enum CirculatorDirection { CW, CCW }

/// <summary>
/// An SPST switch's position — <see cref="On"/> closed, <see cref="Off"/> open.
///
/// <para>The members are numbered to match the ENGINE's <c>State</c> parameter, for the reason
/// <see cref="SwitchThrow"/> spells out below: <c>State</c> names which throw is closed, so 0 is
/// "none of them" and 1 is the SPST's only throw, and <c>Enum.TryParse</c> resolves a bare numeral
/// against the underlying value. Numbering these 0,1 in declaration order would silently draw a
/// closed switch open. Both spellings parse, so <c>State = Off</c> saved before the model existed
/// still reads as Off.</para>
/// </summary>
public enum SwitchState { Off = 0, On = 1 }

/// <summary>
/// Which throw an SPDT switch is connected to, <see cref="None"/> being neither.
///
/// <para>The throws are numbered <b>1 and 2 rather than 0 and 1</b> on purpose: the parameter is
/// written <c>State = 1</c> or <c>State = 2</c>, matching the throws' own labels on the glyph, and
/// <c>Enum.TryParse</c> resolves a bare numeral against the UNDERLYING value. Starting at 0 would
/// silently read "1" as <c>T2</c> — a switch drawn in the wrong position, with nothing to see.</para>
///
/// <para><b><see cref="None"/> is 0, the engine's own "no throw is closed"</b> (owner, 2026-09-13).
/// Without it <c>State = 0</c> parsed to an undefined enum value, which
/// <c>BuiltInSymbols.BuildSwitchD</c>'s two-way test read as the ELSE branch: an SPDT set to open
/// both throws was drawn making a connection to throw 2. Nothing failed and nothing was said — the
/// drawing simply disagreed with the S-matrix. Anything outside 0…2 normalizes to this as well,
/// which is the model's own rule (a <c>State</c> naming a throw that does not exist closes
/// nothing).</para>
/// </summary>
public enum SwitchThrow { None = 0, T1 = 1, T2 = 2 }

// ── Fill ──────────────────────────────────────────────────────────────────────

/// <summary>
/// <b>How strongly a closed shape is filled</b>, 0–1, always in the shape's own colour role: 0 is the outline alone,
/// 1 is solid (<c>Filled</c>), and anything between is the outline over a fill of the line colour at that
/// opacity, so a region reads as shaded without hiding what is drawn over it. The colour is still the theme's,
/// so dark mode needs nothing.
///
/// <para><b>Stored as two fields so no existing file changes.</b> Solid stays the <c>Filled</c> flag every
/// <c>.csym</c> already carries; a strength strictly between 0 and 1 is <c>Tint</c>, written only when set (the
/// shape <see cref="CsymPin.NameAlign"/> already uses). A symbol with no partial fill re-serialises byte for byte
/// and needs no format-version bump, and a build that predates <c>Tint</c> draws such a shape as its outline.
/// <see cref="Set"/> never writes both; a file that has both (by hand) is solid.</para>
/// </summary>
public static class SymbolFill
{
    /// <summary>The finest step a fill is held at: a tenth of a percent.</summary>
    public const double Resolution = 0.001;

    /// <summary>Whether <paramref name="prim"/> has a fill at all — the closed shapes.</summary>
    public static bool Accepts(SymbolPrimitive prim) => prim is RectPrimitive or RoundedRectPrimitive or CirclePrimitive
        or EllipsePrimitive or PolygonPrimitive or ExponentialTaperPrimitive;

    /// <summary>The fill strength of <paramref name="prim"/>, 0–1: 1 when solid, 0 for a shape with no fill.</summary>
    public static double Of(SymbolPrimitive prim)
    {
        var (filled, tint) = prim switch
        {
            RectPrimitive r              => (r.Filled,  r.Tint),
            RoundedRectPrimitive rr      => (rr.Filled, rr.Tint),
            CirclePrimitive c            => (c.Filled,  c.Tint),
            EllipsePrimitive e           => (e.Filled,  e.Tint),
            PolygonPrimitive pg          => (pg.Filled, pg.Tint),
            ExponentialTaperPrimitive et => (et.Filled, et.Tint),
            _                            => (false, 0.0),
        };
        if (filled) return 1;
        return double.IsFinite(tint) ? Math.Clamp(tint, 0, 1) : 0;
    }

    /// <summary>Sets <paramref name="prim"/>'s fill strength: solid at 1, none at 0, a tint between, rounded to
    /// <see cref="Resolution"/>. Nothing happens to a shape that has no fill.</summary>
    public static void Set(SymbolPrimitive prim, double fraction)
    {
        double f = double.IsFinite(fraction) ? Math.Round(Math.Clamp(fraction, 0, 1) / Resolution) * Resolution : 0;
        bool filled = f >= 1;
        double tint = filled || f <= 0 ? 0 : f;
        switch (prim)
        {
            case RectPrimitive r:              r.Filled  = filled; r.Tint  = tint; break;
            case RoundedRectPrimitive rr:      rr.Filled = filled; rr.Tint = tint; break;
            case CirclePrimitive c:            c.Filled  = filled; c.Tint  = tint; break;
            case EllipsePrimitive e:           e.Filled  = filled; e.Tint  = tint; break;
            case PolygonPrimitive pg:          pg.Filled = filled; pg.Tint = tint; break;
            case ExponentialTaperPrimitive et: et.Filled = filled; et.Tint = tint; break;
        }
    }

    /// <summary>
    /// Reads a typed fill percentage. Accepted: a number from 0 to 100, with or without a trailing <c>%</c>,
    /// surrounding spaces, and a decimal point or a decimal comma (<c>25</c>, <c>25 %</c>, <c>12.5</c>,
    /// <c>12,5</c>). Refused, with <paramref name="problem"/> saying why: anything that is not such a number —
    /// <c>NaN</c> and <c>Infinity</c>, which .NET would otherwise parse, thousands separators, exponents — and
    /// anything outside 0–100. The reasons are short enough for the one line the inspector reserves under the box.
    /// The result is rounded to <see cref="Resolution"/>.
    /// </summary>
    public static bool TryParsePercent(string? text, out double percent, out string? problem)
    {
        percent = 0;
        problem = null;
        string t = (text ?? "").Trim();
        if (t.EndsWith('%')) t = t[..^1].TrimEnd();
        if (t.Length == 0)
        {
            problem = "Type 0 to 100 %.";
            return false;
        }
        // One decimal separator, either spelling; nothing else that is not a digit or a leading sign.
        if (t.Count(c => c is '.' or ',') > 1 || !t.Select((c, i) => char.IsAsciiDigit(c) || c is '.' or ',' || (i == 0 && c is '-' or '+')).All(ok => ok)
            || !t.Any(char.IsAsciiDigit))
        {
            problem = "Not a number. Type 0 to 100 %.";
            return false;
        }
        double v = double.Parse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        if (v < 0 || v > 100)
        {
            problem = $"{t.Replace(',', '.')} % is outside 0 to 100 %.";
            return false;
        }
        percent = Math.Round(v / (Resolution * 100)) * Resolution * 100;
        return true;
    }
}

// ── Primitive base ────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LinePrimitive),        "Line")]
[JsonDerivedType(typeof(PolylinePrimitive),    "Polyline")]
[JsonDerivedType(typeof(RectPrimitive),        "Rect")]
[JsonDerivedType(typeof(RoundedRectPrimitive), "RoundedRect")]
[JsonDerivedType(typeof(CirclePrimitive),      "Circle")]
[JsonDerivedType(typeof(EllipsePrimitive),     "Ellipse")]
[JsonDerivedType(typeof(ArcPrimitive),         "Arc")]
[JsonDerivedType(typeof(PolygonPrimitive),     "Polygon")]
[JsonDerivedType(typeof(QuadCurvePrimitive),   "QuadCurve")]
[JsonDerivedType(typeof(CubicCurvePrimitive),  "CubicCurve")]
[JsonDerivedType(typeof(SinePrimitive),             "Sine")]
[JsonDerivedType(typeof(ExponentialTaperPrimitive), "ExpTaper")]
[JsonDerivedType(typeof(TextPrimitive),             "Text")]
[JsonDerivedType(typeof(BitmapPrimitive),           "Bitmap")]
public abstract class SymbolPrimitive { }

// ── Line ─────────────────────────────────────────────────────────────────────

public sealed class LinePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public LinePrimitive() { }
    public LinePrimitive(SymbolColorRole role, SymbolStrokeTier tier,
                         double x1, double y1, double x2, double y2)
    {
        ColorRole = role; StrokeTier = tier; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }
}

// ── Polyline ──────────────────────────────────────────────────────────────────

public sealed class PolylinePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    /// <summary>Point list as [x, y] pairs.</summary>
    public List<double[]>   Points     { get; set; } = [];
}

// ── Rect ──────────────────────────────────────────────────────────────────────

public sealed class RectPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>A fill strength below solid, 0–1 — see <see cref="SymbolFill"/>. Ignored while <see cref="Filled"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }
    /// <summary>Center x, y.</summary>
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double W  { get; set; }
    public double H  { get; set; }
}

// ── RoundedRect ───────────────────────────────────────────────────────────────

public sealed class RoundedRectPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>A fill strength below solid, 0–1 — see <see cref="SymbolFill"/>. Ignored while <see cref="Filled"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }
    public double Cx     { get; set; }
    public double Cy     { get; set; }
    public double W      { get; set; }
    public double H      { get; set; }
    public double Radius { get; set; }
}

// ── Circle ────────────────────────────────────────────────────────────────────

public sealed class CirclePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>A fill strength below solid, 0–1 — see <see cref="SymbolFill"/>. Ignored while <see cref="Filled"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double R  { get; set; }
}

// ── Ellipse ───────────────────────────────────────────────────────────────────

public sealed class EllipsePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>A fill strength below solid, 0–1 — see <see cref="SymbolFill"/>. Ignored while <see cref="Filled"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double Rx { get; set; }
    public double Ry { get; set; }
}

// ── Arc ───────────────────────────────────────────────────────────────────────

public sealed class ArcPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double Cx       { get; set; }
    public double Cy       { get; set; }
    public double R        { get; set; }
    /// <summary>Start angle in degrees, measured clockwise from +x.</summary>
    public double StartDeg { get; set; }
    /// <summary>Sweep angle in degrees; positive = clockwise.</summary>
    public double SweepDeg { get; set; }
}

// ── Polygon / Triangle ────────────────────────────────────────────────────────

public sealed class PolygonPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>A fill strength below solid, 0–1 — see <see cref="SymbolFill"/>. Ignored while <see cref="Filled"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }
    /// <summary>Vertex list as [x, y] pairs.</summary>
    public List<double[]>   Points     { get; set; } = [];
}

// ── QuadCurve ─────────────────────────────────────────────────────────────────

public sealed class QuadCurvePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double P0X   { get; set; }
    public double P0Y   { get; set; }
    public double CtrlX { get; set; }
    public double CtrlY { get; set; }
    public double P2X   { get; set; }
    public double P2Y   { get; set; }
}

// ── CubicCurve ────────────────────────────────────────────────────────────────

public sealed class CubicCurvePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double P0X { get; set; }
    public double P0Y { get; set; }
    public double C1X { get; set; }
    public double C1Y { get; set; }
    public double C2X { get; set; }
    public double C2Y { get; set; }
    public double P3X { get; set; }
    public double P3Y { get; set; }
}

// ── Sine (parameterized smart-path) ──────────────────────────────────────────

public sealed class SinePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole   { get; set; }
    public SymbolStrokeTier StrokeTier  { get; set; }
    /// <summary>Center of the wave's bounding span.</summary>
    public double   Cx         { get; set; }
    public double   Cy         { get; set; }
    public double   Amp        { get; set; }
    public double   Cycles     { get; set; }
    public double   Length     { get; set; }
    /// <summary>
    /// Sample points per full cycle.  Renderer uses ceil(Cycles * PtsPerCycle) segments.
    /// Minimum effective value is 1; renderer clamps to at least 2 total segments.
    /// </summary>
    public int      PtsPerCycle { get; set; } = 20;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SineAxis Axis       { get; set; }
}

// ── ExponentialTaper ─────────────────────────────────────────────────────────
// Width profile: w(x) = W1 · (W2/W1)^(x/L), rendered as a closed filled polygon.
// Cx/Cy is the center of the taper; L is the total length along the taper axis.

public sealed class ExponentialTaperPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool   Filled   { get; set; }
    /// <summary>A fill strength below solid, 0–1 — see <see cref="SymbolFill"/>. Ignored while <see cref="Filled"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }
    /// <summary>Center of the taper in local coords.</summary>
    public double Cx       { get; set; }
    public double Cy       { get; set; }
    /// <summary>Width at the start (x=0) end.</summary>
    public double W1       { get; set; } = 60.0;
    /// <summary>Width at the end (x=L) end.</summary>
    public double W2       { get; set; } = 15.0;
    /// <summary>Length along the taper axis.</summary>
    public double L        { get; set; } = 100.0;
    /// <summary>Sample points per outline side; minimum 2.</summary>
    public int    NumPts   { get; set; } = 20;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SineAxis Axis   { get; set; }
}

// ── Text ──────────────────────────────────────────────────────────────────────

public sealed class TextPrimitive : SymbolPrimitive
{
    public string Content   { get; set; } = "";
    public double AnchorX   { get; set; }
    public double AnchorY   { get; set; }
    public double FontSize  { get; set; } = 12.0;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolFontStyle FontStyle { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolTextAlign Align     { get; set; }

    // ── NEW (default values preserve legacy rendering for old .csym files) ──
    /// <summary>Vertical anchor reference. Baseline = legacy behaviour (anchor on the text baseline).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolTextVAlign VAlign { get; set; } = SymbolTextVAlign.Baseline;

    /// <summary>In-place orientation; the box spins about its center. Default R0.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolRotation Rotation { get; set; } = SymbolRotation.R0;

    /// <summary>When true, a rotated cell instance in the schematic auto-flips this text 180° as
    /// needed so it never renders upside-down/mirrored. When false (default), it rotates rigidly with
    /// the instance. The symbol editor always shows the literal authored rotation regardless.</summary>
    public bool ForceReadable { get; set; }

    /// <summary>Color role for this text. Default SymbolLine preserves legacy rendering
    /// (text historically drew in the SymbolLine color). Set SymbolPlus for "+" polarity marks;
    /// SymbolText for regular label text that should track the dedicated text color.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolColorRole ColorRole { get; set; } = SymbolColorRole.SymbolLine;
}

// ── Bitmap (reference/tracing artwork) ───────────────────────────────────────
// Stored as a path reference, not embedded bytes. Z-index is always lowest
// (behind all vector primitives, enforced by renderer). No color role.

public sealed class BitmapPrimitive : SymbolPrimitive
{
    /// <summary>Path to the image file (absolute or relative to the .csym).</summary>
    public string ImagePathRef { get; set; } = "";
    /// <summary>Placement rect: left edge x, top edge y.</summary>
    public double X       { get; set; }
    public double Y       { get; set; }
    public double W       { get; set; }
    public double H       { get; set; }
    public double Opacity { get; set; } = 1.0;
    /// <summary>When locked, accidental click/drag does not move the bitmap.</summary>
    public bool   Locked  { get; set; }
}

// ── Pin ───────────────────────────────────────────────────────────────────────

/// <summary>
/// A pin placement in the symbol. Data only — the runtime still uses
/// SymbolPortDefs for connectivity; pins here are written/read but not yet wired.
/// Every pin tip must be on P (an exact multiple of 100 in local coords).
/// </summary>
public sealed class SymbolPin
{
    public double  LocalX     { get; set; }
    public double  LocalY     { get; set; }
    public int     PortIndex  { get; set; }
    public string? Name       { get; set; }

    /// <summary>
    /// Which edge of the body this pin sits on, and so which way its NAME runs — see
    /// <see cref="SymbolPinNameAlign"/>.
    ///
    /// <para>Set by every path that produces a pin: a component IMPORT takes it from the source file,
    /// which either states the justification outright or fixes it through the pin's own rotation; a
    /// GENERATED symbol derives it from where the pin sits relative to the rest of them
    /// (<see cref="SymbolPinNameAlign"/>, <c>SymbolPinSides</c>). Left is the default, and is what
    /// every symbol did before this field existed.</para>
    /// </summary>
    public SymbolPinNameAlign NameAlign { get; set; } = SymbolPinNameAlign.Left;

    public SymbolPin() { }
    public SymbolPin(double localX, double localY, int portIndex, string? name = null)
    {
        LocalX = localX; LocalY = localY; PortIndex = portIndex; Name = name;
    }
}

// ── Symbol ───────────────────────────────────────────────────────────────────

/// <summary>
/// A symbol: an ordered list of drawing primitives + a list of pins.
/// Primitives and pins are both in component-LOCAL coordinates.
/// This is the single definition of "what this symbol looks like" — all three
/// consumers (editor, renderer, persistence) read the same model.
/// </summary>
public sealed class Symbol
{
    public IReadOnlyList<SymbolPrimitive> Primitives { get; }
    public IReadOnlyList<SymbolPin>       Pins       { get; }

    /// <summary>
    /// Number of ports this symbol can map pins to.
    /// Defaults to Pins.Count when portCount is 0 or omitted (backward-compat).
    /// Persisted in .csym; not the same as the schematic component's PortCount.
    /// </summary>
    public int PortCount { get; }

    public Symbol(IReadOnlyList<SymbolPrimitive> primitives, IReadOnlyList<SymbolPin> pins,
                  int portCount = 0)
    {
        Primitives = primitives;
        Pins       = pins;
        PortCount  = portCount > 0 ? portCount : pins.Count;
    }
}
