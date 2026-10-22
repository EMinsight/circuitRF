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
        // brief-emi-1 — each chosen solver's own rows, from its descriptor, in the order the run runs them.
        return [.. Em3dSolvers.Of(setup.Solver3D).SelectMany(d => d.Fidelity(problem, setup))];
    }

    /// <summary>The solver's name as a row is headed with.</summary>
    public static string SolverName(Em3dFidelitySolver s) => s == Em3dFidelitySolver.Palace ? "Palace" : "openEMS";
}
