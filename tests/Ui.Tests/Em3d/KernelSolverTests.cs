// ================================================================
//  KernelSolverTests.cs — the gate for brief-em3d-65: both 3D solvers take a kernel solid.
//  Palace imports the worker's B-rep into the .geo and recovers its faces by the worker's tight boxes; openEMS reads a
//  tessellation fitted to its grid from a PLY file, checked before the run. Gates 2, 4 and 8 are scripts and files, built
//  from a face table made by hand (no worker); gate 3 meshes a worker-built solid with Gmsh and gate 6 counts openEMS's
//  metal grid edges — each skips, naming what is missing, where the worker or the solver is not installed.
// ================================================================

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class KernelSolverTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-kernel65-" + Guid.NewGuid().ToString("N")[..12]);
    private static readonly Em3dMaterial Gold = new("Gold", 1, null, 0, 1, 4.1e7);
    private static readonly Em3dMaterial Alumina = new("Alumina", 9.8, null, 0, 1, 0);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 2. the .geo for a kernel solid, headless ────────────────────────────────────────────────

    [Fact]
    public void Gate2_TheScript_ImportsEachKernelSolidByHash_AndRecoversItsFacesByTheirTightBoxes()
    {
        var lid = HandBuilt.Box(0, 0, 0, 1000, 1000, 500, "a1b2c3d4e5f60718293a4b5c6d7e8f90");
        var slab = HandBuilt.SplitSlab("f0e1d2c3b4a5968778695a4b3c2d1e0f");
        var p = Problem([new Em3dSolid("lid", "Gold", Em3dRole.Conductor, lid, 0), new Em3dSolid("slab", "Alumina", Em3dRole.Dielectric, slab, 1)])
                with { FaceBoundaries = [new("slab", "zmax", Em3dFaceBoundaryKind.Pec), new("slab", "bore:side", Em3dFaceBoundaryKind.Pec)] };
        Assert.Empty(p.Validate());

        var low = GmshGeoWriter.Write(p, PalaceSettings.Resolve(null));
        Assert.True(low.Ok, low.Refusal);
        string geo = low.Geo!;

        // The import, under the solid's own list name, and its count kept for the entity table.
        Assert.Contains("s0[] = ShapeFromFile(\"kernel-a1b2c3d4e5f60718.brep\");\nns0 = #s0[];\n", geo, StringComparison.Ordinal);
        Assert.Contains("s1[] = ShapeFromFile(\"kernel-f0e1d2c3b4a59687.brep\");\nns1 = #s1[];\n", geo, StringComparison.Ordinal);
        int lidAttr = low.Groups.Single(g => g.Name == "lid").Attribute;
        Assert.Contains($"If (ns0 != 1) Printf(\"kernel_import_count {lidAttr} %g\", ns0) >> \"entities.txt\"; EndIf", geo, StringComparison.Ordinal);
        Assert.DoesNotContain("OCCTargetUnit", geo, StringComparison.Ordinal);      // D12: a B-rep has no unit (the .step variant is not written)

        // The conductor's faces: one query per face of the face table, each its tight box.
        Assert.Equal(1, Regex.Count(geo, @"^c0\[\] = Surface In BoundingBox", RegexOptions.Multiline));
        Assert.Equal(lid.Faces.Count - 1, Regex.Count(geo, @"^c0\[\] \+= Surface In BoundingBox", RegexOptions.Multiline));
        Assert.Contains("c0[] += Surface In BoundingBox{1000 - e, 0 - e, 0 - e, 1000 + e, 1000 + e, 500 + e};", geo, StringComparison.Ordinal);

        // A named face split in two expects two surfaces, exactly; a curved face one — Palace takes both.
        var zmax = low.Groups.Single(g => g.Name == "slab/zmax");
        var bore = low.Groups.Single(g => g.Name == "slab/bore:side");
        Assert.Equal((2, false), (zmax.Expected, zmax.AtLeast));
        Assert.Equal((1, false), (bore.Expected, bore.AtLeast));

        // The hand-off: each B-rep, its bytes, named as the script names it.
        Assert.Equal(["kernel-a1b2c3d4e5f60718.brep", "kernel-f0e1d2c3b4a59687.brep"], low.KernelFiles!.Select(f => f.FileName));
        Assert.Equal(lid.Brep.ToArray(), low.KernelFiles![0].Bytes);

        // Byte-deterministic, and a file read as anything but one volume is named by the entity check.
        Assert.Equal(geo, GmshGeoWriter.Write(p, PalaceSettings.Resolve(null)).Geo);
        var table = GmshGeoWriter.ReadEntities($"kernel_import_count {lidAttr} 2\nall_volumes 1\nclassified_volumes 1\nunclassified_single_sided 0\n");
        Assert.Contains("'lid' was read from its kernel file as 2 volume(s) where one solid was written.",
                        GmshGeoWriter.CheckEntities(low.Groups, table), StringComparison.Ordinal);
    }

    // ── 3. Gmsh reads it ────────────────────────────────────────────────────────────────────────

    [KernelGmshFact]
    public void Gate3_ABoxMinusACylinder_FromTheWorker_MeshesWithEveryGroupClaimed()
    {
        using var kernel = KernelForTests.New();
        var lid = WorkerBoxMinusBore(kernel, "lid", 1000, 1000, 500, bore: 200);
        var p = Problem([new Em3dSolid("lid", "Gold", Em3dRole.Conductor, lid, 0)]);
        Assert.Empty(p.Validate());
        var settings = PalaceSettings.Resolve(null) with { MaxElementWavelengths = 0.5, EdgeRefinement = 1 };
        var low = GmshGeoWriter.Write(p, settings);
        Assert.True(low.Ok, low.Refusal);

        string dir = Dir("gmsh");
        var step = PalaceRun.Mesh(dir, low, SolverDiscovery.Gmsh.Find(out _)!.Path, null, default);
        Assert.True(step.Ok, step.Message);
        var table = GmshGeoWriter.ReadEntities(File.ReadAllText(Path.Combine(dir, GmshGeoWriter.EntitiesFile)))!;
        foreach (var g in low.Groups) output.WriteLine($"{g.Attribute,3} {g.Name,-14} {g.Kind,-10} {table.Groups[g.Attribute].Count}");
        Assert.Equal(0, table.UnclassifiedSingleSided);
        Assert.Empty(table.KernelImports);
        Assert.True(table.Groups[low.Groups.Single(g => g.Name == "lid").Attribute].Count >= 7);   // six faces and the bore's wall
        Assert.Null(GmshGeoWriter.CheckEntities(low.Groups, table));
        Assert.Equal(lid.Brep.ToArray(), File.ReadAllBytes(Path.Combine(dir, GmshGeoWriter.KernelFileName(lid))));
    }

    // ── 4. the PLY ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ThePly_IsByteIdentical_AtAQuarterOfTheSmallestCell_AndThePreRunCheckRefusesADamagedOne()
    {
        var lid = HandBuilt.Box(0, 0, 0, 1000, 1000, 500, "a1b2c3d4e5f60718293a4b5c6d7e8f90");
        double? askedAt = null;
        lid = lid with { Tessellator = (linear, _) => { askedAt = linear; return lid.Display; } };
        var p = Problem([new Em3dSolid("lid", "Gold", Em3dRole.Conductor, lid, 0)]);
        var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
        var first = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        var second = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(first.Ok, first.Refusal);
        var ply = Assert.Single(first.KernelFiles!);
        Assert.Equal(ply.Bytes, Assert.Single(second.KernelFiles!).Bytes);

        // The deflection the writer used: a quarter of the smallest cell inside the solid's box, on any axis.
        double smallest = new[] { grid.X, grid.Y, grid.Z }.Select((g, a) => Smallest(g.Lines, a == 0 ? (0, 1000) : a == 1 ? (0, 1000) : (0, 500))).Min();
        Assert.Equal(0.25 * smallest, ply.DeflectionM!.Value, 15);
        Assert.Equal(0.25 * smallest, askedAt!.Value, 15);
        Assert.EndsWith($"-{(Math.Round(0.25 * smallest * 1e6, 6)).ToString("R", CultureInfo.InvariantCulture)}.ply", ply.FileName, StringComparison.Ordinal);
        Assert.Contains($"<PolyhedronReader FileName=\"{ply.FileName}\" FileType=\"PLY\"", first.Model, StringComparison.Ordinal);

        // Faces normal to an axis are 1e-4 of a cell outward: xmax is past x = 1000 µm, by nanometres.
        var xs = Encoding.UTF8.GetString(ply.Bytes).Split('\n').SkipWhile(l => l != "end_header").Skip(1)
                         .Where(l => l.Split(' ').Length == 3).Select(l => double.Parse(l.Split(' ')[0], CultureInfo.InvariantCulture)).ToList();
        Assert.InRange(xs.Max() - 1000 * Um, 1e-12, 1e-7);
        Assert.Contains(first.Notes, n => n.Contains("1e-4 of the local cell outward", StringComparison.Ordinal));

        // Staged beside each file naming it, checked before openEMS starts: a truncated file and a missing one are refused.
        string run = Dir("openems");
        OpenEmsRun.Stage(run, first);
        string port = Path.Combine(run, OpenEmsRun.PortDirectory(first.Ports[0]));
        Assert.Null(OpenEmsRun.CheckKernelFiles(port, first, first.PortFiles[0]));
        string file = Path.Combine(port, ply.FileName);
        File.WriteAllBytes(file, ply.Bytes[..(ply.Bytes.Length / 2)]);
        Assert.StartsWith("'lid' is read by openEMS from ", OpenEmsRun.CheckKernelFiles(port, first, first.PortFiles[0]), StringComparison.Ordinal);
        Assert.Contains("which is not what circuitRF wrote", OpenEmsRun.CheckKernelFiles(port, first, first.PortFiles[0]), StringComparison.Ordinal);
        File.Delete(file);
        Assert.Contains("which is missing", OpenEmsRun.CheckKernelFiles(port, first, first.PortFiles[0]), StringComparison.Ordinal);
    }

    // ── 6. the polyhedron's missing face nodes ──────────────────────────────────────────────────

    [KernelOpenEmsFact]
    public void Gate6_AKernelBox_AndTheEqualManagedBox_GiveTheSameMetalGridEdges()
    {
        using var kernel = KernelForTests.New();
        // A 600 × 400 × 35 µm strip: a boolean whose Tool misses, so the result is the Blank as the kernel states it.
        var tree = GeometryKernelTree.From(new C3dBoolean
        {
            Name = "strip", Op = C3dBooleanOp.Subtract,
            Blank = new C3dBox { Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(600_000, 400_000, 35_000) },
            Tools = [new C3dCylinder { Name = "miss", Base = new C3dPoint3(5_000_000, 0, 0), Length = 100_000, Radius = 50_000 }],
        }, 1000);
        var strip = C3dElaborator.ShapeSolid(kernel, tree, "strip").Solid;
        var managed = new Em3dBox(new Point3(0, 0, 0), new Point3(600 * Um, 400 * Um, 35 * Um));
        var box = new Em3dAirBox(new Point3(-400 * Um, -400 * Um, -200 * Um), new Point3(1000 * Um, 800 * Um, 235 * Um),
                                 new Em3dFaces(Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec,
                                               Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec));
        Em3dProblem With(Em3dPrimitive prim) => new([new Em3dSolid("strip", "Gold", Em3dRole.Conductor, prim, 0)], [], [Gold],
            [new Em3dPort(1, "port/1", "strip", "airbox/zmin", new Point3(250 * Um, 200 * Um, -200 * Um), new Point3(350 * Um, 200 * Um, 0),
                          new Point3(0, 0, 1), 50, new Em3dReferencePlane(new Point3(0, 0, 0), new Point3(0, 0, 1), 0))],
            box, new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 25);

        // ONE grid, the kernel problem's, for both: only the primitive differs.
        var kp = With(strip);
        var grid = FdtdGrid.Build(kp, OpenEmsGridSettings.Default, long.MaxValue);
        var polyhedron = CsxcadWriter.Write(kp, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        var boxed = CsxcadWriter.Write(With(managed), grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(polyhedron.Ok && boxed.Ok, polyhedron.Refusal ?? boxed.Refusal);

        int PecEdges(CsxcadLowering low, string name)
        {
            string dir = Dir(name);
            OpenEmsRun.Stage(dir, low);
            string port = Path.Combine(dir, OpenEmsRun.PortDirectory(1));
            string xml = Regex.Replace(low.PortFiles[0], "NumberOfTimesteps=\"\\d+\"", "NumberOfTimesteps=\"10\"");
            File.WriteAllText(Path.Combine(port, CsxcadWriter.ModelFile), xml);
            var psi = new System.Diagnostics.ProcessStartInfo(SolverDiscovery.OpenEms.Find(out _)!.Path)
            {
                WorkingDirectory = port, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add(CsxcadWriter.ModelFile);
            psi.ArgumentList.Add("--debug-PEC");
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string log = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.DoesNotContain("No primitives found", log, StringComparison.Ordinal);
            string dump = File.ReadAllText(Path.Combine(port, "PEC_dump.vtp"));
            var lines = Regex.Match(dump, "NumberOfLines=\"(\\d+)\"");
            Assert.True(lines.Success, "PEC_dump.vtp states no NumberOfLines");
            return int.Parse(lines.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        int asBox = PecEdges(boxed, "box"), asKernel = PecEdges(polyhedron, "kernel");
        output.WriteLine($"PEC grid edges: managed Box {asBox}, kernel polyhedron {asKernel}");
        Assert.True(asBox > 0);
        Assert.Equal(asBox, asKernel);
    }

    // ── 8. a boundary on a curved face ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_ABoundaryOnACurvedKernelFace_IsRefusedForOpenEms_AndLowersForPalace()
    {
        var slab = HandBuilt.SplitSlab("f0e1d2c3b4a5968778695a4b3c2d1e0f");
        var p = Problem([new Em3dSolid("lid", "Gold", Em3dRole.Conductor, HandBuilt.Box(0, 0, 0, 1000, 1000, 500, "a1b2c3d4e5f60718293a4b5c6d7e8f90"), 0),
                         new Em3dSolid("slab", "Alumina", Em3dRole.Dielectric, slab, 1)])
                with { FaceBoundaries = [new("slab", "bore:side", Em3dFaceBoundaryKind.Pec)] };
        Assert.Empty(p.Validate());

        var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
        var openEms = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.Equal("The boundary on 'slab', face 'bore:side', is on a curved face, which openEMS cannot state as a sheet. " +
                     "Palace can; or put the boundary on a planar face.", openEms.Refusal);

        var palace = GmshGeoWriter.Write(p, PalaceSettings.Resolve(null));
        Assert.True(palace.Ok, palace.Refusal);
        Assert.Equal(1, palace.Groups.Single(g => g.Name == "slab/bore:side").Expected);
    }

    // ── 9. agreement, both solvers (Benchmark) ──────────────────────────────────────────────────

    /// <summary>
    /// The showcase-lite case: a copper pin through a 500 µm plate whose bore (r = 600 µm, a 50 Ω air coax with the
    /// 260 µm pin) has both rims filleted at 100 µm, in a PEC box; a lumped port from each end of the pin to the box. Run
    /// through the one door a Simulate uses, with both solvers; the fidelity rows are printed beside the comparison and
    /// recorded in src/Design/RESOLVED.md. Nothing is asserted about the agreement: this is the measurement.
    /// </summary>
    [KernelBothFact]
    [Trait("Category", "Benchmark")]
    public void Gate9_AFilletedBoreLaunch_InBothSolvers_PrintsTheFidelityRowsBesideTheDifference()
    {
        string ws = Dir("ws");
        CircuitRF.Design.Layout.TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new CircuitRF.Design.Layout.Technology
        {
            Name = "tech",
            Materials = [new CircuitRF.Design.Layout.TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        CircuitRF.Design.Workspace.WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"),
            new CircuitRF.Design.Workspace.CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Launch", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Launch.c3d");
        File.WriteAllText(path, LaunchDocument);
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, path);

        var result = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), Path.Combine(ws, "results"));
        foreach (string w in result.Warnings ?? []) output.WriteLine("warning: " + w);
        foreach (string n in result.Notes ?? []) output.WriteLine("note: " + n);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
    }

    private const string LaunchDocument = """
{
  "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 0,
  "Objects": [
    { "$type": "Fillet", "Name": "plate", "Radius": 100000, "Edges": ["bore:side|zmax", "bore:side|zmin"],
      "Target": { "$type": "Boolean", "Op": "Subtract",
        "Blank": { "$type": "Box", "Material": "Copper", "Min": [-1500000, -1500000, 0], "Size": [3000000, 3000000, 500000] },
        "Tools": [ { "$type": "Cylinder", "Name": "bore", "Base": [0, 0, -10000], "Axis": "Z", "Length": 520000, "Radius": 600000 } ] } },
    { "$type": "Cylinder", "Name": "pin", "Material": "Copper", "Base": [0, 0, -300000], "Axis": "Z", "Length": 1100000, "Radius": 260000 }
  ],
  "Ports": [
    { "Number": 1, "Name": "P1", "Kind": "Lumped", "Plane": "XZ", "Offset": 0, "Rect": { "Min": [-50000, -500000], "Size": [100000, 200000] }, "Z0": "50" },
    { "Number": 2, "Name": "P2", "Kind": "Lumped", "Plane": "XZ", "Offset": 0, "Rect": { "Min": [-50000, 800000], "Size": [100000, 200000] }, "Z0": "50" }
  ],
  "Setups": [
    { "FormatVersion": 1, "Name": "Launch",
      "Frequency": { "StartExpr": "1", "StopExpr": "20", "StepExpr": "", "NumPoints": 20, "Mode": "PointCount", "Kind": "Linear",
                     "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "Hz" },
      "Solver3D": "Both",
      "AirBox": { "XMin": { "PaddingUm": 0, "Boundary": "Pec" }, "XMax": { "PaddingUm": 0, "Boundary": "Pec" },
                  "YMin": { "PaddingUm": 0, "Boundary": "Pec" }, "YMax": { "PaddingUm": 0, "Boundary": "Pec" },
                  "ZMin": { "PaddingUm": 200, "Boundary": "Pec" }, "ZMax": { "PaddingUm": 200, "Boundary": "Pec" } },
      "Palace": { "Quality": "Draft" } }
  ]
}
""";

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static Em3dProblem Problem(IReadOnlyList<Em3dSolid> solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        var box = new Em3dAirBox(new Point3(-500 * Um, -500 * Um, -300 * Um), new Point3(1500 * Um, 1500 * Um, 900 * Um),
                                 new Em3dFaces(a, a, a, a, Em3dBoundaryKind.Pec, a));
        // A lumped port from the PEC floor up to the lid's bottom, clear of the bore.
        var port = new Em3dPort(1, "port/1", "lid", "airbox/zmin", new Point3(400 * Um, 100 * Um, -300 * Um), new Point3(600 * Um, 100 * Um, 0),
                                new Point3(0, 0, 1), 50, new Em3dReferencePlane(new Point3(0, 0, 0), new Point3(0, 0, 1), 0));
        return new Em3dProblem(solids, [], [Gold, Alumina], [port], box, new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 25);
    }

    /// <summary>The worker's box minus a through bore at its centre, lowered as the elaboration lowers it.</summary>
    private static Em3dShapeSolid WorkerBoxMinusBore(GeometryKernel kernel, string name, long dx, long dy, long dz, long bore)
    {
        const long U = 1000;
        var tree = GeometryKernelTree.From(new C3dBoolean
        {
            Name = name, Op = C3dBooleanOp.Subtract,
            Blank = new C3dBox { Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(dx * U, dy * U, dz * U) },
            Tools = [new C3dCylinder { Name = "bore", Base = new C3dPoint3(dx / 2 * U, dy / 2 * U, -10 * U), Length = (dz + 20) * U, Radius = bore * U }],
        }, 1000);
        return C3dElaborator.ShapeSolid(kernel, tree, name).Solid;
    }

    private static double Smallest(IReadOnlyList<double> lines, (double Lo, double Hi) umRange)
    {
        double lo = umRange.Lo * Um, hi = umRange.Hi * Um, best = double.PositiveInfinity, tol = 1e-12;
        for (int i = 0; i + 1 < lines.Count; i++)
            if (lines[i] < hi + tol && lines[i + 1] > lo - tol) best = Math.Min(best, lines[i + 1] - lines[i]);
        return best;
    }

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Kernel solids built by hand, as the worker would report them: faces, tight boxes, outward triangles.</summary>
    private static class HandBuilt
    {
        private static Point3 P(double x, double y, double z) => new(x * Um, y * Um, z * Um);

        private sealed class Builder
        {
            public readonly List<Point3> V = [];
            public readonly List<Em3dTriangle> T = [];
            public readonly List<Em3dShapeFace> F = [];

            public void Face(string name, string kind, double radiusUm, params Point3[][] tris)
            {
                int first = T.Count;
                var pts = tris.SelectMany(t => t).ToList();
                foreach (var tri in tris)
                {
                    int b = V.Count;
                    V.AddRange(tri);
                    T.Add(new Em3dTriangle(b, b + 1, b + 2, "", F.Count));
                }
                F.Add(new Em3dShapeFace(name, kind, (pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z), pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)),
                                        radiusUm * Um, first, tris.Length));
            }

            public void Quad(string name, Point3 a, Point3 b, Point3 c, Point3 d) => Face(name, "plane", 0, [a, b, c], [a, c, d]);

            public Em3dShapeSolid Solid(string hash) =>
                new(Encoding.ASCII.GetBytes("B-rep " + hash), hash, new Em3dTriangleMesh(V, T), F, []);
        }

        private static readonly string[] Names = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        private static readonly int[][] Quads = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];

        public static Em3dShapeSolid Box(double x0, double y0, double z0, double x1, double y1, double z1, string hash)
        {
            var b = new Builder();
            Point3 V(int bits) => P((bits & 1) == 0 ? x0 : x1, (bits & 2) == 0 ? y0 : y1, (bits & 4) == 0 ? z0 : z1);
            for (int k = 0; k < 6; k++) b.Quad(Names[k], V(Quads[k][0]), V(Quads[k][1]), V(Quads[k][2]), V(Quads[k][3]));
            return b.Solid(hash);
        }

        /// <summary>A slab at x ∈ [−400, −100] µm whose top is split in two (<c>zmax#1</c>, <c>zmax#2</c>), with a bore's wall.</summary>
        public static Em3dShapeSolid SplitSlab(string hash)
        {
            const double x0 = -400, x1 = -100, y0 = 0, y1 = 1000, z0 = 0, z1 = 300, xm = -250;
            var b = new Builder();
            Point3 V(int bits) => P((bits & 1) == 0 ? x0 : x1, (bits & 2) == 0 ? y0 : y1, (bits & 4) == 0 ? z0 : z1);
            for (int k = 0; k < 5; k++) b.Quad(Names[k], V(Quads[k][0]), V(Quads[k][1]), V(Quads[k][2]), V(Quads[k][3]));
            b.Quad("zmax#1", P(x0, y0, z1), P(xm, y0, z1), P(xm, y1, z1), P(x0, y1, z1));
            b.Quad("zmax#2", P(xm, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(xm, y1, z1));
            var wall = new List<Point3[]>();
            for (int k = 0; k < 16; k++)
            {
                double a = 2 * Math.PI * k / 16, c = 2 * Math.PI * (k + 1) / 16;
                Point3 a0 = P(xm + 40 * Math.Cos(a), 500 + 40 * Math.Sin(a), z0), c0 = P(xm + 40 * Math.Cos(c), 500 + 40 * Math.Sin(c), z0);
                wall.Add([a0, a0 with { Z = z1 * Um }, c0 with { Z = z1 * Um }]);
                wall.Add([a0, c0 with { Z = z1 * Um }, c0]);
            }
            b.Face("bore:side", "cylinder", 40, [.. wall]);
            return b.Solid(hash);
        }
    }
}

