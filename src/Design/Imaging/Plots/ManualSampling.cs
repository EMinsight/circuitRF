// A manual curve sampled into x, y pairs — brief-pdg-4-digitise-by-hand.md R-pdg4-3.
//
// IN THE AXES' OWN COORDINATES. Between fixpoints the curve runs straight (Linear) or as a monotone cubic (Smooth) in
// the coordinates the picture draws it in: log10 x on a log x axis, log10 y on a log y axis. A straight segment on a log
// plot is a power law, and a straight line drawn between two points on it is what the user saw.
//
// SMOOTH NEVER OVERSHOOTS. It is the monotone piecewise cubic of Fritsch and Carlson (PCHIP's slopes): between two
// fixpoints it stays between their two values, and a flat run stays flat. A natural cubic spline would ring past a step
// the user placed, inventing a value the picture never showed.
//
// NOTHING IS EXTRAPOLATED. A grid point before the first fixpoint or after the last is dropped, and how many were is
// returned so the report can say so.

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A curve sampled: the grid's x values kept, the curve's y at each, and how many grid points lay beyond the
/// fixpoints.</summary>
public sealed record ManualSampled(IReadOnlyList<double> X, IReadOnlyList<double> Y, int Dropped);

public static class ManualSampling
{
    /// <summary>The fixpoints (axis values, any order) sampled on <paramref name="spec"/>'s grid. Fixpoints sharing an x
    /// are one, at their mean y. Pure.</summary>
    public static ManualSampled Sample(IReadOnlyList<(double X, double Y)> fixpoints, ManualSampleSpec spec, bool xLog, bool yLog)
    {
        ArgumentNullException.ThrowIfNull(fixpoints);
        ArgumentNullException.ThrowIfNull(spec);
        var (u, w) = Prepare(fixpoints, xLog, yLog);
        if (u.Length == 0) return new ManualSampled([], [], 0);
        double first = Back(u[0], xLog), last = Back(u[^1], xLog);
        double start = spec.Start ?? first, stop = spec.Stop ?? last;
        bool log = spec.Log ?? xLog;
        var grid = Grid(start, stop, spec.Points, spec.Step, log);
        var xs = new List<double>();
        var ys = new List<double>();
        int dropped = 0;
        double slack = 1e-9 * Math.Max(Math.Abs(first), Math.Abs(last));
        foreach (double x in grid)
        {
            if (x < first - slack || x > last + slack || (xLog && !(x > 0))) { dropped++; continue; }
            xs.Add(x);
            ys.Add(Back(Evaluate(u, w, Math.Clamp(Forward(x, xLog), u[0], u[^1]), spec.Method), yLog));
        }
        return new ManualSampled(xs, ys, dropped);
    }

    /// <summary>The curve through <paramref name="fixpoints"/> at each of <paramref name="xs"/> — NaN outside the
    /// fixpoints' range.</summary>
    public static double[] At(IReadOnlyList<(double X, double Y)> fixpoints, IReadOnlyList<double> xs, ManualSampleMethod method, bool xLog, bool yLog)
    {
        var (u, w) = Prepare(fixpoints, xLog, yLog);
        var y = new double[xs.Count];
        for (int i = 0; i < xs.Count; i++)
        {
            double at = Forward(xs[i], xLog);
            y[i] = u.Length == 0 || !double.IsFinite(at) || at < u[0] || at > u[^1] ? double.NaN : Back(Evaluate(u, w, at, method), yLog);
        }
        return y;
    }

    /// <summary>The grid: <paramref name="points"/> from start to stop (log-spaced when <paramref name="log"/>), or every
    /// <paramref name="step"/> from start up to stop.</summary>
    public static IReadOnlyList<double> Grid(double start, double stop, int points, double? step, bool log)
    {
        if (!double.IsFinite(start) || !double.IsFinite(stop)) return [];
        if (step is { } s && s > 0 && double.IsFinite(s))
        {
            var g = new List<double>();
            int n = (int)Math.Floor((stop - start) / s + 1e-9);
            for (int k = 0; k <= Math.Min(n, 1_000_000); k++) g.Add(start + k * s);
            return g;
        }
        if (points < 1) return [];
        if (points == 1 || start == stop) return [start];
        if (log && start > 0 && stop > 0) return new AssemblyGrid(start, stop, points, true).Frequencies();
        return new AssemblyGrid(start, stop, points, false).Frequencies();
    }

    private static double Forward(double v, bool log) => log ? (v > 0 ? Math.Log10(v) : double.NaN) : v;

    private static double Back(double v, bool log) => log ? Math.Pow(10, v) : v;

    /// <summary>The fixpoints in drawn coordinates, sorted by x, one per x.</summary>
    private static (double[] U, double[] W) Prepare(IReadOnlyList<(double X, double Y)> fixpoints, bool xLog, bool yLog)
    {
        var pts = fixpoints.Select(p => (U: Forward(p.X, xLog), W: Forward(p.Y, yLog)))
                           .Where(p => double.IsFinite(p.U) && double.IsFinite(p.W))
                           .GroupBy(p => p.U).Select(g => (U: g.Key, W: g.Average(p => p.W)))
                           .OrderBy(p => p.U).ToList();
        return ([.. pts.Select(p => p.U)], [.. pts.Select(p => p.W)]);
    }

    private static double Evaluate(double[] u, double[] w, double at, ManualSampleMethod method)
    {
        int n = u.Length;
        if (n == 1) return w[0];
        int k = Array.BinarySearch(u, at);
        if (k >= 0) return w[k];
        k = Math.Clamp(~k - 1, 0, n - 2);
        double h = u[k + 1] - u[k], t = (at - u[k]) / h;
        if (method == ManualSampleMethod.Linear) return w[k] + t * (w[k + 1] - w[k]);
        var d = Slopes(u, w);
        double t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * w[k] + (t3 - 2 * t2 + t) * h * d[k] + (-2 * t3 + 3 * t2) * w[k + 1] + (t3 - t2) * h * d[k + 1];
    }

    /// <summary>The monotone cubic's slope at each fixpoint (Fritsch–Carlson; PCHIP's weighted harmonic mean inside, its
    /// shape-preserving three-point formula at the ends): zero at a local extremum and on a flat run, so nothing rings.</summary>
    internal static double[] Slopes(double[] u, double[] w)
    {
        int n = u.Length;
        var d = new double[n];
        if (n < 2) return d;
        var h = new double[n - 1];
        var delta = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            h[k] = u[k + 1] - u[k];
            delta[k] = (w[k + 1] - w[k]) / h[k];
        }
        if (n == 2) { d[0] = d[1] = delta[0]; return d; }
        for (int k = 1; k < n - 1; k++)
        {
            if (delta[k - 1] * delta[k] <= 0) continue;
            double w1 = 2 * h[k] + h[k - 1], w2 = h[k] + 2 * h[k - 1];
            d[k] = (w1 + w2) / (w1 / delta[k - 1] + w2 / delta[k]);
        }
        d[0] = End(h[0], h[1], delta[0], delta[1]);
        d[n - 1] = End(h[n - 2], h[n - 3], delta[n - 2], delta[n - 3]);
        return d;
    }

    private static double End(double h0, double h1, double d0, double d1)
    {
        double d = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
        if (Math.Sign(d) != Math.Sign(d0)) return 0;
        if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(d) > Math.Abs(3 * d0)) return 3 * d0;
        return d;
    }
}
