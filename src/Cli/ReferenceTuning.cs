using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine.Optimization;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The <c>tuning</c> and <c>goals</c> reference topics (TO-1 R-to1-8) — generated from
/// <see cref="AnalysisDirectiveSchema.TuningDirectives"/>, the table <c>CnlReader</c> reads the four
/// directives against, so the page cannot describe a key the reader does not know or miss one it does —
/// and the <c>optimizers</c> topic (brief-tuneopt-7 R-to7-7), generated from
/// <see cref="OptimizerAlgorithms"/>, the registry the optimizer itself reads its option defaults from.
/// </summary>
internal static partial class Reference
{
    internal const string OptimizersTopic = "optimizers";
    private const string OptimizersTitle = "Optimizer algorithms";
    private const string OptimizersSummary =
        "Generated from the algorithm registry the optimizer reads its defaults from: every algorithm= id " +
        "with its menu label, when to use it, the cost forms it accepts, whether it estimates gradients, " +
        "and every alg.<option> with its default.";

    private const string StatisticsTitle = "Monte Carlo, yield and corners";
    private const string StatisticsPageSummary =
        "Generated from the schema the .cnl reader validates against: a tolerance on a tune line (each " +
        "distribution, its spread, percent or absolute, truncation), correlate, statistics and corner with every " +
        "key and default, statistical corners, goal use=, how a trial passes and how yield and its interval are " +
        "reported, the yield verb's nouns, flags and exit codes, the result file's layout, with a worked example " +
        "and one run per mode.";

    private const string TuningTitle = "Tuning directives";
    private const string GoalsTitle  = "Optimization goals";

    private const string TuningSummary =
        "Generated from the schema the .cnl reader validates against: the tune, preset and optimize " +
        "directives — which values can be tuned and how they are named, the range keys, presets and the " +
        "optimizer's settings — with a worked example.";

    private const string GoalsSummary =
        "Generated from the schema the .cnl reader validates against: the goal directive — its expression, " +
        "analysis, range over a swept axis, the five goal types, sloped limits and weights — with a worked example.";

    private static int TuningTopic(string topic)
    {
        string text = RenderTuning(topic);
        bool goals  = topic == AnalysisDirectiveSchema.GoalsTopic;
        JsonRun.Reference = new ReferenceReportJson(
            null,
            new ReferenceTopicJson(topic, goals ? GoalsTitle : TuningTitle, goals ? GoalsSummary : TuningSummary,
                                   ByteLength(text), text),
            null);
        Console.Out.Write(text);
        return 0;
    }

    private static int StatisticsTopicRun()
    {
        string text = RenderStatistics();
        JsonRun.Reference = new ReferenceReportJson(
            null, new ReferenceTopicJson(AnalysisDirectiveSchema.StatisticsTopic, StatisticsTitle, StatisticsPageSummary,
                                         ByteLength(text), text), null);
        Console.Out.Write(text);
        return 0;
    }

    /// <summary>
    /// The <c>statistics</c> page (docs/design/yield.md): the tune line's statistical keys and goal's
    /// <c>use=</c> out of their own specs, the three statistical directives, the distribution table, and
    /// the rules a run applies — every key and default from the schema the reader reads against.
    /// </summary>
    internal static string RenderStatistics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Monte Carlo, yield and corners — a tolerance is part of a tune line, a yield spec is a goal,");
        sb.AppendLine("and correlate, statistics and corner lines sit beside them in a .cnl. A schematic holds the");
        sb.AppendLine("same content in its \"Tuning\" block. A key this page does not list is kept and reported as a");
        sb.AppendLine("warning naming it; a default is never written.");
        sb.AppendLine();

