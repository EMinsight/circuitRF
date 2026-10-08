using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>What one surrogate coordinate stands for.</summary>
public enum SurrogateDimensionKind { Entry, Process, Mismatch }

/// <summary>One coordinate of the surrogate's z-space (brief-yield-12 R-ya12-2): a statistical entry, a kit process
/// stream, or all of one instance's kit mismatch streams together.</summary>
/// <param name="Name">The entry's key, the process stream, or the instance.</param>
/// <param name="Streams">The streams it moves — one, or an instance's mismatch streams, each by x/√n.</param>
public sealed record SurrogateDimension(string Name, SurrogateDimensionKind Kind, IReadOnlyList<string> Streams);

/// <summary>One quantity fitted as a quadratic in z: 1, z_i, z_i², and (full) z_i·z_j for i &lt; j.</summary>
public sealed class QuadraticModel
{
    internal QuadraticModel(double[] coefficients, int k, bool full, double rSquared)
    {
        Coefficients = coefficients;
        Dimensions = k;
        Full = full;
        RSquared = rSquared;
    }

    public double[] Coefficients { get; }

    public int Dimensions { get; }

    /// <summary>Whether the cross terms are fitted.</summary>
    public bool Full { get; }

    /// <summary>The fraction of the fitted points' variance the quadratic explains.</summary>
    public double RSquared { get; }

    public double Predict(ReadOnlySpan<double> z)
    {
        var c = Coefficients;
        int k = Dimensions, n = 0;
        double y = c[n++];
        for (int i = 0; i < k; i++) y += c[n++] * z[i];
        for (int i = 0; i < k; i++) y += c[n++] * z[i] * z[i];
        if (Full)
            for (int i = 0; i < k; i++)
                for (int j = i + 1; j < k; j++) y += c[n++] * z[i] * z[j];
        return y;
    }
}

/// <summary>A candidate scored on the surrogate's virtual trials.</summary>
/// <param name="Objective">The smooth objective, as <see cref="CenteringRun"/> computes it on simulated trials.</param>
public sealed record SurrogateScore(double Objective, int Passes, int Trials);

/// <summary>
/// The quadratic surrogate of design centering (brief-yield-12, docs/design/yield.md §16). For a candidate nominal,
/// <see cref="Design"/> places 2k + 1 points in z-space — the centre and ±δ on each coordinate — plus, when
/// k ≤ <see cref="FullQuadraticLimit"/>, one cross point (+δ, +δ) per pair, so a full quadratic is determined;
/// beyond that the fit is diagonal (no cross terms) and the report says so. <see cref="CheckTrials"/> of the common
/// trials are simulated too, so the least-squares fit is over-determined and its R² measures how quadratic the margin
/// really is, rather than being 1 by construction. Each yield goal is fitted separately; the smooth objective and the
/// yield are then counted on <see cref="VirtualTrials"/> virtual trials of the fits.
///
/// <para><b>What is fitted.</b> A goal on a single number is fitted on its VALUE, and its margin computed from that by
/// the goal's own rule (<see cref="GoalResiduals.Score"/>) — a window's margin has a kink at the window's centre that no
/// quadratic follows, while the value it is measured on is smooth. A goal over a sweep is fitted on its margin.</para>
///
/// <para><b>A search accelerator, never the answer</b> (R-ya12-3): <see cref="CenteringRun"/> still verifies the start
/// and the best point by simulation, and that is the yield it reports.</para>
/// </summary>
public static class QuadraticSurrogate
{
    /// <summary>Up to this many coordinates the cross terms are fitted.</summary>
    public const int FullQuadraticLimit = 12;

    /// <summary>The virtual trials a candidate's yield is counted on.</summary>
    public const int VirtualTrials = 10_000;

    /// <summary>The design's step in z.</summary>
    public const double Delta = 1;

    /// <summary>An R² below this on any goal is a poor fit — a warning on that iteration.</summary>
    public const double PoorFit = 0.9;

    /// <summary>This many poor iterations running switch the run back to simulated trials.</summary>
    public const int PoorFitsBeforeSwitch = 3;

    public static bool IsFull(int k) => k <= FullQuadraticLimit;

    /// <summary>The quadratic's coefficients for <paramref name="k"/> coordinates.</summary>
    public static int Terms(int k) => 1 + 2 * k + (IsFull(k) ? k * (k - 1) / 2 : 0);

    /// <summary>The common trials simulated beside the design so the fit is over-determined.</summary>
    public static int CheckTrials(int k) => k + 2;

    /// <summary>Simulations per candidate: the design plus the check trials.</summary>
    public static int PointsPerCandidate(int k) => Design(k).Count + CheckTrials(k);

