using RfCore.Loadpull;

namespace CircuitRF.Engine.Modeling;

/// <summary>
/// Scattered intrinsic points onto the rectilinear grid a table constituent needs (R-mc7-2). Intrinsic
/// points do not lie on a grid ([P1] Fig. 6.5), so a table is built by evaluating a smooth function of the
/// scattered data at grid sites: a trained network (preferred — [P1] §6.2.7), or a radial-basis
/// interpolant through the points themselves.
/// </summary>
public static class Regrid
{
    /// <summary>
    /// Sites from <paramref name="lo"/> at exactly <paramref name="step"/>, up to the first site at or beyond
    /// <paramref name="hi"/> — so the grid covers [lo, hi] and the spacing is the one asked for. At least
    /// two sites.
    /// </summary>
    public static double[] Axis(double lo, double hi, double step)
    {
        if (!(step > 0) || !double.IsFinite(step))
            throw new ArgumentOutOfRangeException(nameof(step), "the grid step is a positive number.");
        if (!(hi >= lo)) throw new ArgumentException("the axis runs from lo up to hi.");
        int n = Math.Max(2, (int)Math.Ceiling((hi - lo) / step - 1e-9) + 1);
        var sites = new double[n];
        for (int k = 0; k < n; k++) sites[k] = lo + k * step;
        return sites;
    }

    /// <summary><paramref name="f"/> at every grid site, row-major over (vgs, vds), vds fastest.</summary>
    public static double[] Through(Func<double, double, double> f, double[] vgs, double[] vds)
    {
        var values = new double[vgs.Length * vds.Length];
        for (int i = 0; i < vgs.Length; i++)
            for (int j = 0; j < vds.Length; j++)
                values[i * vds.Length + j] = f(vgs[i], vds[j]);
        return values;
    }

    /// <summary>
    /// The most points a re-grid the user chose as RBF is built over (brief MC-30 D2). The factor is one dense LDLᵀ:
    /// two N×N matrices (16·N² bytes) and O(N³) time, measured at 0.11 s / 0.9 s / 11 s / 22 s / 43 s / 119 s for
    /// 1k / 2k / 4k / 5k / 6k / 8k points (src/Design/RESOLVED.md, MC-30). 5,000 is where it passes ~30 s.
    /// </summary>
    public const int RbfPointLimit = 5000;

    /// <summary>What the dense RBF system over <paramref name="points"/> holds, in bytes: the kernel matrix and its factor.</summary>
    public static long RbfBytes(int points) => 16L * points * points;

    /// <summary>
    /// <see cref="Rbf"/> for a re-grid the user picked, refused BEFORE any allocation above <see cref="RbfPointLimit"/> —
    /// the one check the Current and Charge fits share. Above it the factor runs for minutes and then, as the points crowd,
    /// fails ill-conditioned; the ANN re-grid is the route for dense data.
    /// </summary>
    public static Func<double, double, double> RbfWithinLimit(IReadOnlyList<(double Vgs, double Vds)> points, IReadOnlyList<double> values,
                                                              RunControl? rc = null)
    {
        if (points.Count > RbfPointLimit)
            throw new RbfTooLargeException(points.Count);
        return Rbf(points, values, rc);
    }

    /// <summary>
    /// A multiquadric interpolant through the scattered points, each coordinate mapped onto [0, 1] over the
    /// points first so a volt of vgs and a volt of vds weigh what their spans make them weigh. Exact at the
    /// points (no smoothing). <paramref name="rc"/> cancels the factor, checked once per column.
    /// </summary>
    public static Func<double, double, double> Rbf(IReadOnlyList<(double Vgs, double Vds)> points, IReadOnlyList<double> values,
                                                   RunControl? rc = null)
    {
        if (points.Count != values.Count || points.Count < 3)
            throw new ArgumentException("a radial-basis interpolant needs at least three points, one value each.");
        double gLo = points.Min(p => p.Vgs), gHi = points.Max(p => p.Vgs);
        double dLo = points.Min(p => p.Vds), dHi = points.Max(p => p.Vds);
        double gs = gHi > gLo ? 1 / (gHi - gLo) : 1, ds = dHi > dLo ? 1 / (dHi - dLo) : 1;
        var x = points.Select(p => (p.Vgs - gLo) * gs).ToArray();
        var y = points.Select(p => (p.Vds - dLo) * ds).ToArray();
        var rbf = new Rbf2D(x, y, values.ToArray(), RbfKernel.Multiquadric, smooth: 0.0, cancel: rc?.Token ?? default);
        return (vgs, vds) => rbf.Evaluate((vgs - gLo) * gs, (vds - dLo) * ds);
    }
}

/// <summary>An RBF re-grid refused for its size, before anything was allocated (brief MC-30 D2).</summary>
public sealed class RbfTooLargeException(int points)
    : ArgumentException(
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"RBF re-grid builds one dense system over {points:N0} points ({Regrid.RbfBytes(points) / 1e9:0.0} GB); " +
            $"it is meant for a few thousand (at most {Regrid.RbfPointLimit:N0}). Re-grid through the ANN."))
{
    public int Points { get; } = points;
}
