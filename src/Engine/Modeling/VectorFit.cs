// Vector fitting (brief-modelcomposer-12 R-mc12-2; overview D14): a rational model with poles shared by every
// response, F_m(s) ≈ Σ_k R_mk/(s − a_k) + D_m + s·E_m, from frequency samples — the published method of
// B. Gustavsen and A. Semlyen, "Rational approximation of frequency domain responses by vector fitting", IEEE Trans.
// Power Delivery 14(3), 1999, written here from the paper.
//
// Numerics only — no domain types. One iteration:
//   1. With the poles fixed, the residues c̃ of the shared weight σ(s) = 1 + Σ c̃_k/(s − a_k) solve the linear least
//      squares Σ c_k φ_k + d + s·e − F·Σ c̃_k φ_k = F at every sample, every response's own residues, constant and
//      proportional term eliminated by a QR of that response's rows (the fast formulation of the same problem).
//   2. The zeros of σ are the new poles. They are the roots of the secular equation σ(z) = 0, found by an Aberth
//      iteration on p(z) = σ(z)·Π(z − a_k) written so that no term of size 1/|z − a_k| is ever cancelled (below) —
//      which is why this needs no general eigensolver (the repository has none; src/Engine/Mom/Dcim.cs gives why).
//   3. An unstable pole (Re > 0) is reflected into the left half-plane.
// After the last iteration the residues are solved once more with the poles fixed.
//
// REAL ARITHMETIC. A complex pole travels with its conjugate, and the pair's basis functions are
// φ1 = 1/(s − a) + 1/(s − a*) and φ2 = j/(s − a) − j/(s − a*) with real coefficients, so residues come in conjugate
// pairs and the model is real in the time domain by construction. Frequencies are scaled by the top angular
// frequency before anything is solved, so a 1 GHz sweep does not hand the least squares columns 10¹⁰ apart.

using System.Numerics;

namespace CircuitRF.Engine.Modeling;

/// <summary>
/// A rational model sharing its poles across responses: <c>F_m(s) = Σ_k R[m][k]/(s − a_k) + D[m] + s·E[m]</c>, in
/// rad/s. Complex poles appear as conjugate pairs, the member with positive imaginary part first.
/// </summary>
public sealed class RationalModel
{
    public required Complex[] Poles { get; init; }
    /// <summary>[response][pole].</summary>
    public required Complex[][] Residues { get; init; }
    public required double[] D { get; init; }
    public required double[] E { get; init; }

    public int Responses => Residues.Length;

    public Complex Evaluate(int response, Complex s)
    {
        var r = Residues[response];
        Complex sum = D[response] + s * E[response];
        for (int k = 0; k < Poles.Length; k++) sum += r[k] / (s - Poles[k]);
        return sum;
    }

    public Complex EvaluateAtHz(int response, double hz) => Evaluate(response, new Complex(0, 2 * Math.PI * hz));
}

/// <summary>How a fit is run.</summary>
public sealed record VectorFitSettings
{
    /// <summary>Pole-relocation iterations. Exact rational data converges in a handful.</summary>
    public int Iterations { get; init; } = 15;
    /// <summary>Include the constant term <c>D</c>.</summary>
    public bool Constant { get; init; } = true;
    /// <summary>Include the proportional term <c>s·E</c> — a capacitor's admittance, an inductor's impedance.</summary>
    public bool Proportional { get; init; } = true;
    /// <summary>Stop relocating once no pole moves by more than this, relative to the top angular frequency.</summary>
    public double PoleTolerance { get; init; } = 1e-13;

    public static VectorFitSettings Default { get; } = new();
}

/// <summary>A fit: the model, its order and its error over the samples.</summary>
/// <param name="RmsError">√(mean over responses and samples of |F_fit − F|²).</param>
/// <param name="RelativeError"><see cref="RmsError"/> divided by the RMS of |F|.</param>
public sealed record VectorFitResult(RationalModel Model, int Order, double RmsError, double RelativeError, int Iterations);

