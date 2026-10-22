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
//  recovered from two stamps at k w0 +- delta.  A non-affine model
//  (distributed line, S-parameter block, Z(freq)) gets its tangent at
//  k w0; the run reports how far that tangent is from the true
//  admittance at the edge of the envelope band.
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

    private sealed class Product
    {
        public double Omega;
        public SparseLU LuBe = null!, LuG2 = null!;
        public CompressedColumnStorage<Complex> C = null!;
        public Complex[][] ZcolBe = null!, ZcolG2 = null!;     // [port][row]
        public Complex[,] YBe = null!, YG2 = null!;
        public Complex[] XsBe = null!, XsG2 = null!;           // static-source solution
        public Complex[][] XuBe = null!, XuG2 = null!;         // [source][row] unit-drive solution
        public Complex[] Xm1 = null!, Xm2 = null!;             // state history
    }

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
        double rate = 1.92e6;
        double need = 0;
        for (int s = 0; s < sources.Length; s++)
            foreach (var c in signals[s].Carriers)
            {
                double off = sources[s].FcHz + c.OffsetHz - f0;
                need = Math.Max(need, 2 * (Math.Abs(off) + 2.5 * c.Spec.BandwidthHz));
                need = Math.Max(need, c.NativeRateHz);
            }
        while (rate < need) rate *= 2;
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
                var pr = new Product { Omega = w, C = C };
                (pr.LuBe, pr.ZcolBe, pr.YBe, pr.XsBe, pr.XuBe) = Prepare(A0, C, w, aBe, ifNodes, bStatic, bUnit);
                (pr.LuG2, pr.ZcolG2, pr.YG2, pr.XsG2, pr.XuG2) = Prepare(A0, C, w, aG2, ifNodes, bStatic, bUnit);
                products[k] = pr;
            }
        }
        finally
        {
            foreach (var s in sources) s.EnvelopeDrive = null;
        }

        Mark("linear products");
        // ── start: harmonic balance at the first stimulus sample ─────────
        var (V, hbX, qPrev) = SteadyStart(extractor, sources, f0, K, N, ifNodes, gridN, p, s => Drv(s, 0));
        for (int k = 0; k <= K; k++) { products[k].Xm1 = hbX[k]; products[k].Xm2 = hbX[k]; }
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
        DenseLu? lu = null;
        long newtonIters = 0; int jacBuilds = 0;
        var iSrc = new Complex[K + 1][];
        var yEff = new Complex[K + 1][,];
        var xoc = new Complex[K + 1][];
        var vHist = new List<Complex[,]> { (Complex[,])V.Clone() };
        double tolAbs = p.Tol;
        bool prof = Environment.GetEnvironmentVariable("CRF_ENV_PROFILE") is not null;
        var swL = new Stopwatch(); var swE = new Stopwatch(); var swF = new Stopwatch(); var swJ = new Stopwatch(); var swS = new Stopwatch(); var swU = new Stopwatch();
        for (int step = 1; step < total; step++)
        {
            if (prof) swL.Start();
            if ((step & 1023) == 0) control?.ThrowIfCancellationRequested();
            bool be = step == 1;
            double alpha = be ? aBe : aG2;
            double b1 = be ? -aBe : b1G2, b2 = be ? 0.0 : b2G2;
            if (step <= 2) lu = null;                       // alpha changes BE -> Gear-2
            for (int k = 0; k <= K; k++)
            {
                var pr = products[k];
                var luS = be ? pr.LuBe : pr.LuG2;
                var Y = be ? pr.YBe : pr.YG2;
                var xs = be ? pr.XsBe : pr.XsG2;
                var xu = be ? pr.XuBe : pr.XuG2;
                int n = xs.Length;
                // history right-hand side: -C (b1 x(n-1) + b2 x(n-2))
                var hv = new Complex[n];
                for (int r = 0; r < n; r++) hv[r] = b1 * pr.Xm1[r] + b2 * pr.Xm2[r];
                var bh = new Complex[n];
                MulCsc(pr.C, hv, bh, -1.0);
                var xh = new Complex[n];
                luS.Solve(bh, xh);
                var x = xoc[k] = new Complex[n];
                for (int r = 0; r < n; r++) x[r] = xs[r] + xh[r];
                if (k == 1)
                    for (int s = 0; s < sources.Length; s++)
                    {
                        Complex a = Drv(s, step);
                        var u = xu[s];
                        for (int r = 0; r < n; r++) x[r] += a * u[r];
                    }
                var voc = new Complex[N];
                for (int j = 0; j < N; j++) voc[j] = x[ifNodes[j] - 1];
                var isrc = new Complex[N];
                for (int a = 0; a < N; a++) { Complex acc = 0; for (int b = 0; b < N; b++) acc += Y[a, b] * voc[b]; isrc[a] = -acc; }
                if (k == 0) for (int a = 0; a < N; a++) isrc[a] = new Complex(isrc[a].Real, 0);
                iSrc[k] = isrc; yEff[k] = Y;
            }
            if (prof) swL.Stop();
            // device charge history (Gear): b1 q(n-1) + b2 q(n-2)
            var qh = new Complex[N, K + 1];
            for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) qh[a, k] = b1 * qm1[a, k] + b2 * qm2[a, k];

            // predictor: quadratic extrapolation of the interface spectrum
            if (vHist.Count >= 3)
            {
                var v1 = vHist[^1]; var v2 = vHist[^2]; var v3 = vHist[^3];
                for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) V[a, k] = 3 * v1[a, k] - 3 * v2[a, k] + v3[a, k];
            }

            Complex[,] qNow = null!, iTot = null!;
            double r0 = double.NaN;
            bool ok = false, freshJ = false;
            for (int it = 0; it < 50; it++)
            {
                if (prof) swE.Start();
                var (iNl, qNl, G, Cc, buckets) = HbNewton.EvaluateNonlinear(V, N, K, gridN, _nl, ifNodes);
                if (prof) { swE.Stop(); swF.Start(); }
                var inj = new Complex[N, K + 1];
                for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) inj[a, k] = iNl[a, k] + alpha * qNl[a, k] + qh[a, k];
                var F = HbNewton.BuildF(V, yEff, iSrc, inj, qNl, N, K, omega0, buckets);
                double rn = 0; foreach (var fv in F) rn = Math.Max(rn, Math.Abs(fv));
                if (prof) swF.Stop();
                // A stalled residual is accepted only after an update made with a FRESH Jacobian (a true
                // round-off floor). A stalled CHORD iteration means a stale Jacobian: rebuild it instead —
                // accepting there left isolated steps up to 1e3 x Tol off (single-sample glitches).
                if (rn < tolAbs || (freshJ && !double.IsNaN(r0) && rn < 1e2 * tolAbs && rn > 0.5 * r0))
                {
                    qNow = qNl; iTot = inj; ok = true; newtonIters += it; break;
                }
                freshJ = false;
                if (lu is null || (!double.IsNaN(r0) && rn > 0.25 * r0))
                {
                    freshJ = true;
                    var Gs = new Complex[N, N, G.GetLength(2)];
                    for (int a = 0; a < N; a++) for (int b = 0; b < N; b++) for (int m = 0; m < G.GetLength(2); m++)
                        Gs[a, b, m] = G[a, b, m] + alpha * Cc[a, b, m];
                    if (prof) swJ.Start();
                    var J = HbNewton.BuildJ(yEff, Gs, Cc, N, K, omega0, 0, buckets);
                    lu = DenseLu.Factor(J, dof) ?? throw new InvalidOperationException($"envelope step {step}: singular Jacobian");
                    jacBuilds++;
                    if (prof) swJ.Stop();
                }
                if (prof) swS.Start();
                for (int i = 0; i < dof; i++) F[i] = -F[i];
                lu.Solve(F);
                if (prof) swS.Stop();
                for (int a = 0; a < N; a++)
                    for (int k = 0; k <= K; k++)
                    {
                        int ir = 2 * (a * (K + 1) + k);
                        V[a, k] += new Complex(F[ir], k == 0 ? 0 : F[ir + 1]);
                    }
                r0 = rn;
            }
            if (!ok)
                throw new InvalidOperationException($"envelope step {step} (t = {(step - nWarm) * h:G4} s) did not converge");

            if (prof) swU.Start();
            // update linear state: x = x_oc - sum_j Zcol_j * (total device injection)_j
            for (int k = 0; k <= K; k++)
            {
                var pr = products[k];
                var zc = be ? pr.ZcolBe : pr.ZcolG2;
                var x = xoc[k];
                for (int j = 0; j < N; j++)
                {
                    Complex inj = iTot[j, k] + (k > 0 ? new Complex(0, k * omega0) * qNow[j, k] : Complex.Zero);
                    if (k == 0) inj = new Complex(inj.Real, 0);
                    var z = zc[j];
                    for (int r = 0; r < x.Length; r++) x[r] -= z[r] * inj;
                }
                pr.Xm2 = pr.Xm1; pr.Xm1 = x;
            }
            if (prof) swU.Stop();
            qm2 = qm1; qm1 = qNow;
            vHist.Add((Complex[,])V.Clone()); if (vHist.Count > 3) vHist.RemoveAt(0);
            Record(step);
        }

        if (prof)
            Console.Error.WriteLine($"[env-profile] steps {total - 1}  linear {swL.Elapsed.TotalSeconds:0.00}s  eval {swE.Elapsed.TotalSeconds:0.00}s  F {swF.Elapsed.TotalSeconds:0.00}s  J {swJ.Elapsed.TotalSeconds:0.00}s ({jacBuilds})  solve {swS.Elapsed.TotalSeconds:0.00}s  update {swU.Elapsed.TotalSeconds:0.00}s  newton {(double)newtonIters / Math.Max(1, total - 1):0.00}");
        var res = new EnvelopeRunResult
        {
            Time = tOut, ProbeNames = probeNodes.Select(x => x.name).ToArray(), Probe = pOut,
            SupplyNames = supplies.Select(c => c.InstancePath).ToArray(), SupplyCurrent = sOut, SupplyVoltage = supplyV,
            Drive = dOut, DriveRe = sources.Select(s => s.Z.Real).ToArray(),
            F0Hz = f0, StepS = h, MaxHarm = K, SignalRateHz = rate, Sources = sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = (double)newtonIters / Math.Max(1, total - 1),
            JacobianBuilds = jacBuilds, WorstTangentError = tanErrWorst,
            CarrierMix = sources.SelectMany((s, si) => signals[si].Carriers.Select(_ => 1)).ToArray(),
            CarrierOffsetHz = sources.SelectMany((s, si) => signals[si].Carriers.Select(c => s.FcHz + c.OffsetHz - f0)).ToArray(),
            CarrierLabels = sources.SelectMany((s, si) => signals[si].Carriers.Select(c => $"{s.InstanceName}:{c.Spec.Label} @ {(s.FcHz + c.OffsetHz) / 1e9:0.####} GHz")).ToArray(),
        };
        if (tanErrWorst > 0.01)
            res.Notes.Add($"a frequency-dependent (non-lumped) linear model departs from its tangent by {100 * tanErrWorst:0.#} % at the envelope band edge; results there are approximate");
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

    internal static (SparseLU lu, Complex[][] zcol, Complex[,] Y, Complex[] xs, Complex[][] xu) Prepare(
        CompressedColumnStorage<Complex> A0, CompressedColumnStorage<Complex> C, double w, double alpha,
        int[] ifNodes, Complex[] bStatic, Complex[][] bUnit)
    {
        var A = Combine(A0, C, 1.0, alpha);
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
    private (Complex[,] V, Complex[][] X, Complex[,] q) SteadyStart(HbLinearExtractor ex, ModSourceModel[] srcs, double f0, int K,
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
                if (k == 0) (yNN[0], iSrc[0]) = ex.ExtractDC();
                else (yNN[k], iSrc[k]) = ex.Extract(k * w0);
                bSrc[k] = ex.BuildSourceRhs(k == 0 ? 0.0 : k * w0);
            }
        }
        try
        {
            var dc = NonlinearDcEngine.Run(_nl, _settings);
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
                X[k] = ex.SolveFullNetwork(k == 0 ? 0.0 : k * w0, inl, bSrc[k]);
            }
            var (_, qNl, _, _, _) = HbNewton.EvaluateNonlinear(V, N, K, gridN, _nl, ifNodes);
            return (V, X, qNl);
        }
        finally
        {
            foreach (var s in srcs) s.EnvelopeDrive = null;
        }
    }
}
