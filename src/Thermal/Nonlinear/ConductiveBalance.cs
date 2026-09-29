// brief-em3d-77 R-em3d77-4 — conductive balance: Newton's method on the coupled temperature and potential, with the full
// Jacobian ElectrothermalSystem assembles; and (R-em3d77-4d) the fixed-point reference it is tested against.
//
// START. A warm start takes the previous sweep point's state. A cold one puts every temperature at the fixed faces' mean (or
// the convection ambients'), φ at 0, and makes ONE fixed-point sweep — φ at that σ, then T at that heat — so Newton begins
// with a current flowing: from φ = 0 the Joule term's derivative is zero and Newton's first step would be that sweep anyway.
//
// A STEP. J·δ = −r on the free unknowns (fixed temperatures and each conductor's reference potential eliminated), LU on the
// direct path and BiCGStab with a block AMG on the iterative one. The step is halved (up to ten times) while the scaled
// residual ‖r_T‖/ref_T + ‖r_φ‖/ref_φ does not fall or the state is not physical — a conductivity at or below zero, which is
// where a linear ρ(T) goes past its pole: the other branch of a wire past its runaway current solves the equations with a
// NEGATIVE resistance, and must never be taken for an answer.
//
// CONVERGED (R-em3d77-4c): the largest temperature update below Tolerance × the temperature span, AND each residual down by
// 1e-8 of its reference (the larger of its starting residual and its reduced load, as brief 74's Newton, so a warm start
// already at the answer is not asked to fall by 1e-8 of nothing) — or to its ROUND-OFF FLOOR, when that is higher: a residual
// is a difference of terms (a perfect bond's conductance times a potential, say) and cannot be smaller than a few hundred
// ulps of them, whatever the step. The floor is 1000·ε·‖|S||x| + |L|‖ over the block's free rows. A point that does not converge says so; whether that is a
// runaway is the continuation's question (Continuation.cs), never this one's.

using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Solvers;

namespace CircuitRF.Thermal.Nonlinear;

/// <summary>A conductive-balance solve's answer.</summary>
public sealed class ElectrothermalSolution
{
    /// <summary>The thermal answer as brief 74's run reads it: the mesh's temperatures and the energy balance (the Joule heat
    /// counted with the sources).</summary>
    public required ThermalSolution Thermal { get; init; }
    /// <summary>The whole state, [T | φ], as <see cref="ElectrothermalSystem"/> numbers it — a warm start for the next point.</summary>
    public required double[] State { get; init; }
    /// <summary>Per wire, the temperature at each chain node, °C.</summary>
    public required double[][] WireTemperature { get; init; }
    public required bool Converged { get; init; }
    public required int Iterations { get; init; }
    public required double ResidualT { get; init; }
    public required double ResidualPhi { get; init; }
    public required double UpdateK { get; init; }
    /// <summary>Why it stopped, when it did not converge.</summary>
    public string? Failure { get; init; }
    public required double JouleW { get; init; }
    public required double[] JouleByRegion { get; init; }
    public required double[] JouleByWire { get; init; }
    public required double[] WireCurrentA { get; init; }
    public required double[] WireResistanceOhm { get; init; }
    /// <summary>Per current terminal (in the problem's order): V(positive) − V(negative).</summary>
    public required double[] PortVoltage { get; init; }
    /// <summary>Σ V·I over the ports: the electrical power in, which the Joule heat must equal.</summary>
    public required double ElectricalPowerW { get; init; }
    /// <summary>brief-em3d-78 — the RF heat, W: in all, per wire, and per wire per harmonic.</summary>
    public double RfW { get; init; }
    public double[] RfByWire { get; init; } = [];
    public double[][] RfByHarmonic { get; init; } = [];
}

public static class ConductiveBalance
{
    /// <summary>A temperature past which a state is not a solution but a divergence, °C.</summary>
    public const double Absurd = 1e7;