public static class VectorFit
{
    /// <summary>
    /// Fits <paramref name="responses"/> (<c>[response][sample]</c>, at <paramref name="frequenciesHz"/>) with
    /// <paramref name="order"/> shared poles. <paramref name="weights"/> (per sample, null uniform) weight the residual.
    /// </summary>
    public static VectorFitResult Fit(double[] frequenciesHz, IReadOnlyList<Complex[]> responses, int order,
                                      VectorFitSettings? settings = null, double[]? weights = null)
    {
        settings ??= VectorFitSettings.Default;
        int nf = frequenciesHz.Length, nr = responses.Count;
        if (order < 1) throw new ArgumentOutOfRangeException(nameof(order), "The order is at least 1.");
        if (nf < 2 || nr < 1) throw new ArgumentException("A fit needs at least two samples of one response.");
        if (order > MaxOrder(nf, settings))
            throw new ArgumentOutOfRangeException(nameof(order), $"{nf} samples determine an order of at most {MaxOrder(nf, settings)}.");
        foreach (var r in responses)
            if (r.Length != nf) throw new ArgumentException("Every response has one value per frequency.");
        double fMax = frequenciesHz.Max();
        if (!(frequenciesHz.Min() >= 0) || !(fMax > 0)) throw new ArgumentException("Frequencies are positive (a DC sample is allowed).");
        // A DC sample is data like any other (a Touchstone file often starts at 0 Hz); only the starting poles need
        // the lowest POSITIVE frequency, for their logarithmic spread.
        double fMin = frequenciesHz.Where(f => f > 0).Min();

        double ws = 2 * Math.PI * fMax;                     // the scale every angular frequency is divided by
        var s = frequenciesHz.Select(f => new Complex(0, f / fMax)).ToArray();
        double[] w = weights ?? [.. Enumerable.Repeat(1.0, nf)];
        var poles = StartingPoles(fMin / fMax, 1.0, order);

        int it = 0;
        for (; it < settings.Iterations; it++)
        {
            var moved = Relocate(s, responses, w, poles, settings);
            double shift = 0;
            for (int k = 0; k < poles.Count; k++) shift = Math.Max(shift, (moved[k] - poles[k]).Magnitude);
            poles = moved;
            if (shift < settings.PoleTolerance) { it++; break; }
        }

        var (res, d, e) = Residues(s, responses, w, poles, settings);
        var model = new RationalModel
        {
            Poles = [.. poles.Select(p => p * ws)],
            Residues = [.. res.Select(row => row.Select(c => c * ws).ToArray())],
            D = d,
            E = [.. e.Select(x => x / ws)],
        };
        double sq = 0, ref2 = 0;
        for (int m = 0; m < nr; m++)
            for (int k = 0; k < nf; k++)
            {
                var fit = model.EvaluateAtHz(m, frequenciesHz[k]);
                sq += (fit - responses[m][k]).Magnitude * (fit - responses[m][k]).Magnitude;
                ref2 += responses[m][k].Magnitude * responses[m][k].Magnitude;
            }
        double rms = Math.Sqrt(sq / (nr * nf));
        double rel = ref2 > 0 ? Math.Sqrt(sq / ref2) : double.PositiveInfinity;
        return new VectorFitResult(model, order, rms, rel, it);
    }

    /// <summary>
    /// The lowest order from 1 to <paramref name="maxOrder"/> whose relative error is at most
    /// <paramref name="tolerance"/>; when none reaches it, the order with the smallest error.
    /// </summary>
    public static VectorFitResult FitByError(double[] frequenciesHz, IReadOnlyList<Complex[]> responses, double tolerance,
                                             int maxOrder, VectorFitSettings? settings = null, double[]? weights = null,
                                             RunControl? rc = null)
    {
        VectorFitResult? best = null;
        maxOrder = Math.Min(maxOrder, MaxOrder(frequenciesHz.Length, settings ?? VectorFitSettings.Default));
        rc?.BeginStage("Vector fit", Math.Max(maxOrder, 0), "order(s)");
        for (int n = 1; n <= maxOrder; n++)
        {
            rc?.ThrowIfCancellationRequested();
            VectorFitResult r;
            try { r = Fit(frequenciesHz, responses, n, settings, weights); }
            catch (ArithmeticException) { rc?.TickStage(); continue; }
            rc?.TickStage();
            if (best is null || r.RelativeError < best.RelativeError) best = r;
            if (r.RelativeError <= tolerance) return r;
        }
        return best ?? throw new ArithmeticException("No order could be fitted.");
    }

