namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// Where a table or ANN constituent was trained (R-mc2-3): a convex polygon in (<c>vgs</c>, <c>vds</c>)
/// — the convex hull of its training points, possibly trimmed — and a closed interval for each other
/// input. Outside it, the constituent is continued by <see cref="GuidedExtrapolation"/>.
/// </summary>
public sealed record TrainingDomain(
    IReadOnlyList<(double Vgs, double Vds)> Polygon,
    IReadOnlyList<KeyValuePair<string, (double Lo, double Hi)>> Box)
{
    public static TrainingDomain Rectangle(double vgsLo, double vgsHi, double vdsLo, double vdsHi,
                                           params (string Input, double Lo, double Hi)[] box)
        => new([(vgsLo, vdsLo), (vgsHi, vdsLo), (vgsHi, vdsHi), (vgsLo, vdsHi)],
               [.. box.Select(b => new KeyValuePair<string, (double, double)>(b.Input, (b.Lo, b.Hi)))]);
}

/// <summary>
/// The training domain in normalised coordinates, and the nearest-point projection onto it.
///
/// <para><b>Normalised.</b> Each input is measured in units of the domain's own extent along it
/// (<c>u = (p − centre) / extent</c>), so a distance mixes volts and degrees on equal terms and
/// "5 % of the domain's extent" is one number, 0.05, on every axis.</para>
///
/// <para><b>The projection's Jacobian is what makes the continuation exactly differentiable.</b> In
/// the exterior of an edge it is <c>ê êᵀ</c> (motion along the edge moves the nearest point with
/// it); in a vertex's normal cone it is zero; along a box axis it is one inside the interval and zero
/// where the axis is clamped.</para>
/// </summary>
internal sealed class DomainGeometry
{
    private readonly int _d, _iVgs, _iVds;
    private readonly double[] _centre, _extent;
    private readonly double[] _px, _py;          // polygon vertices in u, counter-clockwise
    private readonly double[] _boxLo, _boxHi;    // per input, in u (unused for vgs/vds)

    public int InputCount => _d;
    public ReadOnlySpan<double> Extent => _extent;

    public DomainGeometry(TrainingDomain domain, IReadOnlyList<string> inputs)
    {
        _d = inputs.Count;
        _iVgs = IndexOf(inputs, ComposedCoreProgram.Vgs);
        _iVds = IndexOf(inputs, ComposedCoreProgram.Vds);
        _centre = new double[_d];
        _extent = new double[_d];
        _boxLo = new double[_d];
        _boxHi = new double[_d];

        var poly = domain.Polygon;
        double gLo = poly.Min(v => v.Vgs), gHi = poly.Max(v => v.Vgs);
        double dLo = poly.Min(v => v.Vds), dHi = poly.Max(v => v.Vds);
        Set(_iVgs, gLo, gHi);
        Set(_iVds, dLo, dHi);
        foreach (var (name, (lo, hi)) in domain.Box)
        {
            int k = IndexOf(inputs, name);
            Set(k, lo, hi);
            _boxLo[k] = (lo - _centre[k]) / _extent[k];
            _boxHi[k] = (hi - _centre[k]) / _extent[k];
        }

        int n = poly.Count;
        _px = new double[n];
        _py = new double[n];
        double area = 0.0;
        for (int k = 0; k < n; k++)
        {
            _px[k] = (poly[k].Vgs - _centre[_iVgs]) / _extent[_iVgs];
            _py[k] = (poly[k].Vds - _centre[_iVds]) / _extent[_iVds];
        }
        for (int k = 0; k < n; k++)
            area += _px[k] * _py[(k + 1) % n] - _px[(k + 1) % n] * _py[k];
        if (area < 0) { Array.Reverse(_px); Array.Reverse(_py); }
    }

    private void Set(int k, double lo, double hi)
    {
        _centre[k] = 0.5 * (lo + hi);
        _extent[k] = hi - lo;
    }

    private static int IndexOf(IReadOnlyList<string> inputs, string name)
    {
        for (int k = 0; k < inputs.Count; k++) if (inputs[k] == name) return k;
        throw new ArgumentException($"'{name}' is not an input.");
    }

