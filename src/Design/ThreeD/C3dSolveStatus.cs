// brief-em3d-98 R-em3d98-3 — "which of this 3D view's setups have a current result?", answered in ONE place. The setup list,
// the document tab's glyphs, the workspace tree, a floating window's title, the confirmation before a re-run, `explain` and
// `find` all read this; none of them holds a staleness rule of its own. It adds none either: whether a result is the model's
// is brief 87's C3dRunDocument.Check (which compares CONTENT, so undoing back to the solved model makes it current again — on
// purpose), and how the run ended is the leg's status.json (C3dRunStatus).
//
// An open document is checked against its in-memory model (unsaved edits count, as the stale banner's do); a closed one
// against its file. Framework-free, so it runs on a worker thread and headlessly.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>Which solver a run leg is: Palace (FEM), openEMS (FDTD) or circuitRF's own thermal solver.</summary>
public enum SolverKind { Fem, Fdtd, Thermal }

/// <summary>Whether a leg's result is the model's.</summary>
public enum SolveState { NotRun, Current, OutOfDate }

/// <summary>
/// One (setup, solver leg)'s answer.
/// </summary>
/// <param name="Setup">The embedded setup's name, as the document spells it.</param>
/// <param name="Solver">The leg.</param>
/// <param name="State">No result to speak of; a result of the model as it is now; or one of a model since edited.</param>
/// <param name="Partial">The run did not finish as asked — cancelled, not converged, failed, or interrupted. Independent of
/// <paramref name="State"/>: a not-converged result can still be current.</param>
/// <param name="StaleWhat">What moved on, as a sentence's subject (<c>the model</c>, <c>'Board.clay'</c>); null unless out of date.</param>
/// <param name="Solved">When the run finished (local time); null when it has not.</param>
/// <param name="Took">How long it ran; null when unknown (a run kept before brief 98) or unfinished.</param>
/// <param name="Detail">The run's own sentence — why it did not converge, why it failed. Null for a plain completion.</param>
/// <param name="Running">A run of this leg is under way in a live process (this one or another).</param>
/// <param name="EndedAs">How the last run ended; null when there is no record at all or a run is under way. An interrupted run
/// (left at <c>running</c> by a process that is gone) reads <see cref="C3dRunState.Running"/> here with <paramref name="Running"/>
/// false.</param>
public sealed record SetupSolveStatus(string Setup, SolverKind Solver, SolveState State, bool Partial, string? StaleWhat,
                                      DateTime? Solved, TimeSpan? Took, string? Detail, bool Running = false,
                                      C3dRunState? EndedAs = null)
{
    /// <summary>The solver as a person names it: <c>FEM (Palace)</c>, <c>FDTD (openEMS)</c>, <c>thermal</c>.</summary>
    public string SolverName => C3dSolveStatus.Name(Solver);

    /// <summary>The leg's state in words, for a setup row: <c>Solved 14:32 (18 min)</c>, <c>Solved 14:32 (18 min), not
    /// converged</c>, <c>Out of date: 'Board.clay' has changed</c>, <c>Cancelled 14:32: no complete result</c>, <c>Not run</c>.</summary>
    public string Words => C3dSolveStatus.Words(this);
}

