// ================================================================
//  EnvelopeEngineMt.cs  —  multi-fundamental circuit envelope
//
//  Widely spaced carriers (e.g. two 20 MHz carriers 400 MHz apart, IBW
//  420 MHz) each get their OWN fundamental: the state is the T-tone
//  harmonic-balance lattice (mixing products |k1|+|k2| <= MaxMixOrder),
//  and every product carries its own slowly varying envelope.  The step
//  is set by each carrier's own bandwidth, not by the spacing — a single
//  envelope around the centre would need the step to resolve +-200 MHz
//  carriers and +-600 MHz IM3 (measured with a reference tool: 64x the
//  native rate to converge, 8.7x the cost of this formulation, and 5 dB
//  wrong on IM3 at 16x).
//
//  Same discretisation as the single-tone engine (Gear-2, s-domain
//  linear stamps factored once per product, chord Newton).  Products at
//  a NEGATIVE frequency reuse the factorization at |w| with conjugated
//  inputs and outputs (a real network: A(-jw + a) = conj(A(jw + a))),
//  the same w >= 0 contract the harmonic-balance engine keeps.  The
//  device Jacobian is HbNewtonNd.BuildJNd with dg + alpha*dc as the
//  conductance waveform, which adds exactly the alpha*C triple product.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;
using CSparse.Complex.Factorization;
using CSparse.Storage;

namespace CircuitRF.Engine.Envelope;

public sealed class EnvelopeEngineMt
{
    private readonly ElaboratedNetlist _nl;
    private readonly AnalysisSettings _settings;

    public EnvelopeEngineMt(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        _nl = netlist;
        _settings = settings ?? new AnalysisSettings();
    }

    /// <summary>Carrier clusters: one fundamental per group of carriers closer than 3x the larger bandwidth.</summary>
    public static List<(double centreHz, List<(int src, int car)> members, double needRate)> Plan(ModSourceModel[] sources)
    {
        var all = new List<(double f, double bw, int s, int c)>();
        for (int s = 0; s < sources.Length; s++)
        {
            var sig = sources[s].Signal;
            for (int c = 0; c < sig.Carriers.Count; c++)
                all.Add((sources[s].FcHz + sig.Carriers[c].OffsetHz, Math.Max(sig.Carriers[c].Spec.BandwidthHz, 1e6), s, c));
        }
        all.Sort((a, b) => a.f.CompareTo(b.f));
        var groups = new List<List<(double f, double bw, int s, int c)>> { new() { all[0] } };
        for (int i = 1; i < all.Count; i++)
        {
            var prev = groups[^1][^1];
            if (all[i].f - prev.f > 3 * Math.Max(all[i].bw, prev.bw)) groups.Add(new());
            groups[^1].Add(all[i]);
        }
        var res = new List<(double, List<(int, int)>, double)>();
        foreach (var g in groups)
        {
            double centre = 0.5 * (g.Min(x => x.f) + g.Max(x => x.f));
            double need = g.Max(x => 2 * (Math.Abs(x.f - centre) + 2.5 * x.bw));
            foreach (var x in g) need = Math.Max(need, sources[x.s].Signal.Carriers[x.c].NativeRateHz);
            res.Add((centre, g.Select(x => (x.s, x.c)).ToList(), need));
        }
        return res;
    }

    private sealed class Prod
    {
        public double W;            // signed
        public bool Neg;            // works at |W| with conjugation
        public SparseLU LuBe = null!, LuG2 = null!;
        public CompressedColumnStorage<Complex> C = null!;
        public Complex[][] ZBe = null!, ZG2 = null!;
        public Complex[,] YBe = null!, YG2 = null!;
        public Complex[] XsBe = null!, XsG2 = null!;
        public Complex[][] XuBe = null!, XuG2 = null!;   // [source] unit drive at this product (tone products only)
        public Complex[] Xm1 = null!, Xm2 = null!;       // canonical (|W|) state history
    }

