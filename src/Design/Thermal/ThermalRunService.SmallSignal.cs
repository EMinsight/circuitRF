// brief-em3d-80 — what a thermal run adds when its setup states Rth, Zth or Pulse, after the steady sweep:
//
//   R-em3d80-1  the Rth matrix across the named sources, every boundary homogeneous, at the first solved point (the tangent
//               there when k(T) is on);
//   R-em3d80-2  Z_th(jω) of every source's own place and of each named probe, per watt in each source, over DC plus a
//               logarithmic band;
//   R-em3d80-3  a Foster fit of each, with its error — above 2 % a warning;
//   R-em3d80-4  per sweep point, a pulse train's peak, single-pulse and average temperature of each Z_th place, and its
//               waveform over one period, added to the point's zero-power baseline;
//   R-em3d80-5  each source's self network as <results>/<key>.thermal.<source>.foster.cnl.
//
// The steady sweep is unchanged: all of this is read off the same mesh and the same problem.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Thermal;
using CircuitRF.Thermal.Frequency;
using RfCore.Data;

namespace CircuitRF.Design.Thermal;

public static partial class ThermalRunService
{
    /// <summary>brief-em3d-80 — the group the Rth matrix, Z_th, the Foster fits and the pulse waveforms are carried in. The
    /// pulse's peak, single-pulse and average temperatures are scalars per sweep point and go in <see cref="Group"/>.</summary>
    public const string SmallSignalGroup = "smallsignal";

    /// <summary>R-em3d80-3 — above this fit error the run warns.</summary>
    public const double FosterWarnAbove = 0.02;

    /// <summary>R-em3d80-4b — samples of the pulse waveform over one period.</summary>
    public const int PulseSamples = 200;

    /// <summary>What the small-signal step reads of the steady run.</summary>
    private sealed record SmallSignalInput(
        EmSetup Setup, CemThermal T, C3dDocument Document, C3dElaboration Elaboration, ThermalLowering Lowering, ThermalMesh Mesh,
        ThermalAssembly Assembly, List<ThermalConductivity> Conductivity, List<ThermalProblem?> Problems, List<C3dResolution> Resolutions,
        List<double[]> Fields, bool[] Skipped, List<(string Var, double[] Values, string Unit)> Axes, ThermalSolveOptions Options,
        bool KOfT, bool Electro, string ResultsRoot);

    /// <summary>The cubes, notes, warnings and files the step produced; a refusal sentence stops the run.</summary>
    private sealed class SmallSignalOutput
    {
        public List<(string Group, string Name, DataCube Cube)> Cubes { get; } = [];
        public List<string> Notes { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Files { get; } = [];
        public string? Refusal { get; set; }
    }

