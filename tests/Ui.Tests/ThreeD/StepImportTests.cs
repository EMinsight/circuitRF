// ================================================================
//  StepImportTests.cs — the gate for brief-em3d-68: STEP import and Reload from Source, in process.
//  Every fixture is a STEP file written by circuitRF's own worker from boxes a test builds — or that text with one
//  entity edited (the unit and open-shell cases) — so no third-party STEP file enters the repository. The gates that
//  need OpenCASCADE are [KernelFact] and skip, naming what to build, where the worker is not built.
// ================================================================

using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class StepImportTests : IDisposable
{
    private const long Um = 1000;                        // DBU per µm at the default 1000 DBU/µm
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step68-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. one object per part, at its file position ────────────────────────────────────────

    [KernelFact]
    public void Gate1_ThreePartsTwoOfThemOneInstancedPart_AreThreeObjects_AtTheirFilePositions()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var pin = Tree(Box("pin", 0, 0, 0, 100, 100, 1000));
        string src = WriteStep(ws, "connector.step", kernel.Export(
            [new(pin, "pin") { Location = Shift(1000) }, new(pin, "pin") { Location = Shift(-1000) },
             new(Tree(Box("shell", -500, -500, 0, 1000, 1000, 200)), "shell")], "step", "mm", assembly: true));
        var (doc, path) = NewC3d(ws, "Conn");

        var plan = StepImport.Read(src, kernel, Tech(ws), doc);
        Assert.Equal(["1/1", "1/2", "1/3"], plan.Parts.Select(p => p.Path));
        Assert.Equal(["pin", "pin_2", "shell"], plan.Parts.Select(p => p.Name));
        foreach (var p in plan.Parts) p.Material = "Copper";
        var result = StepImport.Apply(plan, doc, path);
        C3dPersistence.SaveToFile(path, doc);

        Assert.Equal(3, result.Objects.Count);
        Assert.All(result.Objects, o => Assert.Equal(C3dTransform.Identity, o.Placement.ToTransform()));
        var e = new C3dElaborator(null, kernel).Elaborate(C3dPersistence.LoadFromFile(path), path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var a = Bounds(e, "pin");
        var b = Bounds(e, "pin_2");
        const double tol = 1e-12;                         // metres: far below the kernel's 1e-7 µm tolerance × 1e-6
        Assert.Equal(1000e-6, a.X0, tol);
        Assert.Equal(-1000e-6, b.X0, tol);
        Assert.Equal(a.X1 - a.X0, b.X1 - b.X0, tol);
        Assert.Equal(a.Z1 - a.Z0, b.Z1 - b.Z0, tol);
        Assert.Equal(1000e-6, a.Z1, tol);
    }

    // ── 2. units, exactly; an unresolvable one refused by name ──────────────────────────────

    [KernelFact]
    public void Gate2_TheSameBoxInMillimetresAndInches_ImportsToTheSameDbuExtents_AndAnUnknownUnitIsRefusedByName()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var box = Tree(Box("blk", 0, 0, 0, 500, 400, 300));
        long[] Extents(string units)
        {
            string src = WriteStep(ws, $"blk-{units}.step", kernel.Export([new(box, "blk")], "step", units));
            var (doc, path) = NewC3d(ws, "U" + units);
            var plan = StepImport.Read(src, kernel, Tech(ws), doc);
            plan.Parts[0].Material = "Alumina";
            StepImport.Apply(plan, doc, path);
            var s = Bounds(new C3dElaborator(null, kernel).Elaborate(doc, path, null), "blk");
            return [.. new[] { s.X0, s.Y0, s.Z0, s.X1, s.Y1, s.Z1 }.Select(m => (long)Math.Round(m * 1e6 * Um))];
        }
        Assert.Equal(Extents("mm"), Extents("in"));
        Assert.Equal([0L, 0, 0, 500 * Um, 400 * Um, 300 * Um], Extents("mm"));

        // The inch's conversion factor re-pointed at the inch itself: a unit the reader cannot resolve to a length.
        string inch = Encoding.ASCII.GetString(kernel.Export([new(box, "blk")], "step", "in"));
        string unitId = Regex.Match(inch, @"#(\d+) = \( CONVERSION_BASED_UNIT\('INCH'").Groups[1].Value;
        string bad = Regex.Replace(inch, @"LENGTH_MEASURE_WITH_UNIT\(25\.4,#\d+\)", $"LENGTH_MEASURE_WITH_UNIT(25.4,#{unitId})");
        string badPath = WriteStep(ws, "bad-unit.step", Encoding.ASCII.GetBytes(bad));
        var (d2, _) = NewC3d(ws, "Bad");
        var ex = Assert.Throws<GeometryKernelException>(() => StepImport.Read(badPath, kernel, Tech(ws), d2));
        Assert.Contains("'inch'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not guess", ex.Message, StringComparison.Ordinal);
    }

    // ── 3. materials: by name, by colour, unmatched; no technology says why ────────────────

    [KernelFact]
    public void Gate3_EachPartLandsWhereTheMatchRuleSays_WithTheReason()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string src = WriteStep(ws, "mix.step", kernel.Export(
            [new(Tree(Box("copper", 0, 0, 0, 10, 10, 10)), "copper", [0, 0, 1]),
             new(Tree(Box("body", 20, 0, 0, 10, 10, 10)), "body", [1, 0, 0]),
             new(Tree(Box("cap", 40, 0, 0, 10, 10, 10)), "cap", [0, 1, 0])], "step", "mm"));
        var (doc, _) = NewC3d(ws, "Mix");

        var plan = StepImport.Read(src, kernel, Tech(ws), doc);
        Assert.Equal([("Copper", StepMatch.ByName), ("Brass", StepMatch.ByColour), (null, StepMatch.Unmatched)],
                     plan.Parts.Select(p => (p.Material, p.Match)));
        Assert.Null(plan.NoMaterials);

        var none = StepImport.Read(src, kernel, null, doc);
        Assert.All(none.Parts, p => Assert.Equal((null, StepMatch.Unmatched), (p.Material, p.Match)));
        Assert.Equal(StepImport.NoMaterialsReason, none.NoMaterials);

        // Map all of this colour: one gesture for every part of the row's colour, and none of the others.
        var red = plan.Parts[1];
        red.Material = "Copper";
        Assert.Equal(1, StepImport.MapColour(plan, red, "Copper"));
        Assert.Equal(StepMatch.Chosen, red.Match);
        Assert.Equal(StepMatch.ByName, plan.Parts[0].Match);
    }

    // ── 4. a part that is not a closed solid is listed, not imported, and counted ───────────

    [KernelFact]
    public void Gate4_AnOpenShellPart_IsListedUncheckedAndCounted_NeverImported()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string text = Encoding.ASCII.GetString(kernel.Export(
            [new(Tree(Box("lid", 0, 0, 0, 10, 10, 10)), "lid"), new(Tree(Box("skin", 20, 0, 0, 10, 10, 10)), "skin")], "step", "mm"));
        // One face out of the second shell: a surface model with no inside.
        var shells = Regex.Matches(text, @"CLOSED_SHELL\('',\(#\d+,");
        string open = text[..shells[1].Index] + Regex.Replace(text[shells[1].Index..], @"^CLOSED_SHELL\('',\(#\d+,", "CLOSED_SHELL('',(");
        string src = WriteStep(ws, "open.step", Encoding.ASCII.GetBytes(open));
        var (doc, path) = NewC3d(ws, "Open");

        var plan = StepImport.Read(src, kernel, Tech(ws), doc);
        var skin = Assert.Single(plan.Parts, p => !p.Closed);
        Assert.False(skin.Import);
        Assert.NotEmpty(skin.Why);
        var result = StepImport.Apply(plan, doc, path);
        Assert.Equal(["lid"], result.Objects.Select(o => o.Name));
        Assert.Contains(result.Notes, n => n.StartsWith("1 part is not a closed solid", StringComparison.Ordinal) && n.Contains(skin.Why, StringComparison.Ordinal));
    }

    // ── 5. a face reference lands on the same geometric face after a save and a reopen ──────

    [KernelFact]
    public void Gate5_ABoundaryOnFaceN_ElaboratesOntoTheSameGeometricFace_AfterSaveAndReopen()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var (doc, path) = Imported(kernel, ws, "Face", Tree(Box("blk", 0, 0, 0, 500, 400, 300)), "Alumina");
        string top = TopFace(kernel, doc, path);
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "blk", Face = top, Kind = Em3dFaceBoundaryKind.Pec });
        C3dPersistence.SaveToFile(path, doc);

        var again = C3dPersistence.LoadFromFile(path);
        var e = new C3dElaborator(null, kernel).Elaborate(again, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Null(C3dProblemAssembly.FaceBoundaries(again, e, [.. e.Materials], EmSetup.DefaultOperatingTempC, out var landed));
        var b = Assert.Single(landed);
        var face = Faces(e, "blk").Single(f => f.Name == b.Face);
        Assert.Equal(300e-6, face.Box.Z0, 1e-12);
        Assert.Equal(300e-6, face.Box.Z1, 1e-12);
    }

    // ── 6. Reload from Source: re-pointed by geometry; a missing face refused; unchanged is a no-op ──

    [KernelFact]
    public void Gate6_Reload_RepointsByFingerprint_RefusesAFaceThatIsGone_AndDoesNothingForTheSameBytes()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var (doc, path) = Imported(kernel, ws, "Rel", Tree(Box("blk", 0, 0, 0, 500, 400, 300)), "Alumina");
        string top = TopFace(kernel, doc, path);
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "blk", Face = top, Kind = Em3dFaceBoundaryKind.Pec });
        string source = StepImport.ResolveSource((C3dStep)doc.Objects[0], path)!;

        // The same bytes: nothing to do.
        Assert.True(StepImport.PlanReload(doc, path, "blk.step", kernel).NoChange);

        // The same block, its faces listed in another order: the boundary follows the geometry to a new number.
        File.WriteAllBytes(source, kernel.Export([new(Tree(ReorderedBlock("blk", 500, 400, 300)), "blk")], "step", "mm"));
        var plan = StepImport.PlanReload(doc, path, "blk.step", kernel);
        Assert.Empty(plan.Refusals);
        var moved = Assert.Single(plan.Repoints);
        Assert.Equal(top, $"face{moved.From}");
        var (copy, created) = StepImport.ApplyReload(plan, doc, path);
        Assert.True(created);
        Assert.Equal("blk_2.step", Path.GetFileName(copy));
        Assert.Equal($"face{moved.To}", doc.FaceBoundaries[0].Face);
        Assert.Equal(StepImport.HashOf(File.ReadAllBytes(source)), ((C3dStep)doc.Objects[0]).Hash);
        var e = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.Equal(300e-6, Faces(e, "blk").Single(f => f.Name == doc.FaceBoundaries[0].Face).Box.Z1, 1e-12);

        // A shorter block: that top face is gone, and the refusal names the boundary and the face.
        File.WriteAllBytes(source, kernel.Export([new(Tree(Box("blk", 0, 0, 0, 500, 400, 200)), "blk")], "step", "mm"));
        var gone = StepImport.PlanReload(doc, path, "blk_2.step", kernel);
        Assert.Empty(gone.Accepted);
        string refusal = Assert.Single(gone.Refusals);
        Assert.Contains($"blk/{doc.FaceBoundaries[0].Face}, which the revised file no longer has", refusal, StringComparison.Ordinal);
    }

    // ── 7. a Step part is an operand like any solid ─────────────────────────────────────────

    [KernelFact]
    public void Gate7_AStepPart_AsABooleanBlankAndAsAFilletTarget_Elaborates()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var (doc, path) = Imported(kernel, ws, "Ops", Tree(Box("shell", 0, 0, 0, 500, 400, 300)), "Brass");
        var part = (C3dStep)doc.Objects[0];
        string edge = kernel.Edges(GeometryKernelTree.From(Resolved(part, path), 1000), 1)[0].Name;

        // The wrapper takes the name and the material is the operand's (R-em3d64-1d, -3b).
        var boolean = new C3dBoolean
        {
            Name = "shell", Op = C3dBooleanOp.Subtract, Blank = Copy(part, name: "", material: "Brass"),
            Tools = [new C3dCylinder { Name = "bore", Base = new C3dPoint3(250 * Um, 200 * Um, -10 * Um), Length = 400 * Um, Radius = 50 * Um }],
        };
        var rounded = new C3dFillet { Name = "rounded", Radius = 20 * Um, Edges = [edge], Target = Copy(part, name: "", material: "Brass") };
        doc.Objects = [boolean, rounded];
        var e = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Contains(e.Solids, s => s.Name == "shell" && s.Primitive is Em3dShapeSolid);
        Assert.Contains(e.Solids, s => s.Name == "rounded" && s.Primitive is Em3dShapeSolid r && r.Faces.Any(f => f.Name.StartsWith("fillet(", StringComparison.Ordinal)));
    }

    // ── 8. cancel mid-read: the document and the folder are as they were ────────────────────

    [KernelFact]
    public async Task Gate8_CancelMidRead_LeavesTheDocumentByteIdentical_AndTheFolderUnchanged()
    {
        string ws = Workspace();
        using var maker = KernelForTests.New();
        string src = WriteStep(ws, "slow.step", maker.Export([new(Tree(Box("blk", 0, 0, 0, 10, 10, 10)), "blk")], "step", "mm"));
        var (doc, path) = NewC3d(ws, "Slow");
        C3dPersistence.SaveToFile(path, doc);
        string before = File.ReadAllText(path);
        string[] listing = Directory.GetFiles(Path.GetDirectoryName(path)!);

        using var kernel = new GeometryKernel(new GeometryKernelOptions
        {
            DiskCache = false,
            Start = p => new GeometryKernelProcessWorker(p, new Dictionary<string, string>
            {
                ["CRF_GEOMETRY_WORKER_TEST"] = "1", ["CRF_GEOMETRY_WORKER_TEST_IMPORT_SECONDS"] = "30",
            }),
        });
        var vm = new StepImportDialogViewModel(src, control => StepImport.Read(src, kernel, Tech(ws), doc, control), _ => null);
        var reading = vm.StartAsync();
        await Task.Delay(500);
        Assert.True(vm.Reading);
        vm.CancelRead();
        await reading.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Null(vm.Plan);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(before, C3dPersistence.Serialize(doc));
        Assert.Equal(listing, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    // ── 10. counters: reopening asks the worker nothing when the cache holds the part ────────

    [KernelFact]
    public void Gate10_ReopeningADocumentWithAnImportedPart_SendsNoRequestWhenTheCacheHoldsIt()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var (doc, path) = Imported(kernel, ws, "Again", Tree(Box("blk", 0, 0, 0, 50, 40, 30)), "Alumina");
        C3dPersistence.SaveToFile(path, doc);
        Assert.True(new C3dElaborator(null, kernel).Elaborate(C3dPersistence.LoadFromFile(path), path, null).Ok);

        long sent = kernel.RequestsSent;
        var fresh = new C3dElaborator(null, kernel);         // a new editor tab on the same file
        Assert.True(fresh.Elaborate(C3dPersistence.LoadFromFile(path), path, null).Ok);
        Assert.Equal(sent, kernel.RequestsSent);
        Assert.Equal(1, fresh.KernelTreesBuilt);             // lowered by this elaborator, answered by the kernel's cache
    }

    // ── undo, the save and the copy (R-em3d68-4d) ───────────────────────────────────────────

    [KernelFact]
    public void AnUndoneImportsCopy_IsDeletedByTheSave_AndARedoWritesItBack()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string src = WriteStep(ws, "blk.step", kernel.Export([new(Tree(Box("blk", 0, 0, 0, 10, 10, 10)), "blk")], "step", "mm"));
        var (doc, path) = NewC3d(ws, "Undo");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        using var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a(), kernel: kernel);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        var plan = vm.ReadStep(src, null);
        Assert.Null(vm.AcceptStepImport(plan));
        string copy = Path.Combine(Path.GetDirectoryName(path)!, "blk.step");
        Assert.True(File.Exists(copy));
        Assert.Equal(["blk"], vm.Document.Objects.Select(o => o.Name));

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.Objects);
        Assert.Null(vm.Save());
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(src));                        // never a file the import did not create

        vm.UndoRedo.Redo();
        Assert.True(File.Exists(copy));
        Assert.Equal(StepImport.HashOf(File.ReadAllBytes(src)), C3dValidation.StepHash(copy));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private static GeometryKernelTree Tree(C3dObject o) => GeometryKernelTree.From(o, 1000);

    private static double[] Shift(double xUm) => [1, 0, 0, xUm, 0, 1, 0, 0, 0, 0, 1, 0];

    /// <summary>The same block as <see cref="Box"/>, its six faces listed top first: a revised file whose faces are numbered
    /// differently while the geometry is identical.</summary>
    private static C3dPolyhedron ReorderedBlock(string name, long dx, long dy, long dz)
    {
        C3dPoint3 P(int i) => new((i & 1) * dx * Um, ((i >> 1) & 1) * dy * Um, ((i >> 2) & 1) * dz * Um);
        C3dFace F(string n, params int[] loop) => new() { Name = n, Outer = [.. loop] };
        return new C3dPolyhedron
        {
            Name = name, Vertices = [.. Enumerable.Range(0, 8).Select(P)],
            Faces = [F("zmax", 4, 5, 7, 6), F("ymax", 2, 6, 7, 3), F("xmax", 1, 3, 7, 5), F("zmin", 0, 2, 3, 1), F("ymin", 0, 1, 5, 4), F("xmin", 0, 4, 6, 2)],
        };
    }

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Alumina", Epsr = 9.8 },
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Color = "#b87333" },
                new TechMaterial { Name = "Brass", Sigma20 = 1.5e7, Color = "#FF0000" },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static Technology Tech(string ws) => TechPersistence.LoadFromFile(Path.Combine(ws, "tech.ctech"));

    private static string WriteStep(string ws, string name, byte[] bytes)
    {
        string dir = Path.Combine(ws, "incoming");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static (C3dDocument Doc, string Path) NewC3d(string ws, string cell)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        return (new C3dDocument(), Path.Combine(dir, cell + ".c3d"));
    }

    /// <summary>A document holding one imported part, made of <paramref name="material"/>, its source in the workspace.</summary>
    private (C3dDocument Doc, string Path) Imported(GeometryKernel kernel, string ws, string cell, GeometryKernelTree tree, string material)
    {
        string src = WriteStep(ws, tree.Object + ".step", kernel.Export([new(tree, tree.Object)], "step", "mm"));
        var (doc, path) = NewC3d(ws, cell);
        var plan = StepImport.Read(src, kernel, Tech(ws), doc);
        plan.Parts[0].Material = material;
        StepImport.Apply(plan, doc, path);
        return (doc, path);
    }

    /// <summary>The face<n> of the (single) Step part whose plane is the top, z at its maximum.</summary>
    private static string TopFace(GeometryKernel kernel, C3dDocument doc, string path)
    {
        var faces = kernel.Faces(GeometryKernelTree.From(Resolved((C3dStep)doc.Objects[0], path), 1000));
        return faces.Where(f => f.Kind == "plane").OrderByDescending(f => f.Box[2]).First().Name;
    }

    /// <summary>A Step object with its File made absolute, as elaboration hands it to the kernel.</summary>
    private static C3dStep Resolved(C3dStep s, string c3dPath)
        => new() { Name = s.Name, File = Path.Combine(Path.GetDirectoryName(c3dPath)!, s.File), Hash = s.Hash, Part = s.Part };

    private static C3dStep Copy(C3dStep s, string name, string? material = null)
        => new() { Name = name, Material = material, File = s.File, Part = s.Part, Hash = s.Hash, Unit = s.Unit, SourcePath = s.SourcePath };

    private static IReadOnlyList<Em3dShapeFace> Faces(C3dElaboration e, string solid)
        => ((Em3dShapeSolid)e.Solids.Single(s => s.Name == solid).Primitive).Faces;

    private static (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds(C3dElaboration e, string solid)
        => Em3dProblem.Bounds(e.Solids.Single(s => s.Name == solid).Primitive);
}
