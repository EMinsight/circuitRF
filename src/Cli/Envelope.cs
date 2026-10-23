using CircuitRF.Design.Circuit;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf env &lt;file.cnl|.csch&gt; [-a name] [--set var=expr] [--stop t] [--accuracy draft|standard|fine]
/// [--segments n|auto] [--warmup t] [--probes a,b] [--all] [-o out.{npy,mat,txt}]</c> — runs the netlist's envelope
/// (<c>type=env</c>) analysis. The modulated stimulus is the circuit's MOD_Source; every override replaces a field
/// of the directive, as for <c>hb</c>.
/// </summary>
internal static class EnvelopeVerb
{
    public static int Run(string[] args)
    {
        string? input = null, exportPath = null, name = null;
        string? stop = null, acc = null, segs = null, warm = null, probes = null, step = null, mode = null;
        var sets = new List<(string, string)>();
        bool all = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "-a" or "--analysis": name = Next(); break;
                case "-o" or "--export": exportPath = Next(); break;
                case "--stop": stop = Next(); break;
                case "--step": step = Next(); break;
                case "--accuracy": acc = Next(); break;
                case "--envelope": mode = Next(); break;
                case "--segments": segs = Next(); break;
                case "--warmup": warm = Next(); break;
                case "--probes": probes = Next(); break;
                case "--all": all = true; break;
                case "--set":
                {
                    var kv = Next(); int eq = kv.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed("env", kv));
                    sets.Add((kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    break;
                }
                default:
                    if (a.StartsWith('-')) return JsonRun.Fail(CliDiagnostics.RunUnknownOption("env", a));
                    input = a; break;
            }
        }
        if (input is null)
        {
            int code = JsonRun.Fail(CliDiagnostics.InputRequired("env", ".cnl"));
            Console.Error.WriteLine("Usage: circuitrf env <file.cnl|.csch> [-a name] [--set var=expr] [--stop t] [--step t]");
            Console.Error.WriteLine("                     [--accuracy draft|standard|fine] [--envelope auto|single|lattice] [--segments n|auto]");
            Console.Error.WriteLine("                     [--warmup t] [--probes a,b] [--all] [-o out.{npy,mat,txt}]");
            return code;
        }
        JsonRun.InputPath = input;
        if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));
        try
        {
            if (CircuitSource.ReadRunInput("env", input, out int refusal) is not { } source) return refusal;
            var (lib, tb) = source;
            // Under --json the waveforms and spectrum stay in -o's file unless asked for (JsonRun.InlineCubeLimit).
            JsonRun.AllCubes = all;
            JsonRun.InlineCubeLimit = 10_000;
            // Every elaboration of this run resolves relative references (SnP, .cmodel) against the document.
            string? refBase = CircuitSource.ReferenceBaseOf(input);
            foreach (var (n, e) in sets)
            {
                HbCircuitRun.ApplySet(tb, n, e);
                Console.Error.WriteLine($"[circuitRF] set {n} = {e}");
            }
            var envs = tb.Analyses.OfType<EnvelopeAnalysis>().ToList();
            var env = name is null ? envs.FirstOrDefault() : envs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (env is null)
                return JsonRun.Fail(CliDiagnostics.NoAnalysis("env", name is null
                    ? "the netlist declares no envelope analysis (analysis <name> type=env Stop=... )"
                    : $"no envelope analysis named '{name}'"));
            env = new EnvelopeAnalysis(env.Name)
            {
                StopExpr = stop ?? env.StopExpr, StepExpr = step ?? env.StepExpr, Accuracy = acc ?? env.Accuracy,
                MaxHarmExpr = env.MaxHarmExpr, MaxMixOrderExpr = env.MaxMixOrderExpr, Probes = probes ?? env.Probes, ZmeasExpr = env.ZmeasExpr,
                SegmentsExpr = segs ?? env.SegmentsExpr, WarmupExpr = warm ?? env.WarmupExpr, StartExpr = env.StartExpr,
                TolExpr = env.TolExpr, SaveWaveforms = env.SaveWaveforms, Envelope = mode ?? env.Envelope,
            };
            // A parametric_sweep wrapping this envelope (e.g. Pavg -> ACPR vs Pout) is dispatched at the SWEEP, the
            // same promotion `hb` applies: running the inner alone would silently drop the sweep axis.
            if (tb.Analyses.OfType<ParametricSweepAnalysis>().FirstOrDefault(s => WrapsEnvelope(tb, s, env.Name)) is { } sweep
                && !tb.Analyses.OfType<ParametricSweepAnalysis>().Any(o => o.InnerAnalysisName == sweep.Name))
            {
                // overrides land in the directive itself, so every sweep point re-resolves them
                int at = tb.Analyses.FindIndex(x => x.Name == env.Name);
                tb.Analyses[at] = env;
                Console.Error.WriteLine($"[circuitRF] envelope {env.Name} inside sweep {sweep.Name} ({sweep.SweepVarName})");
                var sds = ParametricSweepEngine.Run(sweep, lib, tb, new AnalysisSettings(), refBase, RunHost.Control);
                JsonRun.Analysis = sweep.Name;
                JsonRun.Data = sds;
                PrintSweep(sds, sweep);
                if (exportPath is not null)
                {
                    var fmt = exportPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Mat : ExportFormat.Npy;
                    var dir0 = Path.GetDirectoryName(Path.GetFullPath(exportPath));
                    if (!string.IsNullOrEmpty(dir0)) Directory.CreateDirectory(dir0);
                    DataSetExporter.Export(sds, exportPath, fmt, new ExportOptions(Format: fmt));
                    Console.WriteLine($"Wrote {exportPath}");
                    JsonRun.AddOutput(JsonRun.KindOf(exportPath), exportPath);
                }
                return 0;
            }
            using var first = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
            var plan = EnvelopeRunner.Resolve(env, first.ResolvedGlobals);
            bool used = false;
            ElaboratedNetlist Elab()
            {
                lock (lib)
                {
                    if (!used) { used = true; return first; }
                    return new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
                }
            }
            Console.Error.WriteLine($"[circuitRF] envelope {env.Name}: Stop {plan.Params.StopS * 1e6:0.###} us, accuracy x{plan.Params.Oversample}, segments {(plan.Segments == 0 ? "auto" : plan.Segments.ToString(CultureInfo.InvariantCulture))}");
            var ds = EnvelopeRunner.Run(plan, Elab, new AnalysisSettings(), RunHost.Control, n => Console.Error.WriteLine($"[circuitRF] note: {n}"));
            foreach (var w in first.Warnings) Console.Error.WriteLine($"[circuitRF] warning: {w}");
            foreach (var n in first.Notes) Console.Error.WriteLine($"[circuitRF] note: {n}");
            JsonRun.Analysis = env.Name;
            JsonRun.Data = ds;
            Print(ds, env.Name, input);
            if (exportPath is not null)
            {
                var format = exportPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Mat : ExportFormat.Npy;
                var dir = Path.GetDirectoryName(Path.GetFullPath(exportPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                DataSetExporter.Export(ds, exportPath, format, new ExportOptions(Format: format));
                Console.WriteLine($"Wrote {exportPath}");
                JsonRun.AddOutput(JsonRun.KindOf(exportPath), exportPath);
            }
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("CRF_DEBUG") is not null) Console.Error.WriteLine(ex);
            return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message));
        }
    }

    private static double S(DataSet ds, string k) => ds.Cubes.TryGetValue(k, out var c) ? c.RealValues[0] : double.NaN;

    private static bool WrapsEnvelope(TestBench tb, ParametricSweepAnalysis s, string envName)
    {
        string inner = s.InnerAnalysisName;
        for (int guard = 0; guard < 16; guard++)
        {
            if (inner.Equals(envName, StringComparison.OrdinalIgnoreCase)) return true;
            var next = tb.Analyses.OfType<ParametricSweepAnalysis>().FirstOrDefault(x => x.Name.Equals(inner, StringComparison.OrdinalIgnoreCase));
            if (next is null) return false;
            inner = next.InnerAnalysisName;
        }
        return false;
    }

    /// <summary>One line per sweep point: the swept value, Pout, Gt and the worst ACPR1/ACPR2 at the last probe.</summary>
    private static void PrintSweep(DataSet ds, ParametricSweepAnalysis sweep)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!ds.Cubes.TryGetValue("ACLR", out var a)) { Console.WriteLine("(no ACLR cube)"); return; }
        var ax = a.Axes;
        var vals = ax[0].Values;
        int nNode = ax[1].Values.Length, nCar = ax[2].Values.Length, nMet = ax[3].Values.Length;
        Console.WriteLine($"Sweep {sweep.Name} over {sweep.SweepVarName}:   (last probe = {ax[1].Labels![nNode - 1]}, worst of carriers)");
        Console.WriteLine("  value        Pout dBm   Gt dB    ACPR1 dBc   ACPR2 dBc");
        var pout = ds.Cubes.TryGetValue("Pout_avg_dBm", out var po) ? po.RealValues : null;
        var gt = ds.Cubes.TryGetValue("Gt_dB", out var g) ? g.RealValues : null;
        for (int i = 0; i < vals.Length; i++)
        {
            double w1 = double.NegativeInfinity, w2 = double.NegativeInfinity;
            for (int c = 0; c < nCar; c++)
            {
                int o = ((i * nNode + (nNode - 1)) * nCar + c) * nMet;
                w1 = Math.Max(w1, Math.Max(a.RealValues[o + 1], a.RealValues[o + 2]));
                w2 = Math.Max(w2, Math.Max(a.RealValues[o + 3], a.RealValues[o + 4]));
            }
            Console.WriteLine(string.Format(inv, "  {0,-11:G5} {1,8:0.00} {2,7:0.00} {3,10:0.00} {4,10:0.00}",
                vals[i], pout?[i] ?? double.NaN, gt?[i] ?? double.NaN, w1, w2));
        }
    }

    private static void Print(DataSet ds, string name, string input)
    {
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"Analysis: {name}   ({input})");
        Console.WriteLine(string.Format(inv, "  f0 {0:0.######} GHz   step {1:0.###} ns   signal rate {2:0.##} MS/s   segments {3}   wall {4:0.##} s   Newton/step {5:0.##}",
            S(ds, "__EnvF0_Hz") / 1e9, S(ds, "__EnvStep_s") * 1e9, S(ds, "__EnvSignalRate_Hz") / 1e6, S(ds, "__EnvSegments"),
            S(ds, "__EnvWall_s"), S(ds, "__EnvNewtonPerStep")));
        if (ds.Cubes.TryGetValue("ACLR", out var a))
        {
            var nodes = a.Axes[0].Labels!; var car = a.Axes[1].Labels!;
            Console.WriteLine("  ACLR (dBc)            Pch dBm   ACPR1L   ACPR1U   ACPR2L   ACPR2U");
            for (int p = 0; p < nodes.Length; p++)
                for (int c = 0; c < car.Length; c++)
                {
                    int o = (p * car.Length + c) * 5;
                    Console.WriteLine(string.Format(inv, "  {0,-20} {1,8:0.00} {2,8:0.00} {3,8:0.00} {4,8:0.00} {5,8:0.00}   {6}",
                        nodes[p], a.RealValues[o], a.RealValues[o + 1], a.RealValues[o + 2], a.RealValues[o + 3], a.RealValues[o + 4], car[c]));
                }
        }
        foreach (var k in new[] { "Pin_avg_dBm", "Pout_avg_dBm", "Gt_dB", "NMSE_dB", "DE_pct", "Pdc_W", "__EnvSeamError", "__EnvTangentError" })
            if (ds.Cubes.ContainsKey(k)) Console.WriteLine(string.Format(inv, "  {0,-18} {1:0.####}", k.TrimStart('_'), S(ds, k)));
    }
}