    private static SmallSignalOutput? SmallSignal(SmallSignalInput input, CancellationToken ct, CircuitRF.Engine.RunControl? control)
    {
        var t = input.T;
        if (t.Rth is null && t.Zth is null && t.Pulse is null) return null;
        var output = new SmallSignalOutput();
        var doc = input.Document;
        int op = Array.IndexOf(input.Skipped, false);
        if (op < 0 || input.Problems[op] is not { } problem)
        {
            output.Warnings.Add("No sweep point solved, so no Rth matrix, Z_th or pulse was computed.");
            return output;
        }
        var res = input.Resolutions[op];
        var zero = new ThermalField(input.Mesh, new double[input.Mesh.NodeCount]);
        string where = input.Problems.Count > 1 ? $"the first solved sweep point ({op + 1} of {input.Problems.Count})" : "the setup's one point";
        bool nonlinear = input.KOfT && input.Conductivity.Any(c => !c.IsConstant);
        output.Notes.Add($"Rth / Z_th: every boundary made homogeneous (fixed temperatures and ambients 0), so they are properties of the " +
                         $"structure; the problem is {where}'s" + (nonlinear
                             ? ", and with k(T) on each is the TANGENT (small-signal) response about that point's temperatures."
                             : ", and with constant k they are exact at every power."));
        if (input.Lowering.SymmetryFactor > 1)
            output.Notes.Add($"Rth / Z_th are per watt in the MODELLED 1/{input.Lowering.SymmetryFactor} of each source (the sources carry the modelled " +
                             "part's power), and each includes its mirror images' heating.");
        if (input.Electro)
            output.Notes.Add("Rth / Z_th are the conduction of the meshed solids: the bond wires' own 1D conduction and the Joule heat's " +
                             "dependence on temperature are not in them.");

        // ── the sources, by name ──
        List<SmallSignalSource>? Sources(List<string>? names, string key)
        {
            var list = names is null || names.Contains(ThermalNamesConverter.All) ? doc.HeatSources.Select(h => h.Name).ToList() : names;
            var result = new List<SmallSignalSource>();
            foreach (string n in list)
            {
                if (input.Lowering.SheetSourceTags.TryGetValue(n, out int tag)) result.Add(new SmallSignalSource(n, [tag], []));
                else if (input.Lowering.SolidSourceRegions.TryGetValue(n, out int region)) result.Add(new SmallSignalSource(n, [], [region]));
                else if (input.Lowering.SourcesOutside.Contains(n))
                    output.Notes.Add($"{key}: heat source '{n}' lies outside the submodel, so it is not driven here.");
                else { output.Refusal = $"{key}: heat source '{n}' has no place in the mesh."; return null; }
            }
            if (result.Count == 0) { output.Refusal = $"{key}: no source it names lies in the mesh."; return null; }
            return result;
        }

        // ── R-em3d80-1: the Rth matrix ──
        if (t.Rth is { } rthSetup)
        {
            control?.BeginStage("the Rth matrix");
            var sources = Sources(rthSetup.Sources, "Rth");
            if (sources is null) return output;
            var system = new ThermalSmallSignal(problem, sources, input.Fields[op], input.KOfT, input.Assembly);
            var stat = rthSetup.Stat == ThermalRthStat.Max ? RthStatistic.Max : RthStatistic.Avg;
            var rth = system.Rth(stat, input.Options with { InitialGuess = null });
            string[] names = [.. rth.Names];
            int n = names.Length;
            var flat = new double[n * n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) flat[i * n + j] = rth.R[i, j];
            double[] index = [.. Enumerable.Range(0, n).Select(i => (double)i)];
            output.Cubes.Add((SmallSignalGroup, RthCube, new DataCube([new Axis("rise", index, "", names), new Axis("source", index, "", names)], flat) { Unit = "K/W" }));
            var p = sources.Select(s => SourcePowerW(t, doc, input.Lowering, zero, res, s.Name)).ToArray();
            var dT = new double[n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) dT[i] += rth.R[i, j] * p[j];
            output.Cubes.Add((SmallSignalGroup, RthCube + ":dT", new DataCube([new Axis("source", index, "", names)], dT) { Unit = "K" }));
            output.Cubes.Add((SmallSignalGroup, RthCube + ":P", new DataCube([new Axis("source", index, "", names)], p) { Unit = "W" }));
            output.Notes.Add($"Rth matrix ({n} × {n}, {stat}, {rth.Unknowns:N0} unknowns, {(rth.Solver == ThermalSolverKind.Direct ? "one factorisation" : "one AMG hierarchy")}, " +
                             $"{n} solve(s)): diagonal {string.Join(", ", Enumerable.Range(0, n).Select(i => $"{names[i]} {G(rth.R[i, i])}"))} K/W; " +
                             $"largest asymmetry {rth.Asymmetry:G3} of the largest entry" +
                             (stat == RthStatistic.Max ? " (expected with Max: a source's hottest point is not a mean the stiffness matrix is symmetric in)."
                              : rth.Tangent ? " (the tangent is not symmetric)." : " (Avg is symmetric by construction: this is the solve's own check)."));
            output.Notes.Add($"Effective rises under the setup's powers at {where} (ΔT = R·P): " +
                             string.Join(", ", Enumerable.Range(0, n).Select(i => $"{names[i]} {G(dT[i])} K at {G(p[i])} W")) + ".");
            if (rth.FallbackNote is { } fb) output.Notes.Add("Rth: " + fb);
        }

        // ── R-em3d80-2/-3: Z_th and its fits ──
        if (t.Zth is not { } zSetup) return output;
        ct.ThrowIfCancellationRequested();
        control?.BeginStage("the thermal impedance");
        var zSources = Sources(zSetup.Sources, "Zth");
        if (zSources is null) return output;
        var rhoC = new double[input.Lowering.Regions.Count];
        for (int r = 0; r < rhoC.Length; r++)
        {
            var region = input.Lowering.Regions[r];
            double? rc;
            if (input.Lowering.Effective.TryGetValue(r, out var block)) rc = block.Mixture.RhoC;
            else
            {
                var hc = ThermalMaterials.HeatCapacity(input.Elaboration, region.Solid, region.Material);
                rc = hc?.RhoC;
                if (hc?.Note is { } note && !output.Notes.Contains(note)) output.Notes.Add(note);
            }
            if (rc is not { } v || !(v > 0))
            {
                output.Refusal = $"Z_th needs the heat capacity of every meshed solid, and '{region.Solid}' ('{region.Material}') states no " +
                                 "DensityKgM3 and SpecificHeat. A thermal impedance never assumes one: add both to the material.";
                return output;
            }
            rhoC[r] = v;
        }
        double start = FrequencyOf(zSetup.StartHz, C3dThermal.ZthDefaultStartHz, res, "StartHz", output);
        double stop = FrequencyOf(zSetup.StopHz, C3dThermal.ZthDefaultStopHz, res, "StopHz", output);
        if (output.Refusal is not null) return output;
        int perDecade = zSetup.PerDecade ?? C3dThermal.ZthDefaultPerDecade;
        var freqs = ZthFrequencies(start, stop, perDecade);

        // the operating point: the first solved point's field — or, with k(T) on and a pulse, its average-power field (R-em3d80-4c)
        double[] operating = input.Fields[op];
        PulseValues? pulse0 = null;
        if (t.Pulse is { } pulseSetup)
        {
            pulse0 = PulseAt(pulseSetup, t, doc, input.Lowering, zero, res, zSources, out string? why);
            if (pulse0 is null) { output.Refusal = $"Pulse at {where}: {why}"; return output; }
            if (nonlinear && input.Electro)
                output.Notes.Add("Pulse: with conductive balance the fit is taken about the first point's own temperatures, not re-solved at the " +
                                 "average power.");
            else if (nonlinear)
            {
                var avg = WithPowers(problem, input.Lowering, zero, zSources, [.. pulse0.PowersW.Select(pw => pulse0.Duty * pw)]);
                operating = ThermalSolver.Solve(avg, input.Options with { InitialGuess = input.Fields[op] }, input.Assembly).Temperature;
                output.Notes.Add($"Pulse: with k(T) on, Z_th and its fits are the tangent about the AVERAGE-power temperatures of {where} " +
                                 $"(duty {G(pulse0.Duty)} × {G(pulse0.PowersW.Sum())} W).");
            }
        }
        var zSystem = new ThermalSmallSignal(problem, zSources, operating, input.KOfT, input.Assembly);
        var probes = (zSetup.Probes ?? []).Select(name => doc.Probes.First(p => p.Name == name)).ToList();
        int nf = freqs.Count, ns = zSources.Count, np = probes.Count;
        var probeZ = new Complex[np, ns, nf];
        var bufRe = new double[input.Mesh.NodeCount];
        var bufIm = new double[input.Mesh.NodeCount];
        var fieldRe = new ThermalField(input.Mesh, bufRe);
        var fieldIm = new ThermalField(input.Mesh, bufIm);
        var zth = zSystem.Zth(rhoC, freqs, input.Options with { InitialGuess = null }, np == 0 ? null : (fi, j, field) =>
        {
            for (int i = 0; i < field.Length; i++) { bufRe[i] = field[i].Real; bufIm[i] = field[i].Imaginary; }
            var re = ReadProbes(probes, input.Lowering, fieldRe, doc.DbuPerMicron);
            var im = ReadProbes(probes, input.Lowering, fieldIm, doc.DbuPerMicron);
            for (int k = 0; k < np; k++)
                probeZ[k, j, fi] = new Complex(re.TryGetValue(probes[k].Name, out var a) ? a.Avg : double.NaN,
                                               im.TryGetValue(probes[k].Name, out var b) ? b.Avg : double.NaN);
        });

        // the places Z_th is read at: each source's own (self and mutual), then each probe
        var places = zSources.Select(s => s.Name).Concat(probes.Select(p => p.Name)).ToList();
        Complex[] Series(int place, int source) => [.. Enumerable.Range(0, nf).Select(fi =>
            place < ns ? zth.Z[fi][place, source] : probeZ[place - ns, source, fi])];
        var freqAxis = new Axis("freq", [.. freqs], "Hz");
        var fits = new FosterNetwork?[places.Count, ns];
        for (int o = 0; o < places.Count; o++)
            for (int j = 0; j < ns; j++)
            {
                var z = Series(o, j);
                string key = $"{places[o]}:{zSources[j].Name}";
                output.Cubes.Add((SmallSignalGroup, "Zth:" + key, new DataCube([freqAxis], z) { Unit = "K/W" }));
                if (z.Any(v => !double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary)))
                {
                    output.Warnings.Add($"Z_th of '{places[o]}' per watt in '{zSources[j].Name}' is not finite at every frequency (the probe reads " +
                                        "nothing there), so it has no Foster fit.");
                    continue;
                }
                bool self = o < ns && o == j;
                // a self fit becomes a network and is warned on, so it takes the minimax refinement; a mutual one is data
                var fit = FosterFit.Fit(freqs, z, self ? FosterFit.ReweightPasses : 1);
                fits[o, j] = fit;
                double[] term = [.. Enumerable.Range(0, fit.Terms.Count).Select(k => (double)k)];
                output.Cubes.Add((SmallSignalGroup, $"Foster:{key}:R", new DataCube([new Axis("term", term)], [.. fit.Terms.Select(x => x.R)]) { Unit = "K/W" }));
                output.Cubes.Add((SmallSignalGroup, $"Foster:{key}:tau", new DataCube([new Axis("term", term)], [.. fit.Terms.Select(x => x.Tau)]) { Unit = "s" }));
                output.Cubes.Add((SmallSignalGroup, $"Foster:{key}:error", DataCube.Scalar(fit.FitError)));
                if (self && fit.FitError > FosterWarnAbove)
                    output.Warnings.Add($"The Foster fit of '{zSources[j].Name}''s own Z_th misses it by {100 * fit.FitError:F2} % somewhere in the band " +
                                        $"(above {100 * FosterWarnAbove:G2} %): raise Zth.PerDecade (now {perDecade}) so the band is sampled more finely.");
            }
        output.Notes.Add($"Z_th: {nf} frequencies (DC and {G(start)} Hz – {G(stop)} Hz, {perDecade} per decade), {ns} source(s), {np} probe(s); " +
                         $"{(zth.Solver == ThermalSolverKind.Direct ? "complex LU" : $"{(zSystem.Tangent ? "BiCGStab" : "COCG")} + AMG, at most {zth.MaxIterations} iterations")}, " +
                         $"largest relative residual {zth.MaxResidual:G3}." + (zth.FallbackNote is { } zfb ? " " + zfb : ""));
        output.Notes.Add("Foster fits (R ≥ 0, ΣR = Rth): " + string.Join("; ", Enumerable.Range(0, ns).Where(j => fits[j, j] is not null)
            .Select(j => $"'{zSources[j].Name}' {fits[j, j]!.Terms.Count} stage(s), error {100 * fits[j, j]!.FitError:F3} %")) +
            (places.Count > 1 || ns > 1 ? ". Mutual terms are fitted too and carried as data; they have no simple network form." : "."));

