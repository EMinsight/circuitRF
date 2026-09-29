// brief-em3d-82 §6 — E and H on a picked face or sheet (pixels were not seen; every gate reads the geometry and values):
//
//   1  a painted wall of the PEC cavity is exactly its region's boundary triangles on that wall, values and all
//   2  the physics on the cavity: E is ~0 on the walls it is tangential to, and not on a y-wall (E normal to it)
//   3  a sheet's two sides: a synthetic tet pair straddling one sheet triangle gives opposite E_n and the same |E_t|
//   4  a conductor face (a void), volume quantity: its neighbouring regions' values
//   5  openEMS sampling: a linear E on a synthetic dump reads the analytic field half a cell off the metal
//   6  a phase step with painted faces present builds no geometry (the editor, the real view model)
//   7  the openEMS writer: one E and one H box per saved frequency with SaveH, E alone without it, neither with []

using System.Numerics;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class FieldFaceTests(ITestOutputHelper output) : IDisposable
{
    private const double A = 22860e-6, B = 10160e-6, D = 25000e-6;         // the cavity fixture, metres
    private static readonly (double, double, double) Origin = (0, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em82-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static string Cavity => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");

    /// <summary>The cavity's wall normal to <paramref name="axis"/> at its low (0) or high (1) end: two triangles, and its
    /// outward normal — what the scene hands the painter for a box face.</summary>
    private static FieldFaceTarget Wall(string label, int axis, int end)
    {
        double[] size = [A, B, D];
        Vector3 P(double u, double v)
        {
            var p = new double[3];
            p[axis] = end * size[axis];
            p[(axis + 1) % 3] = u * size[(axis + 1) % 3];
            p[(axis + 2) % 3] = v * size[(axis + 2) % 3];
            return new Vector3((float)p[0], (float)p[1], (float)p[2]);
        }
        var n = Vector3.Zero;
        n[axis] = end == 0 ? -1 : 1;
        double tol = 0.01 * Math.Sqrt(A * A + B * B + D * D);
        return new FieldFaceTarget("cavity", label, FieldFaceRole.Solid, [(P(0, 0), P(1, 0), P(1, 1)), (P(0, 0), P(1, 1), P(0, 1))], n, tol);
    }

    private static (FieldStep Vol, FieldArray E, IReadOnlyList<FieldGroup> Groups) Te101()
    {
        var run = FieldRun.OpenPalace(Cavity)!;
        var vol = FieldStep.Open(run.Solutions.First(s => s.Index == 0).VolumePvtu!, run.ToMetres);
        return (vol, vol.Load("E")!, FieldGroups.Read(Cavity));
    }

    // ── 1. face = region restricted ─────────────────────────────────────────────────────────────

    /// <summary>The xmin wall painted: exactly the cavity region's boundary triangles within the tolerance of that wall and
    /// parallel to it — the same triangles in the same order, with identical values.</summary>
    [Fact]
    public void Gate1_APaintedWall_IsTheRegionBoundaryOnThatWall()
    {
        var (vol, e, groups) = Te101();
        var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
        var wall = Wall("xmin", 0, 0);
        var paint = new FieldFacePainter(q, vol, null, groups, Origin).Paint(wall, out string? why);
        Assert.Null(why);

        var whole = FieldSurfaces.RegionBoundary(vol.Mesh, e, new HashSet<int> { 1 }, Origin);
        var xyz = new List<double>();
        var values = new List<double>();
        for (int t = 0; t < whole.TriangleCount; t++)
        {
            var p = whole.Xyz.AsSpan(9 * t, 9);
            double cx = (p[0] + p[3] + p[6]) / 3;
            double ux = p[3] - p[0], uy = p[4] - p[1], uz = p[5] - p[2], vx = p[6] - p[0], vy = p[7] - p[1], vz = p[8] - p[2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (Math.Abs(cx) > wall.Tol || Math.Abs(nx) < 0.9 * Math.Sqrt(nx * nx + ny * ny + nz * nz)) continue;
            xyz.AddRange(p.ToArray());
            values.AddRange(whole.Values.AsSpan(3 * t * whole.Channels, 3 * whole.Channels).ToArray());
        }
        output.WriteLine($"{paint!.Surface.TriangleCount} of the region's {whole.TriangleCount} boundary triangles lie on xmin");
        Assert.True(paint.Surface.TriangleCount > 0);
        Assert.Equal(xyz, paint.Surface.Xyz);
        Assert.Equal(values, paint.Surface.Values);
        Assert.NotNull(paint.Surface.Recipe);                                   // it revalues, as temperature's faces do
        Assert.Equal(new Vector3(-1, 0, 0), paint.Toward);                      // drawn in front of its own face
    }

    // ── 2. the physics on the cavity ────────────────────────────────────────────────────────────

    /// <summary>TE101's E is ŷ·sin(πx/a)·sin(πz/d): on the four walls it is tangential to, the painted |E| is below brief 29's
    /// display tolerance of the centre's; on a y-wall E is NORMAL to the wall and is not small.</summary>
    [Fact]
    public void Gate2_EVanishesOnTheWallsItIsTangentialTo_AndNotOnAYWall()
    {
        var (vol, e, groups) = Te101();
        var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
        Span<double> ch = stackalloc double[6];
        Assert.True(new FieldSampler(vol.Mesh).Sample(e, A / 2 / 1e-6, B / 2 / 1e-6, D / 2 / 1e-6, ch));
        double centre = q.Evaluate(ch);
        var painter = new FieldFacePainter(q, vol, null, groups, Origin);
        double Largest(FieldFaceTarget wall)
        {
            var s = painter.Paint(wall, out string? why)!.Surface;
            Assert.Null(why);
            double m = 0;
            for (int v = 0; v < s.VertexCount; v++) m = Math.Max(m, q.Evaluate(s.Values.AsSpan(v * s.Channels, s.Channels)));
            return m;
        }
        foreach (var (label, axis, end) in new[] { ("xmin", 0, 0), ("xmax", 0, 1), ("zmin", 2, 0), ("zmax", 2, 1) })
        {
            double wall = Largest(Wall(label, axis, end));
            output.WriteLine($"{label}: largest |E| {wall / centre:E2} of the centre's");
            Assert.True(wall < FieldTests.DrawnTolerance * centre, $"{label} {wall / centre:E2}");
        }
        double yWall = Largest(Wall("ymin", 1, 0));
        output.WriteLine($"ymin: largest |E| {yWall / centre:F3} of the centre's");
        Assert.True(yWall > 0.5 * centre, $"ymin {yWall / centre:F3}");
    }

    // ── 3. a sheet's two sides ──────────────────────────────────────────────────────────────────

    /// <summary>Two tetrahedra of one region sharing a sheet triangle (z = 0), each with its own nodes as Palace writes them,
    /// E = (0.3, 0.4, +2) above and (0.3, 0.4, −2) below: Top and Bottom each paint the triangle once, with opposite E_n and
    /// the same tangential E.</summary>
    [Fact]
    public void Gate3_ASheetsTwoSides_GiveOppositeNormalE_AndTheSameTangentialE()
    {
        float[] points = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0.2f, 0.2f, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0.2f, 0.2f, -1];
        var mesh = new FieldMesh
        {
            Shape = FieldCellShape.Tetrahedron, Order = 1, NodesPerCell = 4, Points = points, Cells = [0, 1, 2, 3, 4, 5, 6, 7],
            Attribute = [1, 1], ToMetres = 1,
        };
        var e = new FieldArray
        {
            Info = new FieldArrayInfo("E", 3, false, false),
            Re = [.. Enumerable.Range(0, 8).SelectMany(n => new[] { 0.3f, 0.4f, n < 4 ? 2f : -2f })],
        };
        var vol = Step(mesh, e);
        var groups = new[] { new FieldGroup("box", 1, 3, "Volume"), new FieldGroup("sheet", 2, 2, "Sheet") };
        var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
        var painter = new FieldFacePainter(q, vol, null, groups, Origin);
        (Vector3, Vector3, Vector3)[] tri = [(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0))];
        FieldSurface Side(int side)
        {
            var p = painter.Paint(new FieldFaceTarget("sheet", "'sheet/surface'", FieldFaceRole.Sheet, tri, Vector3.UnitZ, 1e-6, side), out string? why);
            Assert.Null(why);
            Assert.Equal(side * Vector3.UnitZ, p!.Toward);
            return p.Surface;
        }
        var top = Side(1);
        var bottom = Side(-1);
        Assert.Equal(1, top.TriangleCount);
        Assert.Equal(1, bottom.TriangleCount);
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal(2, top.Values[3 * v + 2]);
            Assert.Equal(-2, bottom.Values[3 * v + 2]);
            Assert.Equal((top.Values[3 * v], top.Values[3 * v + 1]), (bottom.Values[3 * v], bottom.Values[3 * v + 1]));
        }
        // A region's boundary never returns it: both tetrahedra are the region's.
        Assert.Equal(0, FieldFaces.OnFace(FieldSurfaces.RegionBoundary(mesh, e, new HashSet<int> { 1 }, Origin), [.. tri], 1e-6).TriangleCount);
    }

    // ── 4. a conductor face, volume quantity ────────────────────────────────────────────────────

    /// <summary>A conductor's face (z = 0) with nothing below it — a void, as Palace meshes one — and two regions above it,
    /// one tetrahedron each with E = 5 and E = 7 (x-directed): the painted face is the two triangles, each with its own
    /// region's value, and nothing of either region's other faces.</summary>
    [Fact]
    public void Gate4_AConductorFace_ReadsItsNeighbouringRegions()
    {
        float[] points = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0.2f, 0.2f, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0.8f, 0.8f, 1];
        var mesh = new FieldMesh
        {
            Shape = FieldCellShape.Tetrahedron, Order = 1, NodesPerCell = 4, Points = points, Cells = [0, 1, 2, 3, 4, 5, 6, 7],
            Attribute = [1, 2], ToMetres = 1,
        };
        var e = new FieldArray
        {
            Info = new FieldArrayInfo("E", 3, false, false),
            Re = [.. Enumerable.Range(0, 8).SelectMany(n => new[] { n < 4 ? 5f : 7f, 0f, 0f })],
        };
        var groups = new[] { new FieldGroup("sub", 1, 3, "Volume"), new FieldGroup("air", 2, 3, "Volume"), new FieldGroup("trace", 3, 2, "Conductor") };
        var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
        (Vector3, Vector3, Vector3)[] face = [(new(0, 0, 0), new(1, 0, 0), new(1, 1, 0)), (new(0, 0, 0), new(1, 1, 0), new(0, 1, 0))];
        var paint = new FieldFacePainter(q, Step(mesh, e), null, groups, Origin)
            .Paint(new FieldFaceTarget("trace", "'trace/ztop'", FieldFaceRole.Conductor, face, Vector3.UnitZ, 0.01 * Math.Sqrt(2)), out string? why);
        Assert.Null(why);
        var s = paint!.Surface;
        Assert.Equal(2, s.TriangleCount);
        var byTriangle = Enumerable.Range(0, 2).Select(t => (Z: s.Xyz.Skip(9 * t).Take(9).Where((_, i) => i % 3 == 2).Max(),
                                                             E: s.Values.Skip(9 * t).Take(9).Where((_, i) => i % 3 == 0).Distinct().Single()))
                                       .OrderBy(x => x.E).ToList();
        Assert.Equal([(0.0, 5.0), (0.0, 7.0)], byTriangle);
        Assert.Equal(Vector3.UnitZ, paint.ReadToward);                          // read on the regions' side: outward from the metal
    }

    // ── 5. openEMS: sampled half a cell off the metal ───────────────────────────────────────────

    /// <summary>
    /// A synthetic openEMS dump — the simulation grid's lines at every integer, so the dump's nodes are the cell centres
    /// (n + ½) — with a linear E, and a metal face at z = 2, a simulation line midway between the dump nodes 1.5 and 2.5.
    /// Painted from above, every vertex reads E at z = 2.5 (the first dump node on that side, not an average across the
    /// metal); from below, at z = 1.5. The face (3 × 3) is split to the grid's spacing (1): no edge longer than it.
    /// </summary>
    [Fact]
    public void Gate5_OpenEmsSampling_ReadsTheAnalyticFieldHalfACellOffTheMetal()
    {
        double[] x = [.. Enumerable.Range(0, 10).Select(i => i + 0.5)], y = x, z = [0.5, 1.5, 2.5, 3.5];
        static double[] Field(double px, double py, double pz) => [1 + 2 * px - py + 0.5 * pz, 3 - px + 4 * py + pz, 2 + 0.25 * px - 0.5 * py + 3 * pz];
        var values = new float[3 * x.Length * y.Length * z.Length];
        for (int k = 0, p = 0; k < z.Length; k++)
            for (int j = 0; j < y.Length; j++)
                for (int i = 0; i < x.Length; i++, p += 3)
                {
                    var f = Field(x[i], y[j], z[k]);
                    values[p] = (float)f[0]; values[p + 1] = (float)f[1]; values[p + 2] = (float)f[2];
                }
        var grid = new VtrField(x, y, z, "E-Field", 3, values);
        var mesh = VtrReader.Mesh(grid);
        var e = new FieldArray { Info = new FieldArrayInfo("E", 3, false, false), Re = values };
        var sampler = new FieldSampler(mesh);
        var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
        (Vector3, Vector3, Vector3)[] face = [(new(1, 1, 2), new(4, 1, 2), new(4, 4, 2)), (new(1, 1, 2), new(4, 4, 2), new(1, 4, 2))];
        var painter = new FieldFacePainter(q, Step(mesh, e), null, [], Origin, sampler);
        foreach (var (side, atZ) in new[] { (1, 2.5), (-1, 1.5) })
        {
            var paint = painter.Paint(new FieldFaceTarget("trace", "'trace/ztop'", FieldFaceRole.Sheet, face, Vector3.UnitZ, 0.03, side), out string? why);
            Assert.Null(why);
            var s = paint!.Surface;
            Assert.True(paint.Sampled);
            double worst = 0, longest = 0;
            for (int v = 0; v < s.VertexCount; v++)
            {
                Assert.Equal(2, s.Xyz[3 * v + 2]);                              // drawn ON the face
                var f = Field(s.Xyz[3 * v], s.Xyz[3 * v + 1], atZ);
                for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(s.Values[3 * v + c] - f[c]) / Math.Max(1, Math.Abs(f[c])));
            }
            for (int t = 0; t < s.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = 3 * (3 * t + k), b = 3 * (3 * t + (k + 1) % 3);
                    longest = Math.Max(longest, Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (s.Xyz[a + c] - s.Xyz[b + c]) * (s.Xyz[a + c] - s.Xyz[b + c]))));
                }
            output.WriteLine($"side {side}: {s.TriangleCount} triangles, longest edge {longest:G4}, worst relative error {worst:E2}");
            Assert.True(worst < 2e-6, $"{worst:E2}");                           // Float32 storage and positions: a few ULPs
            Assert.True(longest <= 1 + 1e-9 && s.TriangleCount > 2, $"{longest}");
        }
    }

    private static FieldStep Step(FieldMesh mesh, FieldArray e) => FieldStep.InMemory(mesh, e);

    // ── 6. a phase step builds no geometry ──────────────────────────────────────────────────────

    /// <summary>The cavity fixture as the editor's own Palace run; its xmin wall painted with the animated E through the
    /// context menu's call: a hundred phase steps later, nothing has been cut or gathered again and the geometry is the one
    /// drawn (FieldGeometryBuilds and the version unchanged).</summary>
    [Fact]
    public void Gate6_APhaseStep_WithPaintedFaces_BuildsNoGeometry()
    {
        var vm = OpenCavity();
        var v = vm.Viewer;
        Assert.Contains("no EM fields", vm.PlotFieldRefusal());
        var run = vm.ActiveRunSetup!;
        string dir = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), run, Em3dSolver.Palace);
        CopyDirectory(Cavity, dir);
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);   // what the run service keeps (brief-em3d-87)
        vm.RunFinished();
        Until(() => v.FieldsAvailable && v.FieldSolutions.Count > 0, "the run's fields were never read");
        Assert.Null(vm.PlotFieldRefusal());

        var cavity = v.Scene.Objects.Single(o => o.Name == "cavity");
        int xmin = Enumerable.Range(0, cavity.FaceNames.Count).Single(i => cavity.FaceName(i) == "xmin");
        Assert.Null(vm.PlotFieldOnFace(cavity.Id, xmin));
        Assert.Equal([new PaintedFieldFace("cavity", xmin)], v.PaintedFieldFaces);
        Until(() => v.FieldQuantities.Count > 0 && v.FieldGeometry.Vertices.Length > 0, "the face was never painted");
        v.SelectedFieldQuantity = v.FieldQuantities.First(q => q.Array.Name == "E" && q.Animated);
        Until(() => v.FieldText.StartsWith(v.SelectedFieldQuantity.Label, StringComparison.Ordinal), "the animated field was never drawn");
        output.WriteLine(v.FieldText);
        Assert.DoesNotContain("'cavity/xmin'", v.FieldText);                    // no refusal for the face
        long builds = v.FieldGeometryBuilds, version = v.FieldGeometry.Version;
        int vertices = v.FieldGeometry.Vertices.Length;
        Assert.True(vertices > 0);

        for (int i = 1; i <= 100; i++)
        {
            v.FieldPhaseDegrees = 3.6 * i;
            while (_posted.TryDequeue(out var a)) a();
        }
        Thread.Sleep(50);
        while (_posted.TryDequeue(out var a)) a();
        Assert.Equal(builds, v.FieldGeometryBuilds);
        Assert.Equal(version, v.FieldGeometry.Version);

        // Painting it again takes it off: one build, and the face is gone.
        Assert.Null(vm.PlotFieldOnFace(cavity.Id, xmin));
        Assert.Empty(v.PaintedFieldFaces);
        Until(() => v.FieldGeometryBuilds == builds + 1, "taking the face off was never drawn");
    }

    private C3dEditorViewModel OpenCavity()
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        var none = new EmAirBoxFace(0, null);
        var setup = new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolver.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");
        Assert.Equal("modes", vm.ActiveSetupName);
        return vm;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    // ── 7. the openEMS writer ───────────────────────────────────────────────────────────────────

    /// <summary>With <c>SaveH</c>, one E box (DumpType 10) and one H box (DumpType 11) per saved frequency, same mode, file
    /// type and box; without it, the E boxes alone (the opt-in); with <c>SaveFieldsGHz: []</c>, neither. And the .cem field
    /// round-trips onto the run settings.</summary>
    [Fact]
    public void Gate7_TheWriter_DumpsHBesideE_OnlyWhenAsked()
    {
        var problem = OpenEmsBackendTests.WireProblem();
        var grid = FdtdGrid.Build(problem, OpenEmsGridSettings.Default, long.MaxValue);
        var fMin = problem.Frequency.StartHz;
        var fMax = problem.Frequency.StopHz;
        double[] ghz = [fMin / 1e9, (fMin + fMax) / 2e9];
        List<(string Name, string Type, string Mode, string File, string Box)> Dumps(OpenEmsRunSettings run)
        {
            var low = CsxcadWriter.Write(problem, grid, OpenEmsGridSettings.Default, run);
            Assert.True(low.Ok, low.Refusal);
            return [.. XDocument.Parse(low.Model!).Descendants("DumpBox").Select(d => (d.Attribute("Name")!.Value, d.Attribute("DumpType")!.Value,
                d.Attribute("DumpMode")!.Value, d.Attribute("FileType")!.Value, d.Element("Primitives")!.ToString() + d.Element("FD_Samples")!.Value))];
        }
        var withH = Dumps(OpenEmsRunSettings.Default with { SaveFieldsGHz = ghz, SaveH = true });
        Assert.Equal(["efield1", "hfield1", "efield2", "hfield2"], withH.Select(d => d.Name));
        Assert.Equal(["10", "11", "10", "11"], withH.Select(d => d.Type));
        Assert.All(withH, d => Assert.Equal(("2", "0"), (d.Mode, d.File)));
        Assert.Equal(withH[0].Box, withH[1].Box);                              // the same box at the same frequency
        Assert.Equal(withH[2].Box, withH[3].Box);

        var eOnly = Dumps(OpenEmsRunSettings.Default with { SaveFieldsGHz = ghz });
        Assert.Equal(["efield1", "efield2"], eOnly.Select(d => d.Name));
        Assert.Empty(Dumps(OpenEmsRunSettings.Default with { SaveFieldsGHz = [], SaveH = true }));

        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(new EmSetup { OpenEms = new CemOpenEms { SaveH = true } }));
        Assert.True(CemOpenEms.ResolveRun(back.OpenEms).SaveH);
        Assert.False(CemOpenEms.ResolveRun(null).SaveH);
        Assert.Equal("hfield1_f=2.5e9_abs.vtr", Path.GetFileName(FieldRun.HFile(Path.Combine("p1", "efield1_f=2.5e9_abs.vtr"))));
    }
}
