// ================================================================
//  EnvelopeEngine.cs  —  circuit-envelope analysis (single fundamental)
//
//  Each envelope step is one harmonic-balance solve on the interface
//  spectrum V[N, K+1] with a time derivative added to every charge:
//
//    linear:    (G + (j k w0 + alpha) C) x_k(n) = b_k(n) - C (b1 x_k(n-1) + b2 x_k(n-2))
//    devices:   i(V) + (j k w0) q(V) + alpha q(V) + b1 q(n-1) + b2 q(n-2)
//
//  Gear-2 (BDF2) with backward Euler on the first step.  For lumped
//  (affine-in-jw) linear models the s-domain stamp is exact: C is
//  recovered from two stamps at k w0 +- delta.  A two-conductor line is
//  stepped as its delay (EnvelopeLineDelay): its delayed waves enter the
//  right-hand side, and the current step's share of them, when the line
//  is shorter than a step, the stepped matrix.  An S-parameter block, a
//  System Filter and any other element whose stamp is not affine (a
//  Z(freq) block) is replaced for the run by a rational fit that is
//  affine by construction (EnvelopeSnpFit).  One whose fit is refused is
//  approximated to first order around each harmonic (its value and slope
//  there); the run reports how far that is from its true stamp across the
//  signal band, matrix-wide and per element (EnvelopeTangentCheck), and
//  warns naming the element.
//
//  The sparse factorization of G + (j k w0 + alpha) C is done ONCE per
//  harmonic per run (alpha is fixed by the step), so a step costs one
//  sparse back-substitution per harmonic for the history, a small dense
//  Newton on the interface (chord: the Jacobian LU is reused while the
//  contraction stays fast) and a rank-N update of the linear state.
//
//  The device Jacobian/residual come from HbNewton's own BuildJ/BuildF:
//  passing G + alpha*C for the conductance spectra adds exactly the
//  alpha*C conversion block (same weights, same DC handling), and the
//  envelope charge term rides in the nonlinear-current argument — so
//  harmonic balance itself is untouched.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Storage;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public sealed record EnvelopeParams
{
    /// <summary>Simulated time (s) after the start point.</summary>
    public double StopS { get; init; } = 100e-6;
    /// <summary>Explicit envelope step (s); 0 = automatic from the signal rate x <see cref="Oversample"/>.</summary>
    public double StepS { get; init; }
    /// <summary>Steps per signal sample when the step is automatic (1 draft, 4 standard, 8 fine).</summary>
    public int Oversample { get; init; } = 4;
    public int MaxHarm { get; init; } = 5;
    public int FftOverSample { get; init; } = 1;
    public double Tol { get; init; } = 1e-6;
    /// <summary>Nodes whose envelopes are stored (V cube) and measured. Empty = every MOD_Source node.</summary>
    public string[] Probes { get; init; } = Array.Empty<string>();
    /// <summary>Reference impedance for power measurements at a probe node (ohm).</summary>
    public double MeasureZ { get; init; } = 50.0;
    /// <summary>Signal-record offset of t = 0 (s) — lets a segment start mid-record.</summary>
    public double StartS { get; init; }
    /// <summary>Warm-up simulated before <see cref="StartS"/> and discarded (s).</summary>
    public double WarmupS { get; init; }
}

public sealed class EnvelopeRunResult
{
    public required double[] Time { get; init; }
    public required string[] ProbeNames { get; init; }
    /// <summary>[step][probe][harmonic] complex (peak phasors).</summary>
    public required Complex[][][] Probe { get; init; }
    public required string[] SupplyNames { get; init; }
    /// <summary>[step][supply] DC supply branch current (A).</summary>
    public required double[][] SupplyCurrent { get; init; }
    public required double[] SupplyVoltage { get; init; }
    /// <summary>Per MOD source: instantaneous available-drive phasor (V peak) at the fundamental.</summary>
    public required Complex[][] Drive { get; init; }
    public required double[] DriveRe { get; init; }
    public required double F0Hz { get; init; }
    public required double StepS { get; init; }
    public required int MaxHarm { get; init; }
    public required double SignalRateHz { get; init; }
    public required ModSourceModel[] Sources { get; init; }
    public double WallSeconds { get; init; }
    public double MeanNewton { get; init; }
    public int JacobianBuilds { get; init; }
    public double WorstTangentError { get; init; }
    /// <summary>The SnP blocks the run replaced by a rational fit (EnvelopeSnpFit).</summary>
    public IReadOnlyList<SnpFitReport> SnpFits { get; init; } = [];
    /// <summary>Per carrier (sources in order, then their carriers): index on the harmonic/mix axis whose envelope carries it.</summary>
    public int[] CarrierMix { get; init; } = Array.Empty<int>();
    /// <summary>Per carrier: offset (Hz) of the carrier from that product's frequency.</summary>
    public double[] CarrierOffsetHz { get; init; } = Array.Empty<double>();
    public string[] CarrierLabels { get; init; } = Array.Empty<string>();
    /// <summary>Multi-fundamental runs: the tones, and one label + signed frequency per mixing product.</summary>
    public double[]? ToneFreqsHz { get; init; }
    public string[]? MixLabels { get; init; }
    public double[]? MixFreqHz { get; init; }
    public int[][]? MixTones { get; init; }
    public List<string> Notes { get; } = new();
}

