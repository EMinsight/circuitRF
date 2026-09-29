// brief-em3d-92 R-em3d92-1 — an object's transparency: a property of the OBJECT, independent of its material's display colour.
//
// A PERCENTAGE, 0 (opaque) to Max, in steps of 1 — "transparency", not an opacity fraction (D1). Null is the kind's default
// (a dielectric at Scene3DBuilder.DielectricAlpha, a conductor opaque, air at AirAlpha), so a document that states none draws
// exactly as it did before the key existed. An instance's value MULTIPLIES onto each part's: a 50 % instance of a part at
// 50 % shows at 75 %, and a part at its kind's default is that default's opacity times the instance's.
//
// ONE CAP, READ EVERYWHERE. The Inspector's slider and box, `check`, `render --transparency` and every refusal sentence read
// Max from here; no literal copy of it exists anywhere else, so raising it is this one line (and the decision its doc
// comment records).

namespace CircuitRF.Design.ThreeD;

/// <summary>brief-em3d-92 — the transparency percentage's range and arithmetic.</summary>
public static class C3dTransparency
{
    /// <summary>
    /// The largest transparency an object may state, percent (owner, 2026-09-29). Raising it to 100 needs this constant changed
    /// and one decision about picking: a fully transparent object must stay pickable and keep its edges drawn — today the ID
    /// pass ignores colour, so it would, but its edges are drawn only while it is selected.
    /// </summary>
    public const int Max = 95;

    /// <summary>Whether <paramref name="percent"/> is a transparency an object may state.</summary>
    public static bool InRange(int percent) => percent is >= 0 and <= Max;

    /// <summary>The allowed range as a sentence's end: <c>0 to {Max} %</c>.</summary>
    public static string Range => $"0 to {Max} %";

    /// <summary>The refusal for a value outside the range, naming what stated it.</summary>
    public static string OutOfRange(string what, long percent)
        => $"{what} has a transparency of {percent} %; a transparency is a whole number from {Range}.";

    /// <summary>The opacity a stated percentage leaves: 60 % transparent is 0.4 opaque.</summary>
    public static double Opacity(int percent) => 1 - percent / 100.0;

    /// <summary>
    /// The alpha (0–255) an object is drawn at: its own <paramref name="percent"/>'s, or — null — its kind's
    /// <paramref name="kindAlpha"/>, times <paramref name="carried"/>, the opacity its instances multiply onto it. Rounded to
    /// the nearest, a half away from zero: 60 % is 102.
    /// </summary>
    public static byte Alpha(int? percent, double carried, byte kindAlpha)
    {
        double a = (percent is { } p ? 255 * Opacity(p) : kindAlpha) * Math.Clamp(carried, 0, 1);
        return (byte)Math.Clamp(Math.Round(a, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>
    /// An instance's <paramref name="outer"/> composed onto a part's <paramref name="inner"/>, as a percentage — what Flatten
    /// writes onto the part it brings up. Both null is null. A part at its kind's default has no percentage to multiply, so it
    /// takes the instance's value (the nearest statement of what the instance said), and the result is capped at <see cref="Max"/>.
    /// </summary>
    public static int? Compose(int? outer, int? inner)
    {
        if (outer is not { } o) return inner;
        if (inner is not { } i) return o;
        double opacity = Opacity(o) * Opacity(i);
        return Math.Min(Max, (int)Math.Round(100 * (1 - opacity), MidpointRounding.AwayFromZero));
    }
}
