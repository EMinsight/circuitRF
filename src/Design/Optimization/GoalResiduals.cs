using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// One goal measured at one point (brief-tuneopt-6 R-to6-2/3).
/// </summary>
/// <param name="Name">The goal's name.</param>
/// <param name="Residuals">Its part of the residual vector: each grid point's violation divided by the
/// goal's scale, times its weight, divided by √(points).</param>
/// <param name="WorstViolation">The largest violation, in the expression's own unit (0 = met).</param>
/// <param name="WorstAt">Where on the range axis it is, in base SI; null for a single number. For a met
/// goal, the TIGHTEST point — the one closest to its limit.</param>
/// <param name="Axis">That axis's name; null for a single number.</param>
/// <param name="WorstValue">The expression's value at that point.</param>
/// <param name="Error">Why the goal has no value; the other members are then empty.</param>
/// <param name="Margin">How far inside its limit the goal is at that point, in the expression's own
/// unit: positive when met with room, 0 when met at the limit, −(worst violation) when unmet. NaN with
/// an <paramref name="Error"/>.</param>
public sealed record GoalScore(
    string      Name,
    double[]    Residuals,
    double      WorstViolation,
    double?     WorstAt,
    string?     Axis,
    double      WorstValue,
    Diagnostic? Error = null,
    double      Margin = double.NaN)
{
    public bool Met => Error is null && WorstViolation == 0;
}

/// <summary>
/// Goals → residuals (overview D10/D11; the rule is documented in docs/design/tuning-optimization.md §10).
///
/// <para><b>Violation per grid point</b>, for a value x and a limit L (interpolated linearly along the
/// range axis when the limit slopes): <c>le</c> max(0, x − L); <c>ge</c> max(0, L − x); <c>eq</c>
/// |x − L|; <c>in</c> [a, b] the distance to the interval; <c>out</c> [a, b] the distance to the
/// nearer edge when inside, else 0.</para>
///
/// <para><b>Normalization.</b> Each violation is divided by the goal's SCALE and multiplied by its
/// weight, then by 1/√(points) so a 201-point goal and a scalar goal weigh alike. The scale is the
/// goal's own <c>scale=</c> when given; otherwise the band's width b − a for <c>in</c>/<c>out</c>, and
/// max(|L|, 1) in the limit's OWN unit for the others — so a −15 dB limit is worth 15 dB, a 0.5 limit
/// is worth 1, and a 2 pF limit is worth 2 pF rather than 1 F. A sloped limit takes the larger end.</para>
///
/// <para><b>Met.</b> A violation within <see cref="MetTolerance"/> of the goal's scale counts as zero.
/// Least squares on a one-sided violation approaches the limit from the violating side and lands on
/// it only to rounding: a matching run reached |S11| = 1e-4 + 5e-15 against <c>le 1e-4</c> and could
/// not report the goal met.</para>
///
/// <para><b>Either order.</b> A band's two edges, and a range's two ends, are read lowest-first whichever
/// way round they were written. A sloped limit keeps the end it was written against: the first limit
/// belongs to <c>lo=</c>, wherever <c>lo=</c> lies.</para>
///
/// <para><b>Cost.</b> Least squares Σ r², or minimax max r, over the concatenated residual vector.</para>
/// </summary>
public static class GoalResiduals
{
    /// <summary>A violation at most this fraction of the goal's scale is met.</summary>
    public const double MetTolerance = 1e-9;

    /// <summary>The goal's scale in base SI, or null when its limit text is not a number.</summary>
    public static double? Scale(OptimizationGoal g)
    {
        if (g.Scale is { } text && TunableValue.TryParse(text, out _, out _, out double given) && given > 0)
            return given;
        if (!TunableValue.TryParse(g.Limit, out _, out string unit, out double lo)) return null;
        double unitScale = unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1;

        if (g.Type is GoalType.In or GoalType.Out)
        {
            if (g.UpperLimit is null || !TunableValue.TryParse(g.UpperLimit, out _, out _, out double hi)) return null;
            return hi != lo ? Math.Abs(hi - lo) : unitScale;
        }

        // max(|L|, 1) in the limit's own unit, i.e. max(|L|, one of that unit) in base SI.
        double scale = Math.Max(Math.Abs(lo), unitScale);
        if (g.LimitAtHi is { } atHi && TunableValue.TryParse(atHi, out _, out _, out double loHi))
            scale = Math.Max(scale, Math.Abs(loHi));
        return scale;
    }

