namespace CircuitRF.Core.Devices.Composed;

/// <summary>The circuit-set capacitances of a composed FET's intrinsic charge (brief-modelcomposer-22), as flags.</summary>
[Flags]
public enum ChargeBranches
{
    None = 0,
    /// <summary><c>C11 + C12</c>, controlled by <c>vgs</c>, between gate and source.</summary>
    Cgs = 1,
    /// <summary><c>−C12</c>, controlled by <c>vgs − vds</c>, between gate and drain.</summary>
    Cdg = 2,
    /// <summary><c>C22 + C12</c>, controlled by <c>vds</c>, between drain and source.</summary>
    Cds = 4,
    /// <summary><c>C21 − C12</c>, the transcapacitance: drain charge controlled by <c>vgs</c>.</summary>
    Cm = 8,
    All = Cgs | Cdg | Cds | Cm,
}

/// <summary>
/// A terminal charge surface <c>Q̃(vgs, vds)</c>: the intrinsic charge with every input other than the two voltages held,
/// in the terminal frame (any drain–source image already applied).
/// </summary>
public interface IChargeSurface
{
    /// <summary>
    /// <paramref name="qg"/>, <paramref name="qd"/> and <c>C = ∂(Qg, Qd)/∂(vgs, vds)</c> row-major into <paramref name="c"/>
    /// (<c>C11 C12 C21 C22</c>). When <paramref name="dC"/> is not empty, also <c>∂C/∂vgs</c> then <c>∂C/∂vds</c> (eight
    /// numbers, each in <c>c</c>'s order) — returning false when the surface forms no exact second derivative there.
    /// </summary>
    bool Evaluate(double vgs, double vds, out double qg, out double qd, Span<double> c, Span<double> dC);
}

/// <summary>
/// One intrinsic capacitance at a time held at its bias-point value, the others following the model
/// (brief-modelcomposer-22 R-mc22-1). With <c>v_Q</c> the frozen bias point and <c>Δv = v − v_Q</c>, branch <c>b</c> with
/// controlling voltage <c>u_b</c> and terminal incidence <c>e_b</c> has the residual
/// <code>
///   r_b(v) = [ ∫₀¹ C_b(v_Q + s·Δv) ds − C_b(v_Q) ] · Δu_b,      q(v) = Q̃(v) − Σ_{b∈F} e_b · r_b(v)
/// </code>
/// <para>The charge stays a single-valued function of the voltages, so it conserves charge; <c>r_b</c> and its gradient
/// vanish at <c>v_Q</c>, so every combination has the model's own <c>C</c> there; and all four together are
/// <c>Q̃(v_Q) + C(v_Q)·Δv</c> — <c>LinearC</c> — by the fundamental theorem along the line, up to quadrature error. The
/// straight line from the bias point is the definition that makes those three hold.</para>
///
/// <para>The integral is <see cref="Nodes"/>-point Gauss–Legendre, and the Jacobian is the exact derivative of that sum:
/// <c>∇r_b = (I_b − C_b(v_Q))·∇u_b + Δu_b·Σ w_k s_k ∇C_b(v_k)</c>, whose last term needs the charge's second derivatives.
/// Where the surface forms none it is a central difference of the analytic <c>C</c> with a step of
/// <see cref="DifferenceStep"/>.</para>
/// </summary>
public static class BranchLinearisation
{
    /// <summary>Gauss–Legendre nodes along the line: exact for a cubic charge.</summary>
    public const int Nodes = 8;

    /// <summary>The central-difference step for a surface without second derivatives, V.</summary>
    public const double DifferenceStep = 1e-6;

    // Gauss–Legendre on [−1, 1], the positive half: ±x with weight w.
    private static readonly double[] X =
        [0.183434642495649804939476142360184, 0.525532409916328985817739049189246,
         0.796666477413626739591553936475831, 0.960289856497536231683560868569473];
    private static readonly double[] W =
        [0.362683783378361982965150449277196, 0.313706645877887287337962201986601,
         0.222381034453374470544355994426241, 0.101228536290376259152531354309962];

    /// <summary>Node <paramref name="k"/> (0 … <see cref="Nodes"/> − 1, in the order <see cref="Evaluate"/> sums them) of
    /// the rule on [0, 1]: its position <c>s</c> along the line from the bias point and its weight <c>w</c>. The Verilog-A
    /// export writes these, so the module integrates with the same numbers.</summary>
    public static (double S, double W) LineNode(int k)
    {
        double x = k < 4 ? -X[3 - k] : X[k - 4];
        return (0.5 * (1 + x), 0.5 * W[k < 4 ? 3 - k : k - 4]);
    }

    /// <summary>Branch <paramref name="b"/>'s controlling voltage over (vgs, vds) and its terminal incidence over (gate,
    /// drain).</summary>
    public static ((double G, double D) Control, (double G, double D) Incidence) Branch(int b) => (Du[b], E[b]);

