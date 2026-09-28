// brief-em3d-74 R-em3d74-2c — smoothed-aggregation algebraic multigrid, as a preconditioner.
//
// The textbook method (Vaněk, Mandel & Brezina, "Algebraic multigrid by smoothed aggregation for second and fourth order
// elliptic problems", Computing 56, 1996), written from the published algorithm — the one brief 72 measured in its scratch
// harness, whose iteration counts agreed with an independent implementation on the same matrices (brief 72 Q6):
//
//   * strength of connection |aᵢⱼ| ≥ θ·√(|aᵢᵢ aⱼⱼ|), θ = 0 by default (every connection strong — fastest in Q6);
//   * three-pass standard aggregation;
//   * a piecewise-constant tentative prolongator, smoothed by one damped-Jacobi step with ω = (4/3)/ρ(D⁻¹A);
//   * Galerkin coarse operators RAP, R = Pᵀ; a dense Cholesky at the bottom (≤ 400 unknowns);
//   * a V(1,1) cycle whose pre-smoother is a symmetric Gauss–Seidel sweep (forward then backward) and whose post-smoother
//     is its adjoint — so the preconditioner is SYMMETRIC, which conjugate gradients requires.
//
// DETERMINISTIC: the spectral-radius estimate's start vector comes from a fixed seed, and Gauss–Seidel is sequential.

namespace CircuitRF.Thermal.Solvers;

/// <summary>A smoothed-aggregation hierarchy, applied as one V-cycle per call.</summary>
public sealed class SmoothedAggregationAmg
{
    private sealed class Level
    {
        public required SparseRows A;
        public SparseRows? P, R;
        public double[] Diag = [], Tmp = [], Res = [], Xc = [], Bc = [];
    }

    private readonly List<Level> _levels = [];
    private double[,] _coarse = new double[0, 0];

    /// <summary>The coarsest level is factored densely at or below this size.</summary>
    public const int CoarseSize = 400;

    /// <summary>The number of levels.</summary>
    public int LevelCount => _levels.Count;

    /// <summary>Σ nnz over the levels / nnz of the finest — the hierarchy's memory and work factor.</summary>
    public double OperatorComplexity => _levels.Sum(l => (double)l.A.Nnz) / _levels[0].A.Nnz;

    public static SmoothedAggregationAmg Build(SparseRows a, double theta)
    {
        var h = new SmoothedAggregationAmg();
        var cur = a;
        while (true)
        {
            var lvl = new Level { A = cur, Diag = cur.Diagonal(), Tmp = new double[cur.Rows], Res = new double[cur.Rows] };
            h._levels.Add(lvl);
            if (cur.Rows <= CoarseSize) break;
            var agg = Aggregate(cur, lvl.Diag, theta, out int nagg);
            if (nagg >= cur.Rows * 0.9) break;          // not coarsening: stop here
            var t = Tentative(agg, nagg);
            var p = Smooth(cur, t, lvl.Diag);
            var r = p.Transpose();
            lvl.P = p;
            lvl.R = r;
            lvl.Xc = new double[nagg];
            lvl.Bc = new double[nagg];
            cur = r.Times(cur.Times(p));
        }
        h.FactorCoarse();
        return h;
    }

