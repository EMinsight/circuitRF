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
            var list = names is null || names.Contains(ThermalNamesConverter.All) ? doc.HeatSources.Where(h => h.Model).Select(h => h.Name).ToList() : names;
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
            var p = new double[n];
            for (int j = 0; j < n; j++)
            {
                p[j] = SourcePowerW(t, doc, input.Lowering, zero, res, sources[j].Name, out string? powerError);
                if (powerError is not null) output.Warnings.Add($"Rth: heat source '{sources[j].Name}''s power: {powerError}; its ΔT = R·P is not a number.");
            }
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
                                 $"(duty {G(pulse0.Duty)} × {G(pulse0.PowersW.Sum())} W); each point's average temperature is its own steady " +
                                 "solve at its average power, and the peak and single-pulse rise are measured from it.");
            }
        }
        var zSystem = new ThermalSmallSignal(problem, zSources, operating, input.KOfT, input.Assembly);
        var probes = (zSetup.Probes ?? []).Select(name => doc.Probes.First(p => p.Name == name)).ToList();
        int nf = freqs.Count, ns = zSources.Count, np = probes.Count;
        var probeZ = new Complex[np, ns, nf];
        var read = ProbePhasors(probes, input);
        var zth = zSystem.Zth(rhoC, freqs, input.Options with { InitialGuess = null }, np == 0 ? null : (fi, j, field) =>
        {
            var v = read(field);
            for (int k = 0; k < np; k++) probeZ[k, j, fi] = v[k];
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
            (places.Count > 1 || ns > 1 ? ". Mutual terms are fitted too and carried as data; they have no simple network form, and a pulse does not read them." : "."));

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
        if (t.Pulse is { } ps)
        {
            var sweep = Enumerable.Range(0, nf).Select(fi =>
            {
                var m = new Complex[places.Count, ns];
                for (int o = 0; o < places.Count; o++) for (int j = 0; j < ns; j++) m[o, j] = o < ns ? zth.Z[fi][o, j] : probeZ[o - ns, j, fi];
                return m;
            }).ToList();
            control?.BeginStage("the pulse's harmonics");
            Pulse(ps, input, zSources, probes, places, fits, zSystem, rhoC, freqs, sweep, stop, zero, nonlinear, output, ct);
        }
        return output;
    }

    /// <summary>Each probe's mean phasor over a solved field (every node): what Z_th reads at a probe. Safe to call from several
    /// threads at once — Z_th solves frequencies concurrently — since each thread reads through fields of its own.</summary>
    private static Func<Complex[], Complex[]> ProbePhasors(List<C3dProbe> probes, SmallSignalInput input)
    {
        var mine = new ThreadLocal<(double[] Re, double[] Im, ThermalField FRe, ThermalField FIm)>(() =>
        {
            var re = new double[input.Mesh.NodeCount];
            var im = new double[input.Mesh.NodeCount];
            return (re, im, new ThermalField(input.Mesh, re), new ThermalField(input.Mesh, im));
        });
        return field =>
        {
            var (bre, bim, fre, fim) = mine.Value;
            for (int i = 0; i < field.Length; i++) { bre[i] = field[i].Real; bim[i] = field[i].Imaginary; }
            var re = ReadProbes(probes, input.Lowering, fre, input.Document.DbuPerMicron);
            var im = ReadProbes(probes, input.Lowering, fim, input.Document.DbuPerMicron);
            return [.. probes.Select(p => new Complex(re.TryGetValue(p.Name, out var a) ? a.Avg : double.NaN,
                                                      im.TryGetValue(p.Name, out var b) ? b.Avg : double.NaN))];
        };
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
        double v = C3dThermal.Evaluate(res, text, out string? err, ThermalQuantity.Frequency) ?? double.NaN;
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
            double v = (key == "Period" ? C3dThermal.EvaluateTime(res, text, out e)
                        : C3dThermal.Evaluate(res, text, out e, key == "Duty" ? ThermalQuantity.Plain : ThermalQuantity.Power)) ?? double.NaN;
            if (e is not null) e = $"Pulse.{key} '{text}' does not resolve: {e}.";
            return v;
        }
        double period = Eval(p.Period, "Period", out why);
        if (why is not null) return null;
        if (!(period > 0)) { why = $"Pulse.Period '{p.Period}' is {G(period)} s; a period is positive."; return null; }
        double duty = Eval(p.Duty, "Duty", out why);
        if (why is not null) return null;
        if (!(duty >= 0 && duty <= 1)) { why = $"Pulse.Duty '{p.Duty}' is {G(duty)}; a duty lies between 0 and 1."; return null; }
        var powers = new double[sources.Count];
        for (int j = 0; j < powers.Length; j++)
        {
            powers[j] = SourcePowerW(t, doc, lowering, zero, res, sources[j].Name, out string? powerError);
            if (powerError is not null) { why = $"Pulse: heat source '{sources[j].Name}''s power: {powerError}"; return null; }
        }
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

    /// <summary>R-em3d86-1 — the most harmonics of 1/Period one pulse solves for (each is one complex solve per source), however
    /// wide the Z_th band.</summary>
    public const int PulseHarmonicCap = 1000;

    private static void Pulse(CemThermalPulse ps, SmallSignalInput input, List<SmallSignalSource> sources, List<C3dProbe> probes,
                              List<string> places, FosterNetwork?[,] fits, ThermalSmallSignal system, double[] rhoC, List<double> sweepHz,
                              List<Complex[,]> sweep, double stopHz, ThermalField zero, bool nonlinear, SmallSignalOutput output, CancellationToken ct)
    {
        int points = input.Problems.Count, ns = sources.Count, no = places.Count;
        var peak = new double[no][];
        var single = new double[no][];
        var average = new double[no][];
        var wave = new double[no][];
        for (int o = 0; o < no; o++)
        {
            peak[o] = new double[points]; single[o] = new double[points]; average[o] = new double[points];
            wave[o] = new double[points * PulseSamples];
            Array.Fill(peak[o], double.NaN); Array.Fill(single[o], double.NaN); Array.Fill(average[o], double.NaN); Array.Fill(wave[o], double.NaN);
        }
        // the tail above the last harmonic: a source's own fit, and nothing for a transfer term (another source's heat, a probe)
        var tails = new FosterNetwork?[no, ns];
        for (int j = 0; j < ns; j++) tails[j, j] = fits[j, j];
        // a place whose Z_th is not finite somewhere (a probe that reads nothing) has no pulse
        var finite = new bool[no];
        for (int o = 0; o < no; o++)
            finite[o] = sweep.All(z => Enumerable.Range(0, ns).All(j => double.IsFinite(z[o, j].Real) && double.IsFinite(z[o, j].Imaginary)));

        // every point's pulse, then one harmonic solve per distinct period
        var values = new PulseValues?[points];
        for (int pi = 0; pi < points; pi++)
        {
            if (input.Skipped[pi] || input.Problems[pi] is null) continue;
            values[pi] = PulseAt(ps, input.T, input.Document, input.Lowering, zero, input.Resolutions[pi], sources, out string? why);
            if (values[pi] is null) { output.Refusal = $"Pulse at point {pi + 1}: {why}"; return; }
        }
        var responses = new Dictionary<double, PulseResponse>();
        var read = ProbePhasors(probes, input);
        foreach (var group in values.Where(v => v is not null).GroupBy(v => v!.Period))
        {
            ct.ThrowIfCancellationRequested();
            double period = group.Key;
            var r = new PulseResponse(period, sweepHz, sweep, tails);
            responses[period] = r;
            var loads = group.Select(v => new PulseLoad(v!.Duty, v.PowersW)).ToList();
            int inBand = (int)Math.Floor(stopHz * period * (1 + 1e-12));
            int count = Math.Min(inBand, PulseHarmonicCap);
            string at = $"period {G(period)} s";
            if (count < 1)
            {
                output.Notes.Add($"Pulse ({at}): 1/Period is above the Z_th band's top ({G(stopHz)} Hz), so no harmonic was solved; each source's " +
                                 "own place is its Foster fit's response and a transfer term is taken as zero.");
                continue;
            }
            // the harmonics are solved several at once (ThermalSmallSignal.Zth): each frequency's probe values in its own row
            var probeRows = new System.Collections.Concurrent.ConcurrentDictionary<int, Complex[,]>();
            var sweepRun = system.Zth(rhoC, [.. Enumerable.Range(1, count).Select(m => m / period)], input.Options with { InitialGuess = null },
                probes.Count == 0 ? null : (fi, j, field) =>
                {
                    var v = read(field);
                    var row = probeRows.GetOrAdd(fi, _ => new Complex[probes.Count, ns]);
                    for (int k = 0; k < probes.Count; k++) row[k, j] = v[k];
                },
                (fi, zf) =>
                {
                    var m = new Complex[no, ns];
                    for (int o = 0; o < no; o++) for (int j = 0; j < ns; j++) m[o, j] = o < ns ? zf[o, j] : probeRows[fi][o - ns, j];
                    r.Harmonics.Add(m);
                    return PulseHarmonics.Converged(r, loads);
                });
            var open = PulseHarmonics.Unconverged(r, loads);
            output.Notes.Add($"Pulse ({at}): the exact Z_th at harmonics 1 – {r.HarmonicCount} of 1/Period " +
                             (open.Count == 0 ? $"(each place's last {PulseHarmonics.ConsecutiveSmall} below {PulseHarmonics.Tolerance:G2} of its average rise)"
                                              : $"(all the band holds up to {G(Math.Min(stopHz, count / period))} Hz)") +
                             $"{(sweepRun.Solver == ThermalSolverKind.Direct ? "" : $", solved several at once, at most {sweepRun.MaxIterations} iterations")}" +
                             $"{(sweepRun.FallbackNote is { } fb ? "; " + fb : "")}. Above the last one, a source's own place is carried by its Foster fit " +
                             "and a transfer term (another source's heat, a probe) is taken as zero. The single pulse is the train's first pulse " +
                             "from rest, up to the second, from the inverse transform of the same solved samples.");
            if (open.Count > 0)
                output.Warnings.Add($"Pulse ({at}): the harmonic sum at {string.Join(", ", open.Select(o => $"'{places[o]}'"))} had not converged by " +
                                    $"harmonic {r.HarmonicCount} " +
                                    (count < inBand ? $"(the cap of {PulseHarmonicCap} harmonics)" : $"(the Z_th band's top, {G(stopHz)} Hz)") +
                                    ", so its peak lacks what the higher harmonics carry" +
                                    (count < inBand ? "." : ": raise Zth.StopHz. A probe on a heat source converges slowly, since only a source's " +
                                                            "own place has a fit to carry what lies above the band."));
        }

        var summary = new List<string>();
        int first = Array.IndexOf(input.Skipped, false);
        for (int pi = 0; pi < points; pi++)
        {
            ct.ThrowIfCancellationRequested();
            if (values[pi] is not { } v || input.Problems[pi] is not { } problem) continue;
            var r = responses[v.Period];
            var load = new PulseLoad(v.Duty, v.PowersW);
            // the baseline: the point's boundaries with no power — or, with k(T) on (and no conductive balance), the point's own
            // AVERAGE-power steady field, which the rise is then measured from. Z_th is the tangent there (R-em3d80-4c), and a
            // tangent's Duty·ΣP·Z(0) added to the zero-power field is not the average-power temperature once k moves with T: the
            // average read one answer and the steady solve at the same power another. Anchored here, the average IS that solve
            // and the harmonic sum carries only the ripple about it.
            bool anchored = nonlinear && !input.Electro;
            var baseProblem = anchored ? WithPowers(problem, input.Lowering, zero, sources, [.. v.PowersW.Select(pw => v.Duty * pw)])
                                       : WithPowers(problem, input.Lowering, zero, [], []);
            var baseField = new ThermalField(input.Mesh, ThermalSolver.Solve(baseProblem,
                input.Options with { InitialGuess = anchored && input.Fields[pi].All(double.IsFinite) ? input.Fields[pi] : null }, input.Assembly).Temperature);
            var baseProbes = ReadProbes(probes, input.Lowering, baseField, input.Document.DbuPerMicron);
            double[] times = [.. Enumerable.Range(0, PulseSamples).Select(k => v.Period * k / (PulseSamples - 1))];
            for (int o = 0; o < no; o++)
            {
                if (!finite[o] || r.Harmonics.Any(z => Enumerable.Range(0, ns).Any(j => !double.IsFinite(z[o, j].Real) || !double.IsFinite(z[o, j].Imaginary))))
                    continue;
                double baseline = o < ns ? Mean(system.Load(o), baseField.Temperature)
                                         : baseProbes.TryGetValue(places[o], out var b) ? b.Avg : double.NaN;
                double avg = PulseHarmonics.Average(r, o, load);
                if (anchored) baseline -= avg;
                var (pk, pkAt) = PulseHarmonics.Peak(r, o, load);
                var (sg, sgAt) = PulseHarmonics.Single(r, o, load);
                peak[o][pi] = baseline + pk;
                single[o][pi] = baseline + sg;
                average[o][pi] = baseline + avg;
                var w = PulseHarmonics.Waveform(r, o, load, times);
                for (int k = 0; k < PulseSamples; k++) wave[o][pi * PulseSamples + k] = baseline + w[k];
                if (pi == first)
                    summary.Add($"'{places[o]}' peak {peak[o][pi]:F3} °C at {G(pkAt)} s, single pulse {single[o][pi]:F3} °C at {G(sgAt)} s, " +
                                $"average {average[o][pi]:F3} °C " +
                                (anchored ? "(about the average-power steady field)" : $"(baseline {baseline:F3} °C)"));
            }
            if (pi == first)
                output.Notes.Add($"Pulse: period {G(v.Period)} s, duty {G(v.Duty)}, {G(v.PowersW.Sum())} W during the pulse: " +
                                 string.Join("; ", summary) + (points > 1 ? " — at the first point; every point is in the result." : "."));
        }
        Axis[] sweepAxes = [.. input.Axes.Select(a => new Axis(a.Var, a.Values, a.Unit))];
        DataCube Scalars(double[] x) => sweepAxes.Length == 0 ? new DataCube([], x) { Unit = "°C" } : new DataCube(sweepAxes, x) { Unit = "°C" };
        var phase = new Axis("phase", [.. Enumerable.Range(0, PulseSamples).Select(k => (double)k / (PulseSamples - 1))], "");
        for (int o = 0; o < no; o++)
        {
            output.Cubes.Add((Group, $"Pulse:{places[o]}:peak", Scalars(peak[o])));
            output.Cubes.Add((Group, $"Pulse:{places[o]}:single", Scalars(single[o])));
            output.Cubes.Add((Group, $"Pulse:{places[o]}:avg", Scalars(average[o])));
            output.Cubes.Add((SmallSignalGroup, $"Pulse:{places[o]}(t)", new DataCube([.. sweepAxes, phase], wave[o]) { Unit = "°C" }));
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
    private static double SourcePowerW(CemThermal t, C3dDocument doc, ThermalLowering lowering, ThermalField zero, C3dResolution res, string name,
                                       out string? error)
    {
        var h = doc.HeatSources.First(x => x.Name == name);
        string text = (t.Sources ?? []).FirstOrDefault(s => s.Name == name)?.Power ?? h.Power ?? "0";
        double p = C3dThermal.Evaluate(res, text, out error, ThermalQuantity.Power) ?? double.NaN;
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