    public void ToNormalised(ReadOnlySpan<double> p, Span<double> u)
    {
        for (int k = 0; k < _d; k++) u[k] = (p[k] - _centre[k]) / _extent[k];
    }

    public void ToPhysical(ReadOnlySpan<double> u, Span<double> p)
    {
        for (int k = 0; k < _d; k++) p[k] = _centre[k] + _extent[k] * u[k];
    }

    /// <summary>
    /// The nearest domain point <paramref name="ub"/> to <paramref name="u"/>, and the projection's
    /// Jacobian <paramref name="jac"/> (<c>d × d</c>, row-major, symmetric). Returns whether
    /// <paramref name="u"/> is inside (then <paramref name="ub"/> = <paramref name="u"/>).
    /// </summary>
    public bool Project(ReadOnlySpan<double> u, Span<double> ub, Span<double> jac)
    {
        int d = _d;
        jac[..(d * d)].Clear();
        bool inside = true;

        for (int k = 0; k < d; k++)
        {
            if (k == _iVgs || k == _iVds) continue;
            if (u[k] < _boxLo[k])      { ub[k] = _boxLo[k]; inside = false; }
            else if (u[k] > _boxHi[k]) { ub[k] = _boxHi[k]; inside = false; }
            else                       { ub[k] = u[k]; jac[k * d + k] = 1.0; }
        }

        double qx = u[_iVgs], qy = u[_iVds];
        int n = _px.Length;
        bool inPolygon = true;
        for (int k = 0; k < n && inPolygon; k++)
        {
            int m = (k + 1) % n;
            double cross = (_px[m] - _px[k]) * (qy - _py[k]) - (_py[m] - _py[k]) * (qx - _px[k]);
            if (cross < 0) inPolygon = false;
        }

        int g = _iVgs, v = _iVds;
        if (inPolygon)
        {
            ub[g] = qx; ub[v] = qy;
            jac[g * d + g] = 1.0; jac[v * d + v] = 1.0;
            return inside;
        }

        double best = double.PositiveInfinity, bx = 0, by = 0, ex = 0, ey = 0;
        bool onEdge = false;
        for (int k = 0; k < n; k++)
        {
            int m = (k + 1) % n;
            double sx = _px[m] - _px[k], sy = _py[m] - _py[k];
            double len2 = sx * sx + sy * sy;
            if (len2 == 0) continue;
            double t = ((qx - _px[k]) * sx + (qy - _py[k]) * sy) / len2;
            bool interior = t > 0 && t < 1;
            t = Math.Clamp(t, 0.0, 1.0);
            double cx = _px[k] + t * sx, cy = _py[k] + t * sy;
            double dist = (qx - cx) * (qx - cx) + (qy - cy) * (qy - cy);
            if (dist < best)
            {
                best = dist; bx = cx; by = cy; onEdge = interior;
                double len = Math.Sqrt(len2);
                ex = sx / len; ey = sy / len;
            }
        }

        ub[g] = bx; ub[v] = by;
        if (onEdge)
        {
            jac[g * d + g] = ex * ex; jac[g * d + v] = ex * ey;
            jac[v * d + g] = ey * ex; jac[v * d + v] = ey * ey;
        }
        return false;
    }
}

