// brief-em3d-72 Q5/Q6 — SPIKE MATERIAL (see the .csproj). One method, one matrix, one process, one line
// of JSON on stdout, so the driver can read each run's own peak working set.
//
//   cholesky <m.bin>          CSparse SparseCholesky, AMD ordering on A + A'
//   amg <m.bin> [theta]       CG + smoothed-aggregation AMG (V(1,1), symmetric Gauss-Seidel), rel. residual 1e-8
//   ic0 <m.bin>               CG + IC(0) (zero-fill incomplete Cholesky, diagonal shift on breakdown)
//   jacobi <m.bin>            CG + Jacobi
//
// The AMG is a minimal textbook implementation (Vanek, Mandel & Brezina 1996): symmetric strength of
// connection, three-pass standard aggregation, a piecewise-constant tentative prolongator smoothed by one
// damped-Jacobi step with omega = (4/3)/rho(D^-1 A), Galerkin coarse operators, a dense Cholesky at the
// bottom. Written for this measurement, from the published algorithm, with no library code copied.

using System.Diagnostics;
using System.Text.Json;
using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;

static class Program
{
    const double Tol = 1e-8;
    const int MaxIt = 2000;

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: <cholesky|amg|ic0|jacobi> <matrix.bin> [theta]"); return 2; }
        var (A, b) = Csr.Read(args[1]);
        var result = new Dictionary<string, object> { ["method"] = args[0], ["file"] = Path.GetFileName(args[1]),
                                                      ["n"] = A.N, ["nnz"] = A.Nnz };
        double theta = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0.08;
        // small matrices: repeat and keep the fastest, so JIT and first-touch are not what is measured
        int reps = A.N < 100_000 ? 3 : 1;
        for (int r = 0; r < reps; r++)
        {
            var one = args[0] switch
            {
                "cholesky" => Cholesky(A, b),
                "amg" => Pcg(A, b, "amg", theta),
                "ic0" => Pcg(A, b, "ic0", theta),
                "jacobi" => Pcg(A, b, "jacobi", theta),
                _ => throw new ArgumentException(args[0]),
            };
            if (r == 0 || (double)one["total_s"] < (double)result["total_s"])
                foreach (var kv in one) result[kv.Key] = kv.Value;
        }
        // peak memory is read by the driver (/usr/bin/time -l): Process.PeakWorkingSet64 reads 0 on macOS
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }

    static Dictionary<string, object> Cholesky(Csr A, double[] b)
    {
        // symmetric: the CSR arrays ARE the CSC arrays
        var M = new SparseMatrix(A.N, A.N, A.Val, A.Idx, A.Ptr);
        var sw = Stopwatch.StartNew();
        var chol = SparseCholesky.Create(M, ColumnOrdering.MinimumDegreeAtPlusA);
        double tf = sw.Elapsed.TotalSeconds;
        var x = new double[A.N];
        sw.Restart();
        chol.Solve(b, x);
        double ts = sw.Elapsed.TotalSeconds;
        return new() { ["factor_s"] = tf, ["solve_s"] = ts, ["total_s"] = tf + ts, ["L_nnz"] = (long)chol.NonZerosCount,
                       ["L_MB"] = chol.NonZerosCount * 12.0 / 1048576.0, ["rel_residual"] = A.RelResidual(x, b) };
    }

    static Dictionary<string, object> Pcg(Csr A, double[] b, string kind, double theta)
    {
        var sw = Stopwatch.StartNew();
        Action<double[], double[]> precond;
        var info = new Dictionary<string, object>();
        switch (kind)
        {
            case "amg":
            {
                var h = Amg.Build(A, theta);
                precond = h.VCycle;
                info["levels"] = h.Levels.Count;
                info["level_sizes"] = h.Levels.Select(l => l.A.N).ToArray();
                info["operator_complexity"] = h.Levels.Sum(l => (double)l.A.Nnz) / A.Nnz;
                info["hierarchy_MB"] = h.Levels.Sum(l => (l.A.Nnz + (l.P?.Nnz ?? 0) + (l.R?.Nnz ?? 0)) * 12.0) / 1048576.0;
                info["theta"] = theta;
                break;
            }
            case "ic0":
            {
                var (ic, shift) = Ic0.Build(A);
                precond = ic.Apply;
                info["diagonal_shift"] = shift;
                break;
            }
            default:
            {
                var d = A.Diagonal();
                precond = (r, z) => { for (int i = 0; i < r.Length; i++) z[i] = r[i] / d[i]; };
                break;
            }
        }
        double tsetup = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var x = new double[A.N];
        int it = Cg(A, b, x, precond, out double rel);
        double tsolve = sw.Elapsed.TotalSeconds;
        info["setup_s"] = tsetup;
        info["solve_s"] = tsolve;
        info["total_s"] = tsetup + tsolve;
        info["iterations"] = it;
        info["converged"] = rel <= Tol;
        info["rel_residual"] = A.RelResidual(x, b);
        return info;
    }

    static int Cg(Csr A, double[] b, double[] x, Action<double[], double[]> M, out double rel)
    {
        int n = b.Length;
        var r = (double[])b.Clone();
        var z = new double[n];
        var p = new double[n];
        var q = new double[n];
        double bn = Math.Sqrt(Dot(b, b));
        M(r, z);
        Array.Copy(z, p, n);
        double rz = Dot(r, z);
        rel = 1;
        for (int k = 1; k <= MaxIt; k++)
        {
            A.Mul(p, q);
            double alpha = rz / Dot(p, q);
            for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * q[i]; }
            rel = Math.Sqrt(Dot(r, r)) / bn;
            if (rel <= Tol) return k;
            M(r, z);
            double rz1 = Dot(r, z);
            double beta = rz1 / rz;
            rz = rz1;
            for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
        }
        return MaxIt;
    }

    internal static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>Compressed sparse rows.</summary>