        // ── R-em3d80-5: each source's self network, as a .cnl ──
        string key0 = ResultKey(input.Setup);
        for (int j = 0; j < ns; j++)
        {
            if (fits[j, j] is not { Terms.Count: > 0 } fit) continue;
            string file = ThermalFosterNetlist.PathFor(input.ResultsRoot, key0, zSources[j].Name);
            try
            {
                Directory.CreateDirectory(input.ResultsRoot);
                File.WriteAllText(file, ThermalFosterNetlist.Write(zSources[j].Name, input.Setup.Name, fit, start, stop, freqs.Count - 1));
                output.Files.Add(file);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            {
                output.Warnings.Add($"The Foster network of '{zSources[j].Name}' could not be written to {file}: {x.Message}");
            }
        }

        // ── R-em3d80-4: the pulse train, per sweep point ──
        if (t.Pulse is { } ps) Pulse(ps, input, zSources, probes, places, fits, zSystem, zero, output, ct);
        return output;
    }

    /// <summary>The Rth matrix's cube name.</summary>
    public const string RthCube = "Rth";

    /// <summary>DC first, then <paramref name="perDecade"/> logarithmic points per decade from <paramref name="start"/> to
    /// <paramref name="stop"/>, both ends included.</summary>
    public static List<double> ZthFrequencies(double start, double stop, int perDecade)
    {
        int steps = Math.Max(1, (int)Math.Round(Math.Log10(stop / start) * perDecade));
        var f = new List<double> { 0 };
        for (int i = 0; i <= steps; i++) f.Add(start * Math.Pow(stop / start, (double)i / steps));
        return f;
    }

