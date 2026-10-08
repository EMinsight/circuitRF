using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
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

        sb.AppendLine("Worked example");
        sb.AppendLine();
        foreach (var line in goals ? GoalsExample : TuningExample) sb.AppendLine(line.Length == 0 ? "" : "    " + line);
        sb.AppendLine();
        sb.AppendLine(goals
            ? "See also: reference tuning (which values can vary), reference analyses (what a goal reads)."
            : "See also: reference goals (what the optimizer aims for), reference optimizers (each algorithm and its options);\n" +
              "explain <file> --tunables lists every key a design offers.");
        return sb.ToString();
    }

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
