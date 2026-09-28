// brief-em3d-80 R-em3d80-3 — a Foster network fitted to a computed Z_th(jω):
//
//     Z(jω) ≈ Σᵢ Rᵢ / (1 + jωτᵢ),   Rᵢ ≥ 0,
//
// on a FIXED logarithmic grid of τ — four per decade across the computed band, one decade past each end — by non-negative
// least squares (Lawson & Hanson 1974, ch. 23). Non-negative by construction, so the network is passive and has a positive
// C = τ/R per stage; nothing is placed, so nothing diverges. Each frequency contributes its real and imaginary parts, weighted
// by 1/|Z| (a RELATIVE fit: a channel's microseconds matter as much as a package's seconds). The DC point, when the list has
// one, is weighted 10⁴ times harder, and the terms are rescaled at the end so ΣRᵢ equals it exactly — a change of order 1e-8.
// Terms below 1e-4 of the total are pruned and the rest refitted. The fit is then reweighted toward the minimax one (the
// brief's error is the MAX over the band, which least squares does not minimise) — see Fit.

using System.Numerics;

namespace CircuitRF.Thermal.Frequency;

/// <summary>One stage of a Foster network: R ∥ C with τ = R·C.</summary>
public readonly record struct FosterTerm(double R, double Tau)
{
    /// <summary>The stage's capacitance, J/K.</summary>
    public double C => Tau / R;
}

/// <summary>A fitted Foster network and how well it fits.</summary>
public sealed class FosterNetwork
{
    public required IReadOnlyList<FosterTerm> Terms { get; init; }

    /// <summary>The largest relative |Z_fit − Z| / |Z| over the fitted points.</summary>
    public required double FitError { get; init; }

    /// <summary>ΣRᵢ, K/W: the DC value.</summary>
    public double Rth => Terms.Sum(t => t.R);

    public Complex Z(double frequencyHz)
    {
        double w = 2 * Math.PI * frequencyHz;
        Complex s = 0;
        foreach (var t in Terms) s += t.R / new Complex(1, w * t.Tau);
        return s;
    }
}

public static class FosterFit
{
    /// <summary>τ grid points per decade.</summary>
    public const int PerDecade = 4;

    /// <summary>A term below this fraction of the total is pruned.</summary>
    public const double PruneBelow = 1e-4;

    /// <summary>How much harder the DC point pulls than any other.</summary>
    public const double DcWeight = 1e4;

    /// <summary>Fits <paramref name="z"/> at <paramref name="frequenciesHz"/> (a 0 among them is the DC point). At least one
    /// positive frequency is needed. <paramref name="passes"/> — 1 for the plain relative least-squares fit.</summary>
    public static FosterNetwork Fit(IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex> z, int passes = ReweightPasses)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(z);
        if (frequenciesHz.Count != z.Count) throw new ArgumentException("one value per frequency", nameof(z));
        var positive = frequenciesHz.Where(f => f > 0).ToList();
        if (positive.Count == 0) throw new ArgumentException("no positive frequency", nameof(frequenciesHz));
        double fLo = positive.Min(), fHi = positive.Max();
        double tauLo = 1 / (2 * Math.PI * fHi) / 10, tauHi = 1 / (2 * Math.PI * fLo) * 10;
        int count = Math.Max(1, (int)Math.Ceiling(Math.Log10(tauHi / tauLo) * PerDecade - 1e-9)) + 1;
        var taus = Enumerable.Range(0, count).Select(i => tauLo * Math.Pow(10, (double)i / PerDecade)).ToArray();

        int dc = -1;
        for (int k = 0; k < frequenciesHz.Count; k++) if (frequenciesHz[k] == 0) { dc = k; break; }
        double scale = 0;
        foreach (var v in z) scale = Math.Max(scale, v.Magnitude);
        if (!(scale > 0)) return new FosterNetwork { Terms = [], FitError = 0 };
        double floor = 1e-9 * scale;

