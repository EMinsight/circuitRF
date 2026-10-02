// brief-em3d-98 R-em3d98-4 — which "solved" glyph each solver kind shows, as a pure function of C3dSolveStatus's answer and the
// active setup. The control (SolveBadges), the floating window's title and the docs' legend all read this; the brief's table,
// top row first:
//
//   the active setup uses this kind and is Current     → solid, filled
//   the active setup uses this kind and is OutOfDate   → solid, hollow (it wins even when another setup is current)
//   otherwise, another setup of this kind is Current   → faded, filled
//   otherwise                                          → none
//   the glyph shown comes from a Partial result        → a small '*' after it
//
// There is deliberately no "faded hollow": an out-of-date result of a setup nobody is using is noise here, and the setup list
// says it in words. Shape and fill carry the meaning, never colour.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.ThreeD;

/// <summary>How a glyph is drawn.</summary>
public enum SolveBadgeLook { Solid, Hollow, Faded }

/// <summary>One glyph: its solver kind, its look, the <c>*</c>, and its tooltip in words.</summary>
public sealed record SolveBadge(SolverKind Kind, SolveBadgeLook Look, bool Partial, string Tooltip);

/// <summary>What a set of glyphs is drawn from: the statuses and the setup the editor has active.</summary>
public sealed record SolveBadgeSet(IReadOnlyList<SetupSolveStatus> Statuses, string? ActiveSetup)
{
    public static readonly SolveBadgeSet Empty = new([], null);

    /// <summary>At most one glyph per solver kind, FEM, FDTD, thermal in that order.</summary>
    public IReadOnlyList<SolveBadge> Glyphs => SolveBadgeRules.Pick(Statuses, ActiveSetup);
}

public static class SolveBadgeRules
{
    /// <summary>The order glyphs are drawn in: ◆ FEM, ▲ FDTD, ● thermal.</summary>
    public static readonly SolverKind[] Order = [SolverKind.Fem, SolverKind.Fdtd, SolverKind.Thermal];

    public static IReadOnlyList<SolveBadge> Pick(IReadOnlyList<SetupSolveStatus> statuses, string? activeSetup)
    {
        var glyphs = new List<SolveBadge>();
        foreach (var kind in Order)
        {
            var mine = statuses.FirstOrDefault(s => s.Solver == kind && s.Setup == activeSetup);
            if (mine is { State: SolveState.Current })
                glyphs.Add(new SolveBadge(kind, SolveBadgeLook.Solid, mine.Partial, Tooltip(mine)));
            else if (mine is { State: SolveState.OutOfDate })
                glyphs.Add(new SolveBadge(kind, SolveBadgeLook.Hollow, mine.Partial, Tooltip(mine)));
            else if (statuses.Where(s => s.Solver == kind && s.Setup != activeSetup && s.State == SolveState.Current)
                             .OrderBy(s => s.Partial).FirstOrDefault() is { } other)
                glyphs.Add(new SolveBadge(kind, SolveBadgeLook.Faded, other.Partial, Tooltip(other)));
        }
        return glyphs;
    }

    /// <summary>
    /// <c>FEM (Palace), setup 'EM1': solved 14:32, took 18 min</c>, or <c>… out of date: 'Board.clay' has changed since</c>,
    /// with the run's own sentence after it when the result is partial.
    /// </summary>
    public static string Tooltip(SetupSolveStatus s)
    {
        string head = $"{s.SolverName}, setup '{s.Setup}'";
        string body = s.State == SolveState.OutOfDate
            ? $"out of date: {s.StaleWhat} {(s.StaleWhat?.Contains(" and ", StringComparison.Ordinal) == true ? "have" : "has")} changed since"
            : (s.Solved is { } at ? $"solved {C3dSolveStatus.When(at)}" : "solved") +
              (s.Took is { } took ? $", took {C3dSolveStatus.Duration(took)}" : "");
        string partial = s.Partial
            ? " (" + (s.EndedAs switch
              {
                  C3dRunState.Cancelled    => "cancelled",
                  C3dRunState.NotConverged => "not converged",
                  C3dRunState.Failed       => "failed",
                  _                        => "interrupted",
              }) + ")" + (s.Detail is { } d ? ". " + d : "")
            : "";
        return $"{head}: {body}{partial}";
    }

    /// <summary>
    /// R-em3d98-5 item 4 — a floating window's title suffix: the kinds whose glyph is SOLID FILLED, with <c>partial</c> named
    /// where it applies — <c>solved (FEM, thermal)</c>, <c>solved (FEM, partial)</c>. Null when none is, so nothing is added.
    /// It never says "out of date" (D2): a title is read at a glance, and the tab and the setup list say that.
    /// </summary>
    public static string? TitleSuffix(SolveBadgeSet? set)
    {
        if (set is null) return null;
        var solid = set.Glyphs.Where(g => g.Look == SolveBadgeLook.Solid).ToList();
        if (solid.Count == 0) return null;
        var items = solid.Select(g => ShortName(g.Kind) + (g.Partial ? ", partial" : "")).ToList();
        return $"solved ({string.Join(solid.Any(g => g.Partial) && solid.Count > 1 ? "; " : ", ", items)})";
    }

    /// <summary>
    /// R-em3d98-7 — the one line asked before replacing a complete, current result:
    /// <c>Palace result for 'EM1' is current (solved 14:32, took 18 min). Run again?</c> A Both setup names both legs.
    /// </summary>
    public static string RerunQuestion(string setupName, IReadOnlyList<SetupSolveStatus> legs)
    {
        static string Program(SolverKind k) => k switch { SolverKind.Fem => "Palace", SolverKind.Fdtd => "openEMS", _ => "Thermal" };
        static string When(SetupSolveStatus l)
            => (l.Solved is { } at ? $"solved {C3dSolveStatus.When(at)}" : "solved") + (l.Took is { } t ? $", took {C3dSolveStatus.Duration(t)}" : "");
        if (legs.Count == 1)
            return $"{Program(legs[0].Solver)} result for '{setupName}' is current ({When(legs[0])}). Run again?";
        return $"{string.Join(" and ", legs.Select(l => Program(l.Solver)))} results for '{setupName}' are current " +
               $"({string.Join("; ", legs.Select(l => $"{Program(l.Solver)} {When(l)}"))}). Run again?";
    }

    /// <summary><c>FEM</c>, <c>FDTD</c>, <c>thermal</c>.</summary>
    public static string ShortName(SolverKind kind) => kind switch
    {
        SolverKind.Fem  => "FEM",
        SolverKind.Fdtd => "FDTD",
        _               => "thermal",
    };
}
