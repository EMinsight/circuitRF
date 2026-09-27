// ================================================================
//  NoMaterialIgnoredTests.cs — 3D editor bugs round 2: an object with no material does not stop a run; the solver
//  ignores it. It is a warning (elaboration, the run, check's exit code 0), it is in no list a solver reads, and it
//  does not size the air box. A material the technology does not define is still a refusal, and a design whose every
//  object has no material still has nothing to solve.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class NoMaterialIgnoredTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-nomat-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;                 // DBU per µm

    [Fact]
    public void AnObjectWithNoMaterial_IsIgnoredByTheRun_AWarning_InNoSolverList_AndNotInTheAirBox()
    {
        var plain = new C3dDocument { Objects = [Box("sub", "Fill", 0, 0, 0, 1000, 1000, 100)] };
        var withGhost = new C3dDocument { Objects = [Box("sub", "Fill", 0, 0, 0, 1000, 1000, 100), Box("ghost", null, 5000, 0, 0, 100, 100, 100)] };

        var without = C3dProblemAssembly.Assemble(Setup(), plain, WriteC3d(Workspace(), "A", plain), null);
        var g = C3dProblemAssembly.Assemble(Setup(), withGhost, WriteC3d(Workspace(), "B", withGhost), null);

        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(["sub"], g.Problem!.Solids.Select(s => s.Name));
        Assert.Empty(g.Problem.Sheets);
        Assert.Contains(C3dElaborator.NoMaterialWarning("ghost"), g.Warnings);
        // The box is sized by what is solved: 5 mm away, the ghost moves no face of it.
        Assert.Equal(without.Problem!.Boundary.Min, g.Problem.Boundary.Min);
        Assert.Equal(without.Problem.Boundary.Max, g.Problem.Boundary.Max);
    }

    [Fact]
    public void AMaterialTheTechnologyLacks_IsStillARefusal_AndAllMaterialLessHasNothingToSolve()
    {
        var typo = new C3dDocument { Objects = [Box("sub", "Fill", 0, 0, 0, 1000, 1000, 100), Box("typo", "Fil", 2000, 0, 0, 100, 100, 100)] };
        var t = C3dProblemAssembly.Assemble(Setup(), typo, WriteC3d(Workspace(), "Typo", typo), null);
        Assert.False(t.Ok);
        Assert.Contains("'typo' is made of 'Fil'", t.Refusal);

        var bare = new C3dDocument { Objects = [Box("a", null, 0, 0, 0, 100, 100, 100), Box("b", null, 200, 0, 0, 100, 100, 100)] };
        var b = C3dProblemAssembly.Assemble(Setup(), bare, WriteC3d(Workspace(), "Bare", bare), null);
        Assert.False(b.Ok);
        Assert.Equal("None of this 3D view's 2 objects has a material, and the solver ignores an object with none, so there is " +
                     "nothing to solve. Give one a material.", b.Refusal);
    }

    /// <summary>check: a warning, said once, and exit 0 — validation and elaboration both find it, in the same words.</summary>
    [Fact]
    public void Check_ReportsAnObjectWithNoMaterialOnce_AsAWarning_AndExitsZero()
    {
        var doc = new C3dDocument { Objects = [Box("sub", "Fill", 0, 0, 0, 1000, 1000, 100), Box("ghost", null, 2000, 0, 0, 100, 100, 100)] };
        string path = WriteC3d(Workspace(), "Cell", doc);

        var run = RunCli("check", path);

        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        string all = run.StdOut + run.StdErr;
        string warning = C3dElaborator.NoMaterialWarning("ghost");
        int first = all.IndexOf(warning, StringComparison.Ordinal);
        Assert.True(first >= 0, all);
        Assert.Equal(-1, all.IndexOf(warning, first + warning.Length, StringComparison.Ordinal));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, string? material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private static EmSetup Setup() => new() { Name = "S1", Solver3D = Em3dSolver.Palace };

    /// <summary>A workspace whose default technology holds Fill (εr 2).</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Fill", Epsr = 2 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(NoMaterialIgnoredTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