    private static double FrequencyOf(string? text, double fallback, C3dResolution res, string key, SmallSignalOutput output)
    {
        if (text is null) return fallback;
        double v = C3dThermal.Evaluate(res, text, out string? err) ?? double.NaN;
        if (err is not null || !(v > 0)) output.Refusal ??= $"Zth.{key} '{text}' is not a positive frequency{(err is null ? "" : ": " + err)}.";
        return v;
    }

    /// <summary>A pulse's resolved values at one point: period, duty and each driven source's power during the pulse.</summary>
    private sealed record PulseValues(double Period, double Duty, double[] PowersW);

    private static PulseValues? PulseAt(CemThermalPulse p, CemThermal t, C3dDocument doc, ThermalLowering lowering, ThermalField zero,
                                        C3dResolution res, List<SmallSignalSource> sources, out string? why)
    {
        why = null;
        double Eval(string text, string key, out string? e)
        {
            double v = (key == "Period" ? C3dThermal.EvaluateTime(res, text, out e) : C3dThermal.Evaluate(res, text, out e)) ?? double.NaN;
            if (e is not null) e = $"Pulse.{key} '{text}' does not resolve: {e}.";
            return v;
        }
        double period = Eval(p.Period, "Period", out why);
        if (why is not null) return null;
        if (!(period > 0)) { why = $"Pulse.Period '{p.Period}' is {G(period)} s; a period is positive."; return null; }
        double duty = Eval(p.Duty, "Duty", out why);
        if (why is not null) return null;
        if (!(duty >= 0 && duty <= 1)) { why = $"Pulse.Duty '{p.Duty}' is {G(duty)}; a duty lies between 0 and 1."; return null; }
        double[] powers = [.. sources.Select(s => SourcePowerW(t, doc, lowering, zero, res, s.Name))];
        if (p.PeakPower is { } peakText)
        {
            double peak = Eval(peakText, "PeakPower", out why);
            if (why is not null) return null;
            double sum = powers.Sum();
            if (!(sum > 0))
            {
                why = $"Pulse.PeakPower is {G(peak)} W, and the driven sources carry no power of their own to share it in proportion to.";
                return null;
            }
            powers = [.. powers.Select(pw => pw * peak / sum)];
        }
        return new PulseValues(period, duty, powers);
    }

