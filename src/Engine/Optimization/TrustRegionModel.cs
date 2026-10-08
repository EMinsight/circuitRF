namespace CircuitRF.Engine.Optimization;

/// <summary>
/// A derivative-free trust-region method on an interpolating quadratic model (brief-tuneopt-7
/// R-to7-4), in the family of Powell's NEWUOA and BOBYQA and written from their published
/// descriptions: m(x_b + s) = f_b + gᵀs + ½sᵀHs interpolates the cost at <c>points</c> points (2n+1
/// by default — far fewer than the (n+1)(n+2)/2 a full quadratic needs), and the freedom that leaves
/// is taken up by changing H as little as possible, in the Frobenius norm, from the previous model.
/// That least-change model is the solution of one (p+n+1)-square linear system per iteration.
///
/// <para><b>An iteration.</b> The model is built around the best point x_b; its minimum over the
/// trust region ‖s‖∞ ≤ Δ intersected with the box is found (a projected-gradient search on the model,
/// which costs no simulation); the point is evaluated, and Δ grows or shrinks with how well the model
/// predicted the decrease. The new point replaces the interpolation point farthest from the best.</para>
///
/// <para><b>Two radii</b>, as in Powell's methods: Δ moves with each step, and the resolution ρ ≤ Δ
/// only falls. When the model is no help at the current resolution — its step is shorter than ρ/2 or
/// predicts no decrease — a point farther than 2ρ from the best is replaced by a geometry point at
/// distance ρ along the coordinate the near points cover least; when every point is already near, ρ
/// falls (by 10, then to √(ρ·minradius), then to <c>minradius</c>). The run ends when ρ has reached
/// <c>minradius</c> and the model still cannot improve.</para>
///
/// <para><b>Batch</b> = the 2n+1 initial points (x₀, x₀ ± Δeᵢ, stepping inward where a bound is
/// near), then one point.</para>
///
/// <para><b>Failed and infeasible points</b> (R-to7-8) are left out of the interpolation set; a failed
/// trial is a step too long, so Δ shrinks to a quarter of it.</para>
///
/// <para>Options and their defaults: <c>trust_region</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class TrustRegionModel : AskTellAlgorithm
{
    public const string AlgorithmId = "trust_region";

    private readonly int    _npt;
    private readonly double _radius0, _minRadius;

    public TrustRegionModel(double[] start, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        int n = start.Length;
        _npt       = Math.Clamp((int)Option(options, "points", 2 * n + 1), n + 2, (n + 1) * (n + 2) / 2);
        _radius0   = Math.Clamp(Option(options, "radius"), 1e-9, 0.5);
        _minRadius = Math.Min(Option(options, "minradius"), _radius0);
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        double delta = _radius0, rho = _radius0;

        // ── The initial set ──────────────────────────────────────────────────
        var init = new List<double[]> { (double[])Start.Clone() };
        for (int i = 0; i < n && init.Count < _npt; i++)
        {
            bool roomUp = Start[i] + delta <= 1, roomDown = Start[i] - delta >= 0;
            double[] steps = roomUp && roomDown ? [delta, -delta] : roomUp ? [delta, 2 * delta] : [-delta, -2 * delta];
            foreach (double step in steps)
            {
                if (init.Count >= _npt) break;
                var p = (double[])Start.Clone();
                p[i] = Math.Clamp(p[i] + step, 0, 1);
                init.Add(p);
            }
        }
        for (int i = 0; init.Count < _npt; i = (i + 1) % n)
        {
            int j = (i + 1 + init.Count / n) % n;
            if (j == i) continue;
            var p = (double[])Start.Clone();
            p[i] = Math.Clamp(p[i] + (p[i] + delta <= 1 ? delta : -delta), 0, 1);
            p[j] = Math.Clamp(p[j] + (p[j] + delta <= 1 ? delta : -delta), 0, 1);
            init.Add(p);
        }
        yield return [.. init];

        var Y = new List<double[]>();
        var F = new List<double>();
        for (int k = 0; k < init.Count; k++)
            if (!Results[k].Failed) { Y.Add(init[k]); F.Add(Results[k].Cost); }
        if (Y.Count == 0) { Finish("no initial point could be evaluated"); yield break; }

        var H = new double[n, n];
        int geometryCoordinate = 0;

        while (true)
        {
            int b = 0;
            for (int k = 1; k < Y.Count; k++) if (F[k] < F[b]) b = k;
            var xb = Y[b];
            double fb = F[b];

            // ── The least-change model around x_b ─────────────────────────────
            var g = Model(Y, F, b, rho, H, out var Hn);

            double[]? s = null;
            double pred = 0;
            if (g is not null)
            {
                H = Hn!;
                s = Step(g, H, xb, delta);
                pred = -(DenseLinear.Dot(g, s) + 0.5 * Quad(H, s));
            }

            double sInf = s is null ? 0 : s.Max(Math.Abs);
            if (s is null || sInf < 0.5 * rho || pred <= 1e-15 * Math.Max(1, Math.Abs(fb)))
            {
                // ── The model is no help here: improve the set, or refine ─────────
                int far = -1;
                double farDist = 2 * rho;
                for (int k = 0; k < Y.Count; k++)
                {
                    double dk = InfNorm(Y[k], xb);
                    if (k != b && dk > farDist) (far, farDist) = (k, dk);
                }
                if (far >= 0 || Y.Count < n + 1 || g is null)
                {
                    var p = GeometryPoint(Y, xb, rho, ref geometryCoordinate);
                    yield return [p];
                    CompleteIteration();
                    if (!Results[0].Failed)
                    {
                        if (far >= 0) { Y[far] = p; F[far] = Results[0].Cost; }
                        else Insert(Y, F, p, Results[0].Cost, _npt);
                    }
                    else if (far >= 0) { Y.RemoveAt(far); F.RemoveAt(far); }
                    continue;
                }
                if (rho <= _minRadius)
                {
                    Finish("the trust radius reached its minimum");
                    yield break;
                }
                rho = Shrink(rho);
                delta = Math.Max(delta / 2, rho);
                continue;
            }

            // ── The trial step ────────────────────────────────────────────────
            var xn = new double[n];
            for (int i = 0; i < n; i++) xn[i] = xb[i] + s[i];
            xn = Project(xn);
            yield return [xn];
            CompleteIteration();
            var e = Results[0];
            if (e.Failed)
            {
                delta = Math.Max(0.25 * sInf, rho);
                if (delta == rho && rho > _minRadius) rho = Shrink(rho);
                continue;
            }

            double ratio = (fb - e.Cost) / pred;
            if (ratio < 0.1)      delta = Math.Max(0.5 * delta, rho);
            else if (ratio > 0.7) delta = Math.Min(Math.Max(delta, 2 * sInf), 0.5);
            else                  delta = Math.Max(0.5 * delta, Math.Max(sInf, rho));

            Insert(Y, F, xn, e.Cost, _npt);

            // A poor prediction with a well-placed set means the resolution is the limit.
            if (ratio < 0.1 && delta <= rho && Y.All(y => InfNorm(y, xb) <= 2 * rho))
            {
                if (rho <= _minRadius) { Finish("the trust radius reached its minimum"); yield break; }
                rho = Shrink(rho);
            }
        }
    }

    private double Shrink(double rho)
        => rho > 250 * _minRadius ? rho / 10 : rho > 16 * _minRadius ? Math.Sqrt(rho * _minRadius) : _minRadius;

    /// <summary>Adds a point, dropping the one farthest from the best when the set is full.</summary>
    private static void Insert(List<double[]> Y, List<double> F, double[] x, double f, int npt)
    {
        Y.Add(x); F.Add(f);
        if (Y.Count <= npt) return;
        int b = 0;
        for (int k = 1; k < Y.Count; k++) if (F[k] < F[b]) b = k;
        int far = -1;
        double farDist = -1;
        for (int k = 0; k < Y.Count; k++)
        {
            if (k == b) continue;
            double d = InfNorm(Y[k], Y[b]);
            if (d > farDist) (far, farDist) = (k, d);
        }
        Y.RemoveAt(far); F.RemoveAt(far);
    }

    /// <summary>
    /// The least-change model: H' = H + Σ λₖsₖsₖᵀ with (c, g, λ) solving
    /// [A Xᵀ; X 0]·[λ; c; g] = [f − f_b − ½sᵀHs; 0], A_kl = ½(sₖ·s_l)², X the columns (1, sₖ). Solved in
    /// coordinates scaled by ρ for conditioning. Null when the set does not determine a model.
    /// </summary>
    internal static double[]? Model(List<double[]> Y, List<double> F, int b, double rho, double[,] H, out double[,]? Hn)
    {
        int n = Y[0].Length, p = Y.Count, size = p + n + 1;
        Hn = null;
        var S = new double[p][];
        for (int k = 0; k < p; k++)
        {
            S[k] = new double[n];
            for (int i = 0; i < n; i++) S[k][i] = (Y[k][i] - Y[b][i]) / rho;
        }
        // The previous H in scaled coordinates is ρ²H.
        var A = new double[size, size];
        var rhs = new double[size];
        for (int k = 0; k < p; k++)
        {
            for (int l = 0; l < p; l++)
            {
                double d = DenseLinear.Dot(S[k], S[l]);
                A[k, l] = 0.5 * d * d;
            }
            A[k, p] = 1; A[p, k] = 1;
            for (int i = 0; i < n; i++) { A[k, p + 1 + i] = S[k][i]; A[p + 1 + i, k] = S[k][i]; }
            rhs[k] = F[k] - F[b] - 0.5 * rho * rho * Quad(H, S[k]);
        }
        var sol = DenseLinear.Solve(A, rhs);
        if (sol is null || sol.Any(v => !double.IsFinite(v))) return null;

        Hn = (double[,])H.Clone();
        for (int k = 0; k < p; k++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) Hn[i, j] += sol[k] * S[k][i] * S[k][j] / (rho * rho);
        var g = new double[n];
        for (int i = 0; i < n; i++) g[i] = sol[p + 1 + i] / rho;
        return g;
    }

    /// <summary>sᵀHs.</summary>
    private static double Quad(double[,] H, double[] s)
    {
        double q = 0;
        for (int i = 0; i < s.Length; i++)
            for (int j = 0; j < s.Length; j++) q += s[i] * H[i, j] * s[j];
        return q;
    }

    /// <summary>
    /// The model's minimum over max(−Δ, −x) ≤ s ≤ min(Δ, 1 − x): projected gradient descent with a
    /// backtracking step, from the Cauchy direction. The model is cheap, so many steps cost nothing.
    /// </summary>
    internal static double[] Step(double[] g, double[,] H, double[] x, double delta)
    {
        int n = g.Length;
        var lo = new double[n]; var hi = new double[n];
        for (int i = 0; i < n; i++) { lo[i] = Math.Max(-delta, -x[i]); hi[i] = Math.Min(delta, 1 - x[i]); }
        double M(double[] s) => DenseLinear.Dot(g, s) + 0.5 * Quad(H, s);

        var s = new double[n];
        double ms = 0;
        double hNorm = 0;
        foreach (double v in H) hNorm += v * v;
        double alpha = delta / Math.Max(1e-300, g.Max(Math.Abs));
        if (hNorm > 0) alpha = Math.Min(alpha, 1 / Math.Sqrt(hNorm));
        alpha = Math.Max(alpha, 1e-12 * delta);

        for (int iter = 0; iter < 500; iter++)
        {
            var grad = new double[n];
            for (int i = 0; i < n; i++)
            {
                grad[i] = g[i];
                for (int j = 0; j < n; j++) grad[i] += H[i, j] * s[j];
            }
            bool moved = false;
            for (int tries = 0; tries < 40; tries++)
            {
                var t = new double[n];
                for (int i = 0; i < n; i++) t[i] = Math.Clamp(s[i] - alpha * grad[i], lo[i], hi[i]);
                double mt = M(t);
                if (mt < ms - 1e-16 * Math.Abs(ms))
                {
                    double change = InfNorm(t, s);
                    (s, ms) = (t, mt);
                    moved = change > 1e-14 * delta;
                    alpha *= 1.5;
                    break;
                }
                alpha *= 0.5;
            }
            if (!moved) break;
        }
        return s;
    }

    /// <summary>A point at distance ρ from the best along the coordinate the nearby points cover least.</summary>
    private static double[] GeometryPoint(List<double[]> Y, double[] xb, double rho, ref int next)
    {
        int n = xb.Length;
        var cover = new double[n];
        foreach (var y in Y)
        {
            if (InfNorm(y, xb) > 2 * rho) continue;
            for (int i = 0; i < n; i++) cover[i] = Math.Max(cover[i], Math.Abs(y[i] - xb[i]));
        }
        int c = next % n;
        for (int k = 0; k < n; k++)
        {
            int i = (next + k) % n;
            if (cover[i] < cover[c]) c = i;
        }
        next = c + 1;
        var p = (double[])xb.Clone();
        p[c] = xb[c] + rho <= 1 ? xb[c] + rho : xb[c] - rho;
        if (Y.Any(y => InfNorm(y, p) < 1e-3 * rho))
            p[c] = 2 * xb[c] - p[c] is >= 0 and <= 1 ? 2 * xb[c] - p[c] : xb[c] + 0.5 * (p[c] - xb[c]);
        return p;
    }
}
