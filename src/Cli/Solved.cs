using System.Globalization;
using CircuitRF.Design.ThreeD;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// brief-em3d-98 R-em3d98-8 — a 3D view's "is this solved?" answer as <c>explain</c> and <c>find</c> report it. Spelling only:
/// every answer is <see cref="C3dSolveStatus.Of"/>'s, the function the editor's glyphs read, so a result the GUI calls current
/// is current here (the CLI rule, <c>docs/design/cli.md</c>).
/// </summary>
internal static class Solved
{
    /// <summary>The statuses of the 3D view at <paramref name="full"/>, read from its file, against the results root a
    /// headless run writes to (<see cref="ResultsRoot"/>).</summary>
    public static IReadOnlyList<SetupSolveStatus> Of(string full)
        => C3dSolveStatus.OfFile(full, ResultsRoot.For(full, DocumentKinds.AncestorCws(full)));

    public static IReadOnlyList<ExplainSolvedJson> ForExplain(IReadOnlyList<SetupSolveStatus> rows)
        => [.. rows.Select(s => new ExplainSolvedJson(
            s.Setup, Token(s.Solver), Token(s.State), s.Partial, s.Running,
            s.EndedAs is { } e ? Token(e) : null,
            s.Solved?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            s.Took is { } t ? Math.Round(t.TotalSeconds, 1) : null,
            s.StaleWhat, s.Detail))];

    public static IReadOnlyList<FoundSolvedJson> ForFind(IReadOnlyList<SetupSolveStatus> rows)
        => [.. rows.Select(s => new FoundSolvedJson(s.Setup, Token(s.Solver), Token(s.State), s.Partial))];

    /// <summary><c>explain</c>'s text: one line per (setup, solver), in the words the setup list uses.</summary>
    public static void Print(IReadOnlyList<SetupSolveStatus> rows)
    {
        Console.WriteLine();
        Console.WriteLine("Solved");
        if (rows.Count == 0) { Console.WriteLine("  (no setup that can run)"); return; }
        int name = rows.Max(r => r.Setup.Length) + 2;
        foreach (var r in rows)
            Console.WriteLine($"  {("'" + r.Setup + "'").PadRight(name)}  {r.SolverName,-15} {r.Words}" + (r.Detail is { } d ? $" — {d}" : ""));
    }

    public static string Token(SolverKind k) => k switch { SolverKind.Fem => "fem", SolverKind.Fdtd => "fdtd", _ => "thermal" };

    public static string Token(SolveState s) => s switch
    {
        SolveState.Current   => "current",
        SolveState.OutOfDate => "outOfDate",
        _                    => "notRun",
    };

    public static string Token(C3dRunState s) => s switch
    {
        C3dRunState.Complete     => "complete",
        C3dRunState.Cancelled    => "cancelled",
        C3dRunState.NotConverged => "notConverged",
        C3dRunState.Failed       => "failed",
        _                        => "interrupted",
    };
}