    /// <summary>Solves <paramref name="problem"/> by Newton's method. <paramref name="system"/> may be reused across sweep points
    /// of one mesh; <paramref name="warm"/> is the previous point's <see cref="ElectrothermalSolution.State"/>.</summary>
    public static ElectrothermalSolution Solve(ElectrothermalProblem problem, ThermalSolveOptions options, ElectrothermalSystem? system = null,
                                               double[]? warm = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(options);
        var sys = system ?? new ElectrothermalSystem(problem, options.MaxDegreeOfParallelism);
        var ctx = Context.For(problem, sys, options);
        var notes = new List<string>(sys.Notes);
        if (ctx.Conflicts > 0)
            notes.Add($"{ctx.Conflicts} node(s) lie on two fixed-temperature faces at different temperatures; each takes the first face's.");
        var x = warm is { } w && w.Length == sys.Size ? (double[])w.Clone() : null;
        if (x is null)
        {
            x = ctx.ColdStart();
            ctx.PicardSweep(x);
        }
        else ctx.Pin(x);

        bool kOfT = options.KOfT, sOfT = options.SigmaOfT;
        var a = sys.Assemble(problem, x, kOfT, sOfT);
        var (rT, rP) = ctx.Residual(a, x);
        var (refT, refP) = ctx.References(a, x, rT, rP);
        var (floorT, floorP) = ctx.Floors(a, x, refT, refP);
        int its = 0;
        bool converged = false;
        double upd = double.PositiveInfinity, resT = SparseRows.Norm(rT) / refT, resP = SparseRows.Norm(rP) / refP;
        string? failure = null;
        LinearSolveReport? last = null;
        if (a.Invalid) failure = "the starting state is not physical (a conductivity at or below zero)";
        var delta = new double[ctx.NFree];
        var trial = new double[sys.Size];
        // a start that already is the answer (no current and every fixed face at one temperature, say) takes no step: its
        // residual is round-off, and a step "down" from round-off is noise
        bool atStart = failure is null && SparseRows.Norm(rT) + SparseRows.Norm(rP) <= 1e-12 * (refT + refP);
        if (atStart) (converged, upd) = (true, 0);
        for (its = 1; !atStart && failure is null && its <= options.NewtonMaxIterations; its++)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var j = ThermalSolver.Reduce(sys.Matrix(a.Tangent), ctx.Free, ctx.NFree);
            var rhs = new double[ctx.NFree];
            for (int i = 0; i < ctx.NFreeT; i++) rhs[i] = -rT[i];
            for (int i = 0; i < ctx.NFree - ctx.NFreeT; i++) rhs[ctx.NFreeT + i] = -rP[i];
            Array.Clear(delta);
            last = LinearSolver.SolveBlocks(j, rhs, delta, ctx.NFreeT, ctx.Kind, options);
            if (last.FallbackNote is { } fb && !notes.Contains(fb)) notes.Add(fb);
            if (delta.Any(d => !double.IsFinite(d))) { failure = "a Newton step was not finite (the Jacobian is singular there)"; break; }
            double merit = resT + resP, step = 2;
            ElectrothermalAssembled? next = null;
            double[]? nT = null, nP = null;
            bool accepted = false;
            for (int halving = 0; halving <= 10; halving++)
            {
                step /= 2;
                Array.Copy(x, trial, x.Length);
                for (int i = 0; i < sys.Size; i++) if (ctx.Free[i] >= 0) trial[i] += step * delta[ctx.Free[i]];
                if (Diverged(trial, sys.TUnknowns)) continue;
                next = sys.Assemble(problem, trial, kOfT, sOfT);
                if (next.Invalid) continue;
                (nT, nP) = ctx.Residual(next, trial);
                double m = SparseRows.Norm(nT) / refT + SparseRows.Norm(nP) / refP;
                if (m < merit || merit <= floorT + floorP) { accepted = true; break; }
            }
            if (!accepted || next is null)
            {
                failure = "no step along Newton's direction lowered the residual while staying physical";
                break;
            }
            double maxD = 0;
            for (int i = 0; i < sys.TUnknowns; i++) if (ctx.Free[i] >= 0) maxD = Math.Max(maxD, Math.Abs(delta[ctx.Free[i]]));
            upd = step * maxD;
            Array.Copy(trial, x, x.Length);
            a = next;
            (rT, rP) = (nT!, nP!);
            resT = SparseRows.Norm(rT) / refT;
            resP = SparseRows.Norm(rP) / refP;
            (floorT, floorP) = ctx.Floors(a, x, refT, refP);
            double span = Span(x, sys.TUnknowns);
            // floored as the thermal solve's is: at an isothermal answer (no current, every fixed face at one temperature) the
            // span and every update are round-off, and "update ≤ tol × span" never held — a warm start to it ran every step,
            // failed, and a continuation then read a bracket closing near zero as a runaway
            if (upd <= options.NewtonTolerance * Math.Max(span, ThermalSolver.SpanFloorK) && resT <= Math.Max(1e-8, floorT) && resP <= Math.Max(1e-8, floorP))
            { converged = true; break; }
        }
        if (atStart) its = 0;
        if (its > options.NewtonMaxIterations) { its = options.NewtonMaxIterations; failure ??= $"it did not converge in {options.NewtonMaxIterations} Newton steps"; }
        notes.Add(converged
            ? $"Conductive balance converged in {its} Newton step(s): last temperature update {upd:G3} K, residuals {resT:G3} (heat) and {resP:G3} (current) of their start."
            : $"Conductive balance did not converge: {failure}; last temperature update {upd:G3} K, residuals {resT:G3} (heat) and {resP:G3} (current).");
        return ctx.Result(a, x, converged, its, resT, resP, upd, converged ? null : failure, last, notes);
    }

    /// <summary>
    /// R-em3d77-4d — the fixed-point reference: φ at the present σ(T), then T at that heat (and the present k(T)), repeated until
    /// the largest temperature change is below 1e-12 of the span. Not a user option: the tests compare Newton against it.
    /// </summary>
    public static ElectrothermalSolution Picard(ElectrothermalProblem problem, ThermalSolveOptions options, int maxSweeps = 2000)
    {
        var sys = new ElectrothermalSystem(problem, options.MaxDegreeOfParallelism);
        var ctx = Context.For(problem, sys, options);
        var x = ctx.ColdStart();
        int sweeps = 0;
        double change = double.PositiveInfinity;
        bool converged = false;
        for (sweeps = 1; sweeps <= maxSweeps; sweeps++)
        {
            var before = x[..sys.TUnknowns];
            ctx.PicardSweep(x);
            change = 0;
            for (int i = 0; i < sys.TUnknowns; i++) change = Math.Max(change, Math.Abs(x[i] - before[i]));
            if (Diverged(x, sys.TUnknowns)) break;
            if (change <= 1e-12 * Math.Max(Span(x, sys.TUnknowns), ThermalSolver.SpanFloorK)) { converged = true; break; }
        }
        var a = sys.Assemble(problem, x, options.KOfT, options.SigmaOfT);
        var (rT, rP) = ctx.Residual(a, x);
        var (refT, refP) = ctx.References(a, x, rT, rP);
        return ctx.Result(a, x, converged, Math.Min(sweeps, maxSweeps), SparseRows.Norm(rT) / refT, SparseRows.Norm(rP) / refP, change,
                          converged ? null : "the fixed-point iteration did not settle", null,
                          [$"Fixed-point reference: {Math.Min(sweeps, maxSweeps)} sweep(s)."]);
    }

    private static bool Diverged(double[] x, int nT)
    {
        for (int i = 0; i < nT; i++) if (!double.IsFinite(x[i]) || Math.Abs(x[i]) > Absurd) return true;
        return false;
    }

    private static double Span(double[] x, int nT)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < nT; i++) { lo = Math.Min(lo, x[i]); hi = Math.Max(hi, x[i]); }
        return hi - lo;
    }

    /// <summary>One problem's fixed unknowns and the operations both solves share.</summary>
    private sealed class Context
    {
        public required ElectrothermalProblem P;
        public required ElectrothermalSystem S;
        public required ThermalSolveOptions O;
        public required double[] Fixed;        // per unknown: its fixed value, NaN when free
        public required int[] FixedTag;
        public required int[] Free;            // per unknown: its free index, −1 when fixed
        public required int NFree, NFreeT, Conflicts;
        public required ThermalSolverKind Kind;

        public static Context For(ElectrothermalProblem p, ElectrothermalSystem s, ThermalSolveOptions o)
        {
            var t = p.Thermal;
            if (t.Fixed.Count == 0 && t.FixedFields.Count == 0 && t.Convection.Count == 0)
                throw new InvalidOperationException("no fixed-temperature or convection condition: no steady state");
            var (ft, ftag, conflicts) = ThermalSolver.FixedNodes(t);
            var fixedV = new double[s.Size];
            Array.Fill(fixedV, double.NaN);
            Array.Copy(ft, fixedV, ft.Length);
            var tag = new int[s.Size];
            Array.Copy(ftag, tag, ftag.Length);
            foreach (int r in s.Reference) fixedV[s.TUnknowns + r] = 0;
            var free = new int[s.Size];
            int nf = 0, nfT = 0;
            for (int i = 0; i < s.Size; i++)
            {
                free[i] = double.IsNaN(fixedV[i]) ? nf++ : -1;
                if (i == s.TUnknowns - 1) nfT = nf;
            }
            if (s.TUnknowns == 0) nfT = 0;
            if (nfT == s.TUnknowns && t.Convection.Count == 0)
                throw new InvalidOperationException("the fixed-temperature faces select no triangle: no steady state");
            var kind = o.Solver == ThermalSolverKind.Auto ? nf < o.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative : o.Solver;
            return new Context { P = p, S = s, O = o, Fixed = fixedV, FixedTag = tag, Free = free, NFree = nf, NFreeT = nfT, Conflicts = conflicts, Kind = kind };
        }

        public void Pin(double[] x)
        {
            for (int i = 0; i < x.Length; i++) if (!double.IsNaN(Fixed[i])) x[i] = Fixed[i];
        }

        public double[] ColdStart()
        {
            var t = P.Thermal;
            var temps = t.Fixed.Select(f => f.TempC).Concat(t.Convection.Select(c => c.AmbientC)).ToList();
            double t0 = temps.Count > 0 ? temps.Average() : 25;
            var x = new double[S.Size];
            for (int i = 0; i < S.TUnknowns; i++) x[i] = t0;
            Pin(x);
            return x;
        }

        /// <summary>The residual's free T rows and free φ rows at <paramref name="x"/>.</summary>
        public (double[] T, double[] Phi) Residual(ElectrothermalAssembled a, double[] x)
        {
            var sx = new double[S.Size];
            S.Matrix(a.Secant).Multiply(x, sx);
            var rt = new double[NFreeT];
            var rp = new double[NFree - NFreeT];
            for (int i = 0; i < S.Size; i++)
            {
                int f = Free[i];
                if (f < 0) continue;
                double r = sx[i] - a.Load[i];
                if (f < NFreeT) rt[f] = r; else rp[f - NFreeT] = r;
            }
            return (rt, rp);
        }

        /// <summary>Each block's round-off floor relative to its reference: 1000·ε·‖|S||x| + |L|‖ over its free rows.</summary>
        public (double T, double Phi) Floors(ElectrothermalAssembled a, double[] x, double refT, double refP)
        {
            var pat = S.Pattern;
            double ft = 0, fp = 0;
            for (int i = 0; i < S.Size; i++)
            {
                int f = Free[i];
                if (f < 0) continue;
                double m = Math.Abs(a.Load[i]);
                for (int k = pat.Ptr[i]; k < pat.Ptr[i + 1]; k++) m += Math.Abs(a.Secant[k] * x[pat.Idx[k]]);
                if (f < NFreeT) ft += m * m; else fp += m * m;
            }
            const double ulps = 1000 * 2.220446049250313e-16;
            return (ulps * Math.Sqrt(ft) / refT, ulps * Math.Sqrt(fp) / refP);
        }

        /// <summary>Each block's reference: the larger of its residual now and its reduced load (never zero).</summary>
        public (double T, double Phi) References(ElectrothermalAssembled a, double[] x, double[] rT, double[] rP)
        {
            var lift = new double[S.Size];
            for (int i = 0; i < S.Size; i++) lift[i] = Free[i] >= 0 ? 0 : x[i];
            var sl = new double[S.Size];
            S.Matrix(a.Secant).Multiply(lift, sl);
            double bt = 0, bp = 0;
            for (int i = 0; i < S.Size; i++)
            {
                int f = Free[i];
                if (f < 0) continue;
                double b = a.Load[i] - sl[i];
                if (f < NFreeT) bt += b * b; else bp += b * b;
            }
            double refT = Math.Max(SparseRows.Norm(rT), Math.Sqrt(bt)), refP = Math.Max(SparseRows.Norm(rP), Math.Sqrt(bp));
            return (refT > 0 ? refT : 1, refP > 0 ? refP : 1);
        }

        /// <summary>One fixed-point sweep, in place: φ at the present σ(T), then T at the present k(T) and that Joule heat.</summary>
        public void PicardSweep(double[] x)
        {
            if (S.PhiUnknowns > 0)
            {
                var a = S.Assemble(P, x, O.KOfT, O.SigmaOfT);
                SolveSubset(a, x, i => i >= S.TUnknowns, symmetric: true);
            }
            var b = S.Assemble(P, x, O.KOfT, O.SigmaOfT);
            SolveSubset(b, x, i => i < S.TUnknowns, symmetric: false);
        }

        /// <summary>The secant system's free unknowns that <paramref name="which"/> selects, solved with every other unknown held.</summary>
        private void SolveSubset(ElectrothermalAssembled a, double[] x, Func<int, bool> which, bool symmetric)
        {
            var map = new int[S.Size];
            int n = 0;
            for (int i = 0; i < S.Size; i++) map[i] = Free[i] >= 0 && which(i) ? n++ : -1;
            if (n == 0) return;
            var held = (double[])x.Clone();
            for (int i = 0; i < S.Size; i++) if (map[i] >= 0) held[i] = 0;
            var k = S.Matrix(a.Secant);
            var kh = new double[S.Size];
            k.Multiply(held, kh);
            var rhs = new double[n];
            for (int i = 0; i < S.Size; i++) if (map[i] >= 0) rhs[map[i]] = a.Load[i] - kh[i];
            var sol = new double[n];
            for (int i = 0; i < S.Size; i++) if (map[i] >= 0) sol[map[i]] = x[i];
            var r = LinearSolver.Solve(ThermalSolver.Reduce(k, map, n), rhs, sol, symmetric, Kind, O);
            for (int i = 0; i < S.Size; i++) if (map[i] >= 0) x[i] = sol[map[i]];
            _ = r;
        }

        public ElectrothermalSolution Result(ElectrothermalAssembled a, double[] x, bool converged, int its, double resT, double resP, double upd,
                                             string? failure, LinearSolveReport? last, List<string> notes)
        {
            int n = S.HostNodes;
            var sx = new double[S.Size];
            S.Matrix(a.Secant).Multiply(x, sx);
            double fixedOut = 0;
            var byTag = new Dictionary<int, double>();
            for (int i = 0; i < n; i++)
            {
                if (Free[i] >= 0) continue;
                double q = a.Load[i] - sx[i];
                fixedOut += q;
                byTag[FixedTag[i]] = byTag.GetValueOrDefault(FixedTag[i]) + q;
            }
            double conv = S.Thermal.ConvectionOut(P.Thermal, x[..n]) + a.WireConvectionW;
            double pin = a.SourceW + a.JouleW + a.RfW;
            double flow = Math.Max(Math.Abs(pin), Math.Abs(fixedOut) + Math.Abs(conv));
            double balance = flow > 0 ? Math.Abs(pin - fixedOut - conv) / flow : 0;
            var volts = S.TerminalPhi.Select(t => x[S.TUnknowns + t.Positive] - x[S.TUnknowns + t.Negative]).ToArray();
            double pe = 0;
            for (int i = 0; i < volts.Length; i++) pe += volts[i] * P.Currents[i].CurrentA;
            if (P.Currents.Count > 0 && converged)
            {
                double gap = Math.Abs(pe - a.JouleW) / Math.Max(Math.Abs(pe), 1e-300);
                notes.Add($"Electrical power in {pe:G6} W (Σ V·I over the ports); Joule heat {a.JouleW:G6} W ({a.JouleByWire.Sum():G6} W in the wires); " +
                          $"they differ by {gap:G3} of it.");
            }
            if (a.RfByWire.Length > 0 && a.RfW > 0 && converged)
                notes.Add($"RF heat {a.RfW:G6} W in the wires (their harmonic currents, at each wire's solved temperature).");
            var thermal = new ThermalSolution
            {
                Temperature = x[..n], Unknowns = NFree, Solver = last?.Solver ?? Kind, LinearIterations = last?.Iterations ?? 0,
                LinearResidual = last?.RelativeResidual ?? 0, NewtonIterations = its, Converged = converged, NewtonResidual = Math.Max(resT, resP),
                NewtonUpdateK = upd, SourcePowerW = pin, FixedHeatOutW = fixedOut, ConvectionHeatOutW = conv, FixedHeatOutByTag = byTag,
                BalanceRelative = balance, AmgLevels = last?.AmgLevels ?? 0, Notes = notes,
            };
            var wires = new double[P.Wires.Count][];
            for (int w = 0; w < wires.Length; w++) wires[w] = x[S.WireOffset[w]..(S.WireOffset[w] + P.Wires[w].NodeCount)];
            return new ElectrothermalSolution
            {
                Thermal = thermal, State = x, WireTemperature = wires, Converged = converged, Iterations = its, ResidualT = resT, ResidualPhi = resP,
                UpdateK = upd, Failure = failure, JouleW = a.JouleW, JouleByRegion = a.JouleByRegion, JouleByWire = a.JouleByWire,
                WireCurrentA = [.. Enumerable.Range(0, P.Wires.Count).Select(w => S.WireCurrent(P, w, x, O.SigmaOfT))],
                WireResistanceOhm = [.. Enumerable.Range(0, P.Wires.Count).Select(w => S.WireResistance(P, w, x, O.SigmaOfT))],
                PortVoltage = volts, ElectricalPowerW = pe, RfW = a.RfW, RfByWire = a.RfByWire, RfByHarmonic = a.RfByHarmonic,
            };
        }
    }
}
