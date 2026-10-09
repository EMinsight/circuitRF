using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Statistics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf yield mc|estimate|trial &lt;path.csch|path.cnl&gt;</c> — a Monte Carlo or yield run, headless
/// (brief-yield-5, yield overview D12/D13; <c>cli.md</c> §25).
///
/// <para><b>It owns no statistics.</b> The run is <see cref="StatisticalRun"/> in <c>src/Design/Statistics</c>, the
/// object the Yield panel drives, over the same <see cref="PreparedCircuit"/> Simulate prepares; the result file is
/// the one that run writes. What is here is argument parsing, the flag overrides of the file's <c>statistics</c>
/// line, the two narrowing flags, reporting, and the two opt-in writes.</para>
///
/// <para><b>One verb with nouns</b> (the <c>new</c>/<c>history</c> rule): <c>mc</c> is the spread alone and scores
/// every enabled goal; <c>estimate</c> is a yield against the <c>use=yield|both</c> goals; <c>trial</c> re-runs one
/// trial; <c>corners</c> evaluates the corners (YA-6); <c>center</c> centres the design for yield (YA-11).</para>
///
/// <para><b>It writes nothing to the design's values (D12).</b> <c>--save-preset</c> and <c>--save-corner</c> are
/// the two writes — a trial's values as a preset, or a statistical corner naming the trial — to a <c>.csch</c>,
/// after a history checkpoint as <c>opt --save-preset</c> takes one.</para>
///
/// <para><b>Exit codes (D13):</b> 0 finished (and the yield met the target, or there was none) · 3 the yield is below
/// <c>--target</c> · 1 refused · 2 no trial evaluated · 130 cancelled, writing nothing.</para>
/// </summary>
internal static partial class Yield
{
    private const string Verb = "yield";

    /// <summary>The nouns, in the order <c>reference statistics</c> lists them.</summary>
    internal static readonly (string Noun, string Summary)[] Nouns =
    [
        ("mc",       "Monte Carlo: the spread alone. Needs no goal and scores every enabled goal; exits 0 unless it could not run."),
        ("estimate", "Yield against the enabled use=yield|both goals, with its interval; exit 3 when below --target."),
        ("trial",    "Re-runs one trial (--trial n) and prints what it drew and how it scored; -o writes its analysis results."),
        ("corners",  "Evaluates every enabled corner and prints a corner x goal margin table; exit 3 when a goal fails at a corner. --mc runs a Monte Carlo at each, --generate prints corner lines."),
        ("center",   "Design centering: moves the opt=1 nominals to maximize yield on M common trials, then verifies the start and the best point on fresh trials; exit 3 when the verified yield is below --target."),
        ("doe",      "Design of experiments: runs a structured design over the opt=1 ranges (or the stat=1 tolerances) and prints each response's main effects and interactions, Lenth's margin and the alias sets; --optimum searches the fitted model and confirms its best point by simulation. Exits 0 unless it could not run."),
    ];