sealed class Csr(int n, int m, int[] ptr, int[] idx, double[] val)
{
    public int N = n, M = m;
    public int[] Ptr = ptr, Idx = idx;
    public double[] Val = val;
    public int Nnz => Ptr[N];

    public static (Csr, double[]) Read(string path)
    {
        using var f = new BinaryReader(File.OpenRead(path));
        int n = checked((int)f.ReadInt64());
        int nnz = checked((int)f.ReadInt64());
        var ptr = new int[n + 1];
        var idx = new int[nnz];
        var val = new double[nnz];
        var bb = new double[n];
        for (int i = 0; i <= n; i++) ptr[i] = f.ReadInt32();
        for (int i = 0; i < nnz; i++) idx[i] = f.ReadInt32();
        for (int i = 0; i < nnz; i++) val[i] = f.ReadDouble();
        for (int i = 0; i < n; i++) bb[i] = f.ReadDouble();
        return (new Csr(n, n, ptr, idx, val), bb);
    }

    public void Mul(double[] x, double[] y)
    {
        for (int i = 0; i < N; i++)
        {
            double s = 0;
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++) s += Val[k] * x[Idx[k]];
            y[i] = s;
        }
    }

    public double[] Diagonal()
    {
        var d = new double[N];
        for (int i = 0; i < N; i++)
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++)
                if (Idx[k] == i) d[i] = Val[k];
        return d;
    }

    public double RelResidual(double[] x, double[] b)
    {
        var y = new double[N];
        Mul(x, y);
        double s = 0;
        for (int i = 0; i < N; i++) s += (b[i] - y[i]) * (b[i] - y[i]);
        return Math.Sqrt(s / Program.Dot(b, b));
    }

    public Csr Transpose()
    {
        var cnt = new int[M + 1];
        for (int k = 0; k < Nnz; k++) cnt[Idx[k] + 1]++;
        for (int j = 0; j < M; j++) cnt[j + 1] += cnt[j];
        var ptr = (int[])cnt.Clone();
        var idx = new int[Nnz];
        var val = new double[Nnz];
        var next = (int[])cnt.Clone();
        for (int i = 0; i < N; i++)
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++)
            {
                int p = next[Idx[k]]++;
                idx[p] = i;
                val[p] = Val[k];
            }
        return new Csr(M, N, ptr, idx, val);
    }

    /// <summary>this * B, Gustavson's row-by-row product with a dense marker.</summary>
    public Csr Times(Csr B)
    {
        var ptr = new int[N + 1];
        var idx = new List<int>(Nnz * 2);
        var val = new List<double>(Nnz * 2);
        var mark = new int[B.M];
        Array.Fill(mark, -1);
        var acc = new double[B.M];
        var cols = new List<int>();
        for (int i = 0; i < N; i++)
        {
            cols.Clear();
            for (int k = Ptr[i]; k < Ptr[i + 1]; k++)
            {
                int j = Idx[k];
                double a = Val[k];
                for (int l = B.Ptr[j]; l < B.Ptr[j + 1]; l++)
                {
                    int c = B.Idx[l];
                    if (mark[c] != i) { mark[c] = i; acc[c] = 0; cols.Add(c); }
                    acc[c] += a * B.Val[l];
                }
            }
            cols.Sort();
            foreach (int c in cols) { idx.Add(c); val.Add(acc[c]); }
            ptr[i + 1] = idx.Count;
        }
        return new Csr(N, B.M, ptr, idx.ToArray(), val.ToArray());
    }
}

sealed class Amg
{
    public sealed class Level
    {
        public required Csr A;
        public Csr? P, R;
        public double[] Diag = [];
        public double[] Tmp = [], Res = [], Xc = [], Bc = [];
    }