        sb.AppendLine("A tolerance on a tune line");
        sb.AppendLine();
        var tune = AnalysisDirectiveSchema.FindTuningDirective("tune")!;
        foreach (var k in tune.Keys.Where(k => AnalysisDirectiveSchema.StatKeys.Contains(k.Name)))
            sb.AppendLine($"  {k.Name,-16} {k.Default ?? "-",-12} {k.Summary}".TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Distributions");
        sb.AppendLine();
        foreach (var (dist, spread, meaning, example) in AnalysisDirectiveSchema.Distributions)
        {
            sb.AppendLine($"  {dist,-10} {spread}");
            foreach (var line in Wrap(meaning, 92)) sb.AppendLine("             " + line);
            sb.AppendLine("             e.g.  " + example);
        }
        sb.AppendLine();
        foreach (var line in Wrap(
            "Percent or absolute. A spread value is a percent of the nominal (2%), which follows the nominal when " +
            "tuning or centering moves it, or a value in the parameter's unit (0.1 pF, or 0.1 meaning the parameter's own " +
            "unit), which does not. A percent lo, hi or by is that percent OF the nominal: lo=90% hi=110% is 0.9 to 1.1 " +
            "times it. The line is written back in the form it was written.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Truncation. trunc=k (gauss, lognorm) draws from the distribution truncated at +-k sigma — the probability is " +
            "renormalized over the window, never clipped onto its edges. A value that must be positive (a resistance, " +
            "capacitance, inductance, conductance, length or magnitude) and can be drawn at or below zero with " +
            $"probability above {StatisticsValidator.NonPhysicalThreshold.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)} " +
            "is a check warning naming trunc= and lognorm; a gaussian resistance wider than about 16 % untruncated is " +
            "one. stat=0 keeps the distribution without drawing it. A whole-number value takes discrete or unif only. " +
            "On a complex value, a tolerance goes on one part or on a same-system pair (real with imag, mag with " +
            "phase), each drawn independently; real with mag is refused.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        foreach (var spec in AnalysisDirectiveSchema.TuningDirectives.Where(d => d.Topic == AnalysisDirectiveSchema.StatisticsTopic))
        {
            sb.AppendLine("    " + spec.Syntax);
            sb.AppendLine();
            foreach (var line in Wrap(spec.Summary, 96)) sb.AppendLine("  " + line);
            sb.AppendLine();
            foreach (var k in spec.Keys)
                sb.AppendLine($"  {k.Name,-16} {k.Default ?? "-",-12} {k.Summary}".TrimEnd());
            sb.AppendLine();
            sb.AppendLine("  e.g.  " + spec.Example);
            sb.AppendLine();
        }

        foreach (var line in Wrap(
            "Statistical corners. corner <Name> trial=<n> seed=<s> sampling=<m> trials=<N> replays trial n of the run " +
            "those three identify. A trial is stored as its standard-normal draws, so replaying it against a moved " +
            "nominal applies the same relative deviation — the corner moves with tuning and centering. Kit corners: " +
            "a schematic's corner also selects kit corner sections, and netlisting it writes what they bind.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        var use = AnalysisDirectiveSchema.FindTuningDirective("goal")!.Keys.First(k => k.Name == "use");
        sb.AppendLine("Yield specs are goals");
        sb.AppendLine();
        sb.AppendLine($"  {use.Name,-16} {use.Default ?? "-",-12} {use.Summary}");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A trial passes a goal when the goal is met at that trial — the optimizer's own rule: its worst violation is " +
            $"within {GoalResiduals.MetTolerance.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)} of its " +
            "scale. It passes when it passes every enabled goal whose use is yield or both. Centre the design against " +
            "tight goals, then loosen them (or keep tight copies with use=opt) for yield.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("How yield is reported");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Yield is passes / counted trials, overall and per goal, with a Clopper-Pearson interval at confidence=. " +
            "A trial that does not evaluate (no convergence, a non-physical draw, an engine error) counts as a fail " +
            "and is reported apart with nonconverged=fail; with nonconverged=warn it is left out of the count and " +
            "reported as a warning. With autostop=1 the run stops once the interval's lower end reaches target (pass) " +
            "or its upper end falls below it (fail), never before 50 counted trials; trials= is the most it runs. " +
            "explain <file> --analysis reports how wide the interval of the configured trial count is expected to be " +
            $"at a yield of {StatisticsSummary.QuotedYield.ToString(System.Globalization.CultureInfo.InvariantCulture)} %, and the " +
            $"trial count that narrows it to under +-{StatisticsSummary.QuotedHalfWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} %.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("The statistics of a run");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A run writes <design>.yield.npy: every analysis cube with an outer trial axis, the trials group " +
            "(stat:<key>, z:<key>, goal:<name>:pass|margin|worst, pass, status), the nominal group and the yield " +
            "summary. These reduce over the trial axis by default (else freq), skipping a trial with no value: " +
            "mean_over, std_over, median_over, pctl_over(x, p), skew_over, kurt_over, " +
            "yield_over(condition), cpk(x, lo, hi) (either limit \"none\"), sigma_to(x, limit) — min_over and " +
            "max_over take the same default axis but keep a missing value missing; and these build an " +
            "axis: histogram(x, bins[, lo, hi][, \"percent\"]), cdf(x), normq(x), yield_sens(pass, x, bins). They work in measure lines " +
            "and goals. A goal written over the trials' spread (std_over of a trial-stacked " +
            "quantity) is legal, but scoring it takes a whole Monte Carlo run per evaluation.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        // brief-yield-11 R-ya11-8: design centering, beside the runs it is built from.
        sb.AppendLine("Design centering");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Centering moves the nominals of the opt=1 entries, within their ranges, to maximize the yield against the " +
            "use=yield|both goals; an entry that is both opt=1 and toleranced is the usual case, and its percent spread " +
            "follows the moved nominal. Every candidate is scored on the same M trials (center trials=, default " +
            $"{CenteringSettings.DefaultTrials}): trial t's standard-normal draws are fixed once, from the center seed, and " +
            "re-applied around each candidate, so two candidates' yields differ by the design and not by sampling noise. " +
            "The search sees a smooth yield: per trial, the smallest margin over the yield goals divided by each goal's " +
            "scale (the goal's scale=, or its default), through a logistic of width w (default " +
            $"{CenteringSettings.DefaultWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}), averaged over the " +
            "trials; the plain yield on the same trials is reported beside it. A trial that does not evaluate scores as a " +
            "fail, or is left out with nonconverged=warn. The algorithms are the optimizer's that need no derivative, since " +
            "a yield over finitely many trials is flat between them: " +
            string.Join(", ", OptimizerAlgorithms.ForNoisyObjective) + " (default " + CenteringSettings.DefaultAlgorithm + "). " +
            "One iteration costs its candidates × M simulations; explain <file> --analysis states the estimated total " +
            "before anything runs. At the end the start and the best point are each run on the same verify= fresh trials " +
            "(the next seed) with their intervals, so 'yield 71 % -> 94 %' compares like with like, and the report says " +
            "whether the gain lies within the intervals' overlap. The best point's verification is the result file, " +
            "<design>.yield.npy. Stop keeps the best point and still verifies it.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield center pad.cnl --trials 200 --verify 1000 --target 95%");
        sb.AppendLine("    circuitrf yield center amp.csch --save-preset centred      # the centred nominals as a preset");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Over MCP: run analysis=center with the same fields (algorithm, trials, verify, maxIter, maxEvals, time, width, " +
            "target, savePreset, output); a progress notification arrives per iteration, then read the verified yield " +
            "in result.center.verification and the written <design>.yield.npy. Exit 3: the verified yield is below target.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        // brief-yield-5 R-ya5-7: the run, generated from the verb's own noun and flag tables.
        sb.AppendLine("Running it");
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield mc|estimate|trial|corners|center <file.csch|file.cnl> [flags]");
        sb.AppendLine();
        foreach (var (noun, summary) in Yield.Nouns)
            sb.AppendLine($"  {noun,-16} {summary}");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Each flag overrides the file's statistics line for this run only, and nothing is written to the design's " +
            "values: --save-preset and --save-corner are the only writes, to a .csch, after a history checkpoint. " +
            "Over MCP: run analysis=montecarlo or analysis=yield, with these flags as fields (trials, seed, sampling, " +
            "target, autostop, nonconverged, save, output, contributions, trial, savePreset, saveCorner); a progress " +
            "notification arrives per batch.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var (flag, takes, summary) in Yield.Flags)
            sb.AppendLine($"  {(flag + (takes.Length > 0 ? " " + takes : "")),-30} {summary}");
        sb.AppendLine();
        sb.AppendLine("Exit codes");
        sb.AppendLine();
        sb.AppendLine("  0    finished; the yield met --target, or there was no target (mc has none)");
        sb.AppendLine("  3    finished, and the yield is below --target");
        sb.AppendLine("  1    refused before the first trial");
        sb.AppendLine("  2    no trial evaluated");
        sb.AppendLine("  130  cancelled; nothing is written");
        sb.AppendLine();
        sb.AppendLine("The result file");
        sb.AppendLine();
        foreach (var line in Wrap(
            "<design>.yield.npy beside the design (never run.npy, which Simulate owns). Every analysis group the nominal " +
            "produced, each cube with an outer trial axis (1..N) over the trials the save policy kept, a real scalar " +
            "measurement for every trial; trials: stat:<key> (base SI), z:<key>, z:process:<stream>, " +
            "z:mismatch:<stream>, goal:<name>:pass, goal:<name>:margin, goal:<name>:worst, pass, status (0 evaluated, " +
            "k the k-th entry of reasons) and reasons; nominal: each cube with no trial axis, named by its address " +
            "(SP1.S, trials.pass); yield: trials, did_not_evaluate, passes, counted, yield, lower, upper, the same per " +
            "goal as goal:<name>:yield, and confidence, target, seed, saved_trials, mode, sampling, nonconverged, save, " +
            "stopped. read prints the yield group first; --at trial=417 narrows to one trial.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        sb.AppendLine("Worked example");
        sb.AppendLine();
        foreach (var line in StatisticsExample) sb.AppendLine(line.Length == 0 ? "" : "    " + line);
        sb.AppendLine();
        sb.AppendLine("  and one run of each mode on it:");
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield mc       pad.cnl --trials 200                  # the spread of S21, no pass/fail");
        sb.AppendLine("    circuitrf yield estimate pad.cnl --target 95% --autostop --sampling sobol");
        sb.AppendLine("    circuitrf yield trial    pad.cnl --trial 417                   # what the worst trial drew");
        sb.AppendLine("    circuitrf plot pad.yield.npy -o s21.svg --trace \"cube=histogram(trials.goal:S21:worst, 20)\"");
        sb.AppendLine();
        sb.AppendLine("See also: reference tuning (the rest of a tune line), reference goals (what a goal can say);");
        sb.AppendLine("explain <file> --tunables shows each tolerance in numbers, --analysis the statistics settings,");
        sb.AppendLine("the analyses a yield run evaluates and its cost.");
        return sb.ToString();
    }

    private static readonly string[] StatisticsExample =
    [
        "Vdd = 3.3 V",
        "Port:P1 in 0 Num=1 Z=50 Ohm",
        "R:R1 in mid R=50 Ohm",
        "R:R2 mid 0 R=50 Ohm",
        "C:C1 mid out C=2 pF",
        "Port:P2 out 0 Num=2 Z=50 Ohm",
        "analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz",
        "tune R1.R min=25 Ohm max=100 Ohm opt=1 dist=gauss sd=2%",
        "tune R2.R dist=gauss tol=5% sigmas=3 trunc=3",
        "tune C1.C dist=unif tol=0.1 pF",
        "goal S21 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge -10 use=yield",
        "correlate R1.R R2.R rho=0.9",
        "statistics trials=500 seed=7 sampling=lhs target=95%",
        "corner Hot temp=85 Vdd=3.0 V R1.R=47 Ohm",
        "corner Worst_S21 trial=417 seed=7 sampling=lhs trials=500",
    ];

    private static int OptimizersTopicRun()
    {
        string text = RenderOptimizers();
        JsonRun.Reference = new ReferenceReportJson(
            null, new ReferenceTopicJson(OptimizersTopic, OptimizersTitle, OptimizersSummary, ByteLength(text), text), null);
        Console.Out.Write(text);
        return 0;
    }

    /// <summary>The <c>optimizers</c> page: one block per registry entry, in menu order.</summary>
    internal static string RenderOptimizers()
    {
        static string CostToken(OptimizerCost c) => c == OptimizerCost.Minimax ? "minimax" : "lsq";

        var sb = new StringBuilder();
        sb.AppendLine("Optimizer algorithms — the optimize line names one with algorithm=<id>, and");
        sb.AppendLine("alg.<option>=<value> sets one of its options. Every coordinate an algorithm moves is a");
        sb.AppendLine("variable's position in its range, from 0 to 1, so steps and sizes below are fractions of a range.");
        sb.AppendLine();
        sb.AppendLine("    optimize algorithm=de maxevals=2000 seed=1 alg.population=40");
        sb.AppendLine();
        foreach (var a in OptimizerAlgorithms.All)
        {
            sb.AppendLine($"{a.Id,-16}{a.Label}{(OptimizerFactory.IsBuilt(a.Id) ? "" : "  (not in this build)")}");
            foreach (var line in Wrap(a.UseWhen, 94)) sb.AppendLine("  " + line);
            sb.AppendLine($"  cost: {string.Join(", ", a.Costs.Select(CostToken))}" +
                          $"{(a.Costs.Count == 1 ? " (choosing it sets this form)" : "")}" +
                          $" · gradients: {(a.NeedsGradients ? "finite differences, n extra evaluations a step" : "none")}");
            foreach (var o in a.Options) sb.AppendLine($"  {o.Name,-16} {o.Default,-8} {o.Summary}".TrimEnd());
            sb.AppendLine();
        }
        sb.AppendLine("Every algorithm also takes:");
        foreach (var o in OptimizerAlgorithms.CommonOptions) sb.AppendLine($"  {o.Name,-16} {o.Default,-8} {o.Summary}".TrimEnd());
        sb.AppendLine();
        sb.AppendLine("See also: reference tuning (the optimize line's other keys), reference goals (what it minimizes).");
        return sb.ToString();
    }

    internal static string RenderTuning(string topic)
    {
        bool goals = topic == AnalysisDirectiveSchema.GoalsTopic;
        var sb = new StringBuilder();
        sb.AppendLine(goals
            ? "Optimization goals — one goal line each, in a .cnl beside the analysis and measure lines:"
            : "Tuning directives — one line each, in a .cnl beside the analysis and measure lines:");
        sb.AppendLine();
        sb.AppendLine("A schematic holds the same content in its \"Tuning\" block, and netlisting it writes these");
        sb.AppendLine("lines. A key this table does not list is kept and reported as a warning naming it.");
        sb.AppendLine("Values are written the way the schematic holds them: a number, then its unit after a space.");
        sb.AppendLine();

        foreach (var spec in AnalysisDirectiveSchema.TuningDirectives.Where(d => d.Topic == topic))
        {
            sb.AppendLine("    " + spec.Syntax);
            sb.AppendLine();
            foreach (var line in Wrap(spec.Summary, 96)) sb.AppendLine("  " + line);
            sb.AppendLine();
            foreach (var k in spec.Keys)
                sb.AppendLine($"  {k.Name,-16} {k.Default ?? "-",-8} {k.Summary}".TrimEnd());
            foreach (var (word, summary) in spec.BareWords)
                sb.AppendLine($"  {word,-16} {"",-8} {summary}".TrimEnd());
            sb.AppendLine();
            sb.AppendLine("  e.g.  " + spec.Example);
            sb.AppendLine();
        }

        if (goals) AppendScoringAndFunctions(sb);

        sb.AppendLine("Worked example");
        sb.AppendLine();
        foreach (var line in goals ? GoalsExample : TuningExample) sb.AppendLine(line.Length == 0 ? "" : "    " + line);
        sb.AppendLine();
        sb.AppendLine(goals
            ? "See also: reference tuning (which values can vary), reference analyses (what a goal reads);\n" +
              "circuitrf opt <file> runs them (run analysis=optimize over the protocol) — exit 3 when a goal is unmet."
            : "See also: reference goals (what the optimizer aims for), reference optimizers (each algorithm and its options);\n" +
              "explain <file> --tunables lists every key a design offers; circuitrf opt <file> runs the optimize line,\n" +
              "and its flags override it for one run without changing the file.");
        return sb.ToString();
    }

    /// <summary>
    /// The goals page's two generated sections (brief-tuneopt-11 R-to11-6): how a goal is scored —
    /// <see cref="GoalResiduals"/>' own rule and tolerance — and the goal functions, which are the
    /// Optimizer window's template catalog (<see cref="GoalTemplates"/>) run over a bench declaring an
    /// S-parameter analysis, a WSProbe and an HB efficiency measure, so a template added there is a
    /// line here with nothing to edit.
    /// </summary>
    private static void AppendScoringAndFunctions(StringBuilder sb)
    {
        sb.AppendLine("How a goal is scored");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Each grid point's violation is divided by the goal's scale (scale=, else the band's width for in and out, " +
            "else the larger of |limit| and 1 in the limit's own unit) and multiplied by its weight and by 1/sqrt(points), " +
            "so dB, degrees and ohms mix and a swept goal weighs the same as a single number. The cost is the sum of " +
            "their squares (cost=lsq) or the largest one (cost=minimax); a goal is met when its worst violation is within " +
            $"{GoalResiduals.MetTolerance.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)} of its scale, " +
            "and every goal met is a cost of 0.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        sb.AppendLine("Goal functions — the Optimizer's templates, each as the goal line it writes. A template fills");
        sb.AppendLine("over=, lo= and hi= with the analysis's swept range; <limit> is yours. Output power, efficiency and");
        sb.AppendLine("PAE are a measure line you write and then name in a goal, as Eff below.");
        sb.AppendLine();
        var (_, bench) = new CircuitRF.Core.Netlist.CnlReader().Read(string.Join('\n', FunctionsBench), "tb", null);
        string? group = null;
        foreach (var t in GoalTemplates.For(bench))
        {
            string heading = t.Group switch
            {
                GoalTemplateGroup.SParameters  => "S-parameters",
                GoalTemplateGroup.WsProbe      => "WSProbe (stability-wsprobe metrics of one probe)",
                GoalTemplateGroup.Measurements => "Measurements (any measure line, by name)",
                _                              => "Custom",
            };
            if (heading != group) { sb.AppendLine("  " + heading); group = heading; }
            var g = t.Make();
            string type = t.Group is GoalTemplateGroup.Measurements or GoalTemplateGroup.Custom
                ? "le|ge" : g.Type.ToString().ToLowerInvariant();
            string limit = g.Limit.Length > 0 ? g.Limit : g.Type is GoalType.In or GoalType.Out ? "<a> <b>" : "<limit>";
            string expr = g.Expression.Length > 0 ? g.Expression : "<expression>";
            sb.AppendLine($"    {t.Label,-44} goal {g.Name} = {expr}{(g.Analysis is { } a ? $" analysis={a}" : "")} {type} {limit}");
        }
        sb.AppendLine();
    }

    /// <summary>The bench the goal-function list is generated over: one S-parameter analysis with a
    /// WSProbe, and an HB analysis with an efficiency measure built from two others.</summary>
    internal static readonly string[] FunctionsBench =
    [
        "f0 = 2 GHz",
        "Port:P1 in 0 Num=1 Z=50 Ohm",
        "WSProbe:PS in mid",
        "R:R1 mid out R=10 Ohm",
        "Port:P2 out 0 Num=2 Z=50 Ohm",
        "analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz",
        "analysis HB1 type=hb Tone=f0 MaxHarm=4",
        "measure Pout_W = 0.5*real(HB1.V(\"out\", 1)*conj(HB1.I(\"P2\", 1)))",
        "measure Pdc_W = real(HB1.V(\"vdd\", 0)*HB1.I(\"Vdd\", 0))",
        "measure Eff = 100*Pout_W/Pdc_W",
    ];

    /// <summary>A complete netlist both worked examples build on, so either one can be copied into a
    /// file and checked as it stands.</summary>
    private static readonly string[] ExampleCircuit =
    [
        "define DUT (a b)",
        "  parameters Rbias=1 kOhm",
        "  R:R3 a b R=10 Ohm",
        "  R:R4 b 0 R=Rbias",
        "end DUT",
        "",
        "Rload = 50 Ohm",
        "Zsrc = 40+15j Ohm",
        "Port:P1 in 0 Num=1 Z=50 Ohm",
        "R:R1 in mid R=50 Ohm",
        "DUT:X1 mid out Rbias=1 kOhm",
        "Port:P2 out 0 Num=2 Z=Rload",
        "analysis SP1 type=sparam start=0.5 stop=3 npts=51 Unit=GHz",
    ];

    private static readonly string[] TuningExample =
    [
        .. ExampleCircuit,
        "tune R1.R min=10 Ohm max=200 Ohm scale=log tune=1 opt=1",
        "tune Rload min=25 Ohm max=100 Ohm opt=1",
        "tune X1.Rbias min=500 Ohm max=2 kOhm tune=1",
        "tune DUT:R3.R min=5 Ohm max=50 Ohm opt=1",
        "tune mag(Zsrc) min=20 Ohm max=80 Ohm tune=1",
        "tune phase(Zsrc) min=-45 deg max=45 deg scale=lin tune=1",
        "preset \"wide band\" created=2026-10-07T12:00:00Z R1.R=47 Ohm Rload=60 Ohm Zsrc=45+10j Ohm",
        "optimize algorithm=lm maxiter=200 seed=1",
    ];

    private static readonly string[] GoalsExample =
    [
        .. ExampleCircuit,
        "tune R1.R min=10 Ohm max=200 Ohm opt=1",
        "tune Rload min=25 Ohm max=100 Ohm opt=1",
        "goal Gain = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge -12",
        "goal Match = dB(SP1.S(1,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz le -15 to -10 weight=2",
        "goal Flat = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz in -12 -6",
        "goal Floor = Rload - 30 ge 0",
    ];

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
