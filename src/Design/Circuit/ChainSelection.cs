using CircuitRF.Core.Design;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// What chain selection DECIDED, as data rather than as a side effect
/// (<c>docs/design/cli.md</c> §4).
///
/// <para><b>Why this record exists.</b> The rule itself has not changed and neither has a single one
/// of its sentences: a <c>parametric_sweep</c> wrapping the analysis is dispatched AT THE SWEEP, and
/// naming the inner analysis is promoted rather than honoured, because the alternative is a
/// converged, plausible, complete-looking result for a run the caller thinks swept. What changed is
/// that <c>explain --analysis</c> has to REPORT that decision without making it — and a function
/// whose only account of itself is two <c>Console.Error.WriteLine</c> calls cannot be reported on.
/// So the decision comes back here and the CALLER writes the sentence: the run verbs write exactly
/// the two they always wrote (R-aut0-3 — a script watching stderr must not notice this landing), and
/// <c>explain</c> writes nothing and renders the same facts into its report.</para>
///
/// <para><b>This is the extraction R-aut4-2 asks for, not a second copy.</b> Gate 4 of
/// <c>brief-automation-4-check-and-explain.md</c> asserts <c>explain --analysis</c> against what
/// <c>hb</c>/<c>lp</c> actually dispatch rather than against a transcription of this logic — which is
/// only a meaningful gate while there is one implementation for both to reach.</para>
/// </summary>
/// <param name="Selected">The chain that will run, or null when nothing will.</param>
/// <param name="Candidates">
/// Every runnable chain of the requested kind, in declaration order. The first is what runs when
/// nothing was named; more than one is the ambiguity the run verbs report rather than hide.
/// </param>
/// <param name="Requested">The analysis named by <c>-a</c>, when it resolved to a declared one.</param>
/// <param name="PromotedFrom">
/// The named INNER analysis, when <see cref="Selected"/> is its wrapper rather than itself. Null
/// when nothing was named, when the name was already a chain root, or when it belongs to no runnable
/// chain — the three cases where no promotion happened.
/// </param>
/// <param name="Why">Why nothing was selected. Null when something was.</param>
public sealed record ChainSelection(
    Analysis?               Selected,
    IReadOnlyList<Analysis> Candidates,
    Analysis?               Requested,
    Analysis?               PromotedFrom,
    string?                 Why);

/// <summary>
/// Chooses which analysis a run verb dispatches — shared by <c>hb</c>, <c>lp</c> and <c>lpp</c>,
/// which differ only in the base analysis type they are looking for, and read without running by
/// <c>explain --analysis</c>.
///
/// <para>In <c>src/Design</c> since brief-em3d-79: a thermal setup driven from a circuit runs that
/// circuit's HB chain, and must promote a named inner analysis exactly as the <c>hb</c> verb does —
/// so the rule moved to where both can reach it, rather than being copied.</para>
/// </summary>
public static class ChainSelector
{
    /// <param name="isBase">True for the analysis type this verb runs.</param>
    /// <param name="kindLabel">What to call it in a message ("HB", "loadpull", "loadpull-pursuit").</param>
    /// <param name="directiveHint">The directive to suggest when the netlist declares none.</param>
    public static ChainSelection Select(
        TestBench tb, string? requested, Func<Analysis, bool> isBase,
        string kindLabel, string directiveHint)
    {
        // Runnable chain roots (AnalysisChain owns that rule — the Analyses panel resolves the same
        // list), narrowed to the ones bottoming out in the verb's own base analysis.
        var candidates = new List<Analysis>();
        foreach (var top in AnalysisChain.RunnableTops(tb))
            if (BaseOfChain(top, tb) is { } base_ && isBase(base_)) candidates.Add(top);

        if (requested is not null)
        {
            var named = tb.Analyses.FirstOrDefault(
                a => a.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));

            if (named is null)
                return new ChainSelection(null, candidates, null, null,
                    $"no analysis named '{requested}'. Declared: " +
                    (tb.Analyses.Count > 0 ? string.Join(", ", tb.Analyses.Select(a => a.Name)) : "(none)"));

            // Promote to the outermost chain that contains it, so -a HB1 still runs SW1's sweep.
            var owner = candidates.FirstOrDefault(c => ChainContains(c, named.Name, tb));
            bool promoted = owner is not null && !ReferenceEquals(owner, named);
            return new ChainSelection(owner ?? named, candidates, named, promoted ? named : null, null);
        }

        if (candidates.Count == 0)
            return new ChainSelection(null, candidates, null, null,
                tb.Analyses.Any(isBase)
                    ? $"the netlist declares a {kindLabel} analysis but its chain is disabled."
                    : $"the netlist declares no {kindLabel} analysis ({directiveHint}).");

        return new ChainSelection(candidates[0], candidates, null, null, null);
    }

    /// <summary>The promotion note, worded exactly as the run verbs have always worded it — and now
    /// worded in exactly one place, because the Analyses panel reports the same promotion.</summary>
    public static string PromotionNote(Analysis promotedFrom, Analysis owner) =>
        AnalysisChain.PromotionNote(promotedFrom, owner);

    /// <summary>The ambiguity note, worded exactly as the run verbs have always worded it.</summary>
    public static string AmbiguityNote(ChainSelection sel, string kindLabel) =>
        $"{sel.Candidates.Count} {kindLabel} chains declared " +
        $"({string.Join(", ", sel.Candidates.Select(c => c.Name))}); " +
        $"running '{sel.Candidates[0].Name}'. Use -a <name> to pick another.";

    /// <summary>The base (non-sweep) analysis a chain bottoms out in.</summary>
    public static Analysis? BaseOfChain(Analysis top, TestBench tb)
    {
        Analysis? a = top;
        for (int guard = 0; a is ParametricSweepAnalysis ps && guard < 64; guard++)
            a = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
        return a;
    }

    public static bool ChainContains(Analysis top, string name, TestBench tb)
        => AnalysisChain.ChainContains(top, name, tb);
}