    public List<Level> Levels = [];
    double[,] _coarse = new double[0, 0];
    const int CoarseSize = 400;

    public static Amg Build(Csr A, double theta)
    {
        var h = new Amg();
        var cur = A;
        while (true)
        {
            var lvl = new Level { A = cur, Diag = cur.Diagonal(), Tmp = new double[cur.N], Res = new double[cur.N] };
            h.Levels.Add(lvl);
            if (cur.N <= CoarseSize) break;
            var agg = Aggregate(cur, theta, out int nagg);
            if (nagg >= cur.N * 0.9) break; // not coarsening: stop here
            var T = Tentative(agg, nagg);
            var P = Smooth(cur, T, lvl.Diag);
            var R = P.Transpose();
            lvl.P = P;
            lvl.R = R;
            lvl.Xc = new double[nagg];
            lvl.Bc = new double[nagg];
            cur = R.Times(cur.Times(P));
        }
        h.FactorCoarse();
        return h;
    }

    static int[] Aggregate(Csr A, double theta, out int nagg)
    {
        int n = A.N;
        var d = A.Diagonal();
        bool Strong(int i, int k) => A.Idx[k] != i && Math.Abs(A.Val[k]) >= theta * Math.Sqrt(Math.Abs(d[i] * d[A.Idx[k]]));
        var agg = new int[n];
        Array.Fill(agg, -1);
        nagg = 0;
        // pass 1: a node whose strong neighbours are all free seeds an aggregate of itself and them
        for (int i = 0; i < n; i++)
        {
            if (agg[i] >= 0) continue;
            bool free = true;
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1] && free; k++)
                if (Strong(i, k) && agg[A.Idx[k]] >= 0) free = false;
            if (!free) continue;
            agg[i] = nagg;
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++)
                if (Strong(i, k)) agg[A.Idx[k]] = nagg;
            nagg++;
        }
        // pass 2: join a neighbouring pass-1 aggregate (the strongest connection)
        var pass1 = (int[])agg.Clone();
        for (int i = 0; i < n; i++)
        {
            if (agg[i] >= 0) continue;
            double best = -1;
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++)
                if (Strong(i, k) && pass1[A.Idx[k]] >= 0 && Math.Abs(A.Val[k]) > best)
                {
                    best = Math.Abs(A.Val[k]);
                    agg[i] = pass1[A.Idx[k]];
                }
        }
        // pass 3: whatever is left seeds a new aggregate with its free strong neighbours
        for (int i = 0; i < n; i++)
        {
            if (agg[i] >= 0) continue;
            agg[i] = nagg;
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++)
                if (Strong(i, k) && agg[A.Idx[k]] < 0) agg[A.Idx[k]] = nagg;
            nagg++;
        }
        return agg;
    }

    static Csr Tentative(int[] agg, int nagg)
    {
        int n = agg.Length;
        var size = new int[nagg];
        foreach (int a in agg) size[a]++;
        var ptr = new int[n + 1];
        var idx = new int[n];
        var val = new double[n];
        for (int i = 0; i < n; i++)
        {
            ptr[i + 1] = i + 1;
            idx[i] = agg[i];
            val[i] = 1.0 / Math.Sqrt(size[agg[i]]);
        }
        return new Csr(n, nagg, ptr, idx, val);
    }

    static Csr Smooth(Csr A, Csr T, double[] d)
    {
        // omega = (4/3) / rho(D^-1 A), rho by power iteration
        int n = A.N;
        var v = new double[n];
        var w = new double[n];
        var rng = new Random(1);
        for (int i = 0; i < n; i++) v[i] = rng.NextDouble();
        double rho = 1;
        for (int it = 0; it < 15; it++)
        {
            A.Mul(v, w);
            for (int i = 0; i < n; i++) w[i] /= d[i];
            rho = Math.Sqrt(Program.Dot(w, w) / Program.Dot(v, v));
            double s = 1 / Math.Sqrt(Program.Dot(w, w));
            for (int i = 0; i < n; i++) v[i] = w[i] * s;
        }
        double omega = 4.0 / 3.0 / rho;
        // P = T - omega D^-1 A T
        var AT = A.Times(T);
        var ptr = new int[n + 1];
        var idx = new List<int>(AT.Nnz + n);
        var val = new List<double>(AT.Nnz + n);
        for (int i = 0; i < n; i++)
        {
            int tcol = T.Idx[T.Ptr[i]];
            double tval = T.Val[T.Ptr[i]];
            bool placed = false;
            for (int k = AT.Ptr[i]; k < AT.Ptr[i + 1]; k++)
            {
                int c = AT.Idx[k];
                double a = -omega * AT.Val[k] / d[i];
                if (!placed && c > tcol) { idx.Add(tcol); val.Add(tval); placed = true; }
                if (c == tcol) { a += tval; placed = true; }
                idx.Add(c);
                val.Add(a);
            }
            if (!placed) { idx.Add(tcol); val.Add(tval); }
            ptr[i + 1] = idx.Count;
        }
        return new Csr(n, T.M, ptr, idx.ToArray(), val.ToArray());
    }

    void FactorCoarse()
    {
        var A = Levels[^1].A;
        int n = A.N;
        var L = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++) L[i, A.Idx[k]] = A.Val[k];
        for (int j = 0; j < n; j++)
        {
            double s = L[j, j];
            for (int k = 0; k < j; k++) s -= L[j, k] * L[j, k];
            L[j, j] = Math.Sqrt(s);
            for (int i = j + 1; i < n; i++)
            {
                double t = L[i, j];
                for (int k = 0; k < j; k++) t -= L[i, k] * L[j, k];
                L[i, j] = t / L[j, j];
            }
        }
        _coarse = L;
    }

    void CoarseSolve(double[] b, double[] x)
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

    static void GaussSeidel(Csr A, double[] d, double[] x, double[] b, bool forward)
    {
        int n = A.N;
        for (int s = 0; s < n; s++)
        {
            int i = forward ? s : n - 1 - s;
            double r = b[i];
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++) r -= A.Val[k] * x[A.Idx[k]];
            x[i] += r / d[i];
        }
    }

    public void VCycle(double[] r, double[] z) { Cycle(0, r, z); }

    void Cycle(int l, double[] b, double[] x)
    {
        var L = Levels[l];
        if (l == Levels.Count - 1) { CoarseSolve(b, x); return; }
        Array.Clear(x);
        // pre-smoothing: one symmetric Gauss-Seidel sweep (forward then backward)
        GaussSeidel(L.A, L.Diag, x, b, true);
        GaussSeidel(L.A, L.Diag, x, b, false);
        L.A.Mul(x, L.Tmp);
        for (int i = 0; i < L.A.N; i++) L.Res[i] = b[i] - L.Tmp[i];
        L.R!.Mul(L.Res, L.Bc);
        Cycle(l + 1, L.Bc, L.Xc);
        L.P!.Mul(L.Xc, L.Tmp);
        for (int i = 0; i < L.A.N; i++) x[i] += L.Tmp[i];
        // post-smoothing: the adjoint order, so the preconditioner is symmetric
        GaussSeidel(L.A, L.Diag, x, b, true);
        GaussSeidel(L.A, L.Diag, x, b, false);
    }
}