public sealed class EnvelopeEngine
{
    private readonly ElaboratedNetlist _nl;
    private readonly AnalysisSettings _settings;

    public EnvelopeEngine(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        _nl = netlist;
        _settings = settings ?? new AnalysisSettings();
    }

    /// <summary>What one envelope Newton evaluation hands on: the charge and total-injection spectra the step keeps,
    /// and the Jacobian ingredients.</summary>
    private sealed record StepEval(Complex[,] Q, Complex[,] Inj, Complex[,,] G, Complex[,,] Cc,
                                   IReadOnlyList<HigherWeightBucket> Buckets);

    /// <summary>One product's linear network factored at one <c>alpha</c>: <c>G + (j w + alpha) C</c>.</summary>
    internal sealed class Stage
    {
        public SparseLU Lu = null!;
        public Complex[][] Zcol = null!;                     // [port][row]
        public Complex[,] Y = null!;
        public Complex[] Xs = null!;                         // static-source solution
        public Complex[][] Xu = null!;                       // [source][row] unit-drive solution

        public static Stage Of((SparseLU lu, Complex[][] zcol, Complex[,] Y, Complex[] xs, Complex[][] xu) t)
            => new() { Lu = t.lu, Zcol = t.zcol, Y = t.Y, Xs = t.xs, Xu = t.xu };
    }

    private sealed class Product
    {
        public double Omega;
        public CompressedColumnStorage<Complex> A0 = null!, C = null!;
        public Complex[] BStatic = null!;
        public Complex[][] BUnit = null!;
        public Stage Be = null!, G2 = null!;
        /// <summary>Backward Euler at h/m, factored the first time a step is split m ways.</summary>
        public readonly Dictionary<int, Stage> Split = new();
        public Complex[] Xm1 = null!, Xm2 = null!;             // state history
    }

    /// <summary>How finely a step whose Newton failed may be split before the run gives up.</summary>
    internal const int MaxStepSplit = 16;

    public EnvelopeRunResult Run(EnvelopeParams p, RunControl? control = null)
    {
        var sw = Stopwatch.StartNew();
        var sources = _nl.Components.Select(c => c.Model).OfType<ModSourceModel>().ToArray();
        var srcEcs = _nl.Components.Where(c => c.Model is ModSourceModel).ToArray();
        if (sources.Length == 0)
            throw new InvalidOperationException("Envelope analysis needs at least one MOD_Source (the modulated signal).");
        foreach (var ec in _nl.Components)
            if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0)
                throw new InvalidOperationException($"Envelope analysis does not support SDD control currents yet ('{ec.InstancePath}').");

        // ── fundamental and envelope rate ─────────────────────────────────
        double f0 = sources[0].FcHz;
        int K = p.MaxHarm;
        double omega0 = 2 * Math.PI * f0;
        var signals = sources.Select(s => s.Signal).ToArray();
        double need = 0;
        for (int s = 0; s < sources.Length; s++)
            foreach (var c in signals[s].Carriers)
            {
                double off = sources[s].FcHz + c.OffsetHz - f0;
                need = Math.Max(need, 2 * (Math.Abs(off) + 2.5 * c.Spec.BandwidthHz));
                need = Math.Max(need, c.NativeRateHz);
            }
        double rate = SignalRate.AtLeast(need, signals);
        int osr = Math.Max(1, p.Oversample);
        double h = p.StepS > 0 ? p.StepS : 1.0 / (rate * osr);
        double recordRate = 1.0 / h;
        double record = signals.Min(sg => sg.RecordSeconds);
        double nRec = record * recordRate;
        if (Math.Abs(nRec - Math.Round(nRec)) > 1e-6 * nRec)
            throw new InvalidOperationException($"envelope step {h:G4} s does not divide the {record * 1e3:0.###} ms signal record; leave Step automatic or pick a divisor");
        foreach (var sg in signals)
            if (Math.Abs(sg.RecordSeconds - record) > 1e-12)
                throw new InvalidOperationException("all MOD_Sources in one envelope run must use the same record length");
        // Every line is its delay for the whole run — the DC solve, the HB start and the stepping (EnvelopeLineDelay) — and
        // every other non-affine element (an SnP, a System Filter, a Z(freq) block) its rational fit (EnvelopeSnpFit).
        using var lines = EnvelopeLineDelay.Apply(_nl, [.. Enumerable.Range(0, K + 1).Select(k => k * f0)], h, 0.5 * need);
        using var snpFit = EnvelopeSnpFit.Apply(_nl, K * f0 + 0.5 * rate, [.. Enumerable.Range(1, K).Select(k => k * f0)], 0.5 * rate);
        // An element whose fit was refused (any stamp whose 1 Hz / 2 Hz tangent is not exact) is held at its DC stamp for
        // the baseband product.
        using var baseband = EnvelopeBaseband.Apply(_nl, Math.PI * need);

