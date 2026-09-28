// brief-em3d-74 R-em3d74-2c — the linear solvers behind one interface.
//
//   Direct:    CSparse's Cholesky with a fill-reducing (minimum-degree on A + Aᵀ) order for the symmetric positive
//              definite conduction matrix; CSparse's LU for a Newton step's nonsymmetric Jacobian.
//   Iterative: conjugate gradients preconditioned by one smoothed-aggregation V-cycle; BiCGStab for the Jacobian, with the
//              AMG built on its symmetric part. Stops on the relative residual ‖b − Ax‖/‖b‖.
//
// An iterative solve that does not converge falls back to the direct one, and says so: a result is never the last
// iterate of a stalled solve.

using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;

namespace CircuitRF.Thermal.Solvers;

/// <summary>What one linear solve did.</summary>
public sealed record LinearSolveReport(ThermalSolverKind Solver, int Iterations, double RelativeResidual, bool Converged,
                                       string? FallbackNote, int AmgLevels = 0, double AmgOperatorComplexity = 0);

public static class LinearSolver
{
    /// <summary>Solves A·x = b. <paramref name="symmetric"/> says A is symmetric positive definite (Cholesky / CG); otherwise
    /// LU / BiCGStab. <paramref name="x"/> holds the initial guess for an iterative solve and receives the answer.</summary>
    public static LinearSolveReport Solve(SparseRows a, double[] b, double[] x, bool symmetric, ThermalSolverKind kind,
                                          ThermalSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(options);
        if (kind == ThermalSolverKind.Auto) kind = a.Rows < options.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative;
        if (a.Rows == 0) return new(kind, 0, 0, true, null);
        if (kind == ThermalSolverKind.Direct) return Direct(a, b, x, symmetric);

        options.Cancellation.ThrowIfCancellationRequested();
        var amg = SmoothedAggregationAmg.Build(symmetric ? a : a.SymmetricPart(), options.AmgTheta);
        int it;
        double rel;
        if (symmetric) it = Pcg(a, b, x, amg.Apply, options, out rel);
        else it = BiCgStab(a, b, x, amg.Apply, options, out rel);
        bool ok = rel <= options.RelativeTolerance;
        if (ok) return new(ThermalSolverKind.Iterative, it, rel, true, null, amg.LevelCount, amg.OperatorComplexity);
        var direct = Direct(a, b, x, symmetric);
        return direct with
        {
            FallbackNote = $"The iterative solve stopped at a relative residual of {rel:G3} after {it} iterations " +
                           $"(asked for {options.RelativeTolerance:G3}); the direct solver was used instead.",
        };
    }

    /// <summary>
    /// brief-em3d-77 R-em3d77-4a — a nonsymmetric system of two blocks, unknowns [0, <paramref name="split"/>) and the rest
    /// (temperature and potential): LU on the direct path; on the iterative one BiCGStab preconditioned block-diagonally, one
    /// smoothed-aggregation V-cycle on each diagonal block's symmetric part. Falls back to LU, and says so, as <see cref="Solve"/>.
    /// </summary>
    public static LinearSolveReport SolveBlocks(SparseRows a, double[] b, double[] x, int split, ThermalSolverKind kind,
                                                ThermalSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(options);
        if (kind == ThermalSolverKind.Auto) kind = a.Rows < options.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative;
        if (a.Rows == 0) return new(kind, 0, 0, true, null);
        if (kind == ThermalSolverKind.Direct || split <= 0 || split >= a.Rows) return split <= 0 || split >= a.Rows ? Solve(a, b, x, false, kind, options) : Direct(a, b, x, false);
        options.Cancellation.ThrowIfCancellationRequested();
        var first = Block(a, 0, split).SymmetricPart();
        var second = Block(a, split, a.Rows).SymmetricPart();
        var p1 = SmoothedAggregationAmg.Build(first, options.AmgTheta);
        var p2 = SmoothedAggregationAmg.Build(second, options.AmgTheta);
        var r1 = new double[split];
        var z1 = new double[split];
        var r2 = new double[a.Rows - split];
        var z2 = new double[a.Rows - split];
        void Apply(double[] r, double[] z)
        {
            Array.Copy(r, 0, r1, 0, split);
            Array.Copy(r, split, r2, 0, r2.Length);
            p1.Apply(r1, z1);
            p2.Apply(r2, z2);
            Array.Copy(z1, 0, z, 0, split);
            Array.Copy(z2, 0, z, split, z2.Length);
        }
        int it = BiCgStab(a, b, x, Apply, options, out double rel);
        if (rel <= options.RelativeTolerance) return new(ThermalSolverKind.Iterative, it, rel, true, null, p1.LevelCount, p1.OperatorComplexity);
        var direct = Direct(a, b, x, false);
        return direct with
        {
            FallbackNote = $"The iterative solve stopped at a relative residual of {rel:G3} after {it} iterations " +
                           $"(asked for {options.RelativeTolerance:G3}); the direct solver was used instead.",
        };
    }

