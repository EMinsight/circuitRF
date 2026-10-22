// ================================================================
//  EnvelopeRunner.cs  —  resolve an `env` directive, run it (in
//  parallel time segments), and pack the DataSet.
//
//  Parallel in time: the record is split into S segments; each runs on
//  its own freshly elaborated netlist (device models and sparse solvers
//  carry per-instance scratch state, so nothing is shared), starts
//  `Warmup` early from its own harmonic-balance point and discards the
//  warm-up.  With the warm-up past the network's memory the stitched
//  result is identical to one sequential run (measured: 5 us on a
//  7 MHz-resonant drain feed -> bit-identical at float precision).
//  Each segment also runs a short tail into the next one, and the
//  disagreement on that overlap is reported as the seam error.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public static class EnvelopeRunner
{
    /// <param name="Mode">auto / lattice: widely spaced carriers get their own fundamentals; single: one envelope spans them all.</param>
    public sealed record Plan(EnvelopeParams Params, int Segments, double WarmupS, bool SaveWaveforms, double Zmeas, int MaxMixOrder = 5,
                              string Mode = "auto");

    public static Plan Resolve(EnvelopeAnalysis a, IReadOnlyDictionary<string, Value> globals)
    {
        double Num(string expr, double def)
        {
            if (string.IsNullOrWhiteSpace(expr) || expr.Equals("auto", StringComparison.OrdinalIgnoreCase)) return def;
            try
            {
                var v = HbEngine.BuildEvaluator(globals).EvalExpr(Parser.Parse(expr), HbEngine.BuildScope(globals));
                return v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"envelope '{a.Name}': cannot evaluate '{expr}': {ex.Message}");
            }
        }
        int osr = a.Accuracy.ToLowerInvariant() switch
        {
            "draft" => 1,
            "fine" => 8,
            "standard" or "" => 4,
            var x when int.TryParse(x, out int n) && n > 0 => n,
            _ => throw new InvalidOperationException($"envelope '{a.Name}': Accuracy must be draft, standard, fine or an oversampling count"),
        };
        double stop = Num(a.StopExpr, 100e-6);
        if (stop <= 0) throw new InvalidOperationException($"envelope '{a.Name}': Stop must be positive");
        int seg = a.SegmentsExpr.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(1, (int)Num(a.SegmentsExpr, 1));
        double warm = a.WarmupExpr.Equals("auto", StringComparison.OrdinalIgnoreCase) ? double.NaN : Math.Max(0, Num(a.WarmupExpr, 0));
        var p = new EnvelopeParams
        {
            StopS = stop,
            StepS = Num(a.StepExpr, 0),
            Oversample = osr,
            MaxHarm = Math.Max(1, (int)Num(a.MaxHarmExpr, 5)),
            Tol = Num(a.TolExpr, 1e-6),
            Probes = a.Probes.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries),
            MeasureZ = Num(a.ZmeasExpr, 50),
            StartS = Num(a.StartExpr, 0),
        };
        bool save = !a.SaveWaveforms.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                    !a.SaveWaveforms.Equals("false", StringComparison.OrdinalIgnoreCase);
        string mode = (a.Envelope ?? "auto").Trim().ToLowerInvariant();
        if (mode is "" ) mode = "auto";
        if (mode is not ("auto" or "single" or "lattice"))
            throw new InvalidOperationException($"envelope '{a.Name}': Envelope must be auto, single or lattice (got '{a.Envelope}')");
        return new Plan(p, seg, warm, save, p.MeasureZ, Math.Max(1, (int)Num(a.MaxMixOrderExpr, 7)), mode);
    }

    /// <summary>The envelope step the engines will use (same rule as theirs: signal rate x oversampling, or explicit).</summary>
    public static double StepSeconds(Core.Devices.ModSourceModel[] sources, EnvelopeParams p, int fundamentals, bool spanIntermod = false)
    {
        if (p.StepS > 0) return p.StepS;
        double need;
        if (fundamentals == 2) need = EnvelopeEngineMt.Plan(sources).Max(g => g.needRate);
        else
        {
            need = 0;
            double f0 = sources[0].FcHz;
            foreach (var s in sources)
                foreach (var c in s.Signal.Carriers)
                {
                    need = Math.Max(need, 2 * (Math.Abs(s.FcHz + c.OffsetHz - f0) + 2.5 * c.Spec.BandwidthHz));
                    need = Math.Max(need, c.NativeRateHz);
                }
            // One envelope over carriers that are far apart (Envelope=single): the band must also hold their
            // third-order products 2fi - fj, or IM3 would fold back across the envelope's Nyquist edge.
            if (spanIntermod)
            {
                var cs = sources.SelectMany(s => s.Signal.Carriers.Select(c => (f: s.FcHz + c.OffsetHz, bw: c.Spec.BandwidthHz))).ToArray();
                foreach (var a in cs)
                    foreach (var b in cs)
                        if (a.f != b.f)
                            need = Math.Max(need, 2 * (Math.Abs(2 * a.f - b.f - f0) + 2.5 * Math.Max(a.bw, b.bw)));
            }
        }
        double rate = 1.92e6;
        while (rate < need) rate *= 2;
        return 1.0 / (rate * Math.Max(1, p.Oversample));
    }

    /// <summary>Runs the plan; <paramref name="elaborate"/> must return a fresh netlist on every call.</summary>
    public static DataSet Run(Plan plan, Func<ElaboratedNetlist> elaborate, AnalysisSettings? settings = null,
                              RunControl? control = null, Action<string>? note = null)
    {
        var sw = Stopwatch.StartNew();
        var p = plan.Params;
        // probe step size and total steps with a throw-away engine construction (cheap: no run)
        var first = elaborate();
        var src = first.Components.Select(c => c.Model).OfType<Core.Devices.ModSourceModel>().FirstOrDefault()
                  ?? throw new InvalidOperationException("Envelope analysis needs at least one MOD_Source (the modulated signal).");
        _ = src.Signal;                                             // generate (or load) the signal once, up front
        var allSrc = first.Components.Select(c => c.Model).OfType<Core.Devices.ModSourceModel>().ToArray();
        foreach (var m in allSrc) _ = m.Signal;
        int groups = EnvelopeEngineMt.Plan(allSrc).Count;
        int fundamentals = plan.Mode == "single" ? 1 : groups;
        if (plan.Mode == "single" && groups > 1)
            note?.Invoke($"envelope: Envelope=single - one envelope around {allSrc[0].FcHz / 1e9:0.####} GHz spans all {groups} carrier groups and their IM3 (no lattice)");
        if (fundamentals > 2)
            throw new InvalidOperationException($"envelope: {fundamentals} widely spaced carrier groups would need {fundamentals} fundamentals; up to 2 are supported");

        int cores = Math.Max(1, Environment.ProcessorCount);
        double warm = double.IsNaN(plan.WarmupS) ? 5e-6 : plan.WarmupS;
        int segs = plan.Segments > 0 ? plan.Segments : cores;
        // never cut a segment shorter than 4x its warm-up or 20 us — the warm-up would dominate
        segs = (int)Math.Clamp(Math.Floor(p.StopS / Math.Max(4 * warm, 20e-6)), 1, segs);
        if (plan.Segments == 1) segs = 1;
        if (segs > 1 && warm <= 0)
            note?.Invoke($"envelope: Warmup=0 with {segs} parallel segments - each segment starts cold, so the seams are not settled (see EnvSeamError); leave Warmup auto or use Segments=1");
        // segment boundaries on WHOLE steps (a non-integer split drifts the stitched record by samples)
        double h = StepSeconds(allSrc, p, fundamentals, spanIntermod: fundamentals == 1 && groups > 1);
        int totalSteps = (int)Math.Round(p.StopS / h);
        var bound = Enumerable.Range(0, segs + 1).Select(i => (int)((long)totalSteps * i / segs)).ToArray();
        int tailSteps = segs > 1 ? Math.Max(1, (int)Math.Min(0.25 * totalSteps / segs, Math.Round(1e-6 / h))) : 0;

        var results = new EnvelopeRunResult[segs];
        var nets = new ElaboratedNetlist[segs];
        nets[0] = first;
        for (int s = 1; s < segs; s++) nets[s] = elaborate();
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.GetEnvironmentVariable("CRF_ENV_SERIAL") is null ? cores : 1, CancellationToken = control?.Token ?? default };
        Parallel.For(0, segs, opts, s =>
        {
            var ps = p with
            {
                StepS = h,
                StartS = p.StartS + bound[s] * h,
                StopS = (bound[s + 1] - bound[s] + (s < segs - 1 ? tailSteps : 0)) * h,
                WarmupS = segs > 1 && s > 0 ? warm : (plan.Segments == 1 || segs == 1 ? (double.IsNaN(plan.WarmupS) ? 0 : plan.WarmupS) : warm),
            };
            results[s] = fundamentals == 2
                ? new EnvelopeEngineMt(nets[s], settings).Run(ps, plan.MaxMixOrder, control)
                : new EnvelopeEngine(nets[s], settings).Run(ps, control);
        });

        // stitch, measure the seam on the overlap tails
        h = results[0].StepS;
        var time = new List<double>(); var probe = new List<Complex[][]>(); var sup = new List<double[]>();
        var drv = new List<Complex>[results[0].Drive.Length];
        for (int i = 0; i < drv.Length; i++) drv[i] = new List<Complex>();
        double seam = 0;
        for (int s = 0; s < segs; s++)
        {
            var r = results[s];
            int perSeg = bound[s + 1] - bound[s];
            int take = s < segs - 1 ? perSeg : r.Time.Length;
            for (int i = 0; i < take && i < r.Time.Length; i++)
            {
                time.Add((bound[s] + i) * h);
                probe.Add(r.Probe[i]); sup.Add(r.SupplyCurrent[i]);
                for (int d = 0; d < drv.Length; d++) drv[d].Add(r.Drive[d][i]);
            }
            if (s > 0)
            {
                var prev = results[s - 1];
                int pseg = bound[s] - bound[s - 1];
                int ov = Math.Min(prev.Time.Length - pseg, r.Time.Length);
                int kk = Math.Min(1, r.Probe[0][0].Length - 1);
                for (int i = 0; i < ov; i++)
                    for (int pi = 0; pi < r.ProbeNames.Length; pi++)
                    {
                        var a = prev.Probe[pseg + i][pi][kk]; var b = r.Probe[i][pi][kk];
                        double m = Math.Max(a.Magnitude, 1e-12);
                        seam = Math.Max(seam, (a - b).Magnitude / m);
                    }
            }
        }
        var R = results[0];
        var merged = new EnvelopeRunResult
        {
            Time = time.ToArray(), ProbeNames = R.ProbeNames, Probe = probe.ToArray(), SupplyNames = R.SupplyNames,
            SupplyCurrent = sup.ToArray(), SupplyVoltage = R.SupplyVoltage, Drive = drv.Select(d => d.ToArray()).ToArray(),
            DriveRe = R.DriveRe, F0Hz = R.F0Hz, StepS = h, MaxHarm = R.MaxHarm, SignalRateHz = R.SignalRateHz, Sources = R.Sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = results.Average(x => x.MeanNewton),
            JacobianBuilds = results.Sum(x => x.JacobianBuilds), WorstTangentError = results.Max(x => x.WorstTangentError),
            CarrierMix = R.CarrierMix, CarrierOffsetHz = R.CarrierOffsetHz, CarrierLabels = R.CarrierLabels,
            ToneFreqsHz = R.ToneFreqsHz, MixLabels = R.MixLabels, MixFreqHz = R.MixFreqHz, MixTones = R.MixTones,
        };
        foreach (var n in results.SelectMany(x => x.Notes).Distinct()) { merged.Notes.Add(n); note?.Invoke(n); }
        // a run that started cold (no warm-up on its first segment) is measured after a short settle
        double firstWarm = segs > 1 ? warm : (double.IsNaN(plan.WarmupS) ? 0 : plan.WarmupS);
        double skip = firstWarm > 0 ? 0 : Math.Min(5e-6, 0.1 * p.StopS);
        var ds = EnvelopeDataSet.Build(merged, plan, skip);
        ds.Add("__EnvSegments", DataCube.Scalar(segs));
        ds.Add("__EnvWarmup_s", DataCube.Scalar(warm));
        ds.Add("__EnvSeamError", DataCube.Scalar(seam));
        return ds;
    }
}
