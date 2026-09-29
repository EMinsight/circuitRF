// brief-em3d-80 R-em3d80-2b — the frequency-domain system (K + jωC)·T = q, one ω at a time, many right-hand sides.
//
//   Direct:    CSparse's complex sparse LU (the path src/Engine's MNA already takes) with a fill-reducing order.
//   Iterative: COCG — conjugate gradients with the UNCONJUGATED product xᵀy, valid because K + jωC is complex SYMMETRIC (not
//              Hermitian) — for a symmetric K; complex BiCGStab for a Newton tangent's nonsymmetric K. Either is
//              preconditioned by one smoothed-aggregation V-cycle of the REAL matrix K + ωC applied to the real and imaginary
//              parts separately. K alone (the brief's first thought) is the right preconditioner at low ω and a poor one at
//              high ω, where ωC dominates and K⁻¹(K + jωC) = I + jωK⁻¹C has eigenvalues growing with ω without bound;
//              with K + ωC every preconditioned eigenvalue (k + jωc)/(k + ωc) lies between 1/√2 and 1 in modulus, at
//              every frequency. The hierarchy is rebuilt per frequency for that reason — except (brief-em3d-86) when the
//              frequency is within ReuseRatio of the one the previous hierarchy was built at: a pulse's harmonics are 1/Period
//              apart, and K + ω′C for ω′ within 1.3× of ω bounds the preconditioned eigenvalues nearly as well.
//
// A solve that does not converge falls back to the complex LU, and says so.

using System.Numerics;
using CSparse;
using CxMatrix = CSparse.Complex.SparseMatrix;
using CxLu = CSparse.Complex.Factorization.SparseLU;

namespace CircuitRF.Thermal.Solvers;

/// <summary>K + jωC made ready to solve with many right-hand sides. <paramref name="k"/> and <paramref name="c"/> share one
/// pattern (the assembly's, reduced to the free nodes).</summary>
public sealed class ComplexSolve
{
    private readonly SparseRows _k, _c;
    private readonly double _omega;
    private readonly bool _symmetric;
    private readonly ThermalSolveOptions _options;
    private readonly Complex[] _z;
    private readonly SmoothedAggregationAmg? _amg;
    private readonly double _amgOmega;
    private CxLu? _lu;

    /// <summary>brief-em3d-86 — a hierarchy built at ω′ serves a solve at ω when max(ω/ω′, ω′/ω) is at most this.</summary>
    public const double ReuseRatio = 1.3;

    /// <summary>Whether this solve built its own hierarchy (false: it reused another's, or is direct).</summary>
    public bool BuiltPreconditioner { get; }

    private ComplexSolve(SparseRows k, SparseRows c, double omega, bool symmetric, ThermalSolverKind kind, ThermalSolveOptions options,
                         ComplexSolve? previous)
    {
        if (k.Nnz != c.Nnz || k.Rows != c.Rows) throw new ArgumentException("K and C must share one pattern", nameof(c));
        _k = k; _c = c; _omega = omega; _symmetric = symmetric; _options = options;
        Kind = kind;
        _z = new Complex[k.Nnz];
        for (int i = 0; i < _z.Length; i++) _z[i] = new Complex(k.Val[i], omega * c.Val[i]);
        if (k.Rows == 0) return;
        if (kind == ThermalSolverKind.Direct) Factor();
        else if (previous is { _amg: { } amg, _amgOmega: > 0 and var at } && omega > 0 && Math.Max(omega / at, at / omega) <= ReuseRatio
                 && ReferenceEquals(previous._k, k))
        {
            _amg = amg;
            _amgOmega = at;
        }
        else
        {
            BuiltPreconditioner = true;
            _amgOmega = omega;
            var ks = symmetric ? k : k.SymmetricPart();
            var p = new double[k.Nnz];
            for (int i = 0; i < p.Length; i++) p[i] = ks.Val[i] + omega * c.Val[i];
            _amg = SmoothedAggregationAmg.Build(new SparseRows(k.Rows, k.Cols, k.Ptr, k.Idx, p), options.AmgTheta);
        }
    }