    /// <summary>Rows and columns [<paramref name="lo"/>, <paramref name="hi"/>) of <paramref name="a"/>.</summary>
    private static SparseRows Block(SparseRows a, int lo, int hi)
    {
        var ptr = new int[hi - lo + 1];
        var idx = new List<int>();
        var val = new List<double>();
        for (int i = lo; i < hi; i++)
        {
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++)
            {
                int c = a.Idx[k];
                if (c < lo || c >= hi) continue;
                idx.Add(c - lo);
                val.Add(a.Val[k]);
            }
            ptr[i - lo + 1] = idx.Count;
        }
        return new SparseRows(hi - lo, hi - lo, ptr, [.. idx], [.. val]);
    }

    private static LinearSolveReport Direct(SparseRows a, double[] b, double[] x, bool symmetric)
    {
        // CSparse is compressed-COLUMN: A's CSR arrays are Aᵀ's CSC arrays, so a symmetric matrix passes as it is and a
        // nonsymmetric one is transposed first.
        var csc = symmetric ? a : a.Transpose();
        var m = new SparseMatrix(a.Rows, a.Cols, csc.Val, csc.Idx, csc.Ptr);
        if (symmetric)
        {
            var chol = SparseCholesky.Create(m, ColumnOrdering.MinimumDegreeAtPlusA);
            chol.Solve(b, x);
        }
        else
        {
            var lu = SparseLU.Create(m, ColumnOrdering.MinimumDegreeAtPlusA, 1.0);
            lu.Solve(b, x);
        }
        var y = new double[a.Rows];
        a.Multiply(x, y);
        double num = 0, den = 0;
        for (int i = 0; i < y.Length; i++) { num += (b[i] - y[i]) * (b[i] - y[i]); den += b[i] * b[i]; }
        return new(ThermalSolverKind.Direct, 0, den > 0 ? Math.Sqrt(num / den) : Math.Sqrt(num), true, null);
    }

    internal static int Pcg(SparseRows a, double[] b, double[] x, Action<double[], double[]> m, ThermalSolveOptions o, out double rel)
    {
        int n = b.Length;
        var r = new double[n];
        a.Multiply(x, r);
        for (int i = 0; i < n; i++) r[i] = b[i] - r[i];
        var z = new double[n];
        var p = new double[n];
        var q = new double[n];
        double bn = SparseRows.Norm(b);
        if (bn == 0) { Array.Clear(x); rel = 0; return 0; }
        rel = SparseRows.Norm(r) / bn;
        if (rel <= o.RelativeTolerance) return 0;
        m(r, z);
        Array.Copy(z, p, n);
        double rz = SparseRows.Dot(r, z);
        for (int k = 1; k <= o.MaxIterations; k++)
        {
            if ((k & 15) == 0) o.Cancellation.ThrowIfCancellationRequested();
            a.Multiply(p, q);
            double alpha = rz / SparseRows.Dot(p, q);
            for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * q[i]; }
            rel = SparseRows.Norm(r) / bn;
            if (rel <= o.RelativeTolerance) return k;
            m(r, z);
            double rz1 = SparseRows.Dot(r, z);
            double beta = rz1 / rz;
            rz = rz1;
            for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
        }
        return o.MaxIterations;
    }

    /// <summary>Right-preconditioned BiCGStab (van der Vorst 1992).</summary>
    internal static int BiCgStab(SparseRows a, double[] b, double[] x, Action<double[], double[]> m, ThermalSolveOptions o, out double rel)
    {
        int n = b.Length;
        var r = new double[n];
        a.Multiply(x, r);
        for (int i = 0; i < n; i++) r[i] = b[i] - r[i];
        double bn = SparseRows.Norm(b);
        if (bn == 0) { Array.Clear(x); rel = 0; return 0; }
        rel = SparseRows.Norm(r) / bn;
        if (rel <= o.RelativeTolerance) return 0;
        var r0 = (double[])r.Clone();
        var p = new double[n];
        var v = new double[n];
        var s = new double[n];
        var t = new double[n];
        var ph = new double[n];
        var sh = new double[n];
        double rho = 1, alpha = 1, omega = 1;
        for (int k = 1; k <= o.MaxIterations; k++)
        {
            if ((k & 15) == 0) o.Cancellation.ThrowIfCancellationRequested();
            double rho1 = SparseRows.Dot(r0, r);
            if (rho1 == 0) return k;
            double beta = rho1 / rho * (alpha / omega);
            rho = rho1;
            for (int i = 0; i < n; i++) p[i] = r[i] + beta * (p[i] - omega * v[i]);
            m(p, ph);
            a.Multiply(ph, v);
            alpha = rho / SparseRows.Dot(r0, v);
            for (int i = 0; i < n; i++) s[i] = r[i] - alpha * v[i];
            if (SparseRows.Norm(s) / bn <= o.RelativeTolerance)
            {
                for (int i = 0; i < n; i++) x[i] += alpha * ph[i];
                rel = SparseRows.Norm(s) / bn;
                return k;
            }
            m(s, sh);
            a.Multiply(sh, t);
            double tt = SparseRows.Dot(t, t);
            omega = tt > 0 ? SparseRows.Dot(t, s) / tt : 0;
            for (int i = 0; i < n; i++)
            {
                x[i] += alpha * ph[i] + omega * sh[i];
                r[i] = s[i] - omega * t[i];
            }
            rel = SparseRows.Norm(r) / bn;
            if (rel <= o.RelativeTolerance || omega == 0) return k;
        }
        return o.MaxIterations;
    }
}
