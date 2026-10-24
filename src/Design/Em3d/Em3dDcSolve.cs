// brief-emdc-1 R-emdc1-4 — the one entry point of a 3D problem's DC solve: lower (Em3dDcLowering), mesh with Gmsh, solve
// (src/Thermal/Conduction/DcConduction.cs). It reads only the Em3dProblem, so every 3D setup — the .clay route and the .c3d
// route alike — and every full-wave solver get the same row from one implementation. It takes no preference and posts nothing:
// what it has to say is returned (src/Design/CLAUDE.md). Gmsh missing is a refusal whose text is the result's; the install offer
// is the run's to show.

using System.Diagnostics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal.Conduction;

namespace CircuitRF.Design.Em3d;

/// <summary>A problem lowered and meshed for its DC solve, or why not.</summary>
public sealed record Em3dDcPrepared(Em3dDcLowered? Lowered, Em3dDcMeshed? Meshed, string? Refusal, IReadOnlyList<string> Notes);

public static class Em3dDcSolve
{
    /// <summary>
    /// R-emdc1-4 — the DC row of <paramref name="problem"/>, meshed in <paramref name="workDir"/> (a mesh whose script is
    /// unchanged is reused). A non-driven problem, one with no ports, or a port with no DC meaning is a refusal; cancellation
    /// throws. <paramref name="gmsh"/> is discovery's answer for Gmsh when the caller already has it (the run's, or a test's);
    /// null asks discovery.
    /// </summary>
    public static Em3dDcResult Solve(Em3dProblem problem, string workDir, CancellationToken ct, SolverReadiness? gmsh = null)
    {
        var clock = Stopwatch.StartNew();
        var prep = Prepare(problem, workDir, ct, gmsh);
        var notes = new List<string>(prep.Notes);
        if (prep.Meshed is not { } meshed) return new Em3dDcResult(null, prep.Refusal, notes, 0, clock.Elapsed.TotalMilliseconds);
        var r = DcConduction.Solve(meshed.Problem, ct);
        notes.AddRange(r.Notes);
        double ms = clock.Elapsed.TotalMilliseconds;
        notes.Add($"The DC row took {ms / 1000:0.##} s in all, meshing included.");
        return new Em3dDcResult(r.S, r.Refusal, notes, r.Elements, ms);
    }

    /// <summary>The lowering and the mesh read back, without the solve: what the tests of the contacts read.</summary>
    public static Em3dDcPrepared Prepare(Em3dProblem problem, string workDir, CancellationToken ct, SolverReadiness? gmsh = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(workDir);
        var notes = new List<string>();
        var low = Em3dDcLowering.Build(problem, out string? refusal);
        if (low is null) return new(null, null, refusal, notes);
        notes.AddRange(low.Notes);
        gmsh ??= SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh));
        if (!gmsh.Proceeds) return new(low, null, gmsh.Refusal, notes);
        if (gmsh.Warning is { } untested) notes.Add(untested);
        var clock = Stopwatch.StartNew();
        var step = PalaceRun.Mesh(workDir, low.Gmsh, gmsh.Installation!.Path, null, ct);
        if (step.Cancelled) throw new OperationCanceledException(ct);
        if (!step.Ok) return new(low, null, $"The DC conduction mesh could not be made: {step.Message}", notes);
        var raw = MshReader.Read(Path.Combine(workDir, GmshGeoWriter.MeshFile));
        var meshed = Em3dDcLowering.Read(low, raw, out refusal);
        if (meshed is null) return new(low, null, refusal, notes);
        notes.Add($"DC conduction mesh: {meshed.Tetrahedra:N0} tetrahedra and {meshed.SheetTriangles:N0} sheet triangles of order " +
                  $"{low.Input.Sizing.Order}, conductors only, {(step.Reused ? "reused" : "meshed")} in {clock.Elapsed.TotalSeconds:0.##} s ({workDir}).");
        notes.AddRange(meshed.Notes);
        return new(low, meshed, null, notes);
    }
}