        var swSetup = Stopwatch.StartNew();
        bool profS = Environment.GetEnvironmentVariable("CRF_ENV_PROFILE") is not null;
        void Mark(string what) { if (profS) Console.Error.WriteLine($"[env-profile] {what} {swSetup.Elapsed.TotalSeconds:0.00}s"); }
        Mark("signals");
        // per-source drive WINDOW at the envelope rate, relative to f0: only the samples this run touches
        int nWarm = (int)Math.Round(p.WarmupS / h);
        int nSteps = (int)Math.Round(p.StopS / h);
        long i0 = (long)Math.Round(p.StartS / h) - nWarm;
        int nDrive = nWarm + nSteps + 1;
        var drive = new Complex[sources.Length][];
        for (int s = 0; s < sources.Length; s++)
        {
            var x = signals[s].CompositeWindow(recordRate, f0 - sources[s].FcHz, i0, nDrive);
            double vs = sources[s].VsPeak;
            for (int i = 0; i < x.Length; i++) x[i] *= vs;
            drive[s] = x;
        }
        Complex Drv(int s, int step) => drive[s][step];

        // ── linear partition ──────────────────────────────────────────────
        var extractor = new HbLinearExtractor(_nl, _settings);
        int N = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        int gridN = HbFft.GridSize(K, p.FftOverSample);
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is P1ToneModel p1)
            {
                double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == Core.Expressions.ValueKind.Real ? fv.AsReal() : f0;
                p1.SetToneContext(fc: f0, driveFreqHz: driveHz);
            }
            else if (ec.Model is PnToneModel pn) pn.SetToneContext(fc: f0);
        }
        foreach (var s in sources) { s.SetToneContext(f0); s.EnvelopeDrive = Complex.Zero; }
        // Before the products are stamped: stamping an element alone re-numbers a source's branch (LastBranchIndex).
        // Measured out to the signal's own extent (+-need/2: carriers and their ACPR2 bands), where the result is read.
        EnvelopeTangentCheck.Warn(_nl, EnvelopeTangentCheck.Measure(_nl, Enumerable.Range(1, K).Select(k => k * omega0), Math.PI * need), 0.5 * need);

        double hBe = h, aBe = 1.0 / hBe, aG2 = 1.5 / h, b1G2 = -2.0 / h, b2G2 = 0.5 / h;
        double tanErrWorst = 0;
        var products = new Product[K + 1];
        var mna = extractor.NewSmallSignalMna();
        try
        {
            for (int k = 0; k <= K; k++)
            {
                double w = k * omega0;
                double dlt = Math.Max(1e-6 * Math.Max(w, 2 * Math.PI * 1e6), 2 * Math.PI * 1.0);
                CompressedColumnStorage<Complex> A0, C;
                baseband.Holding = k == 0;            // the whole baseband product, its right-hand side included
                lines.Product = k;
                if (k == 0)
                {
                    var a1 = Stamp(extractor, mna, dlt);
                    var a2 = Stamp(extractor, mna, 2 * dlt);
                    C = Combine(a2, a1, 1.0 / new Complex(0, dlt), -1.0 / new Complex(0, dlt));
                    A0 = Combine(a1, C, 1.0, -new Complex(0, dlt));
                }
                else
                {
                    var ap = Stamp(extractor, mna, w + dlt);
                    var am = Stamp(extractor, mna, w - dlt);
                    C = Combine(ap, am, 1.0 / new Complex(0, 2 * dlt), -1.0 / new Complex(0, 2 * dlt));
                    A0 = Stamp(extractor, mna, w);
                    // tangent check at the envelope band edge (affine models: ~round-off)
                    double band = 0.5 * rate;
                    if (w - 2 * Math.PI * band > 0)
                    {
                        var aEdge = Stamp(extractor, mna, w + 2 * Math.PI * band);
                        var tan = Combine(A0, C, 1.0, new Complex(0, 2 * Math.PI * band));
                        tanErrWorst = Math.Max(tanErrWorst, RelDiff(aEdge, tan));
                    }
                }
                // RHS at the exact harmonic (sources: DC at k=0, CW tones at their harmonic)
                var bStatic = RhsAt(extractor, mna, w, sources, null);
                var bUnit = new Complex[sources.Length][];
                for (int s = 0; s < sources.Length; s++)
                {
                    if (k != 1) { bUnit[s] = new Complex[bStatic.Length]; continue; }
                    var b = RhsAt(extractor, mna, w, sources, s);
                    for (int r = 0; r < b.Length; r++) b[r] -= bStatic[r];
                    bUnit[s] = b;
                }
                var pr = new Product { Omega = w, A0 = A0, C = C, BStatic = bStatic, BUnit = bUnit };
                baseband.Holding = false;
                lines.Product = null;
                lines.Bind();
                var delayNow = lines.Coupling(k, 1, A0.RowCount);
                pr.Be = Stage.Of(Prepare(A0, C, w, aBe, ifNodes, bStatic, bUnit, delayNow));
                pr.G2 = Stage.Of(Prepare(A0, C, w, aG2, ifNodes, bStatic, bUnit, delayNow));
                products[k] = pr;
            }
        }
        finally
        {
            baseband.Holding = false;
            lines.Product = null;
            foreach (var s in sources) s.EnvelopeDrive = null;
        }

        Mark("linear products");
        // ── start: harmonic balance at the first stimulus sample ─────────
        var (V, hbX, qPrev) = SteadyStart(extractor, baseband, sources, f0, K, N, ifNodes, gridN, p, s => Drv(s, 0));
        for (int k = 0; k <= K; k++) { products[k].Xm1 = hbX[k]; products[k].Xm2 = hbX[k]; }
        lines.Start(hbX);
        var qm1 = qPrev; var qm2 = qPrev;
        Mark("HB start");

        // probes and supplies
        var probeNodes = ResolveProbes(p, srcEcs);
        static int BranchOf(ComponentModel m) => m switch { VdcModel v => v.LastBranchIndex, ToneSourceModel t => t.LastBranchIndex, _ => -1 };
        var supplies = _nl.Components.Where(c => BranchOf(c.Model) >= 0 &&
                                                 c.Parameters.TryGetValue("Vdc", out var vv) && vv.Kind == Core.Expressions.ValueKind.Real && vv.AsReal() != 0).ToArray();
        var supplyRows = supplies.Select(c => BranchOf(c.Model)).ToArray();   // AddBranch returns the absolute MNA row
        var supplyV = supplies.Select(c => c.Parameters.TryGetValue("Vdc", out var v) && v.Kind == Core.Expressions.ValueKind.Real ? v.AsReal() : 0.0).ToArray();

        int total = nWarm + nSteps + 1;
        var tOut = new double[nSteps + 1];
        var pOut = new Complex[nSteps + 1][][];
        var sOut = new double[nSteps + 1][];
        var dOut = new Complex[sources.Length][];
        for (int s = 0; s < sources.Length; s++) dOut[s] = new Complex[nSteps + 1];
        void Record(int step)
        {
            int o = step - nWarm;
            if (o < 0) return;
            tOut[o] = o * h;
            var pr = new Complex[probeNodes.Length][];
            for (int pi = 0; pi < probeNodes.Length; pi++)
            {
                pr[pi] = new Complex[K + 1];
                int row = probeNodes[pi].node - 1;
                for (int k = 0; k <= K; k++) pr[pi][k] = row >= 0 ? products[k].Xm1[row] : Complex.Zero;
                pr[pi][0] = new Complex(pr[pi][0].Real, 0);
            }
            pOut[o] = pr;
            var su = new double[supplies.Length];
            for (int i = 0; i < supplies.Length; i++) su[i] = products[0].Xm1[supplyRows[i]].Real;
            sOut[o] = su;
            for (int s = 0; s < sources.Length; s++) dOut[s][o] = Drv(s, step);
        }
        Record(0);

        // ── time stepping ─────────────────────────────────────────────────
        int dof = 2 * N * (K + 1);
        bool stateRows = HbNewton.HasStateRow(_nl, ifNodes);
        var newton = new EnvelopeNewton(dof, stateRows);
        var vHist = new List<Complex[,]> { (Complex[,])V.Clone() };
        double tolAbs = p.Tol;
        bool prof = Environment.GetEnvironmentVariable("CRF_ENV_PROFILE") is not null;
        var swL = new Stopwatch(); var swE = new Stopwatch(); var swF = new Stopwatch(); var swJ = new Stopwatch(); var swU = new Stopwatch();
        int splitSteps = 0, finestSplit = 1;

        // One step from the history (x1, x2, q1, q2) to the new state of every product, with V the starting guess and,
        // on return, the solved interface spectrum. Null when Newton did not converge.
        // delayed(k, b) adds the lines' known delayed waves for product k to its right-hand side.
        Complex[][]? Advance(EnvelopeNewton nw, double alpha, double b1, double b2, Func<int, Stage> stageOf,
                             Func<int, Complex> drv, Complex[][] x1, Complex[][] x2, Complex[,] q1, Complex[,] q2,
                             Complex[,] V, Action<int, Complex[]>? delayed, out Complex[,] qNow)
        {
            if (prof) swL.Start();
            var iSrc = new Complex[K + 1][];
            var yEff = new Complex[K + 1][,];
            var xoc = new Complex[K + 1][];
            for (int k = 0; k <= K; k++)
            {
                var sg = stageOf(k);
                int n = sg.Xs.Length;
                // history right-hand side: -C (b1 x(n-1) + b2 x(n-2))
                var hv = new Complex[n];
                for (int r = 0; r < n; r++) hv[r] = b1 * x1[k][r] + b2 * x2[k][r];
                var bh = new Complex[n];
                MulCsc(products[k].C, hv, bh, -1.0);
                delayed?.Invoke(k, bh);
                var xh = new Complex[n];
                sg.Lu.Solve(bh, xh);
                var x = xoc[k] = new Complex[n];
                for (int r = 0; r < n; r++) x[r] = sg.Xs[r] + xh[r];
                if (k == 1)
                    for (int s = 0; s < sources.Length; s++)
                    {
                        Complex a = drv(s);
                        var u = sg.Xu[s];
                        for (int r = 0; r < n; r++) x[r] += a * u[r];
                    }
                var voc = new Complex[N];
                for (int j = 0; j < N; j++) voc[j] = x[ifNodes[j] - 1];
                var isrc = new Complex[N];
                for (int a = 0; a < N; a++) { Complex acc = 0; for (int b = 0; b < N; b++) acc += sg.Y[a, b] * voc[b]; isrc[a] = -acc; }
                if (k == 0) for (int a = 0; a < N; a++) isrc[a] = new Complex(isrc[a].Real, 0);
                iSrc[k] = isrc; yEff[k] = sg.Y;
            }
            if (prof) swL.Stop();
            // device charge history (Gear): b1 q(n-1) + b2 q(n-2)
            var qh = new Complex[N, K + 1];
            for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) qh[a, k] = b1 * q1[a, k] + b2 * q2[a, k];

            var accepted = nw.Solve(V, tolAbs, v =>
            {
                if (prof) swE.Start();
                var (iNl, qNl, G, Cc, buckets) = HbNewton.EvaluateNonlinear(v, N, K, gridN, _nl, ifNodes);
                if (prof) { swE.Stop(); swF.Start(); }
                var inj = new Complex[N, K + 1];
                for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) inj[a, k] = iNl[a, k] + alpha * qNl[a, k] + qh[a, k];
                var F = HbNewton.BuildF(v, yEff, iSrc, inj, qNl, N, K, omega0, buckets);
                if (prof) swF.Stop();
                return (F, new StepEval(qNl, inj, G, Cc, buckets));
            }, e =>
            {
                if (prof) swJ.Start();
                var Gs = new Complex[N, N, e.G.GetLength(2)];
                for (int a = 0; a < N; a++) for (int b = 0; b < N; b++) for (int m = 0; m < e.G.GetLength(2); m++)
                    Gs[a, b, m] = e.G[a, b, m] + alpha * e.Cc[a, b, m];
                var J = HbNewton.BuildJ(yEff, Gs, e.Cc, N, K, omega0, 0, e.Buckets);
                if (prof) swJ.Stop();
                return J;
            });
            if (accepted is null) { qNow = null!; return null; }
            qNow = accepted.Q;

            if (prof) swU.Start();
            // update linear state: x = x_oc - sum_j Zcol_j * (total device injection)_j
            for (int k = 0; k <= K; k++)
            {
                var zc = stageOf(k).Zcol;
                var x = xoc[k];
                for (int j = 0; j < N; j++)
                {
                    Complex inj = accepted.Inj[j, k] + (k > 0 ? new Complex(0, k * omega0) * qNow[j, k] : Complex.Zero);
                    if (k == 0) inj = new Complex(inj.Real, 0);
                    var z = zc[j];
                    for (int r = 0; r < x.Length; r++) x[r] -= z[r] * inj;
                }
            }
            if (prof) swU.Stop();
            return xoc;
        }

        // A step whose Newton failed is retaken as m backward-Euler sub-steps of h/m (m = 2, 4, … MaxStepSplit), the
        // drive interpolated linearly between the step's two samples. The last sub-step lands on the step's own time,
        // so the Gear-2 history at h — x(n-1), x(n-2) — is exactly what the next step needs.
        Complex[][] Split(int step, Complex[,] V, out Complex[,] qNow)
        {
            for (int m = 2; m <= MaxStepSplit; m *= 2)
            {
                double aSub = m / h;
                var nw = new EnvelopeNewton(dof, stateRows);
                var x = products.Select(pr => pr.Xm1).ToArray();
                var q = qm1;
                var vs = (Complex[,])vHist[^1].Clone();
                bool ok = true;
                lines.BeginSplit();
                for (int i = 1; i <= m && ok; i++)
                {
                    double f = (double)i / m;
                    var xn = Advance(nw, aSub, -aSub, 0.0, k => SplitStage(k, m, aSub),
                                     s => Drv(s, step - 1) + f * (Drv(s, step) - Drv(s, step - 1)), x, x, q, q, vs,
                                     lines.Any ? (k, b) => lines.AddSplit(k, b, m, i) : null, out var qn);
                    if (xn is null) ok = false;
                    else { x = xn; q = qn; lines.PushSub(xn); }
                }
                if (!ok) continue;
                Array.Copy(vs, V, V.Length);
                newton.Invalidate();                        // built at the failed full step's last iterate
                splitSteps++; finestSplit = Math.Max(finestSplit, m);
                qNow = q;
                return x;
            }
            throw new InvalidOperationException(
                $"envelope step {step} (t = {p.StartS + (step - nWarm) * h:G4} s) did not converge, even split into {MaxStepSplit} sub-steps");
        }

        Stage SplitStage(int k, int m, double aSub)
        {
            var pr = products[k];
            if (!pr.Split.TryGetValue(m, out var sg))
                pr.Split[m] = sg = Stage.Of(Prepare(pr.A0, pr.C, pr.Omega, aSub, ifNodes, pr.BStatic, pr.BUnit, lines.Coupling(k, m, pr.A0.RowCount)));
            return sg;
        }

        for (int step = 1; step < total; step++)
        {
            if ((step & 1023) == 0) control?.ThrowIfCancellationRequested();
            bool be = step == 1;
            double alpha = be ? aBe : aG2;
            double b1 = be ? -aBe : b1G2, b2 = be ? 0.0 : b2G2;
            if (step <= 2) newton.Invalidate();             // alpha changes BE -> Gear-2

            // predictor: quadratic extrapolation of the interface spectrum
            if (vHist.Count >= 3)
            {
                var v1 = vHist[^1]; var v2 = vHist[^2]; var v3 = vHist[^3];
                for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) V[a, k] = 3 * v1[a, k] - 3 * v2[a, k] + v3[a, k];
            }

            var xNew = Advance(newton, alpha, b1, b2, k => be ? products[k].Be : products[k].G2, s => Drv(s, step),
                               products.Select(pr => pr.Xm1).ToArray(), products.Select(pr => pr.Xm2).ToArray(), qm1, qm2, V,
                               lines.Any ? lines.AddMain : null, out var qNow)
                       ?? Split(step, V, out qNow);
            for (int k = 0; k <= K; k++) { products[k].Xm2 = products[k].Xm1; products[k].Xm1 = xNew[k]; }
            lines.Push(xNew);
            qm2 = qm1; qm1 = qNow;
            vHist.Add((Complex[,])V.Clone()); if (vHist.Count > 3) vHist.RemoveAt(0);
            Record(step);
        }

        if (prof)
            Console.Error.WriteLine($"[env-profile] steps {total - 1}  linear {swL.Elapsed.TotalSeconds:0.00}s  eval {swE.Elapsed.TotalSeconds:0.00}s  F {swF.Elapsed.TotalSeconds:0.00}s  J {swJ.Elapsed.TotalSeconds:0.00}s ({newton.JacobianBuilds})  update {swU.Elapsed.TotalSeconds:0.00}s  newton {(double)newton.Iterations / Math.Max(1, total - 1):0.00}");
        var res = new EnvelopeRunResult
        {
            Time = tOut, ProbeNames = probeNodes.Select(x => x.name).ToArray(), Probe = pOut,
            SupplyNames = supplies.Select(c => c.InstancePath).ToArray(), SupplyCurrent = sOut, SupplyVoltage = supplyV,
            Drive = dOut, DriveRe = sources.Select(s => s.Z.Real).ToArray(),
            F0Hz = f0, StepS = h, MaxHarm = K, SignalRateHz = rate, Sources = sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = (double)newton.Iterations / Math.Max(1, total - 1),
            JacobianBuilds = newton.JacobianBuilds, WorstTangentError = tanErrWorst, SnpFits = snpFit.Reports,
            CarrierMix = sources.SelectMany((s, si) => signals[si].Carriers.Select(_ => 1)).ToArray(),
            CarrierOffsetHz = sources.SelectMany((s, si) => signals[si].Carriers.Select(c => s.FcHz + c.OffsetHz - f0)).ToArray(),
            CarrierLabels = sources.SelectMany((s, si) => signals[si].Carriers.Select(c => $"{s.InstanceName}:{c.Spec.Label} @ {(s.FcHz + c.OffsetHz) / 1e9:0.####} GHz")).ToArray(),
        };
        if (splitSteps > 0)
            res.Notes.Add($"envelope: {splitSteps} step(s) did not converge at the full step and were retaken as up to {finestSplit} sub-steps");
        return res;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private (string name, int node)[] ResolveProbes(EnvelopeParams p, ElaboratedComponent[] srcEcs)
    {
        var list = new List<(string, int)>();
        IEnumerable<string> names = p.Probes.Length > 0 ? p.Probes : srcEcs.Select(e => _nl.Nodes.NameOf(e.Nodes[0]));
        foreach (var nm in names)
        {
            int idx = -1;
            for (int n = 1; n < _nl.Nodes.Count; n++)
                if (string.Equals(_nl.Nodes.NameOf(n), nm, StringComparison.Ordinal)) { idx = n; break; }
            if (idx < 0) throw new InvalidOperationException($"envelope probe '{nm}' is not a node of the circuit");
            list.Add((nm, idx));
        }
        return list.ToArray();
    }

    internal static CompressedColumnStorage<Complex> Stamp(HbLinearExtractor ex, MnaSystem mna, double omega)
    {
        ex.StampSmallSignal(mna, omega);
        return mna.BuildCsc();
    }

    internal static Complex[] RhsAt(HbLinearExtractor ex, MnaSystem mna, double omega, ModSourceModel[] srcs, int? unit)
    {
        for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = unit == s ? Complex.One : Complex.Zero;
        ex.StampSmallSignal(mna, omega);
        var b = mna.BuildRhs();
        for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = Complex.Zero;
        return b;
    }

    /// <summary>a·A + b·B over the union pattern.</summary>
    internal static CompressedColumnStorage<Complex> Combine(CompressedColumnStorage<Complex> A, CompressedColumnStorage<Complex> B, Complex a, Complex b)
    {
        int n = A.RowCount;
        if (B.RowCount != n || A.ColumnCount != B.ColumnCount)
            throw new InvalidOperationException("envelope: the linear network's size changes with frequency (a model adds unknowns only at some frequencies)");
        var dict = new Dictionary<long, Complex>();
        void Add(CompressedColumnStorage<Complex> M, Complex s)
        {
            for (int j = 0; j < M.ColumnCount; j++)
                for (int p = M.ColumnPointers[j]; p < M.ColumnPointers[j + 1]; p++)
                {
                    long key = (long)M.RowIndices[p] * n + j;
                    dict[key] = (dict.TryGetValue(key, out var v) ? v : 0) + s * M.Values[p];
                }
        }
        Add(A, a); Add(B, b);
        var tri = new CoordinateStorage<Complex>(n, A.ColumnCount, dict.Count);
        foreach (var kv in dict) tri.At((int)(kv.Key / n), (int)(kv.Key % n), kv.Value);
        return SparseMatrix.OfIndexed(tri);
    }

    internal static double RelDiff(CompressedColumnStorage<Complex> A, CompressedColumnStorage<Complex> B)
    {
        var d = Combine(A, B, 1.0, -1.0);
        double num = 0, den = 0;
        // A matrix with no stored entries (a purely resistive network has C = 0) may carry no Values array.
        foreach (var v in d.Values ?? []) num = Math.Max(num, v.Magnitude);
        foreach (var v in A.Values ?? []) den = Math.Max(den, v.Magnitude);
        return den > 0 ? num / den : 0;
    }

    internal static void MulCsc(CompressedColumnStorage<Complex> M, Complex[] x, Complex[] y, double scale)
    {
        for (int j = 0; j < M.ColumnCount; j++)
        {
            Complex xj = x[j];
            if (xj == Complex.Zero) continue;
            for (int p = M.ColumnPointers[j]; p < M.ColumnPointers[j + 1]; p++)
                y[M.RowIndices[p]] += scale * M.Values[p] * xj;
        }
    }

    /// <param name="delayNow">The lines' share of the current step's delayed wave (<see cref="EnvelopeLineDelay.Run.Coupling"/>),
    /// null when there is none.</param>
    internal static (SparseLU lu, Complex[][] zcol, Complex[,] Y, Complex[] xs, Complex[][] xu) Prepare(
        CompressedColumnStorage<Complex> A0, CompressedColumnStorage<Complex> C, double w, double alpha,
        int[] ifNodes, Complex[] bStatic, Complex[][] bUnit, CompressedColumnStorage<Complex>? delayNow = null)
    {
        var A = Combine(A0, C, 1.0, alpha);
        if (delayNow is not null) A = Combine(A, delayNow, 1.0, 1.0);
        var lu = SparseLU.Create(A, CSparse.ColumnOrdering.MinimumDegreeAtPlusA, 1.0);
        int n = A.RowCount, N = ifNodes.Length;
        var zcol = new Complex[N][];
        var zP = new Complex[N, N];
        for (int j = 0; j < N; j++)
        {
            var e = new Complex[n]; e[ifNodes[j] - 1] = 1.0;
            var z = new Complex[n];
            lu.Solve(e, z);
            zcol[j] = z;
            for (int i = 0; i < N; i++) zP[i, j] = z[ifNodes[i] - 1];
        }
        var Y = Invert(zP, N);
        var xs = new Complex[n];
        lu.Solve((Complex[])bStatic.Clone(), xs);
        var xu = new Complex[bUnit.Length][];
        for (int s = 0; s < bUnit.Length; s++)
        {
            xu[s] = new Complex[n];
            lu.Solve((Complex[])bUnit[s].Clone(), xu[s]);
        }
        return (lu, zcol, Y, xs, xu);
    }

    internal static Complex[,] Invert(Complex[,] a, int n)
    {
        var m = (Complex[,])a.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = 1;
        for (int c = 0; c < n; c++)
        {
            int p = c; double best = m[c, c].Magnitude;
            for (int r = c + 1; r < n; r++) if (m[r, c].Magnitude > best) { best = m[r, c].Magnitude; p = r; }
            if (best == 0) throw new InvalidOperationException("envelope: singular port impedance matrix");
            if (p != c)
                for (int j = 0; j < n; j++) { (m[c, j], m[p, j]) = (m[p, j], m[c, j]); (inv[c, j], inv[p, j]) = (inv[p, j], inv[c, j]); }
            Complex d = 1.0 / m[c, c];
            for (int j = 0; j < n; j++) { m[c, j] *= d; inv[c, j] *= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == c || m[r, c] == Complex.Zero) continue;
                Complex f = m[r, c];
                for (int j = 0; j < n; j++) { m[r, j] -= f * m[c, j]; inv[r, j] -= f * inv[c, j]; }
            }
        }
        return inv;
    }

    /// <summary>Harmonic balance with each MOD_Source fixed at its t0 envelope value; returns V, full states, q.</summary>
    /// <remarks>The DC solve and the k = 0 extraction see the baseband product's held elements (a line shorted), so the run
    /// starts at the equilibrium it steps from.</remarks>
    private (Complex[,] V, Complex[][] X, Complex[,] q) SteadyStart(HbLinearExtractor ex, EnvelopeBaseband.Hold baseband, ModSourceModel[] srcs, double f0, int K,
        int N, int[] ifNodes, int gridN, EnvelopeParams p, Func<int, Complex> drive0)
    {
        double w0 = 2 * Math.PI * f0;
        var yNN = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        var bSrc = new Complex[K + 1][];
        void ExtractAll(double scale)
        {
            for (int k = 0; k <= K; k++)
            {
                for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = k == 1 ? drive0(s) * scale : Complex.Zero;
                baseband.Holding = k == 0;
                if (k == 0) (yNN[0], iSrc[0]) = ex.ExtractDC();
                else (yNN[k], iSrc[k]) = ex.Extract(k * w0);
                bSrc[k] = ex.BuildSourceRhs(k == 0 ? 0.0 : k * w0);
                baseband.Holding = false;
            }
        }
        try
        {
            baseband.Holding = true;
            var dc = NonlinearDcEngine.Run(_nl, _settings);
            baseband.Holding = false;
            // A linearised-charge model (a composed FET with LinearC) evaluates its charge at the quiescent point:
            // freeze it there before the first HB solve reads charge, as HbEngine does.
            BiasFreeze.Apply(_nl, dc.Converged ? dc.NodeVoltages : null, dc.NodeVoltages);
            Complex[,] V = new Complex[N, K + 1];
            for (int a = 0; a < N; a++)
            {
                int node = ifNodes[a];
                V[a, 0] = dc.Converged && node - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[node - 1] : 0.0;
            }
            HbNewton.SolveResult? sol = null;
            foreach (double sc in new[] { 1.0 })
            {
                ExtractAll(sc);
                sol = HbNewton.Solve(V, yNN, iSrc, f0, K, N, _nl, ifNodes, gridN, _settings, p.Tol);
            }
            if (sol is null || !sol.Converged)
            {
                // drive ramp in 10 steps
                for (int r = 1; r <= 10; r++)
                {
                    ExtractAll(r / 10.0);
                    sol = HbNewton.Solve(V, yNN, iSrc, f0, K, N, _nl, ifNodes, gridN, _settings, p.Tol);
                }
                if (sol is null || !sol.Converged)
                    throw new InvalidOperationException("envelope: the harmonic-balance start point did not converge");
            }
            var X = new Complex[K + 1][];
            for (int k = 0; k <= K; k++)
            {
                var inl = new Complex[N];
                for (int a = 0; a < N; a++) inl[a] = sol.INl[a, k];
                baseband.Holding = k == 0;
                X[k] = ex.SolveFullNetwork(k == 0 ? 0.0 : k * w0, inl, bSrc[k]);
                baseband.Holding = false;
            }
            var (_, qNl, _, _, _) = HbNewton.EvaluateNonlinear(V, N, K, gridN, _nl, ifNodes);
            return (V, X, qNl);
        }
        finally
        {
            baseband.Holding = false;
            foreach (var s in srcs) s.EnvelopeDrive = null;
        }
    }
}
