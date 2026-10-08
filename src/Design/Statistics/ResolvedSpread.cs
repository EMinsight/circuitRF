using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A statistical entry's spread in numbers (docs/design/yield.md §2): the written values turned into
/// base SI against the value's nominal. A percent is a percent OF the nominal — <c>sd=2%</c> of 50 Ω
/// is 1 Ω, <c>lo=90%</c> is 45 Ω — and a value with no unit is in the parameter's own unit.
/// </summary>
/// <param name="NominalSi">The nominal, base SI.</param>
/// <param name="Unit">The parameter's own unit (engine spelling, "" for none).</param>
/// <param name="Sigma">gauss, lognorm: 1σ, base SI. For lognorm, σ/nominal is the log's σ.</param>
/// <param name="Lo">unif, discrete: the lower end, base SI.</param>
/// <param name="Hi">unif, discrete: the upper end, base SI.</param>
/// <param name="Step">discrete: the step, base SI.</param>
/// <param name="Trunc">gauss, lognorm: the truncation in σ; null for none.</param>
public sealed record ResolvedSpread(
    StatDistribution Distribution,
    double  NominalSi,
    string  Unit,
    double? Sigma,
    double? Lo,
    double? Hi,
    double? Step,
    double? Trunc)
{
    /// <summary>The spread of <paramref name="e"/> around <paramref name="t"/>'s value, or null when a
    /// key the distribution needs is missing or not a number — which <see cref="StatisticsValidator"/>
    /// reports.</summary>
    public static ResolvedSpread? Of(TunableEntry e, Tunable t)
    {
        var sp = e.Spread ?? new StatSpread();
        double scale = t.Unit.Length == 0 ? 1 : Units.Scale(t.Unit) ?? 1;
        double nominal = t.Value * scale;
        double? trunc = Number(sp.Trunc);

        switch (e.Distribution)
        {
            case StatDistribution.Gauss:
            case StatDistribution.LogNorm:
            {
                double? sigma = sp.Sd is not null ? Width(sp.Sd, nominal, t.Unit)
                              : Width(sp.Tol, nominal, t.Unit) is { } tol && Number(sp.Sigmas) is { } k && k != 0 ? tol / k
                              : null;
                return sigma is null ? null : new(e.Distribution, nominal, t.Unit, sigma, null, null, null, trunc);
            }
            case StatDistribution.Unif:
            {
                if (sp.Tol is not null)
                    return Width(sp.Tol, nominal, t.Unit) is { } tol
                        ? new(e.Distribution, nominal, t.Unit, null, nominal - tol, nominal + tol, null, null) : null;
                return Point(sp.Lo, nominal, t.Unit) is { } lo && Point(sp.Hi, nominal, t.Unit) is { } hi
                    ? new(e.Distribution, nominal, t.Unit, null, lo, hi, null, null) : null;
            }
            case StatDistribution.Discrete:
                return Point(sp.Lo, nominal, t.Unit) is { } dlo && Point(sp.Hi, nominal, t.Unit) is { } dhi
                       && Width(sp.By, nominal, t.Unit) is { } by
                    ? new(e.Distribution, nominal, t.Unit, null, dlo, dhi, by, null) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The probability a draw is ≤ 0 — non-physical for a value that must be positive (yield overview
    /// D2). A truncated normal is renormalized over its ±k σ window; a log-normal never draws one.
    /// </summary>
    public double NonPhysicalProbability()
    {
        switch (Distribution)
        {
            case StatDistribution.Gauss when Sigma is > 0:
            {
                double z = NominalSi / Sigma.Value;           // how many σ from the mean zero sits
                if (Trunc is not { } k || k <= 0) return SpecialFunctions.NormalCdf(-z);
                if (z >= k) return 0;
                double tail = SpecialFunctions.NormalCdf(-k);
                return (SpecialFunctions.NormalCdf(-z) - tail) / (1 - 2 * tail);
            }
            case StatDistribution.Unif when Lo is { } lo && Hi is { } hi && hi > lo:
                return lo >= 0 ? 0 : (Math.Min(0, hi) - lo) / (hi - lo);
            case StatDistribution.Discrete when Lo is { } dlo && Hi is { } dhi && Step is > 0:
            {
                int n = (int)Math.Floor((dhi - dlo) / Step.Value + 1e-9) + 1;
                int bad = 0;
                for (int i = 0; i < n; i++) if (dlo + i * Step.Value <= 0) bad++;
                return n <= 0 ? 0 : (double)bad / n;
            }
            default:
                return 0;
        }
    }

    /// <summary>A spread width (sd, tol, by) in base SI: a percent of |nominal|, or a value.</summary>
    internal static double? Width(string? text, double nominal, string unit)
        => text is null ? null
         : StatSpread.Percent(text) is { } p ? Math.Abs(nominal) * p / 100
         : Absolute(text, unit);

    /// <summary>A spread END (lo, hi) in base SI: a percent of the nominal, or a value.</summary>
    internal static double? Point(string? text, double nominal, string unit)
        => text is null ? null
         : StatSpread.Percent(text) is { } p ? nominal * p / 100
         : Absolute(text, unit);

    private static double? Absolute(string text, string unit)
    {
        if (TunableValue.InUnit(text, unit) is not { } n) return null;
        return n * (unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1);
    }

    internal static double? Number(string? text)
        => text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