/// <summary>
/// A table or ANN constituent with its guided extrapolation ([P1] §6.2.8, R-mc2-3).
///
/// <para><b>Inside</b> the training domain it is the evaluator. <b>Outside</b>, with <c>p_b</c> the
/// nearest domain point:</para>
/// <code>
/// f(p) = f(p_b) + ∇f(p_b)·(p − p_b)                         (charges stop here)
///      + G_x · (v_own − v_own,b) · d / d₀                     (currents)
/// </code>
/// <para>with <c>d = |p − p_b|</c> and <c>d₀</c> 5 % of the domain's extent, both in normalised
/// coordinates. The last term is a conductance on the constituent's own branch voltage that grows with
/// distance and vanishes, with its derivative, at the boundary — the steeply rising conductance of
/// [P1] Fig. 6.12b, which pulls a Newton iterate back instead of letting it wander.</para>
///
/// <para><b>Local law</b> (<see cref="ExtrapolationMode.Local"/>, R-mc29-1): <c>G_x</c> in the added term is replaced by
/// <c>G_loc(p_b) = min(√((K·g_own)² + G_floor²), G_x)</c>, <c>g_own = ∇f(p_b)·own</c> the constituent's own-branch
/// conductance at the nearest domain point, so a current leaving an edge where the data is flat (a gate below pinch-off)
/// continues flat instead of picking up the forward diode's conductance. <c>G_x</c> stays the cap.</para>
///
/// <para><b>The gradient is exact, not the boundary's.</b> <c>p_b</c> moves with <c>p</c>, so the
/// derivative of the linear continuation is <c>∇f(p_b) + Pᵀ H(p_b)(p − p_b)</c>, with <c>P</c> the
/// projection's Jacobian — which is why the evaluators also return the Hessian along a direction.
/// Value and gradient are continuous across the boundary; across a polygon vertex's normal cone,
/// outside the domain, the gradient may jump (the brief allows it).</para>
/// </summary>
public sealed class GuidedExtrapolation
{
    /// <summary><c>d₀</c>: the distance over which the added conductance reaches <c>G_x</c>, as a
    /// fraction of the domain's extent.</summary>
    public const double BoundaryFraction = 0.05;

    /// <summary>The factor <c>K</c> on a conductance: MC-2's, applied to the largest in the training data for <c>G_x</c> and
    /// to the edge's own for the local law.</summary>
    public const double ConductanceFactor = 10.0;

    /// <summary><c>G_floor</c> as a fraction of <c>G_x</c>: 1e-6 of the largest own-branch conductance in the training data.
    /// A guide that small no longer moves a Newton iterate (a gate at −9 V draws ~0.1 mA through it on a 5 S device), and it
    /// keeps <c>G_loc</c> smooth through <c>g_own = 0</c>.</summary>
    public const double FloorFraction = 1e-6 / ConductanceFactor;

    /// <summary>The default <c>G_x</c>: ten times the largest conductance in the training data.</summary>
    public static double DefaultGx(double largestTrainingConductance) => ConductanceFactor * largestTrainingConductance;

    /// <summary>The local law's added conductance at an edge whose own-branch conductance is <paramref name="gOwn"/>:
    /// <c>min(√((K·g_own)² + G_floor²), G_x)</c> — the soft maximum of <c>K|g_own|</c> and the floor, capped by
    /// <paramref name="gx"/>.</summary>
    public static double LocalConductance(double gOwn, double gx)
    {
        double a = ConductanceFactor * gOwn, floor = FloorFraction * gx;
        return Math.Min(Math.Sqrt(a * a + floor * floor), gx);
    }

    private readonly RuntimeEvaluator _f;
    private readonly DomainGeometry _domain;
    private readonly double _gx;
    private readonly double[]? _own;
    private readonly bool _local;

    /// <param name="ownBranch">The constituent's own branch voltage as a combination of its inputs
    /// (<c>vds</c> for Id, <c>vgs</c> for IgGs, <c>vgs − vds</c> for IgGd), or null for a charge.</param>
    /// <param name="mode">The added conductance's law: <c>G_x</c> everywhere, or local to the edge and capped by it.</param>
    public GuidedExtrapolation(RuntimeEvaluator f, TrainingDomain domain, IReadOnlyList<string> inputs,
                               double gx, double[]? ownBranch, ExtrapolationMode mode = ExtrapolationMode.Global)
    {
        _f = f;
        _domain = new DomainGeometry(domain, inputs);
        _gx = gx;
        _own = ownBranch;
        _local = mode == ExtrapolationMode.Local;
    }

    public int InputCount => _domain.InputCount;

