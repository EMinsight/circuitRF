using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// Every geometry verb's first step: the generated cells its layout places, rebuilt — or a refusal
/// naming the ones that cannot be (brief-generated-cells-2 R-gc2-2/R-gc2-3, <c>docs/design/cli.md</c>
/// §21).
///
/// <para><b>It owns no generation.</b> <see cref="GeneratedCellsRun"/> in <c>src/Design</c> does the
/// walk, through the one rebuild step the application's workspace open uses; this file is the flag,
/// the choice of target, and the sentences.</para>
///
/// <para><b>Read-only verbs stay read-only.</b> <c>check</c>, <c>explain</c>, <c>render</c> and
/// <c>lvs</c> hold what they rebuild in memory for the run; <c>em</c>, <c>rail</c>, <c>impedance</c>,
/// <c>convert</c> and <c>netlist</c> write the folder as the application would, so the next run is
/// free — never on a read-only workspace (SL2), and never a <c>.clay</c>.</para>
/// </summary>
internal static class GeneratedCells
{
    public const string TrustFlag = "--trust-kit";

    /// <summary>The kit directories <c>--trust-kit</c> allowed for THIS invocation. Reset by every
    /// <see cref="TakeTrustFlags"/>, so one call never inherits another call's grant.</summary>
    private static List<string> _trusted = [];

    /// <summary>
    /// What <c>serve --trust-kit</c> allowed, for the life of the server. The OPERATOR's grant, like
    /// <c>--kits</c> (cli.md §11.3): each tool call re-enters <see cref="CliEntry.Run"/>, which resets
    /// <see cref="_trusted"/>, and no tool advertises the flag — a client asking the server to run code
    /// on its own say-so is exactly what the consent exists to stop.
    /// </summary>
    private static List<string> _operatorTrusted = [];

    /// <summary>
    /// How <c>serve</c> asks the PERSON about a kit — MCP elicitation, installed by
    /// <see cref="Serve.McpServer"/> only when the client said it can put the question to its user.
    /// Null everywhere else: the command line has <c>--trust-kit</c>, and a client that cannot ask
    /// gets the refusal. The agent never answers it; the client shows it to the person.
    /// </summary>
    internal static Func<string, CancellationToken, bool?>? Asker { get; set; }

    /// <summary>What the person answered through <see cref="Asker"/>, for the life of the server —
    /// so one "Allow" covers the session and one "Deny" is not asked again on every call. Never
    /// written to the preferences: a grant given to one agent session is not a machine-wide one.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _answered =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets the session's answers — a server shutting down, and the tests.</summary>
    internal static void ForgetSessionAnswers() => _answered.Clear();

