using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Optimization;

/// <summary>How a complex value was written, so a tuned value is written back the same way.</summary>
public enum ComplexForm
{
    /// <summary><c>50+10j</c>, <c>50-j10</c>, <c>-3j</c>.</summary>
    Rect,
    /// <summary><c>complex(50,10)</c>.</summary>
    Call,
    /// <summary><c>polar(51,11.3)</c> — magnitude and phase in degrees.</summary>
    Polar,
}

/// <summary>
/// What counts as a complex value for tuning (overview D18), and the arithmetic of its four parts.
///
/// <para><b>A complex literal is built from numbers only</b>: numbers, <c>j</c>, unary and binary
/// <c>+ - *</c>, or one <c>complex(a,b)</c> / <c>polar(m,deg)</c> whose arguments are numbers, with an
/// optional unit after the whole. Anything that reads a name — <c>4+j*X</c> — is an expression, and is
/// not offered; tuning <c>X</c> is how that one is tuned.</para>
///
/// <para><b>One value, four views.</b> Moving one part holds its PARTNER in the same coordinate system
/// fixed — real with imaginary, magnitude with phase — so each part's slider is one straight path in
/// the complex plane whatever else of the value is tuned.</para>
/// </summary>
public static class ComplexValue
{
    /// <summary>
    /// Reads value text as a complex literal with an optional recognized unit. <paramref name="z"/> is in
    /// that unit. False for a plain real number (that is an ordinary tunable), an expression, a string.
    /// </summary>
    public static bool TryParse(string text, out Complex z, out string unit, out ComplexForm form)
    {
        z = default; unit = ""; form = ComplexForm.Rect;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var (expr, lifted) = Units.LiftInlineUnit(text.Trim());
        if (lifted is not null)
        {
            unit = UnitNormalizer.ToEngineUnit(lifted);
            if (!Units.IsRecognizedUnit(unit)) return false;
        }

        Expr ast;
        try { ast = Parser.Parse(expr); }
        catch (ExpressionException) { return false; }

        if (ast is CallExpr { Name: "complex" or "polar", Args.Length: 2 } call)
        {
            if (RealConstant(call.Args[0]) is not { } a || RealConstant(call.Args[1]) is not { } b) return false;
            form = call.Name == "polar" ? ComplexForm.Polar : ComplexForm.Call;
            z = form == ComplexForm.Polar ? Complex.FromPolarCoordinates(a, b * Math.PI / 180) : new Complex(a, b);
            return true;
        }

        bool sawJ = false;
        if (Fold(ast, ref sawJ) is not { } folded || !sawJ) return false;
        z = folded;
        return double.IsFinite(z.Real) && double.IsFinite(z.Imaginary);
    }

    /// <summary>A number, optionally negated — the only argument a literal's call may take.</summary>
    private static double? RealConstant(Expr e) => e switch
    {
        NumberExpr { Unit: null } n         => n.Value,
        UnaryExpr { Op: "-" } u             => -RealConstant(u.Operand),
        UnaryExpr { Op: "+" } u             => RealConstant(u.Operand),
        _                                   => null,
    };

    private static Complex? Fold(Expr e, ref bool sawJ)
    {
        switch (e)
        {
            case NumberExpr { Unit: null } n: return n.Value;
            case ConstExpr { Name: "j" }:     sawJ = true; return Complex.ImaginaryOne;
            case UnaryExpr { Op: "-" } u:     return -Fold(u.Operand, ref sawJ);
            case UnaryExpr { Op: "+" } u:     return Fold(u.Operand, ref sawJ);
            case BinaryExpr { Op: "+" or "-" or "*" } b:
                if (Fold(b.Left, ref sawJ) is not { } l || Fold(b.Right, ref sawJ) is not { } r) return null;
                return b.Op switch { "+" => l + r, "-" => l - r, _ => l * r };
            default:
                return null;
        }
    }

    /// <summary>
    /// <paramref name="z"/> spelled in <paramref name="form"/>, with no whitespace inside the expression
    /// (an instance line splits on it) and the unit after: <c>47.3-10j Ohm</c>, <c>polar(51,11.3)</c>.
    /// </summary>
    public static string Format(Complex z, string unit, ComplexForm form, string numberFormat = "G6")
        => FormatExpression(z, form, numberFormat) + (unit.Length == 0 ? "" : " " + unit);

