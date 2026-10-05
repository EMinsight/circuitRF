// brief-wbond-wire-temperature R-wbt-3 — one wire array's temperature, by conductive balance in one dimension.
//
// THE METHOD IS ConductiveBalance's (src/Thermal/Nonlinear/ConductiveBalance.cs), applied to wires that touch nothing but their two
// ends: a full Jacobian (the k(T) and σ(T) slopes in it); a step halved up to ten times while the scaled residual does not fall or the
// state is not physical; the stall rule; the same convergence test, with its span floor and its round-off floors; and its "already at
// the answer" start. src/WBond is a leaf and does not reference src/Thermal, so the constants are restated here and
// tests/Thermal.Tests holds each one equal to its original: the two cannot drift without a red test.
//
// THE MODEL. Each wire is a chain of second-order line elements along its 3D arc length (the length wBond's resistance is computed
// from), held at StartC at its input end and EndC at its output end, with adiabatic sides: −(k(T)·A·T′)′ = q′(T). With uniform heating
// and no side loss a wire's shape does not matter, only its length, diameter and metal. q′ is time-averaged (a wire's thermal time
// constant is milliseconds): q′ = I²/(σ(T)·A) + Σ_f ½|I_f|²·R′_ac(f, σ(T)), peak phasors.
//
// DC SHARING IS PART OF THE SOLVE. An array's wires are in parallel between the same two nodes, so they carry one voltage V and the
// array's current divides as I_i = V/R_i(T_i), R_i = ∫ds/(σ(T)·A). Unknowns: every interior temperature, each wire's current, and V.
// A Newton step eliminates each wire's banded temperature block first (O(n) per wire), then its current, then solves the one scalar V:
// the bordered system is never assembled dense. Arrays do not share DC current with each other, so each array is solved on its own.
//
// THE HARMONIC CURRENTS ARE GIVEN per wire (already split by the inductive share — WBondWireTemperature) and do not move with the
// temperature: the circuit is not re-solved (D1).
//
// START. A warm start takes a previous solution. A cold one is the exact ZERO-CURRENT solution: the conduction profile between the
// two ends (linear for a constant k, solved to round-off when k varies) with every current zero — so an array that carries none is
// "already at the answer" and takes no step, and a round-off current changes nothing (D8). No threshold anywhere picks between two
// answers: the start test is ConductiveBalance's, relative to the load.

namespace CircuitRF.WBond.Thermal;

/// <summary>One wire as the temperature solve takes it.</summary>
/// <param name="LengthM">Its 3D arc length, metres — the length its resistance is computed from.</param>
/// <param name="DiameterM">Its diameter, metres.</param>
/// <param name="Material">Its metal: σ(T) and k(T). Must state a thermal conductivity.</param>
public sealed record ThermalWireSpec(double LengthM, double DiameterM, WireMaterial Material);

/// <summary>What one array's wires carry: the array's DC current, and each wire's harmonic currents.</summary>
/// <param name="DcA">The array's DC current, A — divided among its wires by the solve.</param>
/// <param name="FrequenciesHz">Every non-DC frequency the run carries, Hz (harmonics, or mixing products).</param>
/// <param name="PeakA">Per wire, per frequency: the peak phasor magnitude in that wire, A.</param>
public sealed record WireArrayDrive(double DcA, IReadOnlyList<double> FrequenciesHz, IReadOnlyList<double[]> PeakA);

/// <summary>A state of one array's solve — a warm start for the next point.</summary>
public sealed class WireArrayState
{
    public WireArrayState(double[][] t, double[] i, double v) { T = t; I = i; V = v; }

    /// <summary>Per wire, the temperature at each chain node, °C, input end first.</summary>
    public double[][] T { get; }

    /// <summary>Per wire, its DC current, A.</summary>
    public double[] I { get; }

    /// <summary>The array's DC voltage, input minus output, V.</summary>
    public double V { get; internal set; }

    internal WireArrayState Clone() => new([.. T.Select(t => (double[])t.Clone())], (double[])I.Clone(), V);
}

/// <summary>One Newton solve's answer.</summary>
public sealed record WireArraySolution(WireArrayState State, bool Converged, int Iterations, double ResidualT, double ResidualP,
                                       double UpdateK, string? Failure)
{
    /// <summary>The hottest temperature anywhere along any wire, °C — each element's quadratic, not only its nodes.</summary>
    public required double MaxC { get; init; }
}