    /// <summary>The design's points: the centre, ±δ on each coordinate, then (+δ, +δ) on each pair when full.
    /// The centre and the axial points are <see cref="DoeDesigns.CentreAndAxial"/>, the construction design of
    /// experiments' face-centred composite shares (brief-yield-14 R-ya14-2).</summary>
    public static IReadOnlyList<double[]> Design(int k)
    {
        var points = DoeDesigns.CentreAndAxial(k, Delta);
        if (IsFull(k))
            for (int i = 0; i < k; i++)
                for (int j = i + 1; j < k; j++)
                {
                    var both = new double[k]; both[i] = Delta; both[j] = Delta;
                    points.Add(both);
                }
        return points;
    }

    /// <summary>Fits <paramref name="y"/> at <paramref name="z"/> as a quadratic — full when <see cref="IsFull"/>.</summary>
    public static QuadraticModel Fit(IReadOnlyList<double[]> z, IReadOnlyList<double> y)
    {
        int k = z[0].Length;
        bool full = IsFull(k);
        var rows = z.Select(p => Basis(p, full)).ToList();
        var fit = LeastSquares.Fit(rows, y);
        return new QuadraticModel(fit.Coefficients, k, full, fit.RSquared);
    }

    private static double[] Basis(double[] z, bool full)
    {
        int k = z.Length;
        var row = new double[1 + 2 * k + (full ? k * (k - 1) / 2 : 0)];
        int n = 0;
        row[n++] = 1;
        for (int i = 0; i < k; i++) row[n++] = z[i];
        for (int i = 0; i < k; i++) row[n++] = z[i] * z[i];
        if (full)
            for (int i = 0; i < k; i++)
                for (int j = i + 1; j < k; j++) row[n++] = z[i] * z[j];
        return row;
    }

    /// <summary>R-ya12-3: the goals whose fit is poor — R² below <see cref="PoorFit"/> — each a warning on its
    /// iteration.</summary>
    public static IReadOnlyList<KeyValuePair<string, double>> PoorFits(IReadOnlyDictionary<string, double> rSquared)
        => [.. rSquared.Where(kv => kv.Value < PoorFit)];

    /// <summary>A goal on a single number is fitted on its value (see the class remarks).</summary>
    public static bool FitsValue(GoalScore s) => s.Axis is null && s.Residuals.Length == 1;

    /// <summary>The quantity fitted for a goal at one simulated point — its value, or its margin.</summary>
    public static double Fitted(GoalScore s, bool value) => value ? s.WorstValue : s.Margin;

    /// <summary>
    /// The margin a fitted quantity means: a margin as it stands, a value through the goal's own rule. For a goal with
    /// a passing side (<see cref="GoalResiduals.ValueLimits"/>: ge, le, in) that rule is the distance inside the
    /// limits, read once rather than re-parsed per virtual trial; eq and out are scored by
    /// <see cref="GoalResiduals.Score"/> itself.
    /// </summary>
    public static Func<double, double> MarginRule(OptimizationGoal goal, bool value)
    {
        if (!value) return m => m;
        var (lo, up) = GoalResiduals.ValueLimits(goal);
        if (lo is null && up is null) return x => GoalResiduals.Score(goal, new Value(x), null).Margin;
        double l = lo ?? double.NegativeInfinity, u = up ?? double.PositiveInfinity;
        return x => Math.Min(x - l, u - x);
    }

    /// <summary>
    /// The candidate on the virtual trials: per trial, each goal's margin from its model, the smallest of them over the
    /// goal scales through the logistic of width <paramref name="width"/> (CenteringRun's own smooth objective), and a
    /// pass when every margin is at least 0.
    /// </summary>
    public static SurrogateScore Score(IReadOnlyList<QuadraticModel> models, IReadOnlyList<Func<double, double>> marginOf,
                                       IReadOnlyList<double> scales, double width, IReadOnlyList<double[]> virtualZ)
    {
        double sum = 0;
        int passes = 0;
        foreach (var z in virtualZ)
        {
            double m = double.PositiveInfinity;
            bool pass = true;
            for (int g = 0; g < models.Count; g++)
            {
                double margin = marginOf[g](models[g].Predict(z));
                if (double.IsNaN(margin)) margin = double.NegativeInfinity;
                if (margin < 0) pass = false;
                m = Math.Min(m, margin / scales[g]);
            }
            sum += 1 / (1 + Math.Exp(-m / width));
            if (pass) passes++;
        }
        return new SurrogateScore(virtualZ.Count == 0 ? double.NaN : sum / virtualZ.Count, passes, virtualZ.Count);
    }
}