    /// <summary>Every flag the verb reads — the table <c>reference statistics</c> renders, so a flag added here
    /// appears there with nothing else to edit. Each is also a literal in <see cref="Run"/>'s parser.</summary>
    internal static readonly (string Flag, string Takes, string Summary)[] Flags =
    [
        ("--trials",       "n",                  "Trials to run; with --autostop, the most it runs. center: M, the common trials every candidate is scored on."),
        ("--seed",         "n",                  "The seed every draw is a function of, with the trial number. center: the common trials' seed; verification uses the next."),
        ("--sampling",     "random|lhs|sobol",   "How trials are placed."),
        ("--target",       "p%",                 "estimate, center: the yield to meet (exit 3 below it; center: the verified yield)."),
        ("--confidence",   "p%",                 "The confidence of the yield interval."),
        ("--autostop",     "",                   "estimate: stop once the interval clears --target either way (not with lhs)."),
        ("--nonconverged", "fail|warn",          "A trial that does not evaluate counts as a fail, or is left out of the count."),
        ("--save",         "scalars|all|n|auto", "Which trials keep their full analysis results in the result file."),
        ("--process",      "0|1",                "Kit process draws on or off."),
        ("--mismatch",     "0|1",                "Kit mismatch draws on or off."),
        ("--sigma-scale",  "k",                  "Scales every kit sigma."),
        ("--parallel",     "n",                  "Trials evaluated at once (center: simulations)."),
        ("--analyses",     "goals|all",          "Only the analyses the goals name, or every runnable one."),
        ("--set",          "var=expr",           "Override a global before elaboration, as every run verb does."),
        ("--vars",         "k,k",                "Draw only these of the statistical entries; the rest stay at nominal."),
        ("--goals",        "g,g",                "Score only these of the enabled goals."),
        ("--trial",        "n",                  "Re-run only trial n (trial needs it; mc and estimate take it too)."),
        ("--contributions","",                   "Also report what drives each goal's and measurement's spread (--json)."),
        ("--save-preset",  "name",               "A .csch, with --trial: add that trial's values as a preset. center: add the centred nominals as a preset."),
        ("--save-corner",  "name",               "A .csch, with --trial: add a statistical corner naming that trial."),
        ("--corners",      "c,c",                "corners, mc, estimate: only these enabled corners (mc/estimate: a run at each)."),
        ("--mc",           "",                   "corners: a Monte Carlo (a yield, when the design has a yield goal) at each corner, process draws off there."),
        ("--generate",     "spec",               "corners: print the corners a cross product makes, e.g. \"axis=tt,ss;temp=-40,25,85;Vdd=3.0,3.6\"; writes nothing."),
        ("--write",        "",                   "corners --generate, a .csch: append the generated corners, after a history checkpoint."),
        ("--algorithm",    "id",                 "center: the search, one of " + string.Join(", ", OptimizerAlgorithms.ForNoisyObjective) + " (default cmaes)."),
        ("--verify",       "n",                  "center: fresh trials the start and the best point are each verified on."),
        ("--max-iter",     "n",                  "center: iteration limit."),
        ("--max-evals",    "n",                  "center: simulation limit, verification not counted."),
        ("--time",         "limit",              "center: wall-clock limit — seconds, or a number and s, ms, min or h."),
        ("--width",        "w",                  "center: the smooth yield's logistic width, a fraction of each goal's scale."),
        ("--surrogate",    "none|quadratic",     "center: quadratic scores each candidate on a quadratic fit of its margins (2k+1 simulations and a few trials) and 10,000 virtual trials; the result's yield is still simulated."),
        ("--design",       "full2|frac|pb|ccf",  "doe: the design — full factorial, fractional factorial, Plackett–Burman screening, face-centred composite."),
        ("--resolution",   "4|5",                "doe --design frac: the resolution."),
        ("--factors",      "opt|stat",           "doe: the opt=1 entries over their ranges, or the stat=1 entries at nominal ± k sigma."),
        ("--levels",       "range|sigma:k",      "doe: range with opt factors; sigma:k with stat factors."),
        ("--centre",       "n",                  "doe: centre points added (0 adds none)."),
        ("--responses",    "goals|all",          "doe: the goals the factors are for, or every enabled goal; every scalar measure either way."),
        ("--optimum",      "",                   "doe --factors opt: search the fitted model for the goals' best point and confirm it by one simulation."),
        ("-o",             "out.npy",            "Where the result is written; default <design>.yield.npy beside the design (center: the best point's verification; doe: <design>.doe.npy)."),
        ("-q",             "",                   "No progress line on stderr."),
    ];

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            int code = JsonRun.Fail(CliDiagnostics.YieldNoun(args.Length == 0 ? "" : args[0]));
            Usage();
            return code;
        }
        string noun = args[0].ToLowerInvariant();
        if (noun is not ("mc" or "estimate" or "trial" or "corners" or "center" or "doe"))
        {
            int code = JsonRun.Fail(CliDiagnostics.YieldNoun(args[0]));
            Usage();
            return code;
        }

        string? input = null, output = null, presetName = null, cornerName = null;
        string? sampling = null, nonconverged = null, save = null, analyses = null;
        int? trials = null, seed = null, parallel = null, trial = null;
        double? target = null, confidence = null, sigmaScale = null;
        bool? process = null, mismatch = null;
        bool autostop = false, contributions = false, quiet = false, perCorner = false, write = false;
        List<string>? vars = null, goals = null, corners = null;
        string? generate = null;
        string? algorithm = null, time = null;
        int? verify = null, maxIter = null, maxEvals = null;
        double? width = null;
        string? surrogate = null;
        var centerOnly = new List<string>();
        string? design = null, factors = null, levels = null, responses = null;
        int? resolution = null, centre = null;
        bool optimum = false;
        var doeOnly = new List<string>();
        var sets = new List<(string Name, string Expr)>();

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (a)
            {
                case "--trials" when hasValue:      if (!Int(a, args[++i], 1, out trials, out int r1)) return r1; break;
                case "--seed" when hasValue:        if (!Int(a, args[++i], 0, out seed, out int r2)) return r2; break;
                case "--parallel" when hasValue:    if (!Int(a, args[++i], 1, out parallel, out int r3)) return r3; break;
                case "--trial" when hasValue:       if (!Int(a, args[++i], 1, out trial, out int r4)) return r4; break;
                case "--target" when hasValue:      if (!Percent(a, args[++i], out target, out int r5)) return r5; break;
                case "--confidence" when hasValue:  if (!Percent(a, args[++i], out confidence, out int r6, below100: true)) return r6; break;
                case "--sigma-scale" when hasValue:
                {
                    string text = args[++i];
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) || !(k >= 0) || !double.IsFinite(k))
                        return JsonRun.Fail(CliDiagnostics.YieldFlagValue(a, text, "a number, 0 or more"));
                    sigmaScale = k;
                    break;
                }
                case "--process" when hasValue:     if (!Switch(a, args[++i], out process, out int r7)) return r7; break;
                case "--mismatch" when hasValue:    if (!Switch(a, args[++i], out mismatch, out int r8)) return r8; break;
                case "--sampling" when hasValue:    sampling = args[++i]; break;
                case "--nonconverged" when hasValue: nonconverged = args[++i]; break;
                case "--save" when hasValue:        save = args[++i]; break;
                case "--analyses" when hasValue:    analyses = args[++i]; break;
                case "--vars" when hasValue:        vars = List(args[++i]); break;
                case "--goals" when hasValue:       goals = List(args[++i]); break;
                case "--autostop":                  autostop = true; break;
                case "--corners" when hasValue:     corners = List(args[++i]); break;
                case "--mc":                        perCorner = true; break;
                case "--generate" when hasValue:    generate = args[++i]; break;
                case "--write":                     write = true; break;
                case "--contributions":             contributions = true; break;
                case "-q" or "--quiet":             quiet = true; break;
                case "-o" or "--output" when hasValue: output = args[++i]; break;
                case "--save-preset" when hasValue: presetName = args[++i]; break;
                case "--save-corner" when hasValue: cornerName = args[++i]; break;
                // brief-yield-11: the center line's own settings.
                case "--algorithm" when hasValue:   algorithm = args[++i]; centerOnly.Add(a); break;
                case "--time" when hasValue:        time = args[++i]; centerOnly.Add(a); break;
                case "--verify" when hasValue:      if (!Int(a, args[++i], 1, out verify, out int r9)) return r9; centerOnly.Add(a); break;
                case "--max-iter" when hasValue:    if (!Int(a, args[++i], 1, out maxIter, out int r10)) return r10; centerOnly.Add(a); break;
                case "--max-evals" when hasValue:   if (!Int(a, args[++i], 1, out maxEvals, out int r11)) return r11; centerOnly.Add(a); break;
                case "--surrogate" when hasValue:   surrogate = args[++i]; centerOnly.Add(a); break;
                // brief-yield-14: the doe line's own settings.
                case "--design" when hasValue:     design = args[++i]; doeOnly.Add(a); break;
                case "--factors" when hasValue:    factors = args[++i]; doeOnly.Add(a); break;
                case "--levels" when hasValue:     levels = args[++i]; doeOnly.Add(a); break;
                case "--responses" when hasValue:  responses = args[++i]; doeOnly.Add(a); break;
                case "--resolution" when hasValue:
                {
                    string text = args[++i];
                    if (text is not ("4" or "5")) return JsonRun.Fail(CliDiagnostics.YieldFlagValue(a, text, "4 or 5"));
                    resolution = int.Parse(text, CultureInfo.InvariantCulture);
                    doeOnly.Add(a);
                    break;
                }
                case "--centre" or "--center-points" when hasValue: if (!Int(a, args[++i], 0, out centre, out int r12)) return r12; doeOnly.Add(a); break;
                case "--optimum":                  optimum = true; doeOnly.Add(a); break;
                case "--width" when hasValue:
                {
                    string text = args[++i];
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || !(w > 0) || !double.IsFinite(w))
                        return JsonRun.Fail(CliDiagnostics.YieldFlagValue(a, text, "a number above zero"));
                    width = w;
                    centerOnly.Add(a);
                    break;
                }
                case "--set" when hasValue:
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed(Verb, kv));
                    sets.Add((kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    break;
                }
                default:
                    if (a.StartsWith('-')) return JsonRun.Fail(CliDiagnostics.RunUnknownOption(Verb, a));
                    if (input is not null) return JsonRun.Fail(CliDiagnostics.RunMultipleInputs(Verb, a));
                    input = a;
                    break;
            }
        }

        if (input is null)
        {
            int code = JsonRun.Fail(CliDiagnostics.InputRequired(Verb, ".csch or .cnl"));
            Usage();
            return code;
        }
        JsonRun.InputPath = input;

        // ── flag values and combinations, before anything is read ─────────────────
        var mode = noun == "mc" ? StatisticalMode.MonteCarlo : StatisticalMode.Yield;
        if (CornerFlagProblem(noun, perCorner, generate, write, corners, trial) is { } cornerFlag) return JsonRun.Fail(cornerFlag);
        if (noun != "center" && centerOnly.Count > 0) return JsonRun.Fail(CliDiagnostics.YieldCornerFlag(centerOnly[0], "yield center"));
        if (noun != "doe" && doeOnly.Count > 0) return JsonRun.Fail(CliDiagnostics.YieldCornerFlag(doeOnly[0], "yield doe"));
        if (noun == "doe" && DoeFlagProblem(trial, cornerName, presetName, autostop, corners, contributions, vars, trials, seed, sampling, target) is { } doeFlag)
            return JsonRun.Fail(doeFlag);
        if (noun == "center" && CenterFlagProblem(trial, cornerName, autostop, corners, contributions) is { } centerFlag)
            return JsonRun.Fail(centerFlag);
        if (time is not null && TuningValidator.TimeLimitSeconds(time) is null)
            return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--time", time, "seconds, or a number and s, ms, min or h"));
        if (mode == StatisticalMode.MonteCarlo && (target is not null || autostop))
            return JsonRun.Fail(CliDiagnostics.YieldMcHasNoTarget(target is not null ? "--target" : "--autostop"));
        if (noun == "trial" && trial is null) return JsonRun.Fail(CliDiagnostics.YieldTrialRequired());
        foreach (var (flag, name) in new[] { ("--save-preset", presetName), ("--save-corner", cornerName) })
            if (name is not null && trial is null && noun != "center") return JsonRun.Fail(CliDiagnostics.YieldSaveNeedsTrial(flag));
        if (output is not null && !output.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.YieldOutputNotNpy(output));
        int samplingIndex = -1, ncIndex = -1, scopeIndex = -1, surrogateIndex = -1;
        if (surrogate is not null && (surrogateIndex = Token(AnalysisDirectiveSchema.SurrogateTokens, surrogate)) < 0)
            return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--surrogate", surrogate, "none or quadratic"));
        if (sampling is not null && (samplingIndex = Token(AnalysisDirectiveSchema.SamplingTokens, sampling)) < 0)
            return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--sampling", sampling, "random, lhs or sobol"));
        if (nonconverged is not null && (ncIndex = Token(AnalysisDirectiveSchema.NonConvergedTokens, nonconverged)) < 0)
            return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--nonconverged", nonconverged, "fail or warn"));
        if (analyses is not null && (scopeIndex = Token(AnalysisDirectiveSchema.ScopeTokens, analyses)) < 0)
            return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--analyses", analyses, "goals or all"));
        if (save is not null && !(save.ToLowerInvariant() is "scalars" or "all" or "auto"
                                  || (int.TryParse(save, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0)))
            return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--save", save, "scalars, all, auto or a trial count"));

        if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));
        var kind = DocumentKinds.Classify(input);
        if (kind is not (DocumentKind.Netlist or DocumentKind.Schematic))
            return JsonRun.Fail(CliDiagnostics.RunWrongDocumentKind(Verb, input, DocumentKinds.Name(kind)));
        foreach (var (flag, name) in new[] { ("--save-preset", presetName), ("--save-corner", cornerName) })
            if (name is not null && kind != DocumentKind.Schematic)
                return JsonRun.Fail(CliDiagnostics.YieldSaveNeedsSchematic(flag, input));
        if (generate is not null) return Generate(input, Path.GetFullPath(input), kind, generate, write);

        // ── 1. The circuit, prepared as Simulate prepares it ─────────────────────
        string full = Path.GetFullPath(input);
        var circuit = kind == DocumentKind.Schematic
            ? PreparedCircuit.FromSchematic(full)
            : PreparedCircuit.FromFile(full, Path.GetDirectoryName(full));
        if (circuit.ReadError is not null || circuit.Lib is null || circuit.Tb is not { } tb)
            return JsonRun.Fail(CliDiagnostics.RunFailed(circuit.ReadError ?? "the design could not be read"));

        if (noun == "doe")
            return RunDoe(input, full, circuit, tb, sets, output, quiet, goals, optimum, new DoeFlags(design, resolution, factors, levels, centre, responses, parallel));

        if (noun == "center")
            return RunCenter(input, full, circuit, tb, sets, presetName, output, quiet, vars, goals, new CenterFlags(
                algorithm, trials, verify, maxIter, maxEvals, time, width, parallel, seed,
                target, confidence, samplingIndex, ncIndex, save, process, mismatch, sigmaScale, scopeIndex, surrogateIndex));

        // ── 2. The file's setup, with this run's flags over it ───────────────────
        // Handed to the run only when a flag changed it, so a plain run is exactly the in-process one.
        var setup = tb.Tuning?.Clone() ?? new TuningSetup();
        var st = setup.Statistics?.Clone() ?? new StatisticsSettings();
        bool changed = false;
        void Set(Action apply) { apply(); changed = true; }
        if (trials is not null)     Set(() => st.Trials = trials);
        if (seed is not null)       Set(() => st.Seed = seed);
        if (samplingIndex >= 0)     Set(() => st.Sampling = (StatSampling)samplingIndex);
        if (target is not null)     Set(() => st.Target = target);
        if (confidence is not null) Set(() => st.Confidence = confidence);
        if (autostop)               Set(() => st.AutoStop = true);
        if (ncIndex >= 0)           Set(() => st.NonConverged = (NonConvergedPolicy)ncIndex);
        if (save is not null)       Set(() => st.Save = save.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : save.ToLowerInvariant());
        if (process is not null)    Set(() => st.Process = process);
        if (mismatch is not null)   Set(() => st.Mismatch = mismatch);
        if (sigmaScale is not null) Set(() => st.SigmaScale = sigmaScale);
        if (parallel is not null)   Set(() => st.Parallelism = parallel);
        if (scopeIndex >= 0)        Set(() => st.Scope = (OptimizerScope)scopeIndex);
        if (changed) setup.Statistics = st;
        if (vars is not null)
        {
            if (NarrowVariables(setup, vars) is { } refusal) return JsonRun.Fail(refusal);
            changed = true;
        }
        if (goals is not null)
        {
            if (NarrowGoals(setup, goals) is { } refusal) return JsonRun.Fail(refusal);
            changed = true;
        }
        if (presetName is not null && TuningPresets.NameProblem(tb.Tuning, -1, presetName) is { } presetProblem)
            return JsonRun.Fail(CliDiagnostics.YieldSaveName("--save-preset", presetProblem));
        if (cornerName is not null && CornerNameProblem(tb.Tuning, cornerName) is { } cornerProblem)
            return JsonRun.Fail(CliDiagnostics.YieldSaveName("--save-corner", cornerProblem));

        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        // ── corners: one evaluation at each, or a run at each (brief-yield-6) ─────
        if (noun == "corners")
            return RunCorners(input, full, circuit, changed ? setup : null, sets, corners, perCorner,
                              perCorner ? McModeOf(setup) : StatisticalMode.Yield, output, quiet);
        // statistics corners=all|<names> (or --corners) makes mc/estimate a run at each corner (R-ya6-3).
        if (trial is null && (corners is not null || st.CornerNames is not { Count: 0 }))
            return RunCorners(input, full, circuit, changed ? setup : null, sets,
                              corners ?? (st.CornerNames is { } listed ? [.. listed] : null), true, mode, output, quiet);

        // ── 3. The run ────────────────────────────────────────────────────────────
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        string resultPath = output ?? StatisticalRun.ResultPathFor(full);
        StatisticalRun? run = null;
        run = StatisticalRun.Create(circuit, new StatisticalOptions
        {
            Mode         = mode,
            Setup        = changed ? setup : null,
            Sets         = sets,
            Cancellation = ct,
            ResultPath   = trial is null ? resultPath : null,
            Progress     = new Synchronous(p =>
            {
                string line = ProgressLine(p, run!.Settings.EffectiveTrials);
                if (!quiet) Console.Error.WriteLine(line);
                observer?.Invoke(new RunProgress(line, p.Trials, run.Settings.EffectiveTrials));
            }),
        });
        if (run.Refusal is { } refused) return JsonRun.Fail(refused);
        foreach (var note in run.Notes) Report(note);

        if (trial is { } t) return RunTrial(input, full, run, mode, t, output, presetName, cornerName, ct);

        Console.Error.WriteLine($"[circuitRF] {(mode == StatisticalMode.Yield ? "yield" : "Monte Carlo")}: " +
                                $"{run.Entries.Count} statistical entr{(run.Entries.Count == 1 ? "y" : "ies")}, " +
                                $"{run.KitCalls.Count} kit stream(s), {run.Goals.Count} goal(s), up to {run.Settings.EffectiveTrials} trials");
        var result = run.Run();
        foreach (var note in result.Notes.Skip(run.Notes.Count)) Report(note);

        if (result.Outcome == StatisticalOutcome.Cancelled || ct.IsCancellationRequested)
        {
            if (result.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
            JsonRun.Report(CliDiagnostics.YieldCancelled());
            return 130;
        }
        if (result.Outcome == StatisticalOutcome.Refused)
            return JsonRun.Fail(result.Refusal ?? CliDiagnostics.RunFailed(result.FinishReason));
        if (result.Outcome == StatisticalOutcome.NoneEvaluated && result.Refusal is { } none) JsonRun.Report(none);

        // ── 4. The file: StatisticalRun wrote it ──────────────────────────────────
        if (result.WrittenPath is { } written)
        {
            Console.WriteLine($"Wrote {written}");
            JsonRun.AddOutput(JsonRun.KindOf(written), written);
        }

        // ── 5. The report ─────────────────────────────────────────────────────────
        var report = Project(input, run, result, contributions) with { Output = result.WrittenPath };
        JsonRun.Yield = report;
        Print(report);
        return result.ExitCode;
    }

    // ── one trial ────────────────────────────────────────────────────────────────

    private static int RunTrial(string input, string full, StatisticalRun run, StatisticalMode mode, int trial,
                                string? output, string? presetName, string? cornerName, CancellationToken ct)
    {
        TrialRecord record;
        try { record = run.EvaluateTrial(trial, ct); }
        catch (OperationCanceledException) { JsonRun.Report(CliDiagnostics.YieldCancelled()); return 130; }
        if (ct.IsCancellationRequested) { JsonRun.Report(CliDiagnostics.YieldCancelled()); return 130; }
        if (!record.Evaluated && record.Values.Count == 0 && record.Reason is { Id: "yield.trial.out-of-range" } outOfRange)
            return JsonRun.Fail(outOfRange);

        if (output is not null && record.Data is { } data)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir is not null) Directory.CreateDirectory(dir);
            if (File.Exists(output)) File.Delete(output);
            DataSetExporter.Export(data, output, ExportFormat.Npy);
            Console.WriteLine($"Wrote {output}");
            JsonRun.AddOutput(JsonRun.KindOf(output), output);
        }

        int exit = record.Evaluated ? 0 : 2;
        if (!record.Evaluated && record.Reason is { } why) JsonRun.Report(why);

        string? savedPreset = null, savedCorner = null;
        if (presetName is not null)
        {
            if (record.Values.Count == 0) { JsonRun.Report(CliDiagnostics.YieldTrialHasNoValues(trial)); exit = 1; }
            else if (SavePreset(full, presetName.Trim(), trial, record.Values) is { } failed) { JsonRun.Report(failed); exit = 1; }
            else savedPreset = presetName.Trim();
        }
        if (cornerName is not null)
        {
            if (SaveCorner(full, cornerName.Trim(), trial, run.Settings) is { } failed) { JsonRun.Report(failed); exit = 1; }
            else savedCorner = cornerName.Trim();
        }

        var report = new YieldReportJson(
            input, ModeWord(mode), record.Evaluated ? "finished" : "noneEvaluated", $"trial {trial} re-run alone",
            SettingsOf(run), 1, record.Evaluated ? 0 : 1,
            record.Evaluated ? [] : [new YieldReasonJson(record.Reason?.Render() ?? "did not evaluate", 1, [trial])],
            null, null, null, [], [], [], KitOf(run), run.Evaluations,
            Trial: new YieldTrialJson(
                trial, record.Evaluated, run.Goals.Count > 0 && record.Evaluated ? record.Pass : null,
                record.Reason?.Render(),
                new SortedDictionary<string, string>(record.Values.ToDictionary(), StringComparer.Ordinal),
                new SortedDictionary<string, double>(record.Draws.ToDictionary(), StringComparer.Ordinal),
                [.. record.Goals.Select(g => new YieldTrialGoalJson(g.Name, g.Met, Finite(g.Margin), Finite(g.WorstValue)))],
                new SortedDictionary<string, double>(record.Scalars.ToDictionary(), StringComparer.Ordinal)),
            SavedPreset: savedPreset, SavedCorner: savedCorner);
        JsonRun.Yield = report;
        PrintTrial(report, record);
        return exit;
    }

    // ── flags ────────────────────────────────────────────────────────────────────

    private static bool Int(string flag, string text, int min, out int? value, out int refusal)
    {
        refusal = 0;
        value   = null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min) { value = v; return true; }
        refusal = JsonRun.Fail(CliDiagnostics.YieldFlagValue(flag, text, min == 0 ? "a whole number, 0 or more" : "a positive whole number"));
        return false;
    }

    /// <summary>A percent, with or without its sign: <c>95%</c> or <c>95</c>, above 0 and at most 100.</summary>
    /// <param name="below100">A confidence: 100 % has no interval, so the bound is open (brief-yield-15 R-ya15-1).</param>
    private static bool Percent(string flag, string text, out double? value, out int refusal, bool below100 = false)
    {
        refusal = 0;
        value   = null;
        string t = text.Trim().TrimEnd('%').Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && p > 0 && (below100 ? p < 100 : p <= 100))
        { value = p; return true; }
        refusal = JsonRun.Fail(CliDiagnostics.YieldFlagValue(flag, text,
            below100 ? "a percent above 0 and below 100, such as 95%" : "a percent above 0 and at most 100, such as 95%"));
        return false;
    }

    private static bool Switch(string flag, string text, out bool? value, out int refusal)
    {
        refusal = 0;
        value   = text switch { "1" => true, "0" => false, _ => null };
        if (value is not null) return true;
        refusal = JsonRun.Fail(CliDiagnostics.YieldFlagValue(flag, text, "0 or 1"));
        return false;
    }

    private static int Token(IReadOnlyList<string> tokens, string text)
    {
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Equals(text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static List<string> List(string text)
        => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary><c>--vars</c>: only these statistical entries are drawn; the others keep their distribution and stay
    /// at nominal, as <c>stat=0</c> keeps them.</summary>
    private static Diagnostic? NarrowVariables(TuningSetup setup, List<string> keys)
    {
        var statistical = setup.Variables.Where(e => e.IsStatistical).Select(e => e.Key).ToList();
        foreach (var key in keys)
            if (!statistical.Contains(key, StringComparer.Ordinal))
                return CliDiagnostics.YieldVarsNotStatistical(key, statistical.Count == 0 ? "none" : string.Join(", ", statistical));
        foreach (var e in setup.Variables.Where(e => e.IsStatistical && !keys.Contains(e.Key, StringComparer.Ordinal))) e.Stat = false;
        return null;
    }

    /// <summary><c>--goals</c>: only these of the file's enabled goals are scored.</summary>
    private static Diagnostic? NarrowGoals(TuningSetup setup, List<string> names)
    {
        var enabled = setup.Goals.Where(g => g.Enabled).Select(g => g.Name).ToList();
        foreach (var name in names)
            if (!enabled.Contains(name, StringComparer.Ordinal))
                return CliDiagnostics.YieldGoalsUnknown(name, enabled.Count == 0 ? "none" : string.Join(", ", enabled));
        foreach (var g in setup.Goals.Where(g => g.Enabled && !names.Contains(g.Name, StringComparer.Ordinal))) g.Enabled = false;
        return null;
    }

    /// <summary>Why <paramref name="name"/> cannot name a new corner: a corner line's name is one word, and unique.</summary>
    internal static string? CornerNameProblem(TuningSetup? setup, string name)
    {
        string n = name.Trim();
        if (n.Length == 0) return "a corner needs a name";
        if (n.Any(c => char.IsWhiteSpace(c) || c is '"' or '=' or ','))
            return "a corner's name is one word, with no space, quote, comma or '='";
        if (setup?.Corners.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) == true)
            return $"there is already a corner named '{n}'";
        return null;
    }

    private static void Report(Diagnostic d)
    {
        string prefix = d.Severity switch { DiagnosticSeverity.Warning => "warning: ", DiagnosticSeverity.Error => "", _ => "note: " };
        Console.Error.WriteLine(prefix + d.Render());
        JsonRun.Note(d);
    }

    /// <summary>One batch, as stderr prints it and an MCP progress notification carries it.</summary>
    internal static string ProgressLine(StatisticalProgress p, int cap)
    {
        var parts = new List<string> { $"trials {p.Trials}/{cap}" };
        if (p.Yield.Counted > 0 && !double.IsNaN(p.Yield.Yield))
            parts.Add($"yield {Pct(p.Yield.Yield)} [{Pct(p.Yield.Lower)}, {Pct(p.Yield.Upper)}]");
        parts.Add($"{p.DidNotEvaluate} did not evaluate");
        return string.Join(" · ", parts);
    }

    /// <summary>Progress delivered on the run's own thread: <c>Progress&lt;T&gt;</c> would post it to the thread pool
    /// with no synchronization context, racing the result frame (RunHost's reason).</summary>
    private sealed class Synchronous(Action<StatisticalProgress> report) : IProgress<StatisticalProgress>
    {
        public void Report(StatisticalProgress value) => report(value);
    }

    // ── the two writes ───────────────────────────────────────────────────────────

    /// <summary><c>--save-preset</c>: the trial's values as a new preset in the schematic's tuning block —
    /// <see cref="TuningPresets.LockIn"/> — after a checkpoint, every other byte as the file's persistence writes it.</summary>
    private static Diagnostic? SavePreset(string cschPath, string name, int trial, IReadOnlyDictionary<string, string> values)
    {
        if (Optimize.CheckpointBefore(cschPath, $"yield --save-preset {name} --trial {trial}") is { } failed) return failed;
        var (model, view, cellName) = SchematicPersistence.LoadFromFile(cschPath);
        var (next, _) = TuningPresets.LockIn(model.Tuning, values, DateTime.UtcNow, null, name);
        model.Tuning = next;
        SchematicPersistence.SaveToFile(cschPath, model, cellName, view.PanX, view.PanY, view.Zoom);
        Console.WriteLine($"Saved preset \"{name}\" (trial {trial}) to {cschPath}");
        JsonRun.AddOutput("schematic", cschPath);
        return null;
    }

    /// <summary><c>--save-corner</c>: one statistical corner — <c>corner &lt;name&gt; trial=n seed=s sampling=m
    /// trials=N</c>, naming the run the trial came from — added to the schematic's tuning block.</summary>
    private static Diagnostic? SaveCorner(string cschPath, string name, int trial, StatisticsSettings s)
    {
        if (Optimize.CheckpointBefore(cschPath, $"yield --save-corner {name} --trial {trial}") is { } failed) return failed;
        var (model, view, cellName) = SchematicPersistence.LoadFromFile(cschPath);
        var next = model.Tuning?.Clone() ?? new TuningSetup();
        next.Corners.Add(new CornerDefinition
        {
            Name = name, Trial = trial, Seed = s.EffectiveSeed, Sampling = s.Sampling, Trials = s.EffectiveTrials,
        });
        model.Tuning = next;
        SchematicPersistence.SaveToFile(cschPath, model, cellName, view.PanX, view.PanY, view.Zoom);
        Console.WriteLine($"Saved corner {name} (trial {trial}) to {cschPath}");
        JsonRun.AddOutput("schematic", cschPath);
        return null;
    }

    // ── the report ───────────────────────────────────────────────────────────────

    private static YieldReportJson Project(string input, StatisticalRun run, StatisticalResult r, bool contributions)
    {
        var records = r.Records;
        var goals = r.Goals.Select(gy =>
        {
            var tightest = run.WorstTrials(gy.Goal, 1).FirstOrDefault();
            return new YieldGoalJson(gy.Goal, Estimate(gy.Estimate), tightest is null ? null : Finite(tightest.Margin), tightest?.Trial);
        }).ToList();

        var reasons = records.Where(x => !x.Evaluated)
            .GroupBy(x => x.Reason?.Render() ?? "did not evaluate")
            .Select(g => new YieldReasonJson(g.Key, g.Count(), [.. g.Select(x => x.Trial)]))
            .ToList();

        var stats = new List<YieldStatisticJson>();
        foreach (var g in run.Goals)
        {
            var margins = records.Where(x => x.Evaluated)
                .Select(x => x.Goals.FirstOrDefault(s => s.Name == g.Name)?.Margin ?? double.NaN).ToList();
            stats.Add(Statistic($"goal:{g.Name}:margin", "", margins, cpkLowerLimit: 0));
        }
        foreach (var name in MeasureNames(records))
        {
            string unit = r.Data is { } d && d.ContainsGroup(DataSet.MeasurementsGroup)
                       && d.CubesIn(DataSet.MeasurementsGroup).TryGetValue(name, out var cube) ? cube.Unit ?? "" : "";
            stats.Add(Statistic(name, unit,
                [.. records.Where(x => x.Evaluated).Select(x => x.Scalars.TryGetValue(name, out double v) ? v : double.NaN)], null));
        }

        var worst = run.Goals.Select(g => new YieldWorstJson(g.Name, [.. run.WorstTrials(g.Name, 5)
            .Select(w => new YieldWorstTrialJson(w.Trial, w.Margin, w.Worst,
                new SortedDictionary<string, string>(w.Values.ToDictionary(), StringComparer.Ordinal)))])).ToList();

        List<YieldContributionJson>? contribs = null;
        if (contributions)
        {
            contribs = [];
            foreach (var of in run.Goals.Select(g => g.Name).Concat(MeasureNames(records)))
            {
                var c = run.Contributions(of);
                contribs.Add(new YieldContributionJson(of, c.RSquared, c.Underdetermined,
                    [.. c.Contributors.Select(x => new YieldContributorJson(x.Name, x.Kind, x.Coefficient, x.Share, x.Spearman))],
                    c.Refusal?.Render()));
            }
        }

        double? target = run.Mode == StatisticalMode.Yield && run.Settings.Target is { } tp ? tp / 100 : null;
        bool hasYield = run.Goals.Count > 0;
        return new YieldReportJson(
            input, ModeWord(run.Mode), OutcomeWord(r.Outcome), r.FinishReason, SettingsOf(run),
            r.Trials, r.DidNotEvaluate, reasons, target,
            hasYield ? Estimate(r.Yield) : null,
            target is not null && hasYield && !double.IsNaN(r.Yield.Yield) ? r.Outcome != StatisticalOutcome.BelowTarget : null,
            goals, stats, worst, KitOf(run), run.Evaluations, Contributions: contribs);
    }

    private static IEnumerable<string> MeasureNames(IReadOnlyList<TrialRecord> records)
    {
        var seen = new List<string>();
        foreach (var r in records)
            foreach (var k in r.Scalars.Keys)
                if (!seen.Contains(k, StringComparer.Ordinal)) seen.Add(k);
        return seen;
    }

    private static YieldStatisticJson Statistic(string of, string unit, IReadOnlyList<double> values, double? cpkLowerLimit)
    {
        var x = SampleStatistics.Present(values);
        if (x.Length == 0) return new YieldStatisticJson(of, unit, 0, null, null, null, null, null, null);
        return new YieldStatisticJson(of, unit, x.Length,
            Finite(SampleStatistics.Mean(x)), x.Length > 1 ? Finite(SampleStatistics.StdDev(x)) : null,
            x.Min(), x.Max(), Finite(SampleStatistics.Median(x)),
            cpkLowerLimit is { } lo && x.Length > 1 ? Finite(SampleStatistics.Cpk(x, lo, null)) : null);
    }

    private static YieldSettingsJson SettingsOf(StatisticalRun run)
    {
        var s = run.Settings;
        return new YieldSettingsJson(
            s.EffectiveTrials, s.EffectiveSeed, AnalysisDirectiveSchema.SamplingTokens[(int)s.Sampling], s.EffectiveConfidence,
            s.AutoStop, AnalysisDirectiveSchema.NonConvergedTokens[(int)s.NonConverged], s.Save ?? "auto",
            s.Process ?? true, s.Mismatch ?? true, s.SigmaScale ?? 1, run.Parallelism,
            AnalysisDirectiveSchema.ScopeTokens[(int)s.Scope]);
    }

    private static YieldKitJson KitOf(StatisticalRun run)
    {
        var kit = KitStatistics.Report(run.KitCalls);
        return new YieldKitJson(kit.Process, kit.Mismatch, kit.Sections);
    }

    private static YieldEstimateJson Estimate(YieldEstimate e)
        => new(e.Passes, e.Counted, Finite(e.Yield), Finite(e.Lower), Finite(e.Upper));

    private static double? Finite(double v) => double.IsFinite(v) ? v : null;

    private static string ModeWord(StatisticalMode m) => m == StatisticalMode.Yield ? "yield" : "montecarlo";

    private static string OutcomeWord(StatisticalOutcome o) => o switch
    {
        StatisticalOutcome.Finished      => "finished",
        StatisticalOutcome.BelowTarget   => "belowTarget",
        StatisticalOutcome.Refused       => "refused",
        StatisticalOutcome.NoneEvaluated => "noneEvaluated",
        _                                => "cancelled",
    };

    // ── text ─────────────────────────────────────────────────────────────────────

    private static string Pct(double fraction) => double.IsFinite(fraction)
        ? (fraction * 100).ToString("F1", CultureInfo.InvariantCulture) + " %" : "—";

    private static string Pct(double? fraction) => fraction is { } f ? Pct(f) : "—";

    private static string G(double? v, string unit = "") => v is { } x
        ? x.ToString("G4", CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : "") : "—";

    private static void Print(YieldReportJson r)
    {
        var s = r.Settings;
        Console.WriteLine($"{(r.Mode == "yield" ? "Yield estimate" : "Monte Carlo")}: {r.Document}");
        Console.WriteLine($"  seed {s.Seed} · sampling {s.Sampling} · {r.Trials} of {s.Trials} trials · {r.FinishReason}");

        if (r.Yield is { } y)
        {
            string verdict = r.Target is { } tg
                ? $" · target {Pct(tg)}: {(r.TargetMet == true ? "met" : r.TargetMet == false ? "NOT MET" : "—")}" : "";
            Console.WriteLine($"Yield: {Pct(y.Yield)} ({G(s.Confidence)} % interval {Pct(y.Lower)} – {Pct(y.Upper)}; " +
                              $"{y.Passes} of {y.Counted} counted trials pass){verdict}");
        }
        else Console.WriteLine("Yield: — (no goal scored)");

        if (r.Goals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Goals:");
            Table(["name", "yield", "interval", "worst margin", "trial"],
                  r.Goals.Select(g => new[]
                  {
                      g.Name, Pct(g.Yield.Yield), $"{Pct(g.Yield.Lower)} – {Pct(g.Yield.Upper)}",
                      G(g.WorstMargin), g.WorstTrial?.ToString(CultureInfo.InvariantCulture) ?? "",
                  }));
        }

        if (r.DidNotEvaluate > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Did not evaluate: {r.DidNotEvaluate} of {r.Trials} " +
                              (s.NonConverged == "fail" ? "(counted as fails)" : "(left out of the count)"));
            foreach (var why in r.Reasons)
                Console.WriteLine($"  {why.Count} × {why.Reason}  (trials {string.Join(", ", why.Trials.Take(10))}{(why.Trials.Count > 10 ? ", …" : "")})");
        }

        if (r.Statistics.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Statistics over the evaluated trials:");
            Table(["of", "n", "mean", "sigma", "min", "max", "median", "Cpk"],
                  r.Statistics.Select(x => new[]
                  {
                      x.Of, x.Count.ToString(CultureInfo.InvariantCulture), G(x.Mean, x.Unit), G(x.Sigma, x.Unit),
                      G(x.Min, x.Unit), G(x.Max, x.Unit), G(x.Median, x.Unit), x.Cpk is { } c ? G(c) : "",
                  }));
        }

        foreach (var w in r.Worst.Where(w => w.Trials.Count > 0))
        {
            Console.WriteLine();
            Console.WriteLine($"Worst trials for {w.Goal}:");
            foreach (var t in w.Trials)
                Console.WriteLine($"  trial {t.Trial,-6} margin {G(t.Margin),-10} {string.Join("  ", t.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        Console.WriteLine();
        Console.WriteLine($"Kit statistics: {r.Kit.Process} process, {r.Kit.Mismatch} mismatch" +
                          (r.Kit.Sections.Count > 0 ? $" · sections {string.Join(", ", r.Kit.Sections)}" : ""));
    }

    private static void PrintTrial(YieldReportJson r, TrialRecord record)
    {
        var t = r.Trial!;
        Console.WriteLine($"Trial {t.Trial}: {r.Document}");
        Console.WriteLine($"  seed {r.Settings.Seed} · sampling {r.Settings.Sampling} · of {r.Settings.Trials} trials · " +
                          (t.Evaluated ? (t.Pass is { } p ? (p ? "passes" : "FAILS") : "evaluated") : $"did not evaluate: {t.Reason}"));
        if (t.Values.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Values:");
            foreach (var (key, value) in t.Values) Console.WriteLine($"  {key} = {value}");
        }
        if (t.Goals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Goals:");
            Table(["name", "met", "margin", "worst"],
                  t.Goals.Select(g => new[] { g.Name, g.Met ? "yes" : "NO", G(g.Margin), G(g.Worst) }));
        }
        if (t.Measurements.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Measurements:");
            foreach (var (name, value) in t.Measurements)
            {
                string unit = record.Data is { } d && d.ContainsGroup(DataSet.MeasurementsGroup)
                           && d.CubesIn(DataSet.MeasurementsGroup).TryGetValue(name, out var cube) ? cube.Unit ?? "" : "";
                Console.WriteLine($"  {name} = {G(value, unit)}");
            }
        }
    }

    private static void Table(string[] header, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var width = header.Select((h, i) => Math.Max(h.Length, all.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        string Line(string[] cells) => "  " + string.Join("  ", cells.Select((c, i) => c.PadRight(width[i]))).TrimEnd();
        Console.WriteLine(Line(header));
        foreach (var row in all) Console.WriteLine(Line(row));
    }

    private static void Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf yield mc|estimate|trial|corners|center|doe <file.csch|file.cnl> [--trials n] [--seed n]");
        Console.Error.WriteLine("                     [--sampling random|lhs|sobol] [--target p%] [--confidence p%] [--autostop]");
        Console.Error.WriteLine("                     [--nonconverged fail|warn] [--save scalars|all|n|auto] [--process 0|1] [--mismatch 0|1]");
        Console.Error.WriteLine("                     [--sigma-scale k] [--parallel n] [--analyses goals|all] [--set var=expr]");
        Console.Error.WriteLine("                     [--vars k,k] [--goals g,g] [--trial n] [--contributions] [-o out.npy] [-q]");
        Console.Error.WriteLine("                     [--save-preset name --trial n] [--save-corner name --trial n]");
        Console.Error.WriteLine("                     corners: [--corners c,c] [--mc] [--generate \"axis=a,b;temp=-40,25;Vdd=3.0,3.6\" [--write]]");
        Console.Error.WriteLine("                     center: [--algorithm id] [--trials M] [--verify n] [--max-iter n] [--max-evals n]");
        Console.Error.WriteLine("                             [--time limit] [--width w] [--surrogate none|quadratic] [--target p%]");
        Console.Error.WriteLine("                             [--save-preset name]");
        Console.Error.WriteLine("                     doe: [--design full2|frac|pb|ccf] [--resolution 4|5] [--factors opt|stat]");
        Console.Error.WriteLine("                          [--levels range|sigma:k] [--centre n] [--responses goals|all] [--optimum]");
    }
}