    /// <summary>
    /// The highest order <paramref name="samples"/> frequencies determine: a relocation's least squares for one response
    /// has two real rows per sample and 2n + (constant) + (proportional) unknowns, and must not be under-determined.
    /// </summary>
    public static int MaxOrder(int samples, VectorFitSettings settings) =>
        (2 * samples - (settings.Constant ? 1 : 0) - (settings.Proportional ? 1 : 0)) / 2;

    // ── Poles ─────────────────────────────────────────────────────────────────

    /// <summary>Gustavsen's start: conjugate pairs −β/100 ± jβ over the band (logarithmic when it spans more than a
    /// decade), and one real pole for an odd order.</summary>
    internal static List<Complex> StartingPoles(double lo, double hi, int order)
    {
        var p = new List<Complex>();
        int pairs = order / 2;
        bool log = hi / lo > 10;
        for (int k = 0; k < pairs; k++)
        {
            double t = pairs == 1 ? 0.5 : (double)k / (pairs - 1);
            double beta = log ? lo * Math.Pow(hi / lo, t) : lo + t * (hi - lo);
            p.Add(new Complex(-beta / 100, beta));
            p.Add(new Complex(-beta / 100, -beta));
        }
        if (order % 2 == 1) p.Add(new Complex(-(lo + hi) / 2, 0));
        return p;
    }

    /// <summary>The pole groups: each real pole alone, each pair by its upper member.</summary>
    private static List<(int Index, bool Pair)> Groups(List<Complex> poles)
    {
        var g = new List<(int, bool)>();
        for (int k = 0; k < poles.Count; k++)
        {
            if (poles[k].Imaginary == 0) g.Add((k, false));
            else { g.Add((k, true)); k++; }
        }
        return g;
    }

    /// <summary>The real basis at one sample: one column per real pole, two per pair.</summary>
    private static Complex[] Basis(Complex s, List<Complex> poles, List<(int Index, bool Pair)> groups)
    {
        var phi = new Complex[poles.Count];
        int c = 0;
        foreach (var (k, pair) in groups)
        {
            var a = poles[k];
            if (!pair) { phi[c++] = 1 / (s - a); continue; }
            Complex u = 1 / (s - a), v = 1 / (s - Complex.Conjugate(a));
            phi[c++] = u + v;
            phi[c++] = Complex.ImaginaryOne * (u - v);
        }
        return phi;
    }

    /// <summary>The complex residues from the real coefficients: a pair's (c1, c2) is c1 + jc2 on the upper pole and
    /// its conjugate on the lower.</summary>
    private static Complex[] ToResidues(double[] coef, int offset, List<(int Index, bool Pair)> groups, int n)
    {
        var r = new Complex[n];
        int c = offset;
        foreach (var (k, pair) in groups)
        {
            if (!pair) { r[k] = coef[c++]; continue; }
            var z = new Complex(coef[c], coef[c + 1]);
            c += 2;
            r[k] = z;
            r[k + 1] = Complex.Conjugate(z);
        }
        return r;
    }