        // the rows: [Re; Im] per positive frequency, one row for DC; each frequency's pair shares one weight
        var basis = new List<(double[] A, double B, int Point)>();
        for (int k = 0; k < frequenciesHz.Count; k++)
        {
            double w = 2 * Math.PI * frequenciesHz[k];
            double weight = 1 / Math.Max(z[k].Magnitude, floor);
            if (k == dc)
            {
                basis.Add((Enumerable.Repeat(DcWeight * weight, taus.Length).ToArray(), DcWeight * weight * z[k].Real, -1));
                continue;
            }
            var re = new double[taus.Length];
            var im = new double[taus.Length];
            for (int i = 0; i < taus.Length; i++)
            {
                double wt = w * taus[i], d = 1 + wt * wt;
                re[i] = weight / d;
                im[i] = -weight * wt / d;
            }
            basis.Add((re, weight * z[k].Real, k));
            basis.Add((im, weight * z[k].Imaginary, k));
        }

        // Least squares minimises the RMS error; the brief states the MAX. Lawson's reweighting (1961) moves an L2 fit toward
        // the minimax one on the same grid: each frequency's weight is multiplied by its own share of the error, a few times,
        // and the fit with the smallest largest error is kept. Still NNLS on the fixed grid, so still non-negative.
        var extra = new double[frequenciesHz.Count];
        Array.Fill(extra, 1.0);
        FosterNetwork? best = null;
        for (int pass = 0; pass < Math.Max(1, passes); pass++)
        {
            var rows = basis.Select(r => r.Point < 0 ? (r.A, r.B) : (r.A.Select(a => a * extra[r.Point]).ToArray(), r.B * extra[r.Point])).ToList();
            var fitted = Solve(rows, taus, dc >= 0 ? z[dc].Real : double.NaN);
            var err = new double[frequenciesHz.Count];
            for (int k = 0; k < frequenciesHz.Count; k++)
                err[k] = (fitted.Z(frequenciesHz[k]) - z[k]).Magnitude / Math.Max(z[k].Magnitude, floor);
            var network = new FosterNetwork { Terms = fitted.Terms, FitError = err.Max() };
            if (best is null || network.FitError < best.FitError) best = network;
            double sum = 0;
            for (int k = 0; k < err.Length; k++) if (k != dc) sum += extra[k] * err[k];
            if (!(sum > 0)) break;
            int points = frequenciesHz.Count - (dc >= 0 ? 1 : 0);
            for (int k = 0; k < err.Length; k++) if (k != dc) extra[k] = Math.Max(extra[k] * err[k] * points / sum, 1e-6);
        }
        return best!;
    }

    /// <summary>How many times the fit is reweighted toward the minimax one (the first pass is the plain relative L2 fit).</summary>
    public const int ReweightPasses = 40;

    /// <summary>One NNLS fit over the grid, pruned and refitted, scaled so ΣRᵢ equals <paramref name="dcValue"/> when stated.</summary>
    private static FosterNetwork Solve(List<(double[] A, double B)> rows, double[] taus, double dcValue)
    {
        var active = Enumerable.Range(0, taus.Length).ToList();
        double[] r = Nnls(rows, active);
        double total = r.Sum();
        var kept = active.Where((_, i) => r[i] >= PruneBelow * total && r[i] > 0).ToList();
        if (kept.Count < active.Count && kept.Count > 0)
        {
            var sub = rows.Select(row => (A: kept.Select(i => row.A[i]).ToArray(), row.B)).ToList();
            var rk = Nnls(sub, [.. Enumerable.Range(0, kept.Count)]);
            r = new double[taus.Length];
            for (int i = 0; i < kept.Count; i++) r[kept[i]] = rk[i];
        }
        var terms = new List<FosterTerm>();
        for (int i = 0; i < taus.Length; i++) if (r[i] > 0) terms.Add(new FosterTerm(r[i], taus[i]));
        if (dcValue > 0 && terms.Count > 0)
        {
            double s = dcValue / terms.Sum(t => t.R);
            terms = [.. terms.Select(t => t with { R = t.R * s })];
        }
        return new FosterNetwork { Terms = terms, FitError = double.NaN };
    }

    /// <summary>Lawson–Hanson NNLS: min ‖A·x − b‖ subject to x ≥ 0, over the columns <paramref name="columns"/> of the rows.</summary>
    internal static double[] Nnls(IReadOnlyList<(double[] A, double B)> rows, IReadOnlyList<int> columns)
    {
        int m = rows.Count, n = columns.Count;
        var a = new double[m, n];
        var b = new double[m];
        for (int k = 0; k < m; k++)
        {
            b[k] = rows[k].B;
            for (int j = 0; j < n; j++) a[k, j] = rows[k].A[columns[j]];
        }
        var x = new double[n];
        var passive = new bool[n];
        var w = new double[n];
        double tol = 1e-12;
        for (int outer = 0; outer < 3 * n + 10; outer++)
        {
            // w = Aᵀ(b − Ax)
            var res = Residual(a, b, x);
            Array.Clear(w);
            for (int j = 0; j < n; j++) for (int k = 0; k < m; k++) w[j] += a[k, j] * res[k];
            double wn = 0;
            for (int j = 0; j < n; j++) wn = Math.Max(wn, Math.Abs(w[j]));
            int t = -1;
            double best = tol * Math.Max(wn, 1e-300);
            for (int j = 0; j < n; j++) if (!passive[j] && w[j] > best) { best = w[j]; t = j; }
            if (t < 0) break;
            passive[t] = true;
            for (int inner = 0; inner < 3 * n + 10; inner++)
            {
                var z = LeastSquares(a, b, passive);
                bool feasible = true;
                for (int j = 0; j < n; j++) if (passive[j] && z[j] <= 0) { feasible = false; break; }
                if (feasible) { x = z; break; }
                double alpha = double.PositiveInfinity;
                for (int j = 0; j < n; j++)
                    if (passive[j] && z[j] <= 0) alpha = Math.Min(alpha, x[j] / (x[j] - z[j]));
                for (int j = 0; j < n; j++) x[j] += alpha * (z[j] - x[j]);
                for (int j = 0; j < n; j++) if (passive[j] && x[j] <= 1e-300) { passive[j] = false; x[j] = 0; }
            }
        }
        return x;
    }

    private static double[] Residual(double[,] a, double[] b, double[] x)
    {
        int m = b.Length, n = x.Length;
        var r = new double[m];
        for (int k = 0; k < m; k++)
        {
            double s = b[k];
            for (int j = 0; j < n; j++) s -= a[k, j] * x[j];
            r[k] = s;
        }
        return r;
    }

    /// <summary>The unconstrained least-squares solution over the passive columns (others 0), by Householder QR.</summary>
    private static double[] LeastSquares(double[,] a, double[] b, bool[] passive)
    {
        int m = b.Length, n = passive.Length;
        var cols = Enumerable.Range(0, n).Where(j => passive[j]).ToArray();
        int p = cols.Length;
        var q = new double[m, p];
        for (int k = 0; k < m; k++) for (int j = 0; j < p; j++) q[k, j] = a[k, cols[j]];
        var y = (double[])b.Clone();
        for (int j = 0; j < p; j++)
        {
            double norm = 0;
            for (int k = j; k < m; k++) norm += q[k, j] * q[k, j];
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;
            double alpha = q[j, j] > 0 ? -norm : norm;
            var v = new double[m];
            for (int k = j; k < m; k++) v[k] = q[k, j];
            v[j] -= alpha;
            double vv = 0;
            for (int k = j; k < m; k++) vv += v[k] * v[k];
            if (vv == 0) continue;
            for (int c = j; c < p; c++)
            {
                double s = 0;
                for (int k = j; k < m; k++) s += v[k] * q[k, c];
                s = 2 * s / vv;
                for (int k = j; k < m; k++) q[k, c] -= s * v[k];
            }
            double sy = 0;
            for (int k = j; k < m; k++) sy += v[k] * y[k];
            sy = 2 * sy / vv;
            for (int k = j; k < m; k++) y[k] -= sy * v[k];
        }
        var sol = new double[p];
        for (int j = p - 1; j >= 0; j--)
        {
            double s = y[j];
            for (int c = j + 1; c < p; c++) s -= q[j, c] * sol[c];
            sol[j] = q[j, j] != 0 ? s / q[j, j] : 0;
        }
        var x = new double[n];
        for (int j = 0; j < p; j++) x[cols[j]] = sol[j];
        return x;
    }
}
