using System.Text;
using CircuitRF.Core.Netlist;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The <c>tuning</c> and <c>goals</c> reference topics (TO-1 R-to1-8) — generated from
/// <see cref="AnalysisDirectiveSchema.TuningDirectives"/>, the table <c>CnlReader</c> reads the four
/// directives against, so the page cannot describe a key the reader does not know or miss one it does.
/// </summary>
internal static partial class Reference
{
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

    private static string RenderTuning(string topic)
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
            : "See also: reference goals (what the optimizer aims for); explain <file> --tunables lists every key a design offers.");
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
        "preset \"wide band\" created=2026-10-07T12:00:00Z R1.R=47 Ohm Rload=60 Ohm",
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