    public EnvelopeRunResult Run(EnvelopeParams p, int maxMixOrder, RunControl? control = null)
    {
        var sw = Stopwatch.StartNew();
        var srcEcs = _nl.Components.Where(c => c.Model is ModSourceModel).ToArray();
        var sources = srcEcs.Select(c => (ModSourceModel)c.Model).ToArray();
        var plan = Plan(sources);
        if (plan.Count != 2)
            throw new InvalidOperationException(plan.Count > 2
                ? $"envelope: {plan.Count} widely spaced carrier groups need {plan.Count} fundamentals; up to 2 are supported"
                : "envelope: the multi-fundamental engine needs two carrier groups");
        int T = 2;
        var tones = plan.Select(g => g.centreHz).ToArray();
        double rate = 1.92e6, need = plan.Max(g => g.needRate);
        while (rate < need) rate *= 2;
        int osr = Math.Max(1, p.Oversample);
        double h = p.StepS > 0 ? p.StepS : 1.0 / (rate * osr);
        double recordRate = 1.0 / h;

        var apft = HbApft.Get(T, maxMixOrder, _settings.HbApftOversample);
        var lat = apft.Lattice;
        int M = lat.MixCount;
        var wMix = new double[M];
        for (int m = 0; m < M; m++) wMix[m] = 2 * Math.PI * lat.FrequencyOf(m, tones);
        for (int a = 0; a < M; a++)
            for (int b = a + 1; b < M; b++)
                if (Math.Abs(Math.Abs(wMix[a]) - Math.Abs(wMix[b])) < 2 * Math.PI * 1.0)
                    throw new InvalidOperationException($"envelope: mixing products {lat.Label(a)} and {lat.Label(b)} fall on the same frequency " +
                        $"({Math.Abs(wMix[a]) / 2 / Math.PI / 1e9:0.######} GHz) at order {maxMixOrder}; lower MaxMixOrder or move a carrier");
        int[] toneMix = new int[T];
        for (int t = 0; t < T; t++) { var k = new int[T]; k[t] = 1; toneMix[t] = lat.IndexOf(k); }

        // drive windows: per source, per tone (its carriers in that group, centred at the tone)
        int nWarm = (int)Math.Round(p.WarmupS / h);
        int nSteps = (int)Math.Round(p.StopS / h);
        long i0 = (long)Math.Round(p.StartS / h) - nWarm;
        int total = nWarm + nSteps + 1;
        var drv = new Complex[sources.Length, T][];
        for (int s = 0; s < sources.Length; s++)
            for (int t = 0; t < T; t++)
            {
                var idx = plan[t].members.Where(x => x.src == s).Select(x => x.car).ToList();
                if (idx.Count == 0) { drv[s, t] = new Complex[total]; continue; }
                var x = sources[s].Signal.CompositeWindow(recordRate, tones[t] - sources[s].FcHz, i0, total, idx);
                double vs = sources[s].VsPeak;
                for (int i = 0; i < x.Length; i++) x[i] *= vs;
                drv[s, t] = x;
            }

        var extractor = new HbLinearExtractor(_nl, _settings);
        int N = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        double fc = tones.Average();
        foreach (var ec in _nl.Components)
            if (ec.Model is P1ToneModel p1) p1.SetToneContext(fc: fc, driveFreqHz: tones[0]);
            else if (ec.Model is PnToneModel pn) pn.SetToneContext(fc: fc);
        foreach (var s in sources) { s.SetToneContext(fc); s.EnvelopeDrive = Complex.Zero; }

        double aBe = 1.0 / h, aG2 = 1.5 / h, b1G2 = -2.0 / h, b2G2 = 0.5 / h;
        var prods = new Prod[M];
        var mna = extractor.NewSmallSignalMna();
        try
        {
            for (int m = 0; m < M; m++)
            {
                double w = Math.Abs(wMix[m]);
                double dlt = Math.Max(1e-6 * Math.Max(w, 2 * Math.PI * 1e6), 2 * Math.PI * 1.0);
                CompressedColumnStorage<Complex> A0, C;
                if (m == 0)
                {
                    var a1 = EnvelopeEngine.Stamp(extractor, mna, dlt);
                    var a2 = EnvelopeEngine.Stamp(extractor, mna, 2 * dlt);
                    C = EnvelopeEngine.Combine(a2, a1, 1.0 / new Complex(0, dlt), -1.0 / new Complex(0, dlt));
                    A0 = EnvelopeEngine.Combine(a1, C, 1.0, -new Complex(0, dlt));
                }
                else
                {
                    var ap = EnvelopeEngine.Stamp(extractor, mna, w + dlt);
                    var am = EnvelopeEngine.Stamp(extractor, mna, w - dlt);
                    C = EnvelopeEngine.Combine(ap, am, 1.0 / new Complex(0, 2 * dlt), -1.0 / new Complex(0, 2 * dlt));
                    A0 = EnvelopeEngine.Stamp(extractor, mna, w);
                }
                var bStatic = EnvelopeEngine.RhsAt(extractor, mna, w, sources, null);
                int tone = Array.IndexOf(toneMix, m);
                var bUnit = new Complex[sources.Length][];
                for (int s = 0; s < sources.Length; s++)
                {
                    if (tone < 0) { bUnit[s] = new Complex[bStatic.Length]; continue; }
                    var b = EnvelopeEngine.RhsAt(extractor, mna, w, sources, s);
                    for (int r = 0; r < b.Length; r++) b[r] -= bStatic[r];
                    bUnit[s] = b;
                }
                var pr = new Prod { W = wMix[m], Neg = wMix[m] < 0, C = C };
                (pr.LuBe, pr.ZBe, pr.YBe, pr.XsBe, pr.XuBe) = EnvelopeEngine.Prepare(A0, C, w, aBe, ifNodes, bStatic, bUnit);
                (pr.LuG2, pr.ZG2, pr.YG2, pr.XsG2, pr.XuG2) = EnvelopeEngine.Prepare(A0, C, w, aG2, ifNodes, bStatic, bUnit);
                prods[m] = pr;
            }
        }
        finally { foreach (var s in sources) s.EnvelopeDrive = null; }

        // ── start: multi-tone HB with the t0 drives ─────────────────────────
        var (V, X0, q0) = Start(extractor, sources, apft, tones, wMix, toneMix, N, ifNodes, p, (s, t) => drv[s, t][0]);
        for (int m = 0; m < M; m++) { prods[m].Xm1 = X0[m]; prods[m].Xm2 = X0[m]; }
        var qm1 = q0; var qm2 = q0;

        var probes = ResolveProbes(p, srcEcs);
        int P = probes.Length;
        var tOut = new double[nSteps + 1];
        var pOut = new Complex[nSteps + 1][][];
        var dOut = new Complex[sources.Length][];
        for (int s = 0; s < sources.Length; s++) dOut[s] = new Complex[nSteps + 1];
        void Record(int step)
        {
            int o = step - nWarm;
            if (o < 0) return;
            tOut[o] = o * h;
            var pr = new Complex[P][];
            for (int pi = 0; pi < P; pi++)
            {
                pr[pi] = new Complex[M];
                int row = probes[pi].node - 1;
                for (int m = 0; m < M; m++)
                {
                    var v = row >= 0 ? prods[m].Xm1[row] : Complex.Zero;
                    pr[pi][m] = prods[m].Neg ? Complex.Conjugate(v) : v;
                }
                pr[pi][0] = new Complex(pr[pi][0].Real, 0);
            }
            pOut[o] = pr;
            for (int s = 0; s < sources.Length; s++) { Complex a = 0; for (int t = 0; t < T; t++) a += drv[s, t][step]; dOut[s][o] = a; }
        }
        Record(0);

        int dof = 2 * N * M;
        DenseLu? lu = null;
        long newtonIters = 0; int jacBuilds = 0;
        var yEff = new Complex[M][,];
        var iSrc = new Complex[M][];
        var xoc = new Complex[M][];
        var vHist = new List<Complex[,]> { (Complex[,])V.Clone() };
        for (int step = 1; step < total; step++)
        {
            if ((step & 1023) == 0) control?.ThrowIfCancellationRequested();
            bool be = step == 1;
            double alpha = be ? aBe : aG2, b1 = be ? -aBe : b1G2, b2 = be ? 0.0 : b2G2;
            if (step <= 2) lu = null;
            for (int m = 0; m < M; m++)
            {
                var pr = prods[m];
                var luS = be ? pr.LuBe : pr.LuG2;
                var Yc = be ? pr.YBe : pr.YG2;
                var xs = be ? pr.XsBe : pr.XsG2;
                var xu = be ? pr.XuBe : pr.XuG2;
                int n = xs.Length;
                var hv = new Complex[n];
                for (int r = 0; r < n; r++) hv[r] = b1 * pr.Xm1[r] + b2 * pr.Xm2[r];
                var bh = new Complex[n];
                EnvelopeEngine.MulCsc(pr.C, hv, bh, -1.0);
                var xh = new Complex[n];
                luS.Solve(bh, xh);
                var x = xoc[m] = new Complex[n];
                for (int r = 0; r < n; r++) x[r] = xs[r] + xh[r];
                int tone = Array.IndexOf(toneMix, m);
                if (tone >= 0)
                    for (int s = 0; s < sources.Length; s++)
                    {
                        Complex a = drv[s, tone][step];
                        if (a == Complex.Zero) continue;
                        var u = xu[s];
                        for (int r = 0; r < n; r++) x[r] += a * u[r];
                    }
                var Y = pr.Neg ? Conj(Yc) : Yc;
                var isrc = new Complex[N];
                for (int a = 0; a < N; a++)
                {
                    Complex acc = 0;
                    for (int b = 0; b < N; b++)
                    {
                        var vb = x[ifNodes[b] - 1];
                        acc += Y[a, b] * (pr.Neg ? Complex.Conjugate(vb) : vb);
                    }
                    isrc[a] = m == 0 ? new Complex(-acc.Real, 0) : -acc;
                }
                iSrc[m] = isrc; yEff[m] = Y;
            }
            var qh = new Complex[N, M];
            for (int a = 0; a < N; a++) for (int m = 0; m < M; m++) qh[a, m] = b1 * qm1[a, m] + b2 * qm2[a, m];
            if (vHist.Count >= 3)
            {
                var v1 = vHist[^1]; var v2 = vHist[^2]; var v3 = vHist[^3];
                for (int a = 0; a < N; a++) for (int m = 0; m < M; m++) V[a, m] = 3 * v1[a, m] - 3 * v2[a, m] + v3[a, m];
            }

            Complex[,] qNow = null!, inj = null!;
            double r0 = double.NaN; bool ok = false, freshJ = false;
            for (int it = 0; it < 50; it++)
            {
                var (iNl, qNl, dg, dc) = HbNewtonNd.EvaluateNonlinearNd(V, apft, N, _nl, ifNodes);
                var injNow = new Complex[N, M];
                for (int a = 0; a < N; a++) for (int m = 0; m < M; m++) injNow[a, m] = iNl[a, m] + alpha * qNl[a, m] + qh[a, m];
                var F = HbNewtonNd.BuildFNd(V, yEff, iSrc, injNow, qNl, lat, N, tones);
                double rn = 0; foreach (var fv in F) rn = Math.Max(rn, Math.Abs(fv));
                // Stall accepted only after a FRESH-Jacobian update (see EnvelopeEngine): a stalled chord
                // iteration rebuilds the Jacobian instead of accepting an unconverged step.
                if (rn < p.Tol || (freshJ && !double.IsNaN(r0) && rn < 1e2 * p.Tol && rn > 0.5 * r0))
                { qNow = qNl; inj = injNow; ok = true; newtonIters += it; break; }
                freshJ = false;
                if (lu is null || (!double.IsNaN(r0) && rn > 0.25 * r0))
                {
                    freshJ = true;
                    var dgA = new double[dg.Length][];
                    for (int i = 0; i < dg.Length; i++)
                    {
                        dgA[i] = new double[dg[i].Length];
                        for (int j = 0; j < dg[i].Length; j++) dgA[i][j] = dg[i][j] + alpha * dc[i][j];
                    }
                    var J = HbNewtonNd.BuildJNd(yEff, dgA, dc, apft, N, tones);
                    lu = DenseLu.Factor(J, dof) ?? throw new InvalidOperationException($"envelope step {step}: singular Jacobian");
                    jacBuilds++;
                }
                for (int i = 0; i < dof; i++) F[i] = -F[i];
                lu.Solve(F);
                for (int a = 0; a < N; a++)
                    for (int m = 0; m < M; m++)
                    {
                        int ir = 2 * (a * M + m);
                        V[a, m] += new Complex(F[ir], m == 0 ? 0 : F[ir + 1]);
                    }
                r0 = rn;
            }
            if (!ok) throw new InvalidOperationException($"envelope step {step} (t = {(step - nWarm) * h:G4} s) did not converge");

            for (int m = 0; m < M; m++)
            {
                var pr = prods[m];
                var zc = be ? pr.ZBe : pr.ZG2;
                var x = xoc[m];
                for (int j = 0; j < N; j++)
                {
                    Complex tot = inj[j, m] + (m > 0 ? new Complex(0, wMix[m]) * qNow[j, m] : Complex.Zero);
                    if (m == 0) tot = new Complex(tot.Real, 0);
                    if (pr.Neg) tot = Complex.Conjugate(tot);
                    var z = zc[j];
                    for (int r = 0; r < x.Length; r++) x[r] -= z[r] * tot;
                }
                pr.Xm2 = pr.Xm1; pr.Xm1 = x;
            }
            qm2 = qm1; qm1 = qNow;
            vHist.Add((Complex[,])V.Clone()); if (vHist.Count > 3) vHist.RemoveAt(0);
            Record(step);
        }

        var carMix = new List<int>(); var carOff = new List<double>(); var carLab = new List<string>();
        for (int s = 0; s < sources.Length; s++)
            for (int c = 0; c < sources[s].Signal.Carriers.Count; c++)
            {
                int t = plan.FindIndex(g => g.members.Contains((s, c)));
                var cr = sources[s].Signal.Carriers[c];
                carMix.Add(toneMix[t]);
                carOff.Add(sources[s].FcHz + cr.OffsetHz - tones[t]);
                carLab.Add($"{sources[s].InstanceName}:{cr.Spec.Label} @ {(sources[s].FcHz + cr.OffsetHz) / 1e9:0.####} GHz");
            }
        return new EnvelopeRunResult
        {
            Time = tOut, ProbeNames = probes.Select(x => x.name).ToArray(), Probe = pOut,
            SupplyNames = Array.Empty<string>(), SupplyCurrent = Enumerable.Range(0, nSteps + 1).Select(_ => Array.Empty<double>()).ToArray(),
            SupplyVoltage = Array.Empty<double>(), Drive = dOut, DriveRe = sources.Select(s => s.Z.Real).ToArray(),
            F0Hz = tones[0], StepS = h, MaxHarm = M - 1, SignalRateHz = rate, Sources = sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = (double)newtonIters / Math.Max(1, total - 1), JacobianBuilds = jacBuilds,
            CarrierMix = carMix.ToArray(), CarrierOffsetHz = carOff.ToArray(), CarrierLabels = carLab.ToArray(),
            ToneFreqsHz = tones, MixLabels = Enumerable.Range(0, M).Select(m => lat.Label(m)).ToArray(),
            MixFreqHz = wMix.Select(w => w / (2 * Math.PI)).ToArray(),
            MixTones = Enumerable.Range(0, M).Select(m => lat.ToneOf(m)).ToArray(),
        };
    }