    /// <summary>
    /// The base-SI unit the goal's values are in — its value, worst value and margin (brief-yield-16 R-ya16-13). The
    /// limit's own unit when it is written with one (<c>le 2.4 GHz</c> → Hz: the limit is parsed to base SI to compare
    /// with the value); otherwise the expression's: dB for a whole <c>dB</c>, <c>dB20</c> or <c>dB10</c> of something,
    /// dBm for <c>dBm</c>, and a measurement's own unit when the expression is that measurement's name
    /// (<paramref name="measurementUnits"/>). Empty when none of these says.
    /// </summary>
    public static string ValueUnit(OptimizationGoal g, IReadOnlyDictionary<string, string>? measurementUnits = null)
    {
        if (TunableValue.TryParse(g.Limit, out _, out string unit, out _) && unit.Length > 0) return Units.BaseUnit(unit);
        string e = g.Expression.Trim();
        if (OuterCall(e) is { } f)
        {
            if (f is "dB" or "dB20" or "dB10") return "dB";
            if (f == "dBm") return "dBm";
        }
        return measurementUnits is not null && measurementUnits.TryGetValue(e, out var m) && !string.IsNullOrEmpty(m)
            ? Units.BaseUnit(m) : "";
    }

    /// <summary>The function name of an expression that is one call end to end (<c>dB(a)</c>, not <c>dB(a) - b</c>).</summary>
    private static string? OuterCall(string e)
    {
        int open = e.IndexOf('(');
        if (open <= 0 || !e.EndsWith(')')) return null;
        int depth = 0;
        bool quoted = false;
        for (int i = open; i < e.Length; i++)
        {
            char c = e[i];
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']' && --depth == 0 && i != e.Length - 1) return null;
        }
        string name = e[..open].Trim();
        return name.Length > 0 && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? name : null;
    }

    /// <summary>
    /// The bounds ONE value of the goal's quantity must lie within to meet it, in base SI — what a value's
    /// statistics (Cpk, σ to the limit) and a histogram's spec lines are read against (brief-yield-8). A sloped limit
    /// gives its TIGHTER end: <c>ge</c> the larger, <c>le</c> the smaller. <c>in</c> gives both edges. <c>eq</c> and
    /// <c>out</c> give none — no single side is the passing one — and so does a limit that is not a number.
    /// </summary>
    public static (double? Lower, double? Upper) ValueLimits(OptimizationGoal g)
    {
        if (!TunableValue.TryParse(g.Limit, out _, out _, out double lo)) return (null, null);
        double atHi = lo;
        if (g.LimitAtHi is { } h && !TunableValue.TryParse(h, out _, out _, out atHi)) return (null, null);
        switch (g.Type)
        {
            case GoalType.Ge: return (Math.Max(lo, atHi), null);
            case GoalType.Le: return (null, Math.Min(lo, atHi));
            case GoalType.In:
                if (g.UpperLimit is null || !TunableValue.TryParse(g.UpperLimit, out _, out _, out double up)) return (null, null);
                return (Math.Min(lo, up), Math.Max(lo, up));
            default: return (null, null);
        }
    }

    /// <summary>Scores <paramref name="g"/> on the value its expression produced (or the error it raised).</summary>
    public static GoalScore Score(OptimizationGoal g, Value? value, string? error)
    {
        GoalScore Fail(Diagnostic d) => new(g.Name, [], double.PositiveInfinity, null, null, double.NaN, d);

        if (error is not null) return Fail(OptimizationDiagnostics.GoalFailed(g.Name, error));
        if (value is not { } v) return Fail(OptimizationDiagnostics.GoalNotANumber(g.Name));
        if (Scale(g) is not { } scale) return Fail(OptimizationDiagnostics.GoalLimitNotANumber(g.Name, g.Limit));

        if (!TunableValue.TryParse(g.Limit, out _, out _, out double limit))
            return Fail(OptimizationDiagnostics.GoalLimitNotANumber(g.Name, g.Limit));
        double upper = 0, limitHi = limit;
        if (g.Type is GoalType.In or GoalType.Out
            && (g.UpperLimit is null || !TunableValue.TryParse(g.UpperLimit, out _, out _, out upper)))
            return Fail(OptimizationDiagnostics.GoalLimitNotANumber(g.Name, g.UpperLimit ?? ""));
        if (g.LimitAtHi is { } atHi && !TunableValue.TryParse(atHi, out _, out _, out limitHi))
            return Fail(OptimizationDiagnostics.GoalLimitNotANumber(g.Name, atHi));
        if (g.Type is GoalType.In or GoalType.Out && limit > upper) (limit, upper) = (upper, limit);

        // ── The points: (axis value, x) for every element in the range ──────
        var points = new List<(double? At, double X)>();
        string? axisName = g.Range?.Axis;
        double lo = 0, hi = 0;
        if (g.Range is { } r)
        {
            if (!TunableValue.TryParse(r.Lo, out _, out _, out lo)) return Fail(OptimizationDiagnostics.GoalLimitNotANumber(g.Name, r.Lo));
            if (!TunableValue.TryParse(r.Hi, out _, out _, out hi)) return Fail(OptimizationDiagnostics.GoalLimitNotANumber(g.Name, r.Hi));
        }

        switch (v.Kind)
        {
            case ValueKind.Real:
                if (axisName is not null) return Fail(OptimizationDiagnostics.GoalScalarRange(g.Name, axisName));
                points.Add((null, v.AsReal()));
                break;
            case ValueKind.Complex:
                return Fail(OptimizationDiagnostics.GoalComplex(g.Name));
            case ValueKind.Cube:
            {
                var cube = v.AsCube();
                if (cube.DataKind != DataKind.Real) return Fail(OptimizationDiagnostics.GoalComplex(g.Name));
                double[] data = cube.RealValues;
                if (axisName is null)
                {
                    foreach (double x in data) points.Add((null, x));
                    break;
                }
                int a = -1;
                for (int d = 0; d < cube.Rank; d++) if (cube.Axes[d].Name == axisName) a = d;
                if (a < 0)
                {
                    if (cube.Rank == 0) return Fail(OptimizationDiagnostics.GoalScalarRange(g.Name, axisName));
                    return Fail(OptimizationDiagnostics.GoalNoAxis(g.Name, axisName, string.Join(", ", cube.Axes.Select(x => x.Name))));
                }
                var axis = cube.Axes[a];
                int stride = 1;
                for (int d = cube.Rank - 1; d > a; d--) stride *= cube.Axes[d].Length;
                double tol = 1e-9 * Math.Max(Math.Abs(lo), Math.Abs(hi));
                double first = Math.Min(lo, hi), last = Math.Max(lo, hi);
                for (int e = 0; e < data.Length; e++)
                {
                    double at = axis.Values[e / stride % axis.Length];
                    if (at >= first - tol && at <= last + tol) points.Add((at, data[e]));
                }
                if (points.Count == 0)
                    return Fail(OptimizationDiagnostics.GoalRangeEmpty(g.Name, axisName, first, last, axis.Values.Min(), axis.Values.Max()));
                break;
            }
            default:
                return Fail(OptimizationDiagnostics.GoalNotANumber(g.Name));
        }

        // ── Violations ──────────────────────────────────────────────────────
        var residuals = new double[points.Count];
        double norm = g.Weight / scale / Math.Sqrt(points.Count);
        double worst = -1, worstAt = double.NaN, worstX = double.NaN;
        // The tightest point — least slack to the limit — is what a MET goal reports (brief-tuneopt-11
        // R-to11-2's margin column); an unmet goal's tightest point is its worst violation.
        double tight = double.PositiveInfinity, tightAt = double.NaN, tightX = double.NaN;
        for (int k = 0; k < points.Count; k++)
        {
            var (at, x) = points[k];
            if (!double.IsFinite(x)) return Fail(OptimizationDiagnostics.GoalNonFinite(g.Name));
            double L = limit;
            if (g.LimitAtHi is not null && at is { } t && hi != lo) L = limit + (limitHi - limit) * (t - lo) / (hi - lo);

            double viol = g.Type switch
            {
                GoalType.Le  => Math.Max(0, x - L),
                GoalType.Ge  => Math.Max(0, L - x),
                GoalType.Eq  => Math.Abs(x - L),
                GoalType.In  => x < limit ? limit - x : x > upper ? x - upper : 0,
                _            => x > limit && x < upper ? Math.Min(x - limit, upper - x) : 0,
            };
            double slack = g.Type switch
            {
                GoalType.Le  => L - x,
                GoalType.Ge  => x - L,
                GoalType.Eq  => -Math.Abs(x - L),
                GoalType.In  => Math.Min(x - limit, upper - x),
                _            => x <= limit ? limit - x : x >= upper ? x - upper : -Math.Min(x - limit, upper - x),
            };
            if (viol <= MetTolerance * scale) viol = 0;
            residuals[k] = viol * norm;
            if (viol > worst) (worst, worstAt, worstX) = (viol, at ?? double.NaN, x);
            if (slack < tight) (tight, tightAt, tightX) = (slack, at ?? double.NaN, x);
        }
        double margin = worst > 0 ? -worst : Math.Max(0, tight);
        if (worst == 0) (worstAt, worstX) = (tightAt, tightX);
        return new GoalScore(g.Name, residuals, worst, double.IsNaN(worstAt) ? null : worstAt,
                             double.IsNaN(worstAt) ? null : axisName, worstX, Margin: margin);
    }

    /// <summary>The cost of a residual vector: Σ r² (least squares) or max r (minimax).</summary>
    public static double Cost(IEnumerable<double> residuals, OptimizerCost form)
        => form == OptimizerCost.Minimax ? residuals.DefaultIfEmpty(0).Max() : residuals.Sum(r => r * r);
}