/// <summary>A <see cref="KernelFactAttribute"/> that also needs a validated Gmsh.</summary>
public sealed class KernelGmshFactAttribute : FactAttribute
{
    public KernelGmshFactAttribute()
    {
        if (KernelForTests.SkipReason is { } k) Skip = k;
        else if (SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)) is { Proceeds: false } r)
            Skip = $"needs a validated Gmsh: {r.Refusal}";
    }
}

/// <summary>A <see cref="KernelFactAttribute"/> that also needs Palace, Gmsh and openEMS (a Both run).</summary>
public sealed class KernelBothFactAttribute : FactAttribute
{
    public KernelBothFactAttribute()
    {
        if (KernelForTests.SkipReason is { } k) Skip = k;
        else if (SolverDiscovery.ReadinessFor(Em3dSolver.Both).FirstOrDefault(r => !r.Proceeds) is { } r)
            Skip = $"needs Palace, Gmsh and openEMS: {r.Refusal}";
    }
}

/// <summary>A <see cref="KernelFactAttribute"/> that also needs a validated openEMS.</summary>
public sealed class KernelOpenEmsFactAttribute : FactAttribute
{
    public KernelOpenEmsFactAttribute()
    {
        if (KernelForTests.SkipReason is { } k) Skip = k;
        else if (SolverDiscovery.ReadinessFor(Em3dSolver.OpenEms).FirstOrDefault(r => !r.Proceeds) is { } r)
            Skip = $"needs a validated openEMS: {r.Refusal}";
    }
}
