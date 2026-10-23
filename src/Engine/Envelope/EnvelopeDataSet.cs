// ================================================================
//  EnvelopeDataSet.cs  —  envelope results and modulated measurements
//
//  Cubes (probe = a requested node; the LAST probe is the "output" for
//  gain / AM-AM / AM-PM / NMSE / efficiency):
//    V          [time, node, harmonic|mixIndex]  complex peak phasors (SaveWaveforms)
//    Idc        [time, supply]          real   DC supply branch currents (A)
//    PSD        [node, freq]            real   dBm/Hz into Zmeas, absolute frequency
//               single fundamental: the fundamental envelope;  two fundamentals:
//               every carrier product and the IM3/IM5/IM2 products, power-summed onto
//               one ascending grid over the union of their spans (overlaps merge)
//    ACLR       [node, carrier, metric] real   Pch_dBm, ACPR1L, ACPR1U, ACPR2L, ACPR2U (dBc)
//    IMD        [node, product]         real   mean power (dBm) of each listed lattice product
//    Pavg_dBm / PAPR_dB [node]   CCDF [node, prob] (dB above mean)
//    AMAM / AMAM_min / AMAM_max / AMPM  [Pin]  binned on instantaneous available input power;
//               the min/max spread at one Pin is the memory (single fundamental)
//    Pin_avg_dBm, Pout_avg_dBm, Gt_dB, NMSE_dB, Pdc_W, DE_pct   scalars
//    __EnvSnpFit [instance, metric]  order, rel_error, sigma_max of each SnP's
//               rational fit (only when the circuit has one)
//  Measurements skip the first `SkipS` seconds (settling) when the run
//  started with no warm-up.
// ================================================================

