namespace CircuitRF.Engine.Statistics;

/// <summary>
/// The Clopper–Pearson ("exact") binomial interval a yield is reported with (yield overview D8): the
/// set of pass probabilities p for which the observed pass count is not in either α/2 tail. Its ends
/// are quantiles of the Beta distribution — the lower Beta(k, n−k+1) at α/2, the upper Beta(k+1, n−k)
/// at 1−α/2 — with 0 below no passes and 1 above all passes.
/// </summary>
public static class ClopperPearson
{
    /// <summary>The interval for <paramref name="passes"/> of <paramref name="trials"/> at
    /// <paramref name="confidence"/> (a fraction, 0.95).</summary>
    public static (double Lower, double Upper) Interval(int passes, int trials, double confidence)
    {
        if (trials <= 0) throw new ArgumentOutOfRangeException(nameof(trials), "There must be at least one trial.");
        if (passes < 0 || passes > trials) throw new ArgumentOutOfRangeException(nameof(passes));
        if (!(confidence > 0 && confidence < 1)) throw new ArgumentOutOfRangeException(nameof(confidence));

        double alpha = 1 - confidence;
        double lower = passes == 0 ? 0 : SpecialFunctions.InverseRegularizedBeta(alpha / 2, passes, trials - passes + 1);
        double upper = passes == trials ? 1 : SpecialFunctions.InverseRegularizedBeta(1 - alpha / 2, passes + 1, trials - passes);
        return (lower, upper);
    }

    /// <summary>Half the interval's width at the pass count nearest <paramref name="yield"/> of
    /// <paramref name="trials"/> — what a run of that size can be expected to resolve.</summary>
    public static double ExpectedHalfWidth(double yield, int trials, double confidence)
    {
        int passes = (int)Math.Round(yield * trials, MidpointRounding.AwayFromZero);
        var (lo, hi) = Interval(Math.Clamp(passes, 0, trials), trials, confidence);
        return (hi - lo) / 2;
    }

    /// <summary>The fewest trials whose expected half-width at <paramref name="yield"/> is below
    /// <paramref name="halfWidth"/>, searched upward from one trial; null when none up to
    /// <paramref name="maxTrials"/> is.</summary>
    public static int? TrialsFor(double yield, double halfWidth, double confidence, int maxTrials = 1_000_000)
    {
        // The half-width falls as 1/sqrt(n) apart from rounding ripples, so a doubling search brackets
        // the answer and a scan of the last bracket finds the first n below it.
        int hi = 1;
        while (ExpectedHalfWidth(yield, hi, confidence) >= halfWidth)
        {
            if (hi >= maxTrials) return null;
            hi = Math.Min(maxTrials, hi * 2);
        }
        for (int n = Math.Max(1, hi / 2); n <= hi; n++)
            if (ExpectedHalfWidth(yield, n, confidence) < halfWidth) return n;
        return hi;
    }
}