/// <summary>A point reached from zero current (R-wbt-3e): the target's solution, or the runaway bracket and the last state that
/// converged.</summary>
/// <param name="ConvergedAt">The drive scale λ (on every current) of <paramref name="LastConverged"/>.</param>
/// <param name="FailedAt">The lowest λ that did not converge (1 when the target converged).</param>
public sealed record WireArrayOutcome(WireArraySolution? Solution, bool Runaway, double ConvergedAt, double FailedAt,
                                      WireArraySolution? LastConverged, int Solves);

public static class WireConductiveBalance
{
    // ── ConductiveBalance's constants, restated (tests/Thermal.Tests holds each equal to its original) ──────────────────────────

    /// <summary><c>ConductiveBalance.Absurd</c>: a temperature past which a state is a divergence, not a solution, °C.</summary>
    public const double Absurd = 1e7;

    /// <summary><c>ConductiveBalance.StallStep</c>: a step this fraction of Newton's or less is damped.</summary>
    public const double StallStep = 0.125;

    /// <summary><c>ThermalSolver.SpanFloorK</c>: the temperature span the update test is floored at, K.</summary>
    public const double SpanFloorK = 1e-3;

    /// <summary><c>ThermalSolveOptions.NewtonTolerance</c>'s default: the update, relative to the span, that converges.</summary>
    public const double NewtonTolerance = 1e-6;

    /// <summary><c>ThermalSolveOptions.NewtonMaxIterations</c>'s default.</summary>
    public const int NewtonMaxIterations = 30;

    /// <summary><c>Continuation.Bracket</c>: the relative width of a drive bracket at which a runaway is declared.</summary>
    public const double Bracket = 0.005;

    /// <summary>
    /// Second-order elements per wire, chosen by measurement (G1, <c>WireConductiveBalanceTests.ElementCount_Converged</c>; the
    /// numbers are in src/WBond/RESOLVED.md): on a 1 mil gold wire run near 600 °C with both tables on, halving the element size
    /// from this count moves the maximum by far less than 0.01 K.
    /// </summary>
    public const int Elements = 16;

    private static readonly double[] GaussXi = [-Math.Sqrt(0.6), 0.0, Math.Sqrt(0.6)];
    private static readonly double[] GaussW = [5.0 / 9.0, 8.0 / 9.0, 5.0 / 9.0];

    /// <summary>The exact zero-current state for <paramref name="elements"/> elements per wire: each wire's conduction profile
    /// between the two ends, every current and V zero.</summary>
    public static WireArrayState ColdStart(IReadOnlyList<ThermalWireSpec> wires, double startC, double endC, int elements = Elements)
    {
        ArgumentNullException.ThrowIfNull(wires);
        var t = new double[wires.Count][];
        for (int w = 0; w < wires.Count; w++)
        {
            t[w] = Linear(startC, endC, elements);
            if (wires[w].Material.ThermalKVsTemp is { Count: > 1 } && startC != endC) ConductionProfile(wires[w], t[w], elements);
        }
        return new WireArrayState(t, new double[wires.Count], 0.0);
    }

    /// <summary>
    /// Solves one array at drive scale <paramref name="lambda"/> (every current multiplied by it) by Newton's method, from
    /// <paramref name="start"/>; from a cold start when it is null or does not fit.
    /// </summary>
    public static WireArraySolution Solve(IReadOnlyList<ThermalWireSpec> wires, double startC, double endC, WireArrayDrive drive,
                                          double lambda = 1.0, WireArrayState? start = null, int elements = Elements)
    {
        ArgumentNullException.ThrowIfNull(wires);
        ArgumentNullException.ThrowIfNull(drive);
        if (wires.Count == 0) throw new ArgumentException("An array has at least one wire.", nameof(wires));
        if (drive.PeakA.Count != wires.Count)
            throw new ArgumentException($"{drive.PeakA.Count} wires' harmonic currents for {wires.Count} wires.", nameof(drive));
        var sys = new ArraySystem(wires, startC, endC, drive, lambda, elements);
        // a drive with no current at all is answered from the exact zero-current state, whatever start was offered: that IS its
        // solution, so it takes no step (D8)
        bool fits = start is not null && start.T.Length == wires.Count && start.T.All(t => t.Length == 2 * elements + 1)
                    && !sys.CarriesNothing;
        var x = fits ? start!.Clone() : ColdStart(wires, startC, endC, elements);
        sys.Pin(x);
        sys.ShareCurrent(x);
        return sys.Newton(x);
    }