    /// <summary>
    /// One relocation. Each response's rows <c>[Φ, 1, s | −F·Φ]</c> are triangularised on their own and only the rows of
    /// R that the response's own unknowns do not reach are kept — the σ block, n rows per response — and those are
    /// stacked into one small least squares for σ's residues (the fast formulation; the full stacked system has the
    /// same solution and costs (responses)² as much).
    /// </summary>
    private static List<Complex> Relocate(Complex[] s, IReadOnlyList<Complex[]> F, double[] w, List<Complex> poles,
                                          VectorFitSettings settings)
    {
        int n = poles.Count, nf = s.Length, nr = F.Count;
        var groups = Groups(poles);
        int own = n + (settings.Constant ? 1 : 0) + (settings.Proportional ? 1 : 0);
        var phis = s.Select(x => Basis(x, poles, groups)).ToArray();
        var big = new double[nr * n, n];
        var rhs = new double[nr * n];
        for (int m = 0; m < nr; m++)
        {
            var a = new double[2 * nf, own + n];
            var b = new double[2 * nf];
            for (int k = 0; k < nf; k++)
            {
                int re = 2 * k, im = re + 1;
                Complex f = F[m][k];
                for (int j = 0; j < n; j++)
                {
                    a[re, j] = w[k] * phis[k][j].Real;
                    a[im, j] = w[k] * phis[k][j].Imaginary;
                    var t = -f * phis[k][j];
                    a[re, own + j] = w[k] * t.Real;
                    a[im, own + j] = w[k] * t.Imaginary;
                }
                int c = n;
                if (settings.Constant) { a[re, c] = w[k]; c++; }
                if (settings.Proportional) a[im, c] = w[k] * s[k].Imaginary;
                b[re] = w[k] * f.Real;
                b[im] = w[k] * f.Imaginary;
            }
            LeastSquares.Triangularise(a, b);
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) big[m * n + i, j] = i <= j ? a[own + i, own + j] : 0;
                rhs[m * n + i] = b[own + i];
            }
        }
        var x = LeastSquares.Solve(big, rhs);
        var sigma = ToResidues(x, 0, groups, n);
        var zeros = SecularRoots(poles, sigma);
        return Normalise(zeros);
    }

    private static (Complex[][] Res, double[] D, double[] E) Residues(Complex[] s, IReadOnlyList<Complex[]> F, double[] w,
                                                                       List<Complex> poles, VectorFitSettings settings)
    {
        int n = poles.Count, nf = s.Length;
        var groups = Groups(poles);
        int per = n + (settings.Constant ? 1 : 0) + (settings.Proportional ? 1 : 0);
        var phis = s.Select(x => Basis(x, poles, groups)).ToArray();
        var res = new Complex[F.Count][];
        var d = new double[F.Count];
        var e = new double[F.Count];
        for (int m = 0; m < F.Count; m++)
        {
            var a = new double[2 * nf, per];
            var b = new double[2 * nf];
            for (int k = 0; k < nf; k++)
            {
                for (int j = 0; j < n; j++)
                {
                    a[2 * k, j] = w[k] * phis[k][j].Real;
                    a[2 * k + 1, j] = w[k] * phis[k][j].Imaginary;
                }
                int c = n;
                if (settings.Constant) { a[2 * k, c] = w[k]; c++; }
                if (settings.Proportional) a[2 * k + 1, c] = w[k] * s[k].Imaginary;
                b[2 * k] = w[k] * F[m][k].Real;
                b[2 * k + 1] = w[k] * F[m][k].Imaginary;
            }
            var x = LeastSquares.Solve(a, b);
            res[m] = ToResidues(x, 0, groups, n);
            int at = n;
            if (settings.Constant) d[m] = x[at++];
            if (settings.Proportional) e[m] = x[at];
        }
        return (res, d, e);
    }

    /// <summary>
    /// The roots of <c>σ(z) = 1 + Σ r_k/(z − a_k)</c>: an Aberth iteration on <c>p(z) = σ(z)·Π(z − a_k)</c>.
    /// <c>p′/p = σ′/σ + Σ 1/(z − a_k)</c>, and near pole <c>a_i</c> the two terms of size <c>1/|z − a_i|</c> cancel —
    /// exactly where a converging fit puts every root. They are combined analytically:
    /// <c>σ′/σ + 1/(z − a_i) = (1 + ρ + ρ′·(z − a_i)) / ((z − a_i)(1 + ρ) + r_i)</c>, with ρ the sum over the other
    /// poles, so the only small quantity left is the denominator, computed without cancellation of large terms.
    /// </summary>
    internal static Complex[] SecularRoots(List<Complex> poles, Complex[] r)
    {
        int n = poles.Count;
        double scale = Math.Max(poles.Max(p => p.Magnitude), 1e-300);
        var z = new Complex[n];
        for (int k = 0; k < n; k++)
            z[k] = poles[k] + scale * new Complex(1e-3 * (k % 3 + 1), 1.3e-3 * ((k + 1) % 4 + 1));

        for (int iter = 0; iter < 500; iter++)
        {
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                var ratio = NewtonRatio(z[i], poles, r);      // p/p′
                if (ratio == Complex.Zero) continue;
                Complex rep = 0;
                for (int j = 0; j < n; j++) if (j != i) rep += 1 / (z[i] - z[j]);
                var step = ratio / (1 - ratio * rep);
                if (!double.IsFinite(step.Real) || !double.IsFinite(step.Imaginary)) continue;
                z[i] -= step;
                worst = Math.Max(worst, step.Magnitude);
            }
            if (worst <= 1e-16 * scale) break;
        }
        return z;
    }

    /// <summary><c>p(z)/p′(z)</c> for <c>p = σ·Π(z − a_k)</c>, cancellation-free about the nearest pole.</summary>
    private static Complex NewtonRatio(Complex z, List<Complex> poles, Complex[] r)
    {
        int n = poles.Count, near = 0;
        for (int k = 1; k < n; k++)
            if ((z - poles[k]).Magnitude < (z - poles[near]).Magnitude) near = k;
        Complex rho = 0, drho = 0, others = 0;
        for (int k = 0; k < n; k++)
        {
            if (k == near) continue;
            var d = 1 / (z - poles[k]);
            rho += r[k] * d;
            drho -= r[k] * d * d;
            others += d;
        }
        var dz = z - poles[near];
        var num = 1 + rho + drho * dz;
        var den = dz * (1 + rho) + r[near];
        if (den == Complex.Zero) return Complex.Zero;
        var g = num / den + others;                        // p′/p
        return 1 / g;
    }

    /// <summary>
    /// The roots as a model's poles: near-real roots made real, the rest paired with their nearest conjugate and
    /// averaged into an exact pair, unstable ones reflected, the list ordered by magnitude with pairs adjacent.
    /// </summary>
    internal static List<Complex> Normalise(Complex[] roots)
    {
        var real = new List<double>();
        var upper = new List<Complex>();
        var lower = new List<Complex>();
        foreach (var z in roots)
        {
            if (Math.Abs(z.Imaginary) <= 1e-9 * Math.Max(z.Magnitude, 1e-300)) real.Add(z.Real);
            else if (z.Imaginary > 0) upper.Add(z);
            else lower.Add(z);
        }
        var pairs = new List<Complex>();
        foreach (var u in upper.OrderBy(u => u.Magnitude).ToList())
        {
            if (lower.Count == 0) { real.Add(u.Real); continue; }
            var best = lower.MinBy(l => (l - Complex.Conjugate(u)).Magnitude);
            lower.Remove(best);
            pairs.Add(new Complex((u.Real + best.Real) / 2, (u.Imaginary - best.Imaginary) / 2));
        }
        foreach (var l in lower) real.Add(l.Real);

        static double Stable(double re) => re > 0 ? -re : re;
        var list = new List<(double Mag, Complex[] Members)>();
        foreach (var x in real) list.Add((Math.Abs(x), [new Complex(Stable(x), 0)]));
        foreach (var p in pairs)
        {
            var a = new Complex(Stable(p.Real), p.Imaginary);
            list.Add((a.Magnitude, [a, Complex.Conjugate(a)]));
        }
        return [.. list.OrderBy(e => e.Mag).SelectMany(e => e.Members)];
    }
}