    /// <summary>The branches' names in their fixed order: <c>Cgs</c>, <c>Cdg</c>, <c>Cds</c>, <c>Cm</c>.</summary>
    public static IEnumerable<string> Names(ChargeBranches set)
    {
        for (int b = 0; b < 4; b++)
            if ((set & (ChargeBranches)(1 << b)) != 0) yield return Name[b];
    }

    private static readonly string[] Name = ["Cgs", "Cdg", "Cds", "Cm"];

    // Per branch: the controlling voltage's gradient over (vgs, vds) and the terminal incidence over (g, d).
    private static readonly (double G, double D)[] Du = [(1, 0), (1, -1), (0, 1), (1, 0)];
    private static readonly (double G, double D)[] E  = [(1, 0), (1, -1), (0, 1), (0, 1)];

    /// <summary>Branch <paramref name="b"/>'s capacitance from a C matrix (or any linear image of one, such as its
    /// derivative).</summary>
    public static double Capacitance(int b, ReadOnlySpan<double> c) => b switch
    {
        0 => c[0] + c[1],
        1 => -c[1],
        2 => c[3] + c[1],
        _ => c[2] - c[1],
    };

    /// <summary>The four branch capacitances of <paramref name="c"/> into <paramref name="cb"/>.</summary>
    public static void Capacitances(ReadOnlySpan<double> c, Span<double> cb)
    {
        for (int b = 0; b < 4; b++) cb[b] = Capacitance(b, c);
    }

    /// <summary>
    /// The charge at <c>(vgs, vds)</c> with the branches of <paramref name="frozen"/> held at <paramref name="cbAtQ"/>
    /// (<see cref="Capacitances"/> of the surface at the bias point), and its C matrix into <paramref name="c"/>.
    /// </summary>
    public static void Evaluate<TSurface>(in TSurface surface, ChargeBranches frozen, double vgsQ, double vdsQ,
                                          ReadOnlySpan<double> cbAtQ, double vgs, double vds,
                                          out double qg, out double qd, Span<double> c)
        where TSurface : struct, IChargeSurface
    {
        surface.Evaluate(vgs, vds, out qg, out qd, c, []);
        double d0 = vgs - vgsQ, d1 = vds - vdsQ;
        if (frozen == ChargeBranches.None || (d0 == 0 && d1 == 0)) return;   // the residuals vanish at the bias point

        Span<double> ib = stackalloc double[4], gb = stackalloc double[8];
        Span<double> ck = stackalloc double[4], dck = stackalloc double[8];
        ib.Clear(); gb.Clear();
        for (int k = 0; k < Nodes; k++)
        {
            var (s, w) = LineNode(k);
            Node(surface, vgsQ + s * d0, vdsQ + s * d1, ck, dck);
            for (int b = 0; b < 4; b++)
            {
                if ((frozen & (ChargeBranches)(1 << b)) == 0) continue;
                ib[b] += w * Capacitance(b, ck);
                gb[2 * b]     += w * s * Capacitance(b, dck[..4]);
                gb[2 * b + 1] += w * s * Capacitance(b, dck[4..]);
            }
        }

        for (int b = 0; b < 4; b++)
        {
            if ((frozen & (ChargeBranches)(1 << b)) == 0) continue;
            var (ug, ud) = Du[b];
            var (eg, ed) = E[b];
            double excess = ib[b] - cbAtQ[b], du = ug * d0 + ud * d1;
            double r = excess * du;
            double r0 = excess * ug + du * gb[2 * b], r1 = excess * ud + du * gb[2 * b + 1];
            qg -= eg * r;
            qd -= ed * r;
            c[0] -= eg * r0; c[1] -= eg * r1;
            c[2] -= ed * r0; c[3] -= ed * r1;
        }
    }

    /// <summary>C and its derivatives at one node: the surface's own, or a central difference of its C.</summary>
    private static void Node<TSurface>(in TSurface surface, double vgs, double vds, Span<double> c, Span<double> dC)
        where TSurface : struct, IChargeSurface
    {
        if (surface.Evaluate(vgs, vds, out _, out _, c, dC)) return;
        Span<double> plus = stackalloc double[4], minus = stackalloc double[4];
        const double h = DifferenceStep;
        surface.Evaluate(vgs + h, vds, out _, out _, plus, []);
        surface.Evaluate(vgs - h, vds, out _, out _, minus, []);
        for (int i = 0; i < 4; i++) dC[i] = (plus[i] - minus[i]) / (2 * h);
        surface.Evaluate(vgs, vds + h, out _, out _, plus, []);
        surface.Evaluate(vgs, vds - h, out _, out _, minus, []);
        for (int i = 0; i < 4; i++) dC[4 + i] = (plus[i] - minus[i]) / (2 * h);
    }
}