using System.Numerics;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public static class EnvelopeDataSet
{
    private static readonly string[] AclrLabels = ["Pch_dBm", "ACPR1L", "ACPR1U", "ACPR2L", "ACPR2U"];

    /// <summary>AM-AM / AM-PM instantaneous-input-power grid (dBm, 0.5 dB bins) and the samples a bin needs.</summary>
    public const double AmGridLoDbm = -40, AmGridHiDbm = 60;
    public const int AmMinSamples = 16;

    public static DataSet Build(EnvelopeRunResult r, EnvelopeRunner.Plan plan, double skipS = 0)
    {
        var ds = new DataSet();
        int nt = r.Time.Length, np = r.ProbeNames.Length, nk = r.Probe[0][0].Length;
        double zm = plan.Zmeas, fs = 1.0 / r.StepS;
        bool multi = r.MixTones is not null;
        int t0 = Math.Min(nt - 16, (int)Math.Round(skipS / r.StepS));
        int nm = nt - t0;
        var probeAxis = new Axis("node", Enumerable.Range(0, np).Select(i => (double)i).ToArray(), "", r.ProbeNames);

        if (plan.SaveWaveforms)
        {
            var v = new Complex[nt * np * nk];
            for (int t = 0; t < nt; t++)
                for (int p = 0; p < np; p++)
                    for (int k = 0; k < nk; k++)
                        v[(t * np + p) * nk + k] = r.Probe[t][p][k];
            var third = multi
                ? new Axis("mixIndex", r.MixFreqHz!, "Hz", r.MixLabels)
                : new Axis("harmonic", Enumerable.Range(0, nk).Select(i => (double)i).ToArray());
            ds.Add("V", new DataCube([new Axis("time", r.Time, "s"), probeAxis, third], v));
            if (r.SupplyNames.Length > 0)
            {
                var idc = new double[nt * r.SupplyNames.Length];
                for (int t = 0; t < nt; t++)
                    for (int s = 0; s < r.SupplyNames.Length; s++) idc[t * r.SupplyNames.Length + s] = r.SupplyCurrent[t][s];
                ds.Add("Idc", new DataCube([new Axis("time", r.Time, "s"),
                    new Axis("supply", Enumerable.Range(0, r.SupplyNames.Length).Select(i => (double)i).ToArray(), "", r.SupplyNames)], idc));
            }
        }

        Complex[] Env(int p, int k)
        {
            var y = new Complex[nm];
            for (int t = 0; t < nm; t++) y[t] = r.Probe[t0 + t][p][k];
            return y;
        }

        // Spectral resolution follows the CARRIERS' rate, not the envelope's: 8192 bins at the rate a
        // carrier group's own envelope runs at. A single envelope spanning widely spaced carriers
        // (Envelope=single) runs 8-16x faster, and a fixed 8192 bins then widened the bin to 0.5-2 MHz -
        // enough Hann leakage at a 20 MHz channel's edge to read ACPR1 up to 7 dB high.
        double narrow = 1.92e6, needNarrow = EnvelopeEngineMt.Plan(r.Sources).Max(g => g.needRate);
        while (narrow < needNarrow) narrow *= 2;
        int nseg = 8192 * (int)Math.Max(1, Math.Round(r.SignalRateHz / narrow));
        while (nseg > nm / 4 && nseg > 256) nseg /= 2;

        // carriers: spec + product + offset
        var carSpecs = new List<CarrierSpec>();
        foreach (var s in r.Sources) foreach (var c in s.Signal.Carriers) carSpecs.Add(c.Spec);
        int nc = carSpecs.Count;

        // products shown in the spectrum (multi: tones + IM3/IM5/IM2); single: the fundamental
        var specProducts = new List<(int k, double fHz, string label)>();
        var imd = new List<(int k, string label)>();
        bool wideSingle = !multi && WideTwoCarrier(r, carSpecs, out _, out _, out _);
        if (!multi)
        {
            specProducts.Add((1, r.F0Hz, "fundamental"));
            if (wideSingle && nk > 1) specProducts.Insert(0, (0, 0.0, "baseband"));
        }
        else
        {
            int Find(int a, int b)
            {
                for (int m = 0; m < r.MixTones!.Length; m++)
                {
                    var t = r.MixTones[m];
                    if ((t[0] == a && t[1] == b) || (t[0] == -a && t[1] == -b)) return m;
                }
                return -1;
            }
            foreach (var (a, b, lab) in new[] { (1, 0, "C1 (f1)"), (0, 1, "C2 (f2)"), (2, -1, "IM3L (2f1-f2)"), (-1, 2, "IM3U (2f2-f1)"),
                                                 (3, -2, "IM5L (3f1-2f2)"), (-2, 3, "IM5U (3f2-2f1)"), (-1, 1, "IM2 (f2-f1)"),
                                                 (1, 1, "f1+f2"), (2, 0, "H2 (2f1)"), (0, 2, "H2 (2f2)") })
            {
                int m = Find(a, b);
                if (m < 0) continue;
                imd.Add((m, lab));
            }
            // The spectrum shows EVERY lattice product of the baseband and fundamental zones (|k1 + k2| <= 1):
            // the carriers, IM3/IM5/IM7, and the even-order baseband products at multiples of the spacing.
            // Listing only the labelled products left real simulated content out of the plot.
            for (int m = 0; m < r.MixTones!.Length; m++)
            {
                var t = r.MixTones[m];
                if (Math.Abs(t[0] + t[1]) > 1 || Math.Abs(r.MixFreqHz![m]) < 1) continue;
                specProducts.Add((m, Math.Abs(r.MixFreqHz[m]), $"({t[0]},{t[1]})"));
            }
            specProducts.Sort((x, y) => x.fHz.CompareTo(y.fHz));
        }

        var psdF = new List<double>(); var psdV = new List<double>[np];
        for (int p = 0; p < np; p++) psdV[p] = new List<double>();
        var aclr = new double[np * nc * AclrLabels.Length];
        var pavg = new double[np]; var papr = new double[np];
        var ccdf = new double[np * 200];
        double[]? probAxis = null;
        double[]? gridF = null;
        for (int p = 0; p < np; p++)
        {
            // Every product's envelope spectrum sits on its own span fk ± fs/2. With two fundamentals
            // the spans of neighbouring products OVERLAP whenever fs exceeds their spacing (400 MHz
            // apart at the standard 491.52 MHz envelope rate), and appending them gave a frequency
            // axis that ran backwards at each seam: a stitched-looking spectrum with two crossing
            // skirts. The physical spectrum at f is the sum of every product's contribution there,
            // so the spans are power-summed onto ONE ascending grid (linear interpolation of each
            // product's Welch bins; the cross-spectrum between products is neglected, which only
            // matters in the far skirts where two products' regrowth meets).
            var spans = new List<(double fk, double[] f, double[] d)>();
            foreach (var (k, fHz, _) in specProducts)
            {
                var y = Env(p, k);
                bool neg = multi && r.MixFreqHz![k] < 0;
                if (neg) for (int i = 0; i < y.Length; i++) y[i] = Complex.Conjugate(y[i]);
                var (f, d) = SignalMeasure.Welch(y, fs, nseg);
                if (!multi && k == 0) for (int i = 0; i < d.Length; i++) d[i] *= 4;   // real baseband, one-sided
                spans.Add((fHz, f, d));
            }
            // A single envelope wider than the carrier (Envelope=single over widely spaced carriers) runs past
            // 0 Hz. Each harmonic envelope keeps only physical frequencies above 0 (the baseband one its
            // positive half); a mixing product (a,b) lives in envelope k = a+b, so the fundamental's envelope
            // holds IM3/IM5 even beyond +/- f0/2 and is not cropped there.
            if (!multi)
                for (int si = 0; si < spans.Count; si++)
                {
                    var (fk, f, d) = spans[si];
                    int lo = Array.FindIndex(f, x => fk + x > 0);
                    if (lo > 0) spans[si] = (fk, f[lo..], d[lo..]);
                }
            gridF ??= CompositeGrid(spans);
            if (p == 0) psdF.AddRange(gridF);
            var tot = new double[gridF.Length];
            foreach (var (fk, f, d) in spans)
            {
                double df = f[1] - f[0];
                for (int i = 0; i < gridF.Length; i++)
                {
                    double pos = (gridF[i] - fk - f[0]) / df;
                    if (pos < -1e-9 || pos > f.Length - 1 + 1e-9) continue;
                    int i0 = Math.Clamp((int)Math.Floor(pos), 0, f.Length - 1);
                    int i1 = Math.Min(i0 + 1, f.Length - 1);
                    double a = Math.Clamp(pos - i0, 0, 1);
                    tot[i] += d[i0] + (d[i1] - d[i0]) * a;
                }
            }
            foreach (double v in tot) psdV[p].Add(10 * Math.Log10(Math.Max(v, 1e-300) / (2 * zm) * 1e3));
            for (int c = 0; c < nc; c++)
            {
                int k = r.CarrierMix.Length > c ? r.CarrierMix[c] : 1;
                var y = Env(p, k);
                if (multi && r.MixFreqHz![k] < 0) for (int i = 0; i < y.Length; i++) y[i] = Complex.Conjugate(y[i]);
                var (f, d) = SignalMeasure.Welch(y, fs, nseg);
                var (mb, sp) = SignalMeasure.AclrBands(carSpecs[c]);
                var a = SignalMeasure.Aclr(f, d, fs, r.CarrierOffsetHz.Length > c ? r.CarrierOffsetHz[c] : 0, mb, sp);
                int o = (p * nc + c) * AclrLabels.Length;
                aclr[o] = 10 * Math.Log10(Math.Max(a.ChannelPower, 1e-300) / (2 * zm) * 1e3);
                aclr[o + 1] = a.Acpr1L; aclr[o + 2] = a.Acpr1U; aclr[o + 3] = a.Acpr2L; aclr[o + 4] = a.Acpr2U;
            }
            // total in-band envelope power over the carrier products
            var ks = multi ? r.CarrierMix.Distinct().ToArray() : new[] { 1 };
            double ptot = 0;
            foreach (int k in ks) { var y = Env(p, k); foreach (var v in y) ptot += v.Real * v.Real + v.Imaginary * v.Imaginary; }
            ptot /= nm;
            pavg[p] = 10 * Math.Log10(ptot / (2 * zm) * 1e3);
            var y1 = Env(p, ks[0]);
            papr[p] = SignalMeasure.PaprDb(y1, 1e-4);
            var (db, pr) = SignalMeasure.Ccdf(y1, 200);
            probAxis ??= pr;
            Array.Copy(db, 0, ccdf, p * 200, 200);
        }
        int nf = psdF.Count;
        ds.Add("PSD", new DataCube([probeAxis, new Axis("freq", psdF.ToArray(), "Hz")], psdV.SelectMany(x => x).ToArray()));
        ds.Add("ACLR", new DataCube([probeAxis,
            new Axis("carrier", Enumerable.Range(0, nc).Select(i => (double)i).ToArray(), "",
                     r.CarrierLabels.Length == nc ? r.CarrierLabels : carSpecs.Select(c => c.Label).ToArray()),
            new Axis("metric", Enumerable.Range(0, AclrLabels.Length).Select(i => (double)i).ToArray(), "", AclrLabels)], aclr));
        ds.Add("Pavg_dBm", new DataCube([probeAxis], pavg));
        ds.Add("PAPR_dB", new DataCube([probeAxis], papr));
        ds.Add("CCDF", new DataCube([probeAxis, new Axis("prob", probAxis!)], ccdf));
        if (imd.Count > 0)
        {
            var im = new double[np * imd.Count];
            for (int p = 0; p < np; p++)
                for (int i = 0; i < imd.Count; i++)
                {
                    var y = Env(p, imd[i].k);
                    double pw = 0;
                    bool dc = imd[i].k == 0;
                    foreach (var v in y) pw += v.Real * v.Real + v.Imaginary * v.Imaginary;
                    pw /= nm;
                    im[p * imd.Count + i] = 10 * Math.Log10(Math.Max(pw, 1e-300) / (2 * zm) * 1e3);
                }
            ds.Add("IMD", new DataCube([probeAxis,
                new Axis("product", Enumerable.Range(0, imd.Count).Select(i => (double)i).ToArray(), "", imd.Select(x => x.label).ToArray())], im));
        }
        else if (!multi && SingleEnvelopeImd(r, carSpecs, np, nk, zm, fs, nseg, Env) is { } sim)
            ds.Add("IMD", new DataCube([probeAxis,
                new Axis("product", Enumerable.Range(0, sim.labels.Length).Select(i => (double)i).ToArray(), "", sim.labels)], sim.dbm));

        // ── gain, AM-AM / AM-PM, NMSE, efficiency on the last probe vs the (summed) drive ──
        if (r.Drive.Length > 0 && np > 0)
        {
            var drive = new Complex[nm];
            double reZ = r.DriveRe[0];
            for (int t = 0; t < nm; t++) foreach (var d in r.Drive) drive[t] += d[t0 + t];
            double pin = 0;
            for (int t = 0; t < nm; t++) pin += drive[t].Real * drive[t].Real + drive[t].Imaginary * drive[t].Imaginary;
            pin /= nm;
            double pinW = pin / (8 * reZ);
            double poutW = Math.Pow(10, pavg[np - 1] / 10) / 1e3;
            ds.Add("Pin_avg_dBm", DataCube.Scalar(10 * Math.Log10(pinW * 1e3)));
            ds.Add("Pout_avg_dBm", DataCube.Scalar(pavg[np - 1]));
            ds.Add("Gt_dB", DataCube.Scalar(10 * Math.Log10(poutW / pinW)));
            if (!multi)
            {
                var y = Env(np - 1, 1);
                var (g, lag, nmse) = SignalMeasure.Align(drive, y, 16);
                ds.Add("NMSE_dB", DataCube.Scalar(nmse));
                ds.Add("Delay_s", DataCube.Scalar(lag * r.StepS));
                double gphase = Math.Atan2(g.Imaginary, g.Real);
                var bins = new SortedDictionary<int, (double sum, double min, double max, double ph, int n)>();
                for (int t = 0; t + lag < nm; t++)
                {
                    double pi = (drive[t].Real * drive[t].Real + drive[t].Imaginary * drive[t].Imaginary) / (8 * reZ);
                    if (pi <= 0) continue;
                    var yo = y[t + lag];
                    double po = (yo.Real * yo.Real + yo.Imaginary * yo.Imaginary) / (2 * zm);
                    if (po <= 0) continue;
                    double gt = 10 * Math.Log10(po / pi);
                    double ph = (Complex.Log(yo / drive[t]).Imaginary - gphase) * 180 / Math.PI;
                    ph = ((ph + 540) % 360) - 180;
                    int b = (int)Math.Floor(10 * Math.Log10(pi * 1e3) / 0.5);
                    bins[b] = bins.TryGetValue(b, out var e)
                        ? (e.sum + gt, Math.Min(e.min, gt), Math.Max(e.max, gt), e.ph + ph, e.n + 1)
                        : (gt, gt, gt, ph, 1);
                }
                // A FIXED absolute grid (AmGridLoDbm..AmGridHiDbm, 0.5 dB), the same at every sweep point: a
                // data-dependent grid made a swept AM-AM stack every point against the FIRST point's axis.
                // A bin with fewer than AmMinSamples samples is NaN (plots skip it) rather than noise. The cubes are
                // emitted at EVERY point, however few bins hold data: a sweep point without them (a CW drive fills one
                // bin) could not be stacked against the others, and the whole sweep was refused.
                int b0 = (int)Math.Round(AmGridLoDbm / 0.5), b1 = (int)Math.Round(AmGridHiDbm / 0.5);
                int nb = b1 - b0;
                var pinAxis = new Axis("Pin_inst", Enumerable.Range(b0, nb).Select(b => (b + 0.5) * 0.5).ToArray(), "dBm");
                double[] Col(Func<(double sum, double min, double max, double ph, int n), double> f) =>
                    Enumerable.Range(b0, nb).Select(b => bins.TryGetValue(b, out var e) && e.n >= AmMinSamples ? f(e) : double.NaN).ToArray();
                ds.Add("AMAM", new DataCube([pinAxis], Col(e => e.sum / e.n)));
                ds.Add("AMAM_min", new DataCube([pinAxis], Col(e => e.min)));
                ds.Add("AMAM_max", new DataCube([pinAxis], Col(e => e.max)));
                ds.Add("AMPM", new DataCube([pinAxis], Col(e => e.ph / e.n)));
            }
            if (r.SupplyNames.Length > 0)
            {
                double pdc = 0;
                for (int s = 0; s < r.SupplyNames.Length; s++)
                {
                    double iav = 0;
                    for (int t = t0; t < nt; t++) iav += r.SupplyCurrent[t][s];
                    pdc += -r.SupplyVoltage[s] * iav / nm;      // branch current flows + node -> source; a supply delivers -V*I
                }
                ds.Add("Pdc_W", DataCube.Scalar(pdc));
                // NaN rather than absent when no DC power is drawn, for the same reason: every sweep point has the cube.
                ds.Add("DE_pct", DataCube.Scalar(pdc > 0 ? 100 * poutW / pdc : double.NaN));
            }
        }
        ds.Add("__EnvStep_s", DataCube.Scalar(r.StepS));
        ds.Add("__EnvF0_Hz", DataCube.Scalar(r.F0Hz));
        ds.Add("__EnvSignalRate_Hz", DataCube.Scalar(r.SignalRateHz));
        ds.Add("__EnvWall_s", DataCube.Scalar(r.WallSeconds));
        ds.Add("__EnvNewtonPerStep", DataCube.Scalar(r.MeanNewton));
        ds.Add("__EnvJacobians", DataCube.Scalar(r.JacobianBuilds));
        ds.Add("__EnvTangentError", DataCube.Scalar(r.WorstTangentError));
        ds.Add("__EnvMeasureFrom_s", DataCube.Scalar(t0 * r.StepS));
        ds.Add("__EnvFundamentals", DataCube.Scalar(multi ? r.ToneFreqsHz!.Length : 1));
        if (r.SnpFits.Count > 0)
        {
            string[] metrics = ["order", "rel_error", "sigma_max"];
            ds.Add("__EnvSnpFit", new DataCube([
                new Axis("instance", Enumerable.Range(0, r.SnpFits.Count).Select(i => (double)i).ToArray(), "", r.SnpFits.Select(f => f.Instance).ToArray()),
                new Axis("metric", Enumerable.Range(0, metrics.Length).Select(i => (double)i).ToArray(), "", metrics)],
                r.SnpFits.SelectMany(f => new[] { f.Order, f.RelativeError, f.SigmaMax }).ToArray()));
        }
        return ds;
    }

    /// <summary>
    /// The IMD cube of a SINGLE envelope that spans two widely spaced carriers (Envelope=single): the
    /// same products the lattice lists, each read as the power of the harmonic envelope that holds it
    /// (k = a + b for the product a f1 + b f2) integrated over +/- half the carrier spacing around its offset F - k f0. That
    /// is the lattice's per-product mean power by another route, so the two modes compare directly.
    /// Null unless there are exactly two carrier frequencies more than 3x the bandwidth apart.
    /// </summary>
    private static (string[] labels, double[] dbm)? SingleEnvelopeImd(EnvelopeRunResult r, List<CarrierSpec> carSpecs,
        int np, int nk, double zm, double fs, int nseg, Func<int, int, Complex[]> env)
    {
        if (!WideTwoCarrier(r, carSpecs, out double f1, out double f2, out double sp)) return null;
        var prods = new[] { (1, 0, "C1 (f1)"), (0, 1, "C2 (f2)"), (2, -1, "IM3L (2f1-f2)"), (-1, 2, "IM3U (2f2-f1)"),
                            (3, -2, "IM5L (3f1-2f2)"), (-2, 3, "IM5U (3f2-2f1)"), (-1, 1, "IM2 (f2-f1)"),
                            (1, 1, "f1+f2"), (2, 0, "H2 (2f1)"), (0, 2, "H2 (2f2)") };
        var keep = new List<(string lab, int k, double off)>();
        foreach (var (a, b, lab) in prods)
        {
            double F = a * f1 + b * f2;
            if (F <= 0) continue;
            // A product (a,b) is generated in harmonic envelope k = a+b (the fast-time transform treats the
            // envelopes as frozen over a carrier period). round(F/f0) is NOT that: IM5 of carriers at f0 -/+ s
            // sits at f0 -/+ 5s, which for s = f0/10 is exactly 0.5 f0 and 1.5 f0, and banker's rounding sent
            // IM5L to the baseband envelope and IM5U to the second harmonic's.
            int k = a + b;
            if (k < 0) continue;
            double off = F - k * r.F0Hz;
            if (k > nk - 1 || Math.Abs(off) + sp / 2 > fs / 2) continue;     // outside this envelope's band
            keep.Add((lab, k, off));
        }
        if (keep.Count == 0) return null;
        var dbm = new double[np * keep.Count];
        for (int p = 0; p < np; p++)
        {
            var welch = new Dictionary<int, (double[] f, double[] d)>();
            for (int i = 0; i < keep.Count; i++)
            {
                var (_, k, off) = keep[i];
                if (!welch.TryGetValue(k, out var w)) welch[k] = w = SignalMeasure.Welch(env(p, k), fs, nseg);
                double pw = SignalMeasure.BandPower(w.f, w.d, off, sp);
                // the DC envelope is the physical baseband itself: a line at +F appears at +F and -F of its
                // two-sided spectrum, and its power is not halved the way Re(V e^{jwt}) is
                if (k == 0) pw = 2 * (pw + SignalMeasure.BandPower(w.f, w.d, -off, sp));
                dbm[p * keep.Count + i] = 10 * Math.Log10(Math.Max(pw, 1e-300) / (2 * zm) * 1e3);
            }
        }
        return (keep.Select(x => x.lab).ToArray(), dbm);
    }

    /// <summary>Exactly two carrier frequencies more than 3x the widest bandwidth apart (one envelope over them).</summary>
    private static bool WideTwoCarrier(EnvelopeRunResult r, List<CarrierSpec> carSpecs, out double f1, out double f2, out double sp)
    {
        f1 = f2 = sp = 0;
        var cf = r.CarrierOffsetHz.Select(o => r.F0Hz + o).Distinct().OrderBy(x => x).ToArray();
        if (cf.Length != 2 || carSpecs.Count == 0) return false;
        double bw = carSpecs.Max(c => Math.Max(c.BandwidthHz, 1e6));
        f1 = cf[0]; f2 = cf[1]; sp = f2 - f1;
        return sp > 3 * bw;
    }

    /// <summary>One ascending frequency grid over the union of the products' spans fk + f (Welch
    /// offsets, uniform step): overlapping spans merge into one run of points, and a gap between
    /// spans that do not meet is simply not sampled.</summary>
    private static double[] CompositeGrid(List<(double fk, double[] f, double[] d)> spans)
    {
        double df = spans[0].f[1] - spans[0].f[0];
        var iv = spans.Select(s => (lo: s.fk + s.f[0], hi: s.fk + s.f[^1])).OrderBy(x => x.lo).ToList();
        var merged = new List<(double lo, double hi)>();
        foreach (var x in iv)
        {
            if (merged.Count > 0 && x.lo <= merged[^1].hi + df)
                merged[^1] = (merged[^1].lo, Math.Max(merged[^1].hi, x.hi));
            else merged.Add(x);
        }
        var g = new List<double>();
        foreach (var (lo, hi) in merged)
        {
            int n = (int)Math.Floor((hi - lo) / df + 1e-9) + 1;
            for (int i = 0; i < n; i++) g.Add(lo + i * df);
        }
        return g.ToArray();
    }
}