    /// <summary>The expression half of <see cref="Format"/>.</summary>
    public static string FormatExpression(Complex z, ComplexForm form, string numberFormat = "G6")
    {
        string N(double v) => (v == 0 ? 0.0 : v).ToString(numberFormat, CultureInfo.InvariantCulture);
        return form switch
        {
            ComplexForm.Polar => $"polar({N(z.Magnitude)},{N(PhaseDeg(z))})",
            ComplexForm.Call  => $"complex({N(z.Real)},{N(z.Imaginary)})",
            _ => z.Imaginary < 0 ? $"{N(z.Real)}-{N(-z.Imaginary)}j" : $"{N(z.Real)}+{N(z.Imaginary)}j",
        };
    }

    private static double PhaseDeg(Complex z) => z == Complex.Zero ? 0 : Math.Atan2(z.Imaginary, z.Real) * 180 / Math.PI;

    /// <summary>
    /// One part of <paramref name="z"/>. A phase is in degrees and, given <paramref name="phaseNear"/>,
    /// the turn nearest it — so a row whose range is 150 .. 210 deg reads 190, not −170.
    /// </summary>
    public static double Get(Complex z, ComplexPart part, double? phaseNear = null) => part switch
    {
        ComplexPart.Real => z.Real,
        ComplexPart.Imag => z.Imaginary,
        ComplexPart.Mag  => z.Magnitude,
        _ => phaseNear is { } near ? Unwrap(PhaseDeg(z), near) : PhaseDeg(z),
    };

    /// <summary><paramref name="deg"/> moved by whole turns to lie nearest <paramref name="near"/>.</summary>
    public static double Unwrap(double deg, double near) => deg + 360 * Math.Round((near - deg) / 360);

    /// <summary>
    /// <paramref name="z"/> with one part set and its partner held: real keeps the imaginary part,
    /// imaginary the real, magnitude the phase, phase the magnitude. A magnitude below zero is zero.
    /// </summary>
    public static Complex With(Complex z, ComplexPart part, double value) => part switch
    {
        ComplexPart.Real => new Complex(value, z.Imaginary),
        ComplexPart.Imag => new Complex(z.Real, value),
        ComplexPart.Mag  => Complex.FromPolarCoordinates(Math.Max(0, value), z == Complex.Zero ? 0 : z.Phase),
        _                => Complex.FromPolarCoordinates(z.Magnitude, value * Math.PI / 180),
    };

    /// <summary>
    /// The value whose stated parts are <paramref name="parts"/>, the rest taken from
    /// <paramref name="start"/> — the optimizer's decode and a part key in a headless run (overview D18).
    /// Parts of one system (real with imaginary, magnitude with phase) set it directly; a mixed pair is
    /// solved geometrically, the free sign taken from <paramref name="start"/>. Null when no complex value
    /// has those parts (a real part larger than the magnitude) or when more than two are stated — a
    /// complex value has two degrees of freedom.
    /// </summary>
    public static Complex? Compose(Complex start, IReadOnlyDictionary<ComplexPart, double> parts)
    {
        if (parts.Count == 0) return start;
        if (parts.Count > 2) return null;

        bool rect  = parts.Keys.All(p => p is ComplexPart.Real or ComplexPart.Imag);
        bool polar = parts.Keys.All(p => p is ComplexPart.Mag or ComplexPart.Phase);
        if (rect)
            return new Complex(parts.GetValueOrDefault(ComplexPart.Real, start.Real),
                               parts.GetValueOrDefault(ComplexPart.Imag, start.Imaginary));
        if (polar)
            return Complex.FromPolarCoordinates(
                Math.Max(0, parts.GetValueOrDefault(ComplexPart.Mag, start.Magnitude)),
                parts.TryGetValue(ComplexPart.Phase, out double ph) ? ph * Math.PI / 180 : start == Complex.Zero ? 0 : start.Phase);

        // One rectangular part with one polar part.
        var r = parts.First(kv => kv.Key is ComplexPart.Real or ComplexPart.Imag);
        var q = parts.First(kv => kv.Key is ComplexPart.Mag or ComplexPart.Phase);
        var (rp, rv, pp, pv) = (r.Key, r.Value, q.Key, q.Value);
        double tol = 1e-12 * Math.Max(1, Math.Max(Math.Abs(rv), Math.Abs(pv)));

        if (pp == ComplexPart.Mag)
        {
            if (pv < Math.Abs(rv) - tol) return null;
            double other = Math.Sqrt(Math.Max(0, pv * pv - rv * rv));
            double sign  = (rp == ComplexPart.Real ? start.Imaginary : start.Real) < 0 ? -1 : 1;
            return rp == ComplexPart.Real ? new Complex(rv, sign * other) : new Complex(sign * other, rv);
        }

        // A stated part and a direction: the magnitude is part / cos (or sin) of the phase.
        double rad = pv * Math.PI / 180;
        double c   = rp == ComplexPart.Real ? Math.Cos(rad) : Math.Sin(rad);
        if (Math.Abs(c) < 1e-12)
            return Math.Abs(rv) <= tol ? Complex.FromPolarCoordinates(start.Magnitude, rad) : null;
        double m = rv / c;
        return m < -tol ? null : Complex.FromPolarCoordinates(Math.Max(0, m), rad);
    }
}