public static class C3dSolveStatus
{
    /// <summary>
    /// Every (setup, solver leg) of <paramref name="document"/> (at <paramref name="documentPath"/>), in setup order: a Both
    /// setup is two rows (FEM first). A setup that cannot be read contributes nothing — it cannot have been run.
    /// </summary>
    public static IReadOnlyList<SetupSolveStatus> Of(C3dDocument document, string documentPath, string resultsRoot, CancellationToken ct = default)
    {
        var rows = new List<SetupSolveStatus>();
        foreach (var embedded in C3dSetups.Read(document))
        {
            ct.ThrowIfCancellationRequested();
            if (embedded.Setup is not { } setup) continue;
            foreach (var (kind, dir) in RunDirectories(C3dSetups.ForRun(setup, documentPath), resultsRoot))
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(OfLeg(embedded.Name, kind, dir, document, documentPath));
            }
        }
        return rows;
    }

    /// <summary>Of setup <paramref name="setupName"/> alone — what the confirmation before a re-run computes when the
    /// background check has not answered yet.</summary>
    public static IReadOnlyList<SetupSolveStatus> OfSetup(C3dDocument document, string documentPath, string resultsRoot, string setupName)
        => C3dSetups.Read(document).FirstOrDefault(s => s.Name == setupName)?.Setup is { } setup
            ? OfRun(C3dSetups.ForRun(setup, documentPath), setupName, document, documentPath, resultsRoot) : [];

    /// <summary>Of the legs <paramref name="runSetup"/> (as its run names it, a <c>--solver</c> override applied) would run.</summary>
    public static IReadOnlyList<SetupSolveStatus> OfRun(EmSetup runSetup, string setupName, C3dDocument document, string documentPath, string resultsRoot)
        => [.. RunDirectories(runSetup, resultsRoot).Select(x => OfLeg(setupName, x.Kind, x.Directory, document, documentPath))];

    /// <summary>
    /// brief-em3d-98 R-em3d98-7 — whether running these legs again would replace a complete, current result: every leg
    /// <see cref="SolveState.Current"/> and none partial. A partial or out-of-date result has nothing complete to lose.
    /// </summary>
    public static bool AllCurrent(IReadOnlyList<SetupSolveStatus> legs)
        => legs.Count > 0 && legs.All(l => l.State == SolveState.Current && !l.Partial && !l.Running);

    /// <summary>Of a closed document: read from <paramref name="documentPath"/>; empty when it cannot be read.</summary>
    public static IReadOnlyList<SetupSolveStatus> OfFile(string documentPath, string resultsRoot, CancellationToken ct = default)
    {
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(documentPath); }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
        return Of(doc, documentPath, resultsRoot, ct);
    }

    /// <summary>
    /// THE list of where a setup's results live (moved here from the 3D editor so the editor, the CLI and this agree): the
    /// thermal run's own directory, or Palace's and/or openEMS's. <paramref name="runSetup"/> is the setup as its run names it
    /// (<see cref="C3dSetups.ForRun"/>), or a <c>.cem</c>'s own.
    /// </summary>
    public static IEnumerable<(SolverKind Kind, string Directory)> RunDirectories(EmSetup? runSetup, string? resultsRoot)
    {
        if (runSetup is null || resultsRoot is null) yield break;
        if (runSetup.IsThermal) { yield return (SolverKind.Thermal, Thermal.ThermalRunService.RunDirectory(resultsRoot, runSetup)); yield break; }
        if (runSetup.Solver3D is Em3dSolver.Palace or Em3dSolver.Both)
            yield return (SolverKind.Fem, Em3d.Em3dRunService.RunDirectory(resultsRoot, runSetup, Em3dSolver.Palace));
        if (runSetup.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both)
            yield return (SolverKind.Fdtd, Em3d.Em3dRunService.RunDirectory(resultsRoot, runSetup, Em3dSolver.OpenEms));
    }

    private static SetupSolveStatus OfLeg(string setup, SolverKind kind, string dir, C3dDocument document, string documentPath)
    {
        var status = C3dRunStatus.Read(dir);
        var check = C3dRunDocument.Check(dir, document, documentPath, setup);      // another setup's edit is no change to this one
        if (status is null && check is null) return new SetupSolveStatus(setup, kind, SolveState.NotRun, false, null, null, null, null);
        var state = check is null ? SolveState.NotRun : check.Stale ? SolveState.OutOfDate : SolveState.Current;
        DateTime? solved = status?.Finished is { } f ? f.ToLocalTime() : check?.Written;
        if (status is { Running: true }) solved = null;
        return new SetupSolveStatus(setup, kind, state, status?.Partial ?? false, check?.Stale == true ? check.What : null,
                                    solved, status?.Took, status?.Detail, status?.Running ?? false,
                                    status is null || status.Running ? null : status.State);
    }

    /// <summary>A solver kind as a person names it.</summary>
    public static string Name(SolverKind kind) => kind switch
    {
        SolverKind.Fem  => "FEM (Palace)",
        SolverKind.Fdtd => "FDTD (openEMS)",
        _               => "thermal",
    };

    /// <summary>A duration as the setup row spells it: <c>40 s</c>, <c>18 min</c>, <c>2 h 05 min</c>.</summary>
    public static string Duration(TimeSpan t)
        => t.TotalSeconds < 90 ? $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))} s"
         : t.TotalMinutes < 90 ? $"{(int)Math.Round(t.TotalMinutes)} min"
         : $"{(int)t.TotalHours} h {t.Minutes:00} min";

    /// <summary>When a run finished, as the setup row spells it: <c>14:32</c> today, else <c>2 Oct 14:32</c>.</summary>
    public static string When(DateTime local)
        => local.Date == DateTime.Now.Date ? local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                                           : local.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>Solved 14:32 (18 min)</c>, or <c>Solved 14:32</c> when the duration is unknown.</summary>
    public static string SolvedPhrase(SetupSolveStatus s)
        => s.Solved is { } at ? $"Solved {When(at)}" + (s.Took is { } took ? $" ({Duration(took)})" : "") : "Solved";

    /// <summary>The words for a leg (see <see cref="SetupSolveStatus.Words"/>).</summary>
    public static string Words(SetupSolveStatus s)
    {
        if (s.Running) return "Running";
        string partial = s.EndedAs switch
        {
            C3dRunState.Cancelled    => "cancelled",
            C3dRunState.NotConverged => "not converged",
            C3dRunState.Failed       => "failed",
            C3dRunState.Running      => "interrupted",
            _                        => "",
        };
        string ended = partial.Length > 0 && s.Solved is { } at ? $"{char.ToUpperInvariant(partial[0])}{partial[1..]} {When(at)}" : partial;
        return s.State switch
        {
            SolveState.Current   => SolvedPhrase(s) + (s.Partial ? $", {partial}" : ""),
            SolveState.OutOfDate => $"Out of date: {s.StaleWhat} {(s.StaleWhat?.Contains(" and ", StringComparison.Ordinal) == true ? "have" : "has")} changed" +
                                    (s.Partial ? $" ({partial})" : ""),
            _ => s.Partial && ended.Length > 0 ? $"{ended}: no complete result" : "Not run",
        };
    }
}