    private static int[] Aggregate(SparseRows a, double[] d, double theta, out int nagg)
    {
        int n = a.Rows;
        bool Strong(int i, int k) => a.Idx[k] != i && Math.Abs(a.Val[k]) >= theta * Math.Sqrt(Math.Abs(d[i] * d[a.Idx[k]]));
        var agg = new int[n];
        Array.Fill(agg, -1);
        nagg = 0;
        // pass 1: a node whose strong neighbours are all free seeds an aggregate of itself and them
        for (int i = 0; i < n; i++)
        {
            if (agg[i] >= 0) continue;
            bool free = true;
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1] && free; k++)
                if (Strong(i, k) && agg[a.Idx[k]] >= 0) free = false;
            if (!free) continue;
            agg[i] = nagg;
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++)
                if (Strong(i, k)) agg[a.Idx[k]] = nagg;
            nagg++;
        }
        // pass 2: join a neighbouring pass-1 aggregate (the strongest connection)
        var pass1 = (int[])agg.Clone();
        for (int i = 0; i < n; i++)
        {
            if (agg[i] >= 0) continue;
            double best = -1;
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++)
                if (Strong(i, k) && pass1[a.Idx[k]] >= 0 && Math.Abs(a.Val[k]) > best)
                {
                    best = Math.Abs(a.Val[k]);
                    agg[i] = pass1[a.Idx[k]];
                }
        }
        // pass 3: whatever is left seeds a new aggregate with its free strong neighbours
        for (int i = 0; i < n; i++)
        {
            if (agg[i] >= 0) continue;
            agg[i] = nagg;
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++)
                if (Strong(i, k) && agg[a.Idx[k]] < 0) agg[a.Idx[k]] = nagg;
            nagg++;
        }
        return agg;
    }

    private static SparseRows Tentative(int[] agg, int nagg)
    {
        int n = agg.Length;
        var size = new int[nagg];
        foreach (int g in agg) size[g]++;
        var ptr = new int[n + 1];
        var idx = new int[n];
        var val = new double[n];
        for (int i = 0; i < n; i++)
        {
            ptr[i + 1] = i + 1;
            idx[i] = agg[i];
            val[i] = 1.0 / Math.Sqrt(size[agg[i]]);
        }
        return new SparseRows(n, nagg, ptr, idx, val);
    }

    private static SparseRows Smooth(SparseRows a, SparseRows t, double[] d)
    {
        // ω = (4/3)/ρ(D⁻¹A), ρ by power iteration from a fixed-seed start
        int n = a.Rows;
        var v = new double[n];
        var w = new double[n];
        var rng = new Random(1);
        for (int i = 0; i < n; i++) v[i] = rng.NextDouble();
        double rho = 1;
        for (int it = 0; it < 15; it++)
        {
            a.Multiply(v, w);
            for (int i = 0; i < n; i++) w[i] /= d[i];
            rho = Math.Sqrt(SparseRows.Dot(w, w) / SparseRows.Dot(v, v));
            double s = 1 / SparseRows.Norm(w);
            for (int i = 0; i < n; i++) v[i] = w[i] * s;
        }
        double omega = 4.0 / 3.0 / rho;
        // P = T − ω D⁻¹ A T
        var at = a.Times(t);
        var ptr = new int[n + 1];
        var idx = new List<int>(at.Nnz + n);
        var val = new List<double>(at.Nnz + n);
        for (int i = 0; i < n; i++)
        {
            int tcol = t.Idx[t.Ptr[i]];
            double tval = t.Val[t.Ptr[i]];
            bool placed = false;
            for (int k = at.Ptr[i]; k < at.Ptr[i + 1]; k++)
            {
                int c = at.Idx[k];
                double x = -omega * at.Val[k] / d[i];
                if (!placed && c > tcol) { idx.Add(tcol); val.Add(tval); placed = true; }
                if (c == tcol) { x += tval; placed = true; }
                idx.Add(c);
                val.Add(x);
            }
            if (!placed) { idx.Add(tcol); val.Add(tval); }
            ptr[i + 1] = idx.Count;
        }
        return new SparseRows(n, t.Cols, ptr, [.. idx], [.. val]);
    }

    private void FactorCoarse()
    {
        var a = _levels[^1].A;
        int n = a.Rows;
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++) l[i, a.Idx[k]] = a.Val[k];
        for (int j = 0; j < n; j++)
        {
            double s = l[j, j];
            for (int k = 0; k < j; k++) s -= l[j, k] * l[j, k];
            // A symmetric positive semi-definite coarse operator can round to a non-positive pivot; a tiny floor keeps the
            // preconditioner defined (it only has to be good, not exact).
            l[j, j] = Math.Sqrt(Math.Max(s, 1e-300));
            for (int i = j + 1; i < n; i++)
            {
                double t = l[i, j];
                for (int k = 0; k < j; k++) t -= l[i, k] * l[j, k];
                l[i, j] = t / l[j, j];
            }
        }
        _coarse = l;
    }

    private void CoarseSolve(double[] b, double[] x)
    {
        int n = b.Length;
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int k = 0; k < i; k++) s -= _coarse[i, k] * x[k];
            x[i] = s / _coarse[i, i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = x[i];
            for (int k = i + 1; k < n; k++) s -= _coarse[k, i] * x[k];
            x[i] = s / _coarse[i, i];
        }
    }

    private static void GaussSeidel(SparseRows a, double[] d, double[] x, double[] b, bool forward)
    {
        int n = a.Rows;
        for (int s = 0; s < n; s++)
        {
            int i = forward ? s : n - 1 - s;
            double r = b[i];
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++) r -= a.Val[k] * x[a.Idx[k]];
            x[i] += r / d[i];
        }
    }

    /// <summary>z = M⁻¹r: one V-cycle from a zero guess.</summary>
    public void Apply(double[] r, double[] z) => Cycle(0, r, z);

    private void Cycle(int l, double[] b, double[] x)
    {
        var lv = _levels[l];
        if (l == _levels.Count - 1) { CoarseSolve(b, x); return; }
        Array.Clear(x);
        GaussSeidel(lv.A, lv.Diag, x, b, true);
        GaussSeidel(lv.A, lv.Diag, x, b, false);
        lv.A.Multiply(x, lv.Tmp);
        for (int i = 0; i < lv.A.Rows; i++) lv.Res[i] = b[i] - lv.Tmp[i];
        lv.R!.Multiply(lv.Res, lv.Bc);
        Cycle(l + 1, lv.Bc, lv.Xc);
        lv.P!.Multiply(lv.Xc, lv.Tmp);
        for (int i = 0; i < lv.A.Rows; i++) x[i] += lv.Tmp[i];
        // post-smoothing: the adjoint order, so the preconditioner is symmetric
        GaussSeidel(lv.A, lv.Diag, x, b, true);
        GaussSeidel(lv.A, lv.Diag, x, b, false);
    }
}