/// <summary>
/// The part of the complex plane every range of one complex value allows (overview D18: the limits of
/// a value's parts are always respected). Bounds are in the value's own unit; a phase's in degrees.
/// </summary>
public sealed class ComplexRegion
{
    private readonly Dictionary<ComplexPart, (double Lo, double Hi)> _bounds;

    public ComplexRegion(IEnumerable<(ComplexPart Part, double Lo, double Hi)> bounds)
    {
        _bounds = [];
        foreach (var (p, lo, hi) in bounds)
            _bounds[p] = _bounds.TryGetValue(p, out var b) ? (Math.Max(b.Lo, lo), Math.Min(b.Hi, hi)) : (lo, hi);
    }

    /// <summary>The ranges <paramref name="setup"/>'s entries give the value <paramref name="wholeKey"/> —
    /// every entry of it, tuned, optimized or neither: a range belongs to the entry, not to a window.
    /// <paramref name="unit"/> is the value's unit. A bound that does not parse is no bound.</summary>
    public static ComplexRegion Of(TuningSetup? setup, string wholeKey, string unit)
    {
        var bounds = new List<(ComplexPart, double, double)>();
        foreach (var e in setup?.Variables ?? [])
        {
            if (!TunableKey.TryParse(e.Key, out var k) || k.Part is not { } part || k.Whole.ToString() != wholeKey) continue;
            string u = part == ComplexPart.Phase ? "deg" : unit;
            bounds.Add((part, TunableValue.InUnit(e.Min, u) ?? double.NegativeInfinity,
                              TunableValue.InUnit(e.Max, u) ?? double.PositiveInfinity));
        }
        return new ComplexRegion(bounds);
    }

    /// <summary>The ranges this region was built from, by part.</summary>
    public IReadOnlyDictionary<ComplexPart, (double Lo, double Hi)> Bounds => _bounds;

    public bool Contains(Complex z)
    {
        double scale = Math.Max(1, z.Magnitude);
        foreach (var (part, (lo, hi)) in _bounds)
        {
            if (part == ComplexPart.Phase)
            {
                if (hi - lo >= 360 || z == Complex.Zero) continue;
                double d = ComplexValue.Get(z, ComplexPart.Phase);
                double wrapped = lo + (((d - lo) % 360) + 360) % 360;
                if (wrapped > hi + 1e-9 && wrapped - 360 < lo - 1e-9) return false;
                continue;
            }
            double finite = Math.Max(double.IsFinite(lo) ? Math.Abs(lo) : 0, double.IsFinite(hi) ? Math.Abs(hi) : 0);
            double v = ComplexValue.Get(z, part), tol = 1e-12 * Math.Max(scale, finite);
            if (v < lo - tol || v > hi + tol) return false;
        }
        return true;
    }

    /// <summary>
    /// No complex value lies inside every range. Exact, not sampled: the plane is cut into wedges of at
    /// most 90°; inside one, the real and imaginary ranges and the wedge make a convex polygon, over which
    /// the magnitude takes every value between its nearest and farthest point — so the magnitude range
    /// either meets that interval or it does not.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            var (a, b) = Bound(ComplexPart.Real);
            var (c, d) = Bound(ComplexPart.Imag);
            var (e, f) = Bound(ComplexPart.Mag);
            if (a > b || c > d || e > f || f < 0) return true;

            double big = 1;
            foreach (double x in new[] { a, b, c, d, e, f })
                if (double.IsFinite(x)) big = Math.Max(big, Math.Abs(x));
            big *= 4;
            double lim = double.IsFinite(f) ? f : big;
            double x0 = Math.Max(a, -lim), x1 = Math.Min(b, lim), y0 = Math.Max(c, -lim), y1 = Math.Min(d, lim);
            if (x0 > x1 || y0 > y1) return true;
            double eps = 1e-12 * big;
            bool originIn = x0 <= eps && x1 >= -eps && y0 <= eps && y1 >= -eps;

