// brief-em3d-65 R-em3d65-4d — the fidelity rows for one SETUP: its solver's (both solvers' for Both), from the problem it
// makes, with the settings its run would use. `check` and the 3D editor's Setup Analyses dialog read this; the run itself
// states the same rows from its own lowering (Em3dRunService, CsxcadWriter, FdtdGrid), so all three say the same thing.
//
// Cheap by construction: no worker call and no solver — the face tables are already in the problem, the openEMS rows need
// the grid placed (managed, milliseconds), and a problem with no kernel solid returns at once.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class Em3dFidelityReport
{
    /// <summary>The rows <paramref name="setup"/>'s solver(s) give <paramref name="problem"/>; empty when it holds no kernel
    /// solid, or a setting the run would refuse leaves nothing to compute them from.</summary>
    public static IReadOnlyList<Em3dFidelityFinding> For(Em3dProblem problem, EmSetup setup)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(setup);
        if (!problem.Solids.Any(s => s.Primitive is Em3dShapeSolid)) return [];
        var rows = new List<Em3dFidelityFinding>();
        if (setup.Solver3D is Em3dSolver.Palace or Em3dSolver.Both)
        {
            var settings = PalaceSettings.Resolve(setup.Palace);
            double? smallest = settings.Problems().Count == 0 ? GmshGeoWriter.SmallestRequestedSizeM(problem, settings) : null;
            rows.AddRange(Em3dFidelity.For(problem, Em3dFidelitySolver.Palace, null, smallest));
        }
        if (setup.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both && !problem.IsStatic && problem.Type != Em3dProblemType.Eigenmode)
        {
            var grid = CemOpenEms.ResolveGrid(setup.OpenEms);
            if (grid.Problems().Count == 0)
            {
                try { rows.AddRange(Em3dFidelity.For(problem, Em3dFidelitySolver.OpenEms, FdtdGrid.Build(problem, grid, long.MaxValue))); }
                catch (InvalidOperationException) { /* the run reports a grid it cannot build; nothing to measure against */ }
            }
        }
        return rows;
    }

    /// <summary>The solver's name as a row is headed with.</summary>
    public static string SolverName(Em3dFidelitySolver s) => s == Em3dFidelitySolver.Palace ? "Palace" : "openEMS";
}