    /// <summary>
    /// R-wbt-3e — the point at full drive, from <paramref name="warm"/> (a previous point's state) or a cold start; and if that
    /// fails, by bisecting the drive scale λ from zero current (whose solution is the cold start, exactly) toward 1, as
    /// <c>Continuation.Advance</c> does: a converged λ becomes the last converged and the lowest known failure is tried again from
    /// it; a failure narrows the bracket; a bracket <see cref="Bracket"/> wide is a runaway. Running out of solves with the bracket
    /// open is NOT a runaway. Its fold prediction is not ported: it exists to save 3D solves, and a 1D one costs microseconds.
    /// </summary>
    public static WireArrayOutcome Advance(IReadOnlyList<ThermalWireSpec> wires, double startC, double endC, WireArrayDrive drive,
                                           WireArrayState? warm = null, int maxSolves = 60)
    {
        int solves = 1;
        var direct = Solve(wires, startC, endC, drive, 1.0, warm);
        if (direct.Converged) return new(direct, false, 1, 1, direct, solves);

        var last = Solve(wires, startC, endC, drive, 0.0);
        solves++;
        if (!last.Converged) return new(null, false, 0, 0, null, solves);

        double lo = 0, hi = 1, t = 0.5;     // λ = 1 has just failed: bisect at once
        while (solves < maxSolves)
        {
            var s = Solve(wires, startC, endC, drive, t, last.State);
            solves++;
            if (s.Converged)
            {
                lo = t;
                last = s;
                if (t >= 1) return new(s, false, 1, 1, s, solves);
                if (t >= hi) hi = 1;        // the lowest failure converged from nearer: a step too long, not the limit
                t = hi;
                continue;
            }
            hi = t;
            if ((hi - lo) / hi <= Bracket) return new(null, true, lo, hi, last, solves);
            t = (lo + hi) / 2;
        }
        return new(null, false, lo, hi, last, solves);
    }

    // ── the discretisation ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static double[] Linear(double a, double b, int elements)
    {
        var t = new double[2 * elements + 1];
        for (int j = 0; j < t.Length; j++) t[j] = j == t.Length - 1 ? b : a + (b - a) * j / (t.Length - 1);
        return t;
    }

    /// <summary>The zero-current profile of a wire whose k varies, in place, from the linear one: Newton to round-off. The
    /// conduction problem is monotone, so full steps converge; it stops when the residual reaches its floor or stops falling.</summary>
    private static void ConductionProfile(ThermalWireSpec wire, double[] t, int elements)
    {
        var sys = new ArraySystem([wire], t[0], t[^1], new WireArrayDrive(0, [], [[]]), 0.0, elements);
        var x = new WireArrayState([t], [0.0], 0.0);
        double previous = double.PositiveInfinity;
        for (int it = 0; it < NewtonMaxIterations; it++)
        {
            var a = sys.Assemble(x);
            double r = Norm(a.RT);
            if (a.Invalid || r <= a.FloorAbsT || !(r < previous)) break;
            previous = r;
            var d = sys.Step(a, x);
            if (d is null) break;
            for (int j = 1; j < t.Length - 1; j++) t[j] += d.T[0][j];
        }
    }

    private static double Norm(double[] v)
    {
        double s = 0;
        foreach (double x in v) s += x * x;
        return Math.Sqrt(s);
    }

    /// <summary>The hottest point of a wire's chain: each element's quadratic, at its nodes and at its vertex when that lies inside.</summary>
    internal static double ChainMax(double[] t)
    {
        double max = double.NegativeInfinity;
        for (int e = 0; 2 * e + 2 < t.Length; e++)
        {
            double t0 = t[2 * e], t1 = t[2 * e + 1], t2 = t[2 * e + 2];
            max = Math.Max(max, Math.Max(t0, Math.Max(t1, t2)));
            double b = (t2 - t0) / 2, c = (t0 - 2 * t1 + t2) / 2;
            if (c < 0 && Math.Abs(b) <= -2 * c) max = Math.Max(max, t1 - b * b / (4 * c));
        }
        return max;
    }