    private static Complex[,] Conj(Complex[,] a)
    {
        int n = a.GetLength(0), m = a.GetLength(1);
        var r = new Complex[n, m];
        for (int i = 0; i < n; i++) for (int j = 0; j < m; j++) r[i, j] = Complex.Conjugate(a[i, j]);
        return r;
    }

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

    private (Complex[,] V, Complex[][] X, Complex[,] q) Start(HbLinearExtractor ex, ModSourceModel[] srcs, HbApft apft, double[] tones,
        double[] wMix, int[] toneMix, int N, int[] ifNodes, EnvelopeParams p, Func<int, int, Complex> drive0)
    {
        int M = wMix.Length;
        var yNN = new Complex[M][,]; var iSrc = new Complex[M][]; var bSrc = new Complex[M][];
        void ExtractAll(double scale)
        {
            for (int m = 0; m < M; m++)
            {
                int t = Array.IndexOf(toneMix, m);
                for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = t >= 0 ? drive0(s, t) * scale : Complex.Zero;
                double w = Math.Abs(wMix[m]);
                Complex[,] y; Complex[] isr;
                if (m == 0) (y, isr) = ex.ExtractDC(); else (y, isr) = ex.Extract(w);
                bSrc[m] = ex.BuildSourceRhs(m == 0 ? 0.0 : w);
                if (wMix[m] < 0) { y = Conj(y); isr = isr.Select(Complex.Conjugate).ToArray(); }
                yNN[m] = y; iSrc[m] = isr;
            }
        }
        try
        {
            var dc = NonlinearDcEngine.Run(_nl, _settings);
            // A linearised-charge model (a composed FET with LinearC) evaluates its charge at the quiescent point:
            // freeze it there before the first HB solve reads charge, as HbEngine does.
            BiasFreeze.Apply(_nl, dc.Converged ? dc.NodeVoltages : null, dc.NodeVoltages);
            var V = new Complex[N, M];
            for (int a = 0; a < N; a++)
                V[a, 0] = dc.Converged && ifNodes[a] - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[ifNodes[a] - 1] : 0.0;
            ExtractAll(1.0);
            var V0 = (Complex[,])V.Clone();
            var sol = HbNewtonNd.Solve(V, yNN, iSrc, apft, tones, N, _nl, ifNodes, _settings, p.Tol);
            if (!sol.Converged)
            {
                Array.Copy(V0, V, V0.Length);
                for (int r = 1; r <= 10; r++)                                   // drive ramp
                {
                    ExtractAll(r / 10.0);
                    sol = HbNewtonNd.Solve(V, yNN, iSrc, apft, tones, N, _nl, ifNodes, _settings, p.Tol);
                }
            }
            if (sol is null || !sol.Converged) throw new InvalidOperationException("envelope: the multi-tone harmonic-balance start point did not converge");
            var X = new Complex[M][];
            for (int m = 0; m < M; m++)
            {
                var inl = new Complex[N];
                for (int a = 0; a < N; a++) inl[a] = wMix[m] < 0 ? Complex.Conjugate(sol.INl[a, m]) : sol.INl[a, m];
                X[m] = ex.SolveFullNetwork(m == 0 ? 0.0 : Math.Abs(wMix[m]), inl, bSrc[m]);  // canonical |w| state
            }
            var (_, qNl, _, _) = HbNewtonNd.EvaluateNonlinearNd(V, apft, N, _nl, ifNodes);
            return (V, X, qNl);
        }
        finally { foreach (var s in srcs) s.EnvelopeDrive = null; }
    }
}
