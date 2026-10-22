// brief-emi-1 R-emi1-1c — THE list of 3D solvers.
//
// Every place that offers or enumerates the solvers — the Solver picker, `em --solver`, `solver list`, the MCP tool's
// option text, the readiness check — reads this list, so adding a solver is a line here and a folder beside this file.
// The order is fixed: it is the order a comparison runs them in and the order every list shows them.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class Em3dSolvers
{
    /// <summary>Every 3D solver, in order.</summary>
    public static IReadOnlyList<Em3dSolverDescriptor> All { get; } = [PalaceSolver.Descriptor, OpenEmsSolver.Descriptor];

    /// <summary>The solver a new 3D setup starts on (overview D4: the FEM default).</summary>
    public static Em3dSolverChoice Default => PalaceSolver.Id;

    /// <summary>What the format's <c>Both</c> means, and always will: Palace then openEMS, compared against Palace.</summary>
    public static Em3dSolverChoice Both { get; } = Em3dSolverChoice.Compare([PalaceSolver.Id, OpenEmsSolver.Id], PalaceSolver.Id);

    private const string NoneSpelling = "None";
    private const string BothSpelling = "Both";

    /// <summary>The descriptor of <paramref name="id"/>.</summary>
    public static Em3dSolverDescriptor Get(Em3dSolverId id)
        => All.FirstOrDefault(d => d.Id == id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such 3D solver.");

    /// <summary>The descriptors of the solvers <paramref name="choice"/> runs, in the order it runs them.</summary>
    public static IReadOnlyList<Em3dSolverDescriptor> Of(Em3dSolverChoice choice) => [.. choice.Solvers.Select(Get)];

    /// <summary>Whether any solver <paramref name="choice"/> runs has <paramref name="capability"/>.</summary>
    public static bool Any(Em3dSolverChoice choice, Func<Em3dSolverCapabilities, bool> capability)
        => choice.Solvers.Any(id => capability(Get(id).Capabilities));

    /// <summary>The first solver <paramref name="choice"/> runs that has <paramref name="capability"/>, or null.</summary>
    public static Em3dSolverDescriptor? FirstWith(Em3dSolverChoice choice, Func<Em3dSolverCapabilities, bool> capability)
        => Of(choice).FirstOrDefault(d => capability(d.Capabilities));

    /// <summary>Every solver (installed or not) that has <paramref name="capability"/>, in order.</summary>
    public static IReadOnlyList<Em3dSolverDescriptor> With(Func<Em3dSolverCapabilities, bool> capability)
        => [.. All.Where(d => capability(d.Capabilities))];

    /// <summary>The solvers whose method is <paramref name="method"/>, by name: <c>Palace</c>, or <c>A and B</c>.</summary>
    public static string NamesOf(Em3dSolverFit method) => Names(All.Where(d => d.Method == method));

    /// <summary><paramref name="solvers"/>' names joined as a sentence lists them: <c>A</c>, <c>A and B</c>, <c>A, B and C</c>.</summary>
    public static string Names(IEnumerable<Em3dSolverDescriptor> solvers)
    {
        var names = solvers.Select(d => d.DisplayName).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    /// <summary>The programs every solver needs, each once, in order — what <c>solver list</c> lists and Settings ▸
    /// Solvers shows.</summary>
    public static IReadOnlyList<SolverTool> Tools => [.. All.SelectMany(d => d.Tools).Concat(Integrating).Distinct()];

    /// <summary>
    /// brief-emi-2 — the programs of a solver whose run is not in yet: EMerge, until its descriptor joins <see cref="All"/>
    /// with its run (EMI-4/EMI-5), which then deletes this. Installed, found and listed like every other program; no
    /// <c>Solver3D</c> and no picker can name it meanwhile.
    /// </summary>
    private static IEnumerable<SolverTool> Integrating => EmergeSolver.Tools;

    /// <summary>The solver whose <see cref="Em3dSolverDescriptor.ResultToken"/> is <paramref name="token"/>, any case.</summary>
    public static Em3dSolverDescriptor? ByToken(string token)
        => All.FirstOrDefault(d => string.Equals(d.ResultToken, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>The solver named <paramref name="displayName"/> (<c>Palace</c>, <c>openEMS</c>), or null.</summary>
    public static Em3dSolverDescriptor? ByName(string? displayName)
        => All.FirstOrDefault(d => d.DisplayName == displayName);

    /// <summary><c>em --solver</c>'s words: each solver's token, then <c>both</c>.</summary>
    public static IReadOnlyList<string> CliWords => [.. All.Select(d => d.ResultToken), BothSpelling.ToLowerInvariant()];

    /// <summary><see cref="CliWords"/> as a sentence lists them: <c>palace, openems or both</c>.</summary>
    public static string CliWordList => string.Join(", ", CliWords.SkipLast(1)) + " or " + CliWords[^1];

    /// <summary>The choice <c>em --solver &lt;word&gt;</c> names, or null.</summary>
    public static Em3dSolverChoice? FromCliWord(string word)
        => string.Equals(word, BothSpelling, StringComparison.OrdinalIgnoreCase) ? Both
         : ByToken(word) is { } d ? d.Id : null;

    // ── R-emi1-3 — the format's spellings ─────────────────────────────────────────────────────────

    /// <summary>The spellings the key <c>Solver3D</c> accepts, as a refusal lists them.</summary>
    public static IReadOnlyList<string> Spellings => [.. All.Select(d => d.Spelling), BothSpelling];

    /// <summary>The key <c>Solver3D</c>'s spelling of <paramref name="choice"/>: <c>None</c>, a solver's own, or <c>Both</c>.</summary>
    public static string Spelling(Em3dSolverChoice choice)
        => choice.IsNone ? NoneSpelling
         : !choice.IsCompare ? Get(choice.Reference).Spelling
         : choice == Both ? BothSpelling
         : string.Join("+", Of(choice).Select(d => d.Spelling));

    /// <summary>The choice spelt <paramref name="spelling"/> (any case, as the format has always read it), or null.</summary>
    public static Em3dSolverChoice? FromSpelling(string spelling)
        => string.Equals(spelling, NoneSpelling, StringComparison.OrdinalIgnoreCase) ? Em3dSolverChoice.None
         : string.Equals(spelling, BothSpelling, StringComparison.OrdinalIgnoreCase) ? Both
         : All.FirstOrDefault(d => string.Equals(d.Spelling, spelling, StringComparison.OrdinalIgnoreCase)) is { } d ? d.Id
         : null;

    /// <summary>A <c>Solver3D</c> spelling no solver answers to, refused with the ones that do.</summary>
    public static string UnknownSpelling(string spelling)
        => $"Solver3D '{spelling}' names no 3D solver this version of circuitRF knows; it takes {string.Join(", ", Spellings.SkipLast(1))} " +
           $"or {Spellings[^1]}.";
}