/// <summary>Dense real least squares by Householder QR with column scaling — small, deterministic, no factorisation
/// library behind it.</summary>
public static class LeastSquares
{
    /// <summary>
    /// Householder triangularisation in place: on return the upper triangle of <paramref name="a"/> (rows 0…n−1) is R
    /// and <paramref name="b"/> is Qᵀb; below the diagonal is left as zero. No column scaling, so R's rows for the
    /// trailing columns are the projection a caller eliminating the leading columns wants.
    /// </summary>
    public static void Triangularise(double[,] a, double[] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        var v = new double[m];
        for (int j = 0; j < Math.Min(n, m); j++)
        {
            double norm = 0;
            for (int i = j; i < m; i++) norm += a[i, j] * a[i, j];
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;
            double alpha = a[j, j] > 0 ? -norm : norm;
            double vnorm2 = 0;
            for (int i = j; i < m; i++) { v[i] = a[i, j]; }
            v[j] -= alpha;
            for (int i = j; i < m; i++) vnorm2 += v[i] * v[i];
            if (vnorm2 == 0) continue;
            for (int c = j; c < n; c++)
            {
                double dot = 0;
                for (int i = j; i < m; i++) dot += v[i] * a[i, c];
                double f = 2 * dot / vnorm2;
                for (int i = j; i < m; i++) a[i, c] -= f * v[i];
            }
            double db = 0;
            for (int i = j; i < m; i++) db += v[i] * b[i];
            double fb = 2 * db / vnorm2;
            for (int i = j; i < m; i++) b[i] -= fb * v[i];
            for (int i = j + 1; i < m; i++) a[i, j] = 0;
        }
    }