    /// <summary>Takes every <c>--trust-kit &lt;dir&gt;</c> out of <paramref name="args"/>, the way
    /// <c>--kits</c> is taken: before the verb, so every geometry verb honours it without learning
    /// it. Relative to the working directory.</summary>
    public static string[] TakeTrustFlags(string[] args)
    {
        _trusted = [];
        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == TrustFlag && i + 1 < args.Length)
            {
                _trusted.Add(PCellTrustStore.Normalize(args[++i]));
                continue;
            }
            rest.Add(args[i]);
        }
        if (rest.Count > 0 && rest[0] == "serve") _operatorTrusted = [.. _trusted];
        return [.. rest];
    }

    /// <summary>
    /// A kit may run when THIS invocation granted it, or when this installation already recorded that
    /// it may — the decision a user made at the keyboard, read and never written. Anything else is
    /// "not allowed", and no kit code runs.
    /// </summary>
    private static Func<string, PCellTrustDecision> Trust()
    {
        var granted = new HashSet<string>(_trusted.Concat(_operatorTrusted), StringComparer.OrdinalIgnoreCase);
        var recorded = PCellTrustStore.FromRecordedPreferences();
        return dir =>
        {
            string key = PCellTrustStore.Normalize(dir);
            if (granted.Contains(key)) return PCellTrustDecision.Allowed;
            var decision = recorded.Decide(dir);
            return decision == PCellTrustDecision.Unknown && _answered.TryGetValue(key, out bool allowed)
                ? allowed ? PCellTrustDecision.Allowed : PCellTrustDecision.Denied
                : decision;
        };
    }

    /// <summary>The run's question, when there is someone to ask — answered once per kit per session.</summary>
    private static Func<string, bool?>? Ask()
    {
        if (Asker is not { } asker) return null;
        return dir =>
        {
            string key = PCellTrustStore.Normalize(dir);
            if (_answered.TryGetValue(key, out bool known)) return known;

            bool? answer = asker(key, RunHost.Cancellation);
            // Dismissed or unanswerable is not a decision: nothing is remembered and the next call may
            // ask again. Only an answer the person gave is kept.
            if (answer is { } given) _answered[key] = given;
            return answer;
        };
    }

    /// <summary>
    /// Rebuilds what <paramref name="view"/> places. Returns the run — dispose it when the verb is
    /// done with the geometry — and, when something the layout places cannot be rebuilt, the exit
    /// code the verb returns instead of running (every sentence already printed).
    /// </summary>
    public static GeneratedCellsRun Prepare(LayoutView view, string clayPath, bool mayWrite, out int? refusal)
    {
        var run = GeneratedCellsRun.Prepare(view, clayPath, Options(mayWrite));
        refusal = Report(run);
        return run;
    }

    /// <inheritdoc cref="Prepare(LayoutView, string, bool, out int?)"/>
    public static GeneratedCellsRun Prepare(string clayPath, bool mayWrite, out int? refusal)
    {
        var run = GeneratedCellsRun.Prepare(clayPath, Options(mayWrite));
        refusal = Report(run);
        return run;
    }

    /// <summary>
    /// <c>check</c>'s form: the same run, reported as FINDINGS rather than as a refusal — an error per
    /// unbuildable cell, so <c>check</c>'s exit code carries it (R-gc2-3).
    /// </summary>
    public static GeneratedCellsRun PrepareForCheck(LayoutView view, string clayPath, Action<Diagnostic> add)
    {
        var run = GeneratedCellsRun.Prepare(view, clayPath, Options(mayWrite: false));
        foreach (var cell in run.Unbuildable) add(CliDiagnostics.CheckGeneratedCellUnbuildable(clayPath, cell.Sentence));
        foreach (var kit in run.KitsNotAllowed) add(CliDiagnostics.CheckGeneratedCellKitNotAllowed(clayPath, KitSentence(kit)));
        foreach (string note in run.Notes) add(CliDiagnostics.CheckGeneratedCellNote(clayPath, note));
        return run;
    }

    private static GeneratedCellRunOptions Options(bool mayWrite) => new()
    {
        Target    = mayWrite ? GeneratedCellTarget.Disk : GeneratedCellTarget.Memory,
        Trust     = Trust(),
        AskTrust  = Ask(),
    };

    private static int? Report(GeneratedCellsRun run)
    {
        foreach (string note in run.Notes) JsonRun.Report(CliDiagnostics.GeneratedCellNote(note));
        if (run.CellsBuilt > 0)
            Console.Error.WriteLine($"[circuitRF] generated cells: {run.CellsBuilt} rebuilt from their layouts' snapshots");

        if (run.Unbuildable.Count == 0) return null;

        foreach (var kit in run.KitsNotAllowed) JsonRun.Report(CliDiagnostics.GeneratedCellKitNotAllowed(KitSentence(kit)));
        foreach (var cell in run.Unbuildable) JsonRun.Report(CliDiagnostics.GeneratedCellUnbuildable(cell.Sentence, cell.ReferencedFrom));
        return 1;
    }

    private static string KitSentence(KitNotAllowed kit)
    {
        if (_answered.TryGetValue(kit.Directory, out bool allowed) && !allowed)
            return $"The kit '{kit.Directory}' was not allowed to run when circuitRF asked in this session.";
        if (kit.Recorded == PCellTrustDecision.Denied)
            return $"The kit '{kit.Directory}' is recorded as NOT allowed to run on this machine. {TrustFlag} " +
                   $"\"{kit.Directory}\" allows it for this run only.";
        if (Serving && Asker is not null)
            return $"The kit '{kit.Directory}' has not been allowed to run on this machine, and circuitRF's " +
                   "question about it went unanswered. The next call that needs it asks again.";
        if (Serving)
            return $"The kit '{kit.Directory}' has not been allowed to run on this machine, and this client " +
                   "cannot put the question to its user. Allow it in circuitRF, or start the server with " +
                   $"{TrustFlag} \"{kit.Directory}\".";
        return $"The kit '{kit.Directory}' has not been allowed to run on this machine, and a headless run " +
               $"cannot ask. {TrustFlag} \"{kit.Directory}\" allows it for this run only.";
    }

    /// <summary>Set by <c>serve</c>, whose refusal names what a CLIENT can do rather than a flag it cannot pass.</summary>
    internal static bool Serving { get; set; }
}