    private static void Pulse(CemThermalPulse ps, SmallSignalInput input, List<SmallSignalSource> sources, List<C3dProbe> probes,
                              List<string> places, FosterNetwork?[,] fits, ThermalSmallSignal system, ThermalField zero,
                              SmallSignalOutput output, CancellationToken ct)
    {
        int points = input.Problems.Count, ns = sources.Count;
        var peak = new double[places.Count][];
        var single = new double[places.Count][];
        var average = new double[places.Count][];
        var wave = new double[places.Count][];
        for (int o = 0; o < places.Count; o++)
        {
            peak[o] = new double[points]; single[o] = new double[points]; average[o] = new double[points];
            wave[o] = new double[points * PulseSamples];
            Array.Fill(peak[o], double.NaN); Array.Fill(single[o], double.NaN); Array.Fill(average[o], double.NaN); Array.Fill(wave[o], double.NaN);
        }
        var summary = new List<string>();
        for (int pi = 0; pi < points; pi++)
        {
            ct.ThrowIfCancellationRequested();
            if (input.Skipped[pi] || input.Problems[pi] is not { } problem) continue;
            var res = input.Resolutions[pi];
            var values = PulseAt(ps, input.T, input.Document, input.Lowering, zero, res, sources, out string? why);
            if (values is null) { output.Refusal = $"Pulse at point {pi + 1}: {why}"; return; }
            double tOn = values.Duty * values.Period;
            // the baseline: the point's boundaries with no power
            var cold = WithPowers(problem, input.Lowering, zero, [], []) ;
            var baseField = new ThermalField(input.Mesh, ThermalSolver.Solve(cold, input.Options with { InitialGuess = null }, input.Assembly).Temperature);
            var baseProbes = ReadProbes(probes, input.Lowering, baseField, input.Document.DbuPerMicron);
            double[] times = [.. Enumerable.Range(0, PulseSamples).Select(k => values.Period * k / (PulseSamples - 1))];
            for (int o = 0; o < places.Count; o++)
            {
                double baseline = o < ns ? Mean(system.Load(o), baseField.Temperature)
                                         : baseProbes.TryGetValue(places[o], out var b) ? b.Avg : double.NaN;
                var drives = new List<PulseDrive>();
                for (int j = 0; j < ns; j++) if (fits[o, j] is { } f) drives.Add(new PulseDrive(values.PowersW[j], f));
                if (drives.Count < ns) continue;
                peak[o][pi] = baseline + PulseTrain.Peak(drives, values.Period, tOn);
                single[o][pi] = baseline + PulseTrain.Single(drives, tOn);
                average[o][pi] = baseline + PulseTrain.Average(drives, values.Period, tOn);
                var w = PulseTrain.Waveform(drives, values.Period, tOn, times);
                for (int k = 0; k < PulseSamples; k++) wave[o][pi * PulseSamples + k] = baseline + w[k];
                if (pi == Array.IndexOf(input.Skipped, false))
                    summary.Add($"'{places[o]}' peak {peak[o][pi]:F3} °C, single pulse {single[o][pi]:F3} °C, average {average[o][pi]:F3} °C " +
                                $"(baseline {baseline:F3} °C)");
            }
            if (pi == Array.IndexOf(input.Skipped, false))
                output.Notes.Add($"Pulse: period {G(values.Period)} s, duty {G(values.Duty)}, {G(values.PowersW.Sum())} W during the pulse: " +
                                 string.Join("; ", summary) + (points > 1 ? " — at the first point; every point is in the result." : "."));
        }
        Axis[] sweep = [.. input.Axes.Select(a => new Axis(a.Var, a.Values, a.Unit))];
        DataCube Scalars(double[] v) => sweep.Length == 0 ? new DataCube([], v) { Unit = "°C" } : new DataCube(sweep, v) { Unit = "°C" };
        var phase = new Axis("phase", [.. Enumerable.Range(0, PulseSamples).Select(k => (double)k / (PulseSamples - 1))], "");
        for (int o = 0; o < places.Count; o++)
        {
            output.Cubes.Add((Group, $"Pulse:{places[o]}:peak", Scalars(peak[o])));
            output.Cubes.Add((Group, $"Pulse:{places[o]}:single", Scalars(single[o])));
            output.Cubes.Add((Group, $"Pulse:{places[o]}:avg", Scalars(average[o])));
            output.Cubes.Add((SmallSignalGroup, $"Pulse:{places[o]}(t)", new DataCube([.. sweep, phase], wave[o]) { Unit = "°C" }));
        }
    }

