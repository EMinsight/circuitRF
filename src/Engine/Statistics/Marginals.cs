namespace CircuitRF.Engine.Statistics;

/// <summary>
/// One statistical variable's distribution (yield overview D2), in the copula form D3 samples with: a
/// value is <see cref="FromNormal"/> of a standard normal z, i.e. the inverse CDF at Φ(z). Correlating
/// the z's first is what lets a Gaussian value correlate with a uniform one. Pure numerics, no units.
/// </summary>
public abstract class Marginal
{
    /// <summary>The value at standard normal <paramref name="z"/>: F⁻¹(Φ(z)).</summary>
    public abstract double FromNormal(double z);

    /// <summary>P(X ≤ x).</summary>
    public abstract double Cdf(double x);

    public abstract double Mean { get; }

    public abstract double Variance { get; }
}

/// <summary>
/// The standard normal truncated to [−k, k], sampled exactly: z is mapped through Φ onto the truncated
/// probability interval [Φ(−k), Φ(k)] and back through Φ⁻¹. Never clipped — clipping would pile
/// the tails' probability onto the edges.
/// </summary>
public static class TruncatedNormal
{
    /// <summary>The truncated standard normal at untruncated <paramref name="z"/>; <paramref name="k"/>
    /// null (or not positive and finite) is no truncation. Odd in z, so each half is computed from the
    /// lower tail, where Φ is relatively accurate.</summary>
    public static double Quantile(double z, double? k)
    {
        if (k is not { } kk || !(kk > 0) || double.IsPositiveInfinity(kk)) return z;
        if (z > 0) return -Quantile(-z, k);
        double tail = SpecialFunctions.NormalCdf(-kk);
        return Math.Max(-kk, SpecialFunctions.InverseNormalCdf(tail + SpecialFunctions.NormalCdf(z) * (1 - 2 * tail)));
    }

    /// <summary>P(Y ≤ y) for the truncated standard normal Y.</summary>
    public static double Cdf(double y, double? k)
    {
        if (k is not { } kk || !(kk > 0) || double.IsPositiveInfinity(kk)) return SpecialFunctions.NormalCdf(y);
        if (y <= -kk) return 0;
        if (y >= kk) return 1;
        double tail = SpecialFunctions.NormalCdf(-kk);
        return (SpecialFunctions.NormalCdf(y) - tail) / (1 - 2 * tail);
    }

    /// <summary>E[e^{tY}] for the truncated standard normal Y — e^{t²/2}·(Φ(k − t) − Φ(−k − t))/(1 − 2Φ(−k)).</summary>
    public static double MomentGenerating(double t, double? k)
    {
        if (k is not { } kk || !(kk > 0) || double.IsPositiveInfinity(kk)) return Math.Exp(t * t / 2);
        double tail = SpecialFunctions.NormalCdf(-kk);
        return Math.Exp(t * t / 2) * (SpecialFunctions.NormalCdf(kk - t) - SpecialFunctions.NormalCdf(-kk - t)) / (1 - 2 * tail);
    }

    /// <summary>Var Y: 1 − 2kφ(k)/(1 − 2Φ(−k)).</summary>
    public static double Variance(double? k)
    {
        if (k is not { } kk || !(kk > 0) || double.IsPositiveInfinity(kk)) return 1;
        return 1 - 2 * kk * SpecialFunctions.NormalPdf(kk) / (1 - 2 * SpecialFunctions.NormalCdf(-kk));
    }
}

/// <summary>Normal with <paramref name="mean"/> and 1σ <paramref name="sigma"/>, optionally truncated at
/// ±<paramref name="trunc"/> σ.</summary>
public sealed class NormalMarginal(double mean, double sigma, double? trunc = null) : Marginal
{
    public override double FromNormal(double z) => mean + sigma * TruncatedNormal.Quantile(z, trunc);

    public override double Cdf(double x) => TruncatedNormal.Cdf((x - mean) / sigma, trunc);

    public override double Mean => mean;

    public override double Variance => sigma * sigma * TruncatedNormal.Variance(trunc);
}

/// <summary>
/// Log-normal: ln X is normal about ln <paramref name="median"/> with 1σ <paramref name="logSigma"/>,
/// optionally truncated at ±<paramref name="trunc"/> σ of the log. The median is the nominal; the mean
/// sits above it by e^{s²/2}.
/// </summary>
public sealed class LogNormalMarginal(double median, double logSigma, double? trunc = null) : Marginal
{
    public override double FromNormal(double z) => median * Math.Exp(logSigma * TruncatedNormal.Quantile(z, trunc));

    public override double Cdf(double x) => x <= 0 ? 0 : TruncatedNormal.Cdf(Math.Log(x / median) / logSigma, trunc);

    public override double Mean => median * TruncatedNormal.MomentGenerating(logSigma, trunc);

    public override double Variance
    {
        get
        {
            double m1 = TruncatedNormal.MomentGenerating(logSigma, trunc);
            return median * median * (TruncatedNormal.MomentGenerating(2 * logSigma, trunc) - m1 * m1);
        }
    }
}

/// <summary>Uniform on [<paramref name="lo"/>, <paramref name="hi"/>].</summary>
public sealed class UniformMarginal(double lo, double hi) : Marginal
{
    public override double FromNormal(double z) => lo + (hi - lo) * SpecialFunctions.NormalCdf(z);

    public override double Cdf(double x) => x <= lo ? 0 : x >= hi ? 1 : (x - lo) / (hi - lo);

    public override double Mean => (lo + hi) / 2;

    public override double Variance => (hi - lo) * (hi - lo) / 12;
}

/// <summary>
/// Equally likely values lo, lo + step, … ≤ hi (a step that does not divide the range exactly keeps
/// the rungs at or below hi, to 1e-9 of a step). z picks rung ⌊Φ(z)·n⌋.
/// </summary>
public sealed class DiscreteMarginal : Marginal
{
    private readonly double _lo, _step;

    public DiscreteMarginal(double lo, double hi, double step)
    {
        _lo = lo;
        _step = step;
        Count = step > 0 && hi >= lo ? (int)Math.Floor((hi - lo) / step + 1e-9) + 1 : 1;
    }

    /// <summary>How many values there are.</summary>
    public int Count { get; }

    /// <summary>The <paramref name="k"/>-th value (0-based).</summary>
    public double Value(int k) => _lo + k * _step;

    public override double FromNormal(double z)
        => Value(Math.Clamp((int)Math.Floor(SpecialFunctions.NormalCdf(z) * Count), 0, Count - 1));

    public override double Cdf(double x)
    {
        if (x < _lo) return 0;
        int below = (int)Math.Floor((x - _lo) / _step + 1e-9) + 1;
        return Math.Min(below, Count) / (double)Count;
    }

    public override double Mean => _lo + _step * (Count - 1) / 2;

    public override double Variance => _step * _step * ((double)Count * Count - 1) / 12;
}
