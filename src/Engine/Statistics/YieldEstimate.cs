namespace CircuitRF.Engine.Statistics;

/// <summary>What auto-stop decided after a trial count (yield overview D8).</summary>
public enum AutoStopVerdict
{
    /// <summary>The interval still straddles the target, or too few trials are counted — keep going.</summary>
    Continue,
    /// <summary>The interval's lower bound is at or above the target.</summary>
    Above,
    /// <summary>The interval's upper bound is below the target.</summary>
    Below,
}

/// <summary>
/// A yield and its Clopper–Pearson interval (yield overview D8): passes of counted trials at a confidence.
/// </summary>
public readonly record struct YieldEstimate(int Passes, int Counted, double Yield, double Lower, double Upper)
{
    /// <summary>No trial counted: yield and interval are NaN.</summary>
    public static YieldEstimate None => new(0, 0, double.NaN, double.NaN, double.NaN);

    /// <param name="confidence">A fraction, 0.95.</param>
    public static YieldEstimate Of(int passes, int counted, double confidence)
    {
        if (counted <= 0) return None;
        var (lo, hi) = ClopperPearson.Interval(passes, counted, confidence);
        return new YieldEstimate(passes, counted, (double)passes / counted, lo, hi);
    }

    /// <summary>The fewest counted trials auto-stop ever decides on.</summary>
    public const int AutoStopMinimum = 50;

    /// <summary>
    /// Auto-stop's rule: with at least <see cref="AutoStopMinimum"/> counted trials, <see cref="AutoStopVerdict.Above"/>
    /// when the lower bound is at or above <paramref name="target"/> (a fraction), <see cref="AutoStopVerdict.Below"/>
    /// when the upper bound is below it.
    /// </summary>
    public AutoStopVerdict Decide(double target)
    {
        if (Counted < AutoStopMinimum) return AutoStopVerdict.Continue;
        if (Lower >= target) return AutoStopVerdict.Above;
        if (Upper < target) return AutoStopVerdict.Below;
        return AutoStopVerdict.Continue;
    }
}