    /// <summary>The Hessian along <paramref name="w"/> into <paramref name="hw"/> where <paramref name="p"/> is inside the
    /// training domain — the evaluator's own; false outside, where the continuation's curvature is not formed.</summary>
    public bool HessianAlong(ReadOnlySpan<double> p, ReadOnlySpan<double> w, Span<double> hw)
    {
        int d = _domain.InputCount;
        Span<double> u   = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> ub  = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> jac = stackalloc double[RuntimeEvaluator.MaxInputs * RuntimeEvaluator.MaxInputs];
        Span<double> g   = stackalloc double[RuntimeEvaluator.MaxInputs];
        _domain.ToNormalised(p, u[..d]);
        if (!_domain.Project(u[..d], ub[..d], jac)) return false;
        _f.Evaluate(p, g[..d], w, hw);
        return true;
    }

    public double Evaluate(ReadOnlySpan<double> p, Span<double> grad)
    {
        int d = _domain.InputCount;
        Span<double> u   = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> ub  = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> jac = stackalloc double[RuntimeEvaluator.MaxInputs * RuntimeEvaluator.MaxInputs];
        u = u[..d]; ub = ub[..d];

        _domain.ToNormalised(p, u);
        if (_domain.Project(u, ub, jac)) return _f.Evaluate(p, grad);

        Span<double> pb = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> wp = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> gb = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> hw = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> gu = stackalloc double[RuntimeEvaluator.MaxInputs];
        pb = pb[..d]; wp = wp[..d];

        _domain.ToPhysical(ub, pb);
        double dist2 = 0.0;
        for (int k = 0; k < d; k++)
        {
            wp[k] = p[k] - pb[k];
            double wu = u[k] - ub[k];
            dist2 += wu * wu;
        }
        double dist = Math.Sqrt(dist2);

        double fb = _f.Evaluate(pb, gb, wp, hw);
        var ext = _domain.Extent;

        // The linear continuation, and its exact gradient in normalised coordinates:
        // D ∇f(p_b) + P (D H w).
        double value = fb;
        for (int k = 0; k < d; k++) value += gb[k] * wp[k];
        for (int k = 0; k < d; k++)
        {
            double s = ext[k] * gb[k];
            for (int m = 0; m < d; m++) s += jac[k * d + m] * ext[m] * hw[m];
            gu[k] = s;
        }

        if (_own is { } own && dist > 0)
        {
            // G_x · s · d/d₀ with s = own·(p − p_b): ∇_u s = (I − P)(D own), ∇_u d = (u − u_b)/d.
            double s = 0.0;
            for (int k = 0; k < d; k++) s += own[k] * wp[k];
            double gx = _gx;
            if (_local)
            {
                // G_loc(p_b): the soft maximum √(a² + floor²) of a = K·g_own and the floor, capped by G_x. It moves with
                // p_b, so its gradient is ∂G/∂a · K · P (D H own) below the cap and zero at it.
                double a = 0.0;
                for (int k = 0; k < d; k++) a += gb[k] * own[k];
                a *= ConductanceFactor;
                double floor = FloorFraction * _gx, soft = Math.Sqrt(a * a + floor * floor);
                if (soft < _gx)
                {
                    gx = soft;
                    Span<double> g2 = stackalloc double[RuntimeEvaluator.MaxInputs];
                    Span<double> ho = stackalloc double[RuntimeEvaluator.MaxInputs];
                    _f.Evaluate(pb, g2[..d], own, ho);
                    double c = ConductanceFactor * a / soft * s * dist / BoundaryFraction;
                    for (int k = 0; k < d; k++)
                    {
                        double dg = 0.0;
                        for (int m = 0; m < d; m++) dg += jac[k * d + m] * ext[m] * ho[m];
                        gu[k] += c * dg;
                    }
                }
            }
            double k0 = gx / BoundaryFraction;
            value += k0 * s * dist;
            for (int k = 0; k < d; k++)
            {
                double ds = ext[k] * own[k];
                for (int m = 0; m < d; m++) ds -= jac[k * d + m] * ext[m] * own[m];
                gu[k] += k0 * (ds * dist + s * (u[k] - ub[k]) / dist);
            }
        }

        for (int k = 0; k < d; k++) grad[k] = gu[k] / ext[k];
        return value;
    }
}