    /// <summary>The x minimising ‖A x − b‖₂ for a tall <paramref name="a"/> (rows ≥ columns). A column the data does not
    /// reach gets 0.</summary>
    public static double[] Solve(double[,] a, double[] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        if (m < n) throw new ArgumentException("Least squares needs at least as many rows as columns.");
        var q = (double[,])a.Clone();
        var y = (double[])b.Clone();
        var scale = new double[n];
        for (int j = 0; j < n; j++)
        {
            double sum = 0;
            for (int i = 0; i < m; i++) sum += q[i, j] * q[i, j];
            scale[j] = sum > 0 ? 1 / Math.Sqrt(sum) : 0;
            for (int i = 0; i < m; i++) q[i, j] *= scale[j];
        }
        var diag = new double[n];
        for (int j = 0; j < n; j++)
        {
            double norm = 0;
            for (int i = j; i < m; i++) norm += q[i, j] * q[i, j];
            norm = Math.Sqrt(norm);
            if (norm == 0) { diag[j] = 0; continue; }
            double alpha = q[j, j] > 0 ? -norm : norm;
            q[j, j] -= alpha;                               // v = x − αe₁, stored in place
            double vnorm2 = 0;
            for (int i = j; i < m; i++) vnorm2 += q[i, j] * q[i, j];
            for (int c = j + 1; c < n; c++)
            {
                double dot = 0;
                for (int i = j; i < m; i++) dot += q[i, j] * q[i, c];
                double f = 2 * dot / vnorm2;
                for (int i = j; i < m; i++) q[i, c] -= f * q[i, j];
            }
            {
                double dot = 0;
                for (int i = j; i < m; i++) dot += q[i, j] * y[i];
                double f = 2 * dot / vnorm2;
                for (int i = j; i < m; i++) y[i] -= f * q[i, j];
            }
            diag[j] = alpha;
        }
        // Back substitution on R (diagonal in diag, above it in q).
        double rmax = diag.Max(Math.Abs);
        var x = new double[n];
        for (int j = n - 1; j >= 0; j--)
        {
            if (Math.Abs(diag[j]) <= 1e-14 * rmax) { x[j] = 0; continue; }
            double sum = y[j];
            for (int c = j + 1; c < n; c++) sum -= q[j, c] * x[c];
            x[j] = sum / diag[j];
        }
        for (int j = 0; j < n; j++) x[j] *= scale[j];
        return x;
    }
}