    /// <summary><paramref name="problem"/> with its heat sources replaced: each of <paramref name="sources"/> at its power, W (a
    /// uniform flux over its sheet, a uniform density through its solid); no other source.</summary>
    private static ThermalProblem WithPowers(ThermalProblem problem, ThermalLowering lowering, ThermalField zero,
                                             IReadOnlyList<SmallSignalSource> sources, IReadOnlyList<double> powersW)
    {
        var sheets = new List<SurfaceSource>();
        var volumes = new List<VolumeSource>();
        for (int j = 0; j < sources.Count; j++)
        {
            foreach (int tag in sources[j].SurfaceTags)
                sheets.Add(new SurfaceSource(tag, powersW[j] / (zero.Surface(new HashSet<int> { tag })?.Measure ?? double.NaN)));
            foreach (int region in sources[j].Regions)
                volumes.Add(new VolumeSource(region, powersW[j] / (zero.Region(region)?.Measure ?? double.NaN)));
        }
        return new ThermalProblem
        {
            Mesh = problem.Mesh, Conductivity = problem.Conductivity, Fixed = problem.Fixed, FixedFields = problem.FixedFields,
            Convection = problem.Convection, SurfaceSources = sheets, VolumeSources = volumes,
        };
    }

    /// <summary>Heat source <paramref name="name"/>'s power at a point, watts in the modelled part (its density times its
    /// area or volume in the mesh, when stated as a density).</summary>
    private static double SourcePowerW(CemThermal t, C3dDocument doc, ThermalLowering lowering, ThermalField zero, C3dResolution res, string name)
    {
        var h = doc.HeatSources.First(x => x.Name == name);
        string text = (t.Sources ?? []).FirstOrDefault(s => s.Name == name)?.Power ?? h.Power ?? "0";
        double p = C3dThermal.Evaluate(res, text, out _) ?? double.NaN;
        if (lowering.SheetSourceTags.TryGetValue(name, out int tag))
            return h.Density == C3dHeatDensity.PerArea ? p * (zero.Surface(new HashSet<int> { tag })?.Measure ?? double.NaN) : p;
        if (lowering.SolidSourceRegions.TryGetValue(name, out int region))
            return h.Density == C3dHeatDensity.PerVolume ? p * (zero.Region(region)?.Measure ?? double.NaN) : p;
        return 0;
    }

    private static double Mean(double[] unitLoad, double[] field)
    {
        double s = 0;
        for (int i = 0; i < field.Length; i++) if (unitLoad[i] != 0) s += unitLoad[i] * field[i];
        return s;
    }
}