    public ThermalSolverKind Kind { get; private set; }

    public string? FallbackNote { get; private set; }

    /// <summary>The iterations of the last solve (0 for a direct one).</summary>
    public int LastIterations { get; private set; }

    /// <summary>Prepares K + jωC. <paramref name="symmetric"/> says K is symmetric (constant k; not a Newton tangent).</summary>
    public static ComplexSolve Create(SparseRows k, SparseRows c, double omega, bool symmetric, ThermalSolverKind kind,
                                      ThermalSolveOptions options, ComplexSolve? previous = null)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(options);
        if (kind == ThermalSolverKind.Auto) kind = k.Rows < options.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative;
        return new ComplexSolve(k, c, omega, symmetric, kind, options, previous);
    }

    /// <summary>Solves (K + jωC)·x = b; <paramref name="x"/> is an iterative solve's initial guess. Returns the relative
    /// residual.</summary>
    public double Solve(Complex[] b, Complex[] x)
    {
        int n = b.Length;
        LastIterations = 0;
        if (n == 0) return 0;
        _options.Cancellation.ThrowIfCancellationRequested();
        if (Kind == ThermalSolverKind.Iterative)
        {
            int it = _symmetric ? Cocg(b, x, out double rel) : BiCgStab(b, x, out rel);
            LastIterations = it;
            if (rel <= _options.RelativeTolerance) return rel;
            FallbackNote ??= $"A frequency-domain iterative solve stopped at a relative residual of {rel:G3} after {it} iterations " +
                             $"(asked for {_options.RelativeTolerance:G3}); the complex LU was used from there on.";
            Kind = ThermalSolverKind.Direct;
            Factor();
        }
        _lu!.Solve(b, x);
        var y = new Complex[n];
        Multiply(x, y);
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) { num += (b[i] - y[i]).MagnitudeSquared(); den += b[i].MagnitudeSquared(); }
        return den > 0 ? Math.Sqrt(num / den) : Math.Sqrt(num);
    }

    private void Factor()
    {
        // compressed-COLUMN, as LinearSolver.Direct: the CSR arrays of K + jωC are those of its transpose
        Complex[] vals;
        int[] ptr, idx;
        if (_symmetric) { vals = _z; ptr = _k.Ptr; idx = _k.Idx; }
        else
        {
            var kt = _k.Transpose();
            var ct = _c.Transpose();
            vals = new Complex[kt.Nnz];
            for (int i = 0; i < vals.Length; i++) vals[i] = new Complex(kt.Val[i], _omega * ct.Val[i]);
            ptr = kt.Ptr; idx = kt.Idx;
        }
        _lu = CxLu.Create(new CxMatrix(_k.Rows, _k.Cols, vals, idx, ptr), ColumnOrdering.MinimumDegreeAtPlusA, 1.0);
    }

    private void Multiply(ReadOnlySpan<Complex> x, Span<Complex> y)
    {
        for (int i = 0; i < _k.Rows; i++)
        {
            Complex s = 0;
            for (int p = _k.Ptr[i]; p < _k.Ptr[i + 1]; p++) s += _z[p] * x[_k.Idx[p]];
            y[i] = s;
        }
    }

    private double[]? _pr, _pi, _qr, _qi;

    /// <summary>z = M⁻¹r: one V-cycle on the real part and one on the imaginary part (M is real).</summary>
    private void Precondition(Complex[] r, Complex[] z)
    {
        int n = r.Length;
        _pr ??= new double[n]; _pi ??= new double[n]; _qr ??= new double[n]; _qi ??= new double[n];
        for (int i = 0; i < n; i++) { _pr[i] = r[i].Real; _pi[i] = r[i].Imaginary; }
        _amg!.Apply(_pr, _qr);
        _amg.Apply(_pi, _qi);
        for (int i = 0; i < n; i++) z[i] = new Complex(_qr[i], _qi[i]);
    }

    private static Complex Inner(Complex[] a, Complex[] b)
    {
        Complex s = 0;
        for (int i = 0; i < a.Length; i++) s += Complex.Conjugate(a[i]) * b[i];
        return s;
    }

    private static double Norm(Complex[] a)
    {
        double s = 0;
        foreach (var v in a) s += v.MagnitudeSquared();
        return Math.Sqrt(s);
    }

    /// <summary>
    /// Preconditioned COCG (van der Vorst &amp; Melissen 1990), on SPLIT real and imaginary arrays: K and ωC are real, so
    /// (K + jωC)(u + jv) = (Ku − ωCv) + j(Kv + ωCu) in plain double arithmetic. A Debug build (what the owner runs) does not
    /// inline System.Numerics.Complex's operators, and the vector loops ran ~10× slower through it.
    /// </summary>
    private int Cocg(Complex[] b, Complex[] x, out double rel)
    {
        int n = b.Length;
        var xr = new double[n]; var xi = new double[n];
        var rr = new double[n]; var ri = new double[n];
        var zr = new double[n]; var zi = new double[n];
        var pr = new double[n]; var pim = new double[n];
        var qr = new double[n]; var qi = new double[n];
        double bn = 0;
        for (int i = 0; i < n; i++) { xr[i] = x[i].Real; xi[i] = x[i].Imaginary; bn += b[i].Real * b[i].Real + b[i].Imaginary * b[i].Imaginary; }
        bn = Math.Sqrt(bn);
        if (bn == 0) { Array.Clear(x); rel = 0; return 0; }
        MultiplySplit(xr, xi, rr, ri);
        for (int i = 0; i < n; i++) { rr[i] = b[i].Real - rr[i]; ri[i] = b[i].Imaginary - ri[i]; }
        rel = NormSplit(rr, ri) / bn;
        int k = 0;
        if (rel > _options.RelativeTolerance)
        {
            _amg!.Apply(rr, zr);
            _amg.Apply(ri, zi);
            Array.Copy(zr, pr, n); Array.Copy(zi, pim, n);
            var rho = BilinearSplit(rr, ri, zr, zi);
            for (k = 1; k <= _options.MaxIterations; k++)
            {
                if ((k & 15) == 0) _options.Cancellation.ThrowIfCancellationRequested();
                MultiplySplit(pr, pim, qr, qi);
                var alpha = rho / BilinearSplit(pr, pim, qr, qi);
                double ar = alpha.Real, ai = alpha.Imaginary;
                double norm = 0;
                for (int i = 0; i < n; i++)
                {
                    xr[i] += ar * pr[i] - ai * pim[i]; xi[i] += ar * pim[i] + ai * pr[i];
                    rr[i] -= ar * qr[i] - ai * qi[i];  ri[i] -= ar * qi[i] + ai * qr[i];
                    norm += rr[i] * rr[i] + ri[i] * ri[i];
                }
                rel = Math.Sqrt(norm) / bn;
                if (rel <= _options.RelativeTolerance || !double.IsFinite(rel)) break;     // a breakdown (NaN): the caller falls back
                _amg.Apply(rr, zr);
                _amg.Apply(ri, zi);
                var rho1 = BilinearSplit(rr, ri, zr, zi);
                var beta = rho1 / rho;
                rho = rho1;
                double br = beta.Real, bi = beta.Imaginary;
                for (int i = 0; i < n; i++)
                {
                    double tr = zr[i] + br * pr[i] - bi * pim[i];
                    double ti = zi[i] + br * pim[i] + bi * pr[i];
                    pr[i] = tr; pim[i] = ti;
                }
            }
            if (k > _options.MaxIterations) k = _options.MaxIterations;
        }
        for (int i = 0; i < n; i++) x[i] = new Complex(xr[i], xi[i]);
        return k;
    }

    /// <summary>(yr + j·yi) = (K + jωC)(xr + j·xi).</summary>
    private void MultiplySplit(double[] xr, double[] xi, double[] yr, double[] yi)
    {
        var kv = _k.Val; var cv = _c.Val; var ptr = _k.Ptr; var idx = _k.Idx;
        double w = _omega;
        for (int i = 0; i < _k.Rows; i++)
        {
            double sr = 0, si = 0;
            for (int p = ptr[i]; p < ptr[i + 1]; p++)
            {
                int c = idx[p];
                double a = kv[p], wc = w * cv[p];
                sr += a * xr[c] - wc * xi[c];
                si += a * xi[c] + wc * xr[c];
            }
            yr[i] = sr; yi[i] = si;
        }
    }

    /// <summary>The unconjugated aᵀb of two split vectors.</summary>
    private static Complex BilinearSplit(double[] ar, double[] ai, double[] br, double[] bi)
    {
        double re = 0, im = 0;
        for (int i = 0; i < ar.Length; i++) { re += ar[i] * br[i] - ai[i] * bi[i]; im += ar[i] * bi[i] + ai[i] * br[i]; }
        return new Complex(re, im);
    }

    private static double NormSplit(double[] ar, double[] ai)
    {
        double s = 0;
        for (int i = 0; i < ar.Length; i++) s += ar[i] * ar[i] + ai[i] * ai[i];
        return Math.Sqrt(s);
    }

    /// <summary>Right-preconditioned complex BiCGStab, for a nonsymmetric K.</summary>
    private int BiCgStab(Complex[] b, Complex[] x, out double rel)
    {
        int n = b.Length;
        var r = new Complex[n];
        Multiply(x, r);
        for (int i = 0; i < n; i++) r[i] = b[i] - r[i];
        double bn = Norm(b);
        if (bn == 0) { Array.Clear(x); rel = 0; return 0; }
        rel = Norm(r) / bn;
        if (rel <= _options.RelativeTolerance) return 0;
        var r0 = (Complex[])r.Clone();
        var p = new Complex[n]; var v = new Complex[n]; var s = new Complex[n]; var t = new Complex[n];
        var ph = new Complex[n]; var sh = new Complex[n];
        Complex rho = 1, alpha = 1, omega = 1;
        for (int k = 1; k <= _options.MaxIterations; k++)
        {
            if ((k & 15) == 0) _options.Cancellation.ThrowIfCancellationRequested();
            Complex rho1 = Inner(r0, r);
            if (rho1 == Complex.Zero) return k;
            Complex beta = rho1 / rho * (alpha / omega);
            rho = rho1;
            for (int i = 0; i < n; i++) p[i] = r[i] + beta * (p[i] - omega * v[i]);
            Precondition(p, ph);
            Multiply(ph, v);
            alpha = rho / Inner(r0, v);
            for (int i = 0; i < n; i++) s[i] = r[i] - alpha * v[i];
            if (Norm(s) / bn <= _options.RelativeTolerance)
            {
                for (int i = 0; i < n; i++) x[i] += alpha * ph[i];
                rel = Norm(s) / bn;
                return k;
            }
            Precondition(s, sh);
            Multiply(sh, t);
            double tt = Inner(t, t).Real;
            omega = tt > 0 ? Inner(t, s) / tt : 0;
            for (int i = 0; i < n; i++) { x[i] += alpha * ph[i] + omega * sh[i]; r[i] = s[i] - omega * t[i]; }
            rel = Norm(r) / bn;
            if (rel <= _options.RelativeTolerance || omega == Complex.Zero || !double.IsFinite(rel)) return k;
        }
        return _options.MaxIterations;
    }
}

internal static class ComplexExtensions
{
    public static double MagnitudeSquared(this Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
}