    /// <summary>One assembly: the residual and its Jacobian at a state.</summary>
    private sealed class Assembled
    {
        public required double[] RT;           // the free T rows, every wire's in turn, W
        public required double[] RP;           // per wire (R·I − V)/R, then Σ I − λ·I_dc, A
        public required double[][] Band;       // per wire, its free rows' Jacobian, 5 diagonals
        public required double[][] JTI;        // per wire, ∂r_T/∂I on its free rows
        public required double[][] JIT;        // per wire, ∂(R·I − V)/∂T on its free columns
        public required double[] R;            // per wire, its DC resistance, Ω
        public required double[] G;            // per wire, R·I − V, V
        public required double H;              // Σ I − λ·I_dc, A
        public required double HeatNorm;       // ‖the heat load on the free rows‖, W
        public required double LiftedLoadNorm; // ‖heat load − K·(fixed ends)‖ on the free rows, W
        public required double FloorAbsT, FloorAbsP;
        public required bool Invalid;
    }

    /// <summary>A Newton step: per wire, its temperature change at every node (0 at the two ends) and its current change; and V's.</summary>
    private sealed record Delta(double[][] T, double[] I, double V);

    /// <summary>One array's equations at one drive scale.</summary>
    private sealed class ArraySystem(IReadOnlyList<ThermalWireSpec> wires, double startC, double endC, WireArrayDrive drive, double lambda,
                                     int elements)
    {
        private readonly int _m = wires.Count;
        private readonly int _n = 2 * elements + 1;      // nodes per wire
        private int Free => _n - 2;

        public void Pin(WireArrayState x)
        {
            foreach (var t in x.T) { t[0] = startC; t[^1] = endC; }
        }

        /// <summary>Whether this drive carries no current at all: no DC and every harmonic zero.</summary>
        public bool CarriesNothing => lambda * drive.DcA == 0 && drive.PeakA.All(p => p.All(v => lambda * v == 0));

        /// <summary>
        /// The starting currents: the array's DC (at this drive scale) divided by conductance at the start's temperatures. Applied to
        /// a warm start too — it is the predictor for the new drive, and without it a start from a lower drive carries the old
        /// heat, which the line search measures its steps against: from zero current that is round-off, and every step that heats
        /// the wire looks like a blow-up. Zero current is exactly zero, and a negated current exactly negated.
        /// </summary>
        public void ShareCurrent(WireArrayState x)
        {
            double idc = lambda * drive.DcA;
            if (idc == 0)
            {
                Array.Clear(x.I);
                x.V = 0;
                return;
            }
            var g = new double[_m];
            double sum = 0;
            for (int w = 0; w < _m; w++) { g[w] = 1.0 / Resistance(w, x.T[w]); sum += g[w]; }
            x.V = idc / sum;
            for (int w = 0; w < _m; w++) x.I[w] = g[w] * x.V;
        }

        private double Resistance(int w, double[] t)
        {
            var spec = wires[w];
            double area = Math.PI * spec.DiameterM * spec.DiameterM / 4, jac = spec.LengthM / elements / 2, r = 0;
            for (int e = 0; e < elements; e++)
                for (int g = 0; g < 3; g++)
                {
                    double xi = GaussXi[g];
                    double tg = xi * (xi - 1) / 2 * t[2 * e] + (1 - xi * xi) * t[2 * e + 1] + xi * (xi + 1) / 2 * t[2 * e + 2];
                    r += GaussW[g] * jac / (spec.Material.SigmaWithSlopeAt(tg).Sigma * area);
                }
            return r;
        }

        public Assembled Assemble(WireArrayState x)
        {
            int nf = Free;
            var rt = new double[_m * nf];
            var band = new double[_m][];
            var jti = new double[_m][];
            var jit = new double[_m][];
            var res = new double[_m];
            var gres = new double[_m];
            var rowMagT = new double[_m * nf];
            double heat2 = 0, lifted2 = 0;
            bool invalid = false;
            var n = new double[3];
            var dn = new double[3];
            double sumI = 0, sumAbsI = 0, rowP2 = 0;

            for (int w = 0; w < _m; w++)
            {
                var spec = wires[w];
                var t = x.T[w];
                double i = x.I[w];
                double area = Math.PI * spec.DiameterM * spec.DiameterM / 4, h = spec.LengthM / elements, jac = h / 2;
                double radius = spec.DiameterM / 2;
                var peaks = drive.PeakA[w];
                var b = band[w] = new double[nf * 5];
                var cti = jti[w] = new double[nf];
                var cit = jit[w] = new double[nf];
                var load = new double[_n];
                var lift = new double[_n];
                var r = new double[_n];
                var mag = new double[_n];
                double rw = 0;

                for (int e = 0; e < elements; e++)
                {
                    for (int g = 0; g < 3; g++)
                    {
                        double xi = GaussXi[g];
                        n[0] = xi * (xi - 1) / 2; n[1] = 1 - xi * xi; n[2] = xi * (xi + 1) / 2;
                        dn[0] = (xi - 0.5) / jac; dn[1] = -2 * xi / jac; dn[2] = (xi + 0.5) / jac;
                        double tg = 0, tp = 0;
                        for (int a = 0; a < 3; a++) { tg += n[a] * t[2 * e + a]; tp += dn[a] * t[2 * e + a]; }

                        var (k, dk) = spec.Material.ThermalKAt(tg);
                        var (sg, ds) = spec.Material.SigmaWithSlopeAt(tg);
                        if (!(sg > 0) || !double.IsFinite(sg) || !(k > 0) || Math.Abs(tg) > Absurd || !double.IsFinite(tg)
                            || spec.Material.BeyondTables(tg))
                        {
                            invalid = true;
                            sg = Math.Max(sg, double.Epsilon);     // keep the numbers finite; the state is refused by Invalid
                            k = Math.Max(k, double.Epsilon);
                        }

                        // q′ = I²/(σA) + Σ ½(λ·Î)²·R′_ac(f, σ): DC through this wire, and its harmonics (a zero harmonic asks for no R′_ac)
                        double q = i * i / (sg * area), dq = -i * i * ds / (sg * sg * area), dqdi = 2 * i / (sg * area);
                        for (int f = 0; f < peaks.Length; f++)
                        {
                            double pk = lambda * peaks[f];
                            if (pk == 0) continue;
                            var (rac, slope) = InternalImpedance.ResistanceWithSigmaSlope(drive.FrequenciesHz[f], radius, sg);
                            q += 0.5 * pk * pk * rac;
                            dq += 0.5 * pk * pk * slope * ds;
                        }

                        double wj = GaussW[g] * jac;
                        rw += wj / (sg * area);
                        for (int a = 0; a < 3; a++)
                        {
                            int ia = 2 * e + a;
                            r[ia] += wj * (k * area * tp * dn[a] - n[a] * q);
                            load[ia] += wj * n[a] * q;
                            mag[ia] += Math.Abs(wj * n[a] * q);
                            for (int c = 0; c < 3; c++)
                            {
                                int ib = 2 * e + c;
                                double kab = wj * k * area * dn[a] * dn[c];
                                mag[ia] += Math.Abs(kab * t[ib]);
                                if (ib == 0 || ib == _n - 1) lift[ia] += kab * t[ib];
                                if (ia == 0 || ia == _n - 1 || ib == 0 || ib == _n - 1) continue;
                                double jab = kab + wj * dk * n[c] * area * tp * dn[a] - wj * n[a] * n[c] * dq;
                                b[(ia - 1) * 5 + (ib - ia + 2)] += jab;
                            }
                            if (ia > 0 && ia < _n - 1)
                            {
                                cti[ia - 1] += -wj * n[a] * dqdi;
                                cit[ia - 1] += wj * i * (-ds / (sg * sg * area)) * n[a];
                            }
                        }
                    }
                }

                for (int j = 1; j < _n - 1; j++)
                {
                    rt[w * nf + j - 1] = r[j];
                    rowMagT[w * nf + j - 1] = mag[j];
                    heat2 += load[j] * load[j];
                    lifted2 += (load[j] - lift[j]) * (load[j] - lift[j]);
                }
                res[w] = rw;
                gres[w] = rw * i - x.V;
                sumI += i;
                sumAbsI += Math.Abs(i);
                double rowP = Math.Abs(i) + Math.Abs(x.V) / rw;
                rowP2 += rowP * rowP;
            }

            double idc = lambda * drive.DcA;
            var rp = new double[_m + 1];
            for (int w = 0; w < _m; w++) rp[w] = gres[w] / res[w];
            rp[_m] = sumI - idc;
            double rowH = sumAbsI + Math.Abs(idc);
            rowP2 += rowH * rowH;

            const double ulps = 1000 * 2.220446049250313e-16;
            return new Assembled
            {
                RT = rt, RP = rp, Band = band, JTI = jti, JIT = jit, R = res, G = gres, H = sumI - idc,
                HeatNorm = Math.Sqrt(heat2), LiftedLoadNorm = Math.Sqrt(lifted2),
                FloorAbsT = ulps * Norm(rowMagT), FloorAbsP = ulps * Math.Sqrt(rowP2), Invalid = invalid,
            };
        }

        /// <summary>
        /// J·δ = −r by block elimination: per wire, its banded T block (LU, no pivoting: on the stable branch it is the conduction
        /// stiffness less a heat slope smaller than it) against two right-hand sides; then its current; then V, a scalar. Null when
        /// a pivot vanishes or the step is not finite — the Jacobian is singular there.
        /// </summary>
        public Delta? Step(Assembled a, WireArrayState x)
        {
            int nf = Free;
            var at = new double[_m][];
            var bt = new double[_m][];
            var c = new double[_m];
            var s = new double[_m];
            for (int w = 0; w < _m; w++)
            {
                var lu = (double[])a.Band[w].Clone();
                if (!Factor(lu, nf)) return null;
                var ra = new double[nf];
                var rb = new double[nf];
                for (int j = 0; j < nf; j++) { ra[j] = -a.RT[w * nf + j]; rb[j] = -a.JTI[w][j]; }
                SolveBand(lu, nf, ra);
                SolveBand(lu, nf, rb);
                at[w] = ra;
                bt[w] = rb;
                double ja = 0, jb = 0;
                for (int j = 0; j < nf; j++) { ja += a.JIT[w][j] * ra[j]; jb += a.JIT[w][j] * rb[j]; }
                c[w] = -a.G[w] - ja;
                s[w] = a.R[w] + jb;
            }
            double num = -a.H, den = 0;
            for (int w = 0; w < _m; w++) { num -= c[w] / s[w]; den += 1 / s[w]; }
            double dv = num / den;
            var dt = new double[_m][];
            var di = new double[_m];
            for (int w = 0; w < _m; w++)
            {
                di[w] = (c[w] + dv) / s[w];
                var d = dt[w] = new double[_n];
                for (int j = 0; j < nf; j++) d[j + 1] = at[w][j] + bt[w][j] * di[w];
                if (!double.IsFinite(di[w]) || d.Any(v => !double.IsFinite(v))) return null;
            }
            return double.IsFinite(dv) ? new Delta(dt, di, dv) : null;
        }

        /// <summary>LU of a band of two sub- and two super-diagonals in place (entry (i, j) at i·5 + j − i + 2), no pivoting.</summary>
        private static bool Factor(double[] b, int n)
        {
            for (int k = 0; k < n; k++)
            {
                double p = b[k * 5 + 2];
                if (p == 0 || !double.IsFinite(p)) return false;
                for (int i = k + 1; i <= Math.Min(k + 2, n - 1); i++)
                {
                    double l = b[i * 5 + (k - i + 2)] / p;
                    b[i * 5 + (k - i + 2)] = l;
                    for (int j = k + 1; j <= Math.Min(k + 2, n - 1); j++) b[i * 5 + (j - i + 2)] -= l * b[k * 5 + (j - k + 2)];
                }
            }
            return true;
        }

        private static void SolveBand(double[] lu, int n, double[] x)
        {
            for (int i = 0; i < n; i++)
                for (int k = Math.Max(0, i - 2); k < i; k++) x[i] -= lu[i * 5 + (k - i + 2)] * x[k];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = i + 1; j <= Math.Min(i + 2, n - 1); j++) x[i] -= lu[i * 5 + (j - i + 2)] * x[j];
                x[i] /= lu[i * 5 + 2];
            }
        }

        private void Apply(WireArrayState from, Delta d, double step, WireArrayState into)
        {
            for (int w = 0; w < _m; w++)
            {
                for (int j = 0; j < _n; j++) into.T[w][j] = from.T[w][j] + step * d.T[w][j];
                into.I[w] = from.I[w] + step * d.I[w];
            }
            into.V = from.V + step * d.V;
        }

        private bool Diverged(WireArrayState x)
        {
            foreach (var t in x.T)
                foreach (double v in t)
                    if (!double.IsFinite(v) || Math.Abs(v) > Absurd) return true;
            return false;
        }

        private static double Span(WireArrayState x)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var t in x.T) foreach (double v in t) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return hi - lo;
        }

        /// <summary>ConductiveBalance.Solve's loop, on this system.</summary>
        public WireArraySolution Newton(WireArrayState x)
        {
            var a = Assemble(x);
            double nT = Norm(a.RT), nP = Norm(a.RP);
            double refT = Math.Max(nT, a.LiftedLoadNorm), refP = Math.Max(nP, Math.Abs(lambda * drive.DcA));
            if (!(refT > 0)) refT = 1;
            if (!(refP > 0)) refP = 1;
            double floorT = a.FloorAbsT / refT, floorP = a.FloorAbsP / refP;
            // the line search's heat scale: the heat the step is balancing, not refT (which the ends' lift dominates)
            double heatT = Math.Max(nT, a.HeatNorm);
            if (!(heatT > 0)) heatT = refT;
            int its;
            bool converged = false;
            double upd = double.PositiveInfinity, resT = nT / refT, resP = nP / refP;
            string? failure = a.Invalid ? "the starting state is not physical (a conductivity at or below zero, or past a table's top row)" : null;
            var damped = new List<double>();
            var trial = x.Clone();
            // a start that already is the answer (no current, say) takes no step: its residual is round-off, and a step "down"
            // from round-off is noise
            bool atStart = failure is null && nT + nP <= 1e-12 * (refT + refP);
            if (atStart) (converged, upd) = (true, 0);
            for (its = 1; !atStart && failure is null && its <= NewtonMaxIterations; its++)
            {
                var d = Step(a, x);
                if (d is null) { failure = "a Newton step was not finite (the Jacobian is singular there)"; break; }
                double merit = Norm(a.RT) / heatT + resP, step = 2;
                Assembled? next = null;
                bool accepted = false;
                for (int halving = 0; halving <= 10; halving++)
                {
                    step /= 2;
                    Apply(x, d, step, trial);
                    if (Diverged(trial)) continue;
                    next = Assemble(trial);
                    if (next.Invalid) continue;
                    double m = Norm(next.RT) / heatT + Norm(next.RP) / refP;
                    if (m < merit || resT <= floorT && resP <= floorP) { accepted = true; break; }
                }
                if (!accepted || next is null)
                {
                    failure = "no step along Newton's direction lowered the residual while staying physical";
                    break;
                }
                double maxD = 0;
                foreach (var dt in d.T) foreach (double v in dt) maxD = Math.Max(maxD, Math.Abs(v));
                upd = step * maxD;
                (x, trial) = (trial, x);
                a = next;
                if (step <= StallStep) damped.Add(merit); else damped.Clear();
                resT = Norm(a.RT) / refT;
                resP = Norm(a.RP) / refP;
                floorT = a.FloorAbsT / refT;
                floorP = a.FloorAbsP / refP;
                if (upd <= NewtonTolerance * Math.Max(Span(x), SpanFloorK) && resT <= Math.Max(1e-8, floorT) && resP <= Math.Max(1e-8, floorP))
                { converged = true; break; }
                if (damped.Count >= 3 && Norm(a.RT) / heatT + resP > 0.5 * damped[^3])
                {
                    failure = $"Newton stalled: three damped steps in a row (at most {StallStep:G3} of a full step) did not halve the residual";
                    break;
                }
            }
            if (atStart) its = 0;
            if (its > NewtonMaxIterations) { its = NewtonMaxIterations; failure ??= $"it did not converge in {NewtonMaxIterations} Newton steps"; }
            return new WireArraySolution(x, converged, its, resT, resP, upd, converged ? null : failure)
            {
                MaxC = x.T.Max(ChainMax),
            };
        }
    }
}