/// <summary>Zero-fill incomplete Cholesky as ILU(0) of the symmetric matrix (which yields U = D L'),
/// applied as (L)(U) solves. A non-positive pivot restarts with a growing diagonal shift.</summary>
sealed class Ic0
{
    Csr _lu = null!;
    int[] _diag = [];

    public static (Ic0, double) Build(Csr A)
    {
        foreach (double shift in new[] { 0.0, 1e-4, 1e-3, 1e-2, 1e-1 })
        {
            var ic = new Ic0();
            if (ic.TryFactor(A, shift)) return (ic, shift);
        }
        throw new InvalidOperationException("IC(0) broke down at every shift");
    }

    bool TryFactor(Csr A, double shift)
    {
        int n = A.N;
        var val = (double[])A.Val.Clone();
        var diag = new int[n];
        for (int i = 0; i < n; i++)
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++)
                if (A.Idx[k] == i) { diag[i] = k; val[k] *= 1 + shift; }
        var pos = new int[n];
        Array.Fill(pos, -1);
        for (int i = 0; i < n; i++)
        {
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++) pos[A.Idx[k]] = k;
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++)
            {
                int j = A.Idx[k];
                if (j >= i) break;
                double lij = val[k] / val[diag[j]];
                val[k] = lij;
                for (int m = diag[j] + 1; m < A.Ptr[j + 1]; m++)
                {
                    int p = pos[A.Idx[m]];
                    if (p >= 0) val[p] -= lij * val[m];
                }
            }
            if (val[diag[i]] <= 0) return false;
            for (int k = A.Ptr[i]; k < A.Ptr[i + 1]; k++) pos[A.Idx[k]] = -1;
        }
        _lu = new Csr(n, n, A.Ptr, A.Idx, val);
        _diag = diag;
        return true;
    }

    public void Apply(double[] r, double[] z)
    {
        int n = r.Length;
        var P = _lu.Ptr;
        var I = _lu.Idx;
        var V = _lu.Val;
        for (int i = 0; i < n; i++)
        {
            double s = r[i];
            for (int k = P[i]; k < _diag[i]; k++) s -= V[k] * z[I[k]];
            z[i] = s;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = z[i];
            for (int k = _diag[i] + 1; k < P[i + 1]; k++) s -= V[k] * z[I[k]];
            z[i] = s / V[_diag[i]];
        }
    }
}