            foreach (var (g, h) in Wedges())
            {
                var poly = new List<(double X, double Y)> { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
                var (cg, sg) = (Math.Cos(g * Math.PI / 180), Math.Sin(g * Math.PI / 180));
                var (ch, sh) = (Math.Cos(h * Math.PI / 180), Math.Sin(h * Math.PI / 180));
                poly = Clip(poly, p => cg * p.Y - sg * p.X, eps);    // left of the ray at g
                poly = Clip(poly, p => p.X * sh - p.Y * ch, eps);    // right of the ray at h
                if (poly.Count == 0) continue;

                double rMax = poly.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
                double rMin = originIn ? 0 : MinDistance(poly);
                if (rMax >= e - eps && rMin <= f + eps) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Where moving part <paramref name="part"/> of <paramref name="from"/> towards
    /// <paramref name="target"/> (its partner held) ends: at the target when the whole path stays inside,
    /// otherwise at the first edge it meets — a slider stops at the wall. A start already outside (the
    /// schematic holds a value outside the ranges) is not held back.
    /// </summary>
    public Complex Move(Complex from, ComplexPart part, double target)
    {
        var end = ComplexValue.With(from, part, target);
        if (Contains(end) || !Contains(from)) return end;

        double start = ComplexValue.Get(from, part, phaseNear: target);
        double ok = 0, bad = 1;
        const int scan = 256;
        for (int i = 1; i <= scan; i++)
        {
            double t = (double)i / scan;
            if (!Contains(ComplexValue.With(from, part, start + t * (target - start)))) { bad = t; break; }
            ok = t;
        }
        for (int i = 0; i < 60; i++)
        {
            double mid = (ok + bad) / 2;
            if (Contains(ComplexValue.With(from, part, start + mid * (target - start)))) ok = mid; else bad = mid;
        }
        return ComplexValue.With(from, part, start + ok * (target - start));
    }

    private (double Lo, double Hi) Bound(ComplexPart p)
        => _bounds.TryGetValue(p, out var b) ? b : (double.NegativeInfinity, double.PositiveInfinity);

    private IEnumerable<(double, double)> Wedges()
    {
        double lo = -180, hi = 180;
        if (_bounds.TryGetValue(ComplexPart.Phase, out var ph) && ph.Hi - ph.Lo < 360)
            (lo, hi) = ph;
        if (hi < lo) yield break;
        int n = Math.Max(1, (int)Math.Ceiling((hi - lo) / 90));
        double w = (hi - lo) / n;
        for (int i = 0; i < n; i++) yield return (lo + i * w, lo + (i + 1) * w);
    }

    /// <summary>Sutherland–Hodgman: the part of a convex polygon where <paramref name="side"/> ≥ −eps.</summary>
    private static List<(double X, double Y)> Clip(List<(double X, double Y)> poly, Func<(double X, double Y), double> side, double eps)
    {
        var result = new List<(double X, double Y)>();
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            double sp = side(p), sq = side(q);
            bool pin = sp >= -eps, qin = sq >= -eps;
            if (pin) result.Add(p);
            if (pin != qin)
            {
                double t = sp / (sp - sq);
                result.Add((p.X + t * (q.X - p.X), p.Y + t * (q.Y - p.Y)));
            }
        }
        return result;
    }

    private static double MinDistance(List<(double X, double Y)> poly)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            double dx = q.X - p.X, dy = q.Y - p.Y, len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Clamp(-(p.X * dx + p.Y * dy) / len2, 0, 1);
            double x = p.X + t * dx, y = p.Y + t * dy;
            best = Math.Min(best, Math.Sqrt(x * x + y * y));
        }
        return best;
    }

    /// <summary>
    /// The refusal for an edit that leaves <paramref name="setup"/>'s ranges for the value
    /// <paramref name="key"/> belongs to with no complex value inside every one of them (overview D18),
    /// or null when the edit stands — including for a key that names no complex part.
    /// </summary>
    public static string? Conflict(TuningSetup setup, string key, string unit)
    {
        if (!TunableKey.TryParse(key, out var k) || k.Part is null) return null;
        string whole = k.Whole.ToString();
        if (!Of(setup, whole, unit).IsEmpty) return null;

        var others = setup.Variables
            .Where(e => e.Key != key && TunableKey.TryParse(e.Key, out var o) && o.Part is not null && o.Whole.ToString() == whole)
            .Select(e => $"{e.Key} {e.Min} .. {e.Max}");
        return $"{key}'s range leaves no value of {whole} inside every range ({string.Join(", ", others)})";
    }
}
