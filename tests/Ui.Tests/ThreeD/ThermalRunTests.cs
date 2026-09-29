// ================================================================
//  ThermalRunTests.cs — brief-em3d-74's design-side gates: the lowering (gate 8), the run end to end through `em`
//  (gate 9) and the refusals (gate 10). The solver's own gates (1–7) are tests/Thermal.Tests.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalRunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th74-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per µm

    // ── gate 8: the lowering ─────────────────────────────────────────────────────────────────────────

    /// <summary>A two-solid thermal .geo with a source sheet: no air, the source's embedded group, the face groups asked for.</summary>
    [Fact]
    public void Gate8_TheThermalGeo_HasNoAir_TheSourcesGroup_AndTheFacesAskedFor()
    {
        string ws = Workspace();
        var doc = Doc(Setup());
        string path = WriteC3d(ws, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var low = ThermalLowerings.Build(doc, e, Setup().Thermal!, 1, out string? why);
        Assert.NotNull(low);
        Assert.Null(why);
        string geo = low!.Gmsh.Geo!;
        Assert.DoesNotContain(GmshGeoWriter.BackgroundName, geo);
        Assert.DoesNotContain("Box(bx)", geo);
        Assert.DoesNotContain("Recursive Delete", geo);                                   // a conductor is a volume here
        Assert.Contains("Physical Volume(\"flange\"", geo);
        Assert.Contains("Physical Volume(\"die\"", geo);
        Assert.Contains("Physical Surface(\"fingers\"", geo);                             // the embedded source sheet
        Assert.Contains("Physical Surface(\"flange/zmin\"", geo);                         // the FixedT face
        Assert.Contains("Physical Surface(\"die/zmax\"", geo);                            // the probe face
        Assert.Equal(1, geo.Split("= Box;").Length - 1);                                  // the mesh region
        Assert.Equal([2, 1, 2], [low.Regions.Count, low.SheetSourceTags.Count, low.Faces.Count]);
    }

    /// <summary>A face that is a boundary AND a probe's (or a current's contact) is two groups — its exterior, and the whole face —
    /// and Gmsh refuses a second physical group of one name, so the boundary's is written under its own.</summary>
    [Fact]
    public void Gate8_AFaceThatIsABoundaryAndAProbe_IsTwoDifferentlyNamedGroups()
    {
        string ws = Workspace();
        var doc = Doc(Setup());
        doc.Probes.Add(new C3dProbe { Name = "base", Face = "flange/zmin", Stat = C3dProbeStat.Avg });
        string path = WriteC3d(ws, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        var low = ThermalLowerings.Build(doc, e, Setup().Thermal!, 1, out string? why);
        Assert.Null(why);
        string geo = low!.Gmsh.Geo!;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(geo, "Physical Surface\\(\"flange/zmin\","));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(geo, "Physical Surface\\(\"flange/zmin \\(exterior\\)\","));
    }

    /// <summary>Mesh regions in a Palace lowering: one Box field for one region, and none — not a byte changed — without.</summary>
    [Fact]
    public void Gate8_APalaceGeo_GetsOneBoxFieldPerRegion_AndIsItsGoldenWithout()
    {
        var (problem, settings) = PalaceBackendTests.LowerableFor("microstrip");
        string golden = File.ReadAllText(Path.Combine(Repo(), "testdata", "em3d", "palace-goldens", "microstrip", GmshGeoWriter.GeoFile));
        Assert.Equal(golden, GmshGeoWriter.Write(problem, settings).Geo);
        var region = new Em3dMeshRegion("fine", new Point3(0, 0, 0), new Point3(1e-4, 1e-4, 1e-4), 5e-6, null);
        string geo = GmshGeoWriter.Write(problem with { MeshRegions = [region] }, settings).Geo!;
        Assert.Equal(1, geo.Split("= Box;").Length - 1);
        Assert.Contains("VIn = 5;", geo);
    }

    // ── gate 9: the run end to end, through `em` ─────────────────────────────────────────────────────

    [GmshFact]
    public void Gate9_EmRunsAThermalSetup_TheNpyThePvdAndAMeasure()
    {
        string ws = Workspace();
        var setup = Setup();
        setup.Thermal!.Mesh = new CemThermalMesh { Check = true };
        string path = WriteC3d(ws, Doc(setup));
        var (code, stdout, stderr) = RunCli(null, "em", path, "--setup", "Hot");
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.True(code == 0, stderr);

        string npy = Path.Combine(ws, "results", "Cell Hot.thermal.npy");
        Assert.True(File.Exists(npy), stdout);
        var data = DataSetImporter.Import(npy).DataSet;
        var rth = data["Rth"].RealValues;
        Assert.Equal(2, rth.Length);
        Assert.All(rth, r => Assert.True(r > 0 && double.IsFinite(r), $"Rth {r}"));
        // linear problem: Rth does not move with power
        Assert.Equal(rth[0], rth[1], 1e-6 * rth[0]);
        Assert.All(data["thermal.Energy:balance"].RealValues, b => Assert.True(b <= 1e-6, $"balance {b}"));
        Assert.Contains("energy balance", stderr);
        Assert.Contains("Mesh-convergence check", stderr);                                // R-em3d74-5e, a report
        Assert.Contains("probe 'die_top' (max):", stderr);

        string pvd = Path.Combine(ws, "results", "Cell Hot.thermal", "postpro", "paraview", "thermal", "thermal.pvd");
        var steps = FieldRun.Steps(pvd);
        Assert.Equal(2, steps.Count);
        var piece = VtuReader.ReadPiece(FieldRun.Pieces(steps[1].Pvtu).Single());
        var t = new float[piece.NumberOfPoints];
        VtuReader.ReadFloats(piece, piece.Array("T_C")!, t);
        Assert.InRange(t.Max(), 25.0f, 1000f);
        Assert.Equal(25.0f, t.Min(), 1e-3f);

        // R-em3d74-6 — explain reports the size before any mesher runs
        var (ecode, eout, _) = RunCli(null, "explain", path, "--analysis", "Hot");
        output.WriteLine(eout);
        Assert.Equal(0, ecode);
        Assert.Contains("tetrahedra of order 2", eout);
        Assert.Contains("solver; about", eout);
    }

    // ── gate 10: refusals ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_NoGmsh_IsThePalaceRefusal()
    {
        string ws = Workspace();
        string path = WriteC3d(ws, Doc(Setup()));
        var (code, _, stderr) = RunCli(("CIRCUITRF_GMSH", Path.Combine(_root, "no-such-gmsh")), "em", path, "--setup", "Hot");
        Assert.Equal(1, code);
        Assert.Contains("Gmsh", stderr);
        Assert.False(File.Exists(Path.Combine(ws, "results", "Cell Hot.thermal.npy")));
    }

    [Fact]
    public void Gate10_NoSink_AndAMaterialWithoutK_AreRefusedByName()
    {
        string ws = Workspace();
        var noSink = Setup();
        noSink.Thermal!.Boundaries = [];
        var doc = Doc(noSink);
        string path = WriteC3d(ws, doc);
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, "Hot").Setup!, path), doc, path, null, Path.Combine(ws, "results"));
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("no FixedT or Convection boundary", run.Error);

        var plastic = Doc(Setup());
        ((C3dBox)plastic.Objects[1]).Material = "Plastic";
        path = WriteC3d(ws, plastic);
        run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(plastic, "Hot").Setup!, path), plastic, path, null, Path.Combine(ws, "results"));
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("'Plastic', which states no thermal conductivity", run.Error);
        Assert.Contains("'die'", run.Error);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A copper flange with a die on it, a 1 W (at Pdiss = 1) sheet on the die's top, probes and a mesh region.</summary>
    private static C3dDocument Doc(EmSetup setup) => new()
    {
        Objects = [Box("flange", "Copper", 0, 0, 0, 1000, 1000, 200), Box("die", "Die", 300, 300, 200, 400, 400, 100)],
        Variables = [new C3dVariable { Name = "Pdiss", Expression = "1" }],
        HeatSources = [new C3dHeatSource { Name = "fingers", Power = "Pdiss", Sheet = new C3dHeatSheet { Plane = C3dPlane.XY, Offset = 300 * Um, Rect = Rect(450, 450, 100, 100) } }],
        Probes =
        [
            new C3dProbe { Name = "die_top", Face = "die/zmax", Stat = C3dProbeStat.Max },
            new C3dProbe { Name = "ir", Spot = new C3dProbeSpot { Face = "die/zmax", Center = new(500 * Um, 500 * Um, 300 * Um), Diameter = 50 * Um } },
            new C3dProbe { Name = "core", Solid = "die" },
            new C3dProbe { Name = "pt", Point = new(500 * Um, 500 * Um, 250 * Um) },
            new C3dProbe { Name = "axis", Line = new C3dProbeLine { From = new(500 * Um, 500 * Um, 0), To = new(500 * Um, 500 * Um, 300 * Um) } },
        ],
        MeshRegions = [new C3dMeshRegion { Name = "fingers_box", Min = new(400 * Um, 400 * Um, 250 * Um), Size = new(200 * Um, 200 * Um, 50 * Um), SizeUm = 20 }],
        Setups = [EmSetupPersistence.ToEmbedded(setup)],
    };

    private static EmSetup Setup() => new()
    {
        Name = "Hot",
        Problem3D = Em3dProblemType.Thermal,
        Thermal = new CemThermal
        {
            Boundaries = [new CemThermalBoundary { Face = "flange/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }],
            Sweep = [new CemThermalSweep { Var = "Pdiss", Start = "1", Stop = "2", Points = 2 }],
            Measures = ["Rth = (Tmax(die_top) - 25) / Pdiss"],
        },
    };

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dBox Box(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, ThermalK = 400 },
                new TechMaterial { Name = "Die", Epsr = 12.9, ThermalK = 150 },
                new TechMaterial { Name = "Plastic", Epsr = 3 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, C3dDocument doc)
    {
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli((string Name, string Value)? env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(ThermalRunTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (env is { } e) psi.Environment[e.Name] = e.Value;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string Repo() => PalaceBackendTests.RepoRoot();
}
