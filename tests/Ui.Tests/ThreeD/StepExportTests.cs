// ================================================================
//  StepExportTests.cs — the gate for brief-em3d-69: STEP export, in process. What is written is the ELABORATED model
//  (precedence applied, ports and material-less objects left out, a hidden object kept), in the document's unit mapped to
//  millimetres or inches, with a header that carries no user and no path; a cancelled export writes nothing. The round
//  trips read the file back through brief 68's reader and rebuild every part from it. Every gate that needs OpenCASCADE is
//  [KernelFact] and skips, naming what to build, where the worker is not built.
// ================================================================

using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class StepExportTests : IDisposable
{
    private const long Um = 1000;                        // DBU per µm at the default 1000 DBU/µm
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step69-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 2. round trip: the same solids, names and volumes ───────────────────────────────────

    [KernelFact]
    public void Gate2_AFlattenedC3dWithABooleanAndAFillet_ReadsBackAsTheSameSolidsNamesAndVolumes()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string path = WriteC3d(ws, "Part", Mixed());

        foreach (bool assembly in (bool[])[false, true])
        {
            string step = Path.Combine(_root, assembly ? "asm" : "flat", "part.step");
            var result = StepExport.Export(path, step, new StepExportOptions { Assembly = assembly }, kernel);
            AssertRoundTrip(kernel, result, step);
            Assert.Equal(["base", "lid", "rounded", "cap"], Reimport(kernel, step).Select(p => p.Name));
        }
    }

    [KernelFact]
    public void Gate2_AsAssembly_ACellPlacedFourTimesIsWrittenOnce_AndEachPlacementReadsBackWhereItWasPlaced()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        // The die holds a kernel solid — a fillet — so its B-rep is carried into the sub-assembly's own frame.
        WriteC3d(ws, "die", new C3dDocument
        {
            Objects = [new C3dFillet { Name = "chip", Radius = 10 * Um, Edges = ["xmax|zmax"], Target = Box("", "Copper", 0, 0, 0, 100, 80, 40) }],
        });
        string path = WriteC3d(ws, "pkg", new C3dDocument
        {
            Objects = [Box("base", "Alumina", -100, -100, -50, 1400, 400, 50)],
            Instances =
            [
                new C3dInstance { Name = "U1", CellRef = "../../die" },
                new C3dInstance { Name = "U2", CellRef = "../../die", Placement = new C3dPlacement { Origin = new C3dPoint3(300 * Um, 0, 0) } },
                new C3dInstance { Name = "U3", CellRef = "../../die", Placement = new C3dPlacement { Origin = new C3dPoint3(600 * Um, 0, 0) },
                                  Array = new C3dArray { Counts = [2, 1, 1], Pitch = new C3dPoint3(300 * Um, 0, 0) } },
            ],
        });

        string step = Path.Combine(_root, "pkg.step");
        var result = StepExport.Export(path, step, new StepExportOptions { Assembly = true }, kernel);
        Assert.Equal(1, result.Plan.SubAssemblies);
        Assert.Equal(["base", "chip"], result.Plan.PartNames);
        Assert.Single(Regex.Matches(File.ReadAllText(step), @"PRODUCT\('chip'"));
        AssertRoundTrip(kernel, result, step);

        var back = Reimport(kernel, step);
        Assert.Equal(["base", "chip", "chip", "chip", "chip"], back.Select(p => p.Name));
        // Where each placement landed, read from the file: the die's xmin at 0, 300, 600 and 900 µm.
        var xs = back.Where(p => p.Name == "chip").Select(p => Math.Round(p.Box[0], 6)).Order().ToList();
        Assert.Equal([0.0, 300.0, 600.0, 900.0], xs);
    }

    [KernelFact]
    public void Gate2_The3DPackageExamplesPackageCell_AndItsDieLayout_ReadBackAsTheSameSolidsNamesAndVolumes()
    {
        using var kernel = KernelForTests.New();
        string example = Path.Combine(RepoRoot(), "examples", "3D Package");

        string flat = Path.Combine(_root, "package.step");
        var r = StepExport.Export(Path.Combine(example, "Package"), flat, new StepExportOptions(), kernel);
        Assert.False(r.Plan.IsLayout);
        Assert.Contains(r.Plan.PartNames, n => n.StartsWith("U1/", StringComparison.Ordinal));   // the die, by instance path
        Assert.Contains("w1", r.Plan.PartNames);                                                    // a bond wire: a loft
        AssertRoundTrip(kernel, r, flat);

        string asm = Path.Combine(_root, "package-asm.step");
        var a = StepExport.Export(Path.Combine(example, "Package"), asm, new StepExportOptions { Assembly = true }, kernel);
        Assert.Equal(1, a.Plan.SubAssemblies);
        Assert.Contains("PRODUCT('Thru die'", File.ReadAllText(asm), StringComparison.Ordinal);    // named by the layout cell
        AssertRoundTrip(kernel, a, asm);

        string layout = Path.Combine(_root, "die.step");
        var l = StepExport.Export(Path.Combine(example, "Thru die", "layout", "Thru die.clay"), layout, new StepExportOptions(), kernel);
        Assert.True(l.Plan.IsLayout);
        AssertRoundTrip(kernel, l, layout);
    }

    /// <summary>R-em3d69-2b — a material's #rrggbb is what the file's COLOUR_RGB says (sRGB, not OCCT's linear RGB), a
    /// dielectric carries the 3D view's alpha as a transparency, and brief 68's reader gives the same #rrggbb back.</summary>
    [KernelFact]
    public void Gate2_EachSolidCarriesItsMaterialsColour_ADielectricItsTransparency_AndTheReaderGivesTheSameColourBack()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string path = WriteC3d(ws, "C", new C3dDocument
        {
            Objects = [Box("sub", "Alumina", 0, 0, 0, 100, 100, 10), Box("cu", "Copper", 0, 0, 10, 10, 10, 10)],
        });
        string step = Path.Combine(_root, "colours.step");
        StepExport.Export(path, step, new StepExportOptions(), kernel);
        string text = File.ReadAllText(step);

        double[] Rgb(string hex) => [.. Enumerable.Range(0, 3).Select(k => Convert.ToInt32(hex.Substring(1 + 2 * k, 2), 16) / 255.0)];
        var written = Regex.Matches(text, @"COLOUR_RGB\('',([^,]+),([^,]+),([^)]+)\)")
                           .Select(m => new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value }.Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
        foreach (string hex in (string[])["#b87333", "#e0e0d0"])
            Assert.Contains(written, w => w.Zip(Rgb(hex)).All(p => Math.Abs(p.First - p.Second) < 1e-6));
        var transparency = double.Parse(Regex.Match(text, @"SURFACE_STYLE_TRANSPARENT\(([^)]+)\)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1 - StepExport.DielectricAlpha / 255.0, transparency, 1e-6);

        var back = kernel.ImportStep(File.ReadAllBytes(step)).Parts.ToDictionary(p => p.Name, p => StepImport.Hex(p.Colour!));
        Assert.Equal(("#e0e0d0", "#b87333"), (back["sub"], back["cu"]));
    }

    // ── 3. precedence ───────────────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate3_ADielectricDrawnAfterTheMetalItSwallows_LosesTheMetalsVolume_AndAsDrawnBothAreWhole()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string path = WriteC3d(ws, "P", new C3dDocument
        {
            Objects = [Box("metal", "Copper", 100, 100, 50, 200, 200, 100), Box("sub", "Alumina", 0, 0, 0, 400, 400, 200)],
        });
        const double vMetal = 200.0 * 200 * 100, vSub = 400.0 * 400 * 200;

        var applied = StepExport.Export(path, Path.Combine(_root, "a.step"), new StepExportOptions(), kernel);
        Assert.Equal(vMetal, Volume(applied, "metal"), vMetal * 1e-9);
        Assert.Equal(vSub - vMetal, Volume(applied, "sub"), vSub * 1e-9);
        Assert.Contains("precedence applied", File.ReadAllText(applied.Path), StringComparison.Ordinal);

        var drawn = StepExport.Export(path, Path.Combine(_root, "b.step"), new StepExportOptions { AsDrawn = true }, kernel);
        Assert.Equal(vMetal, Volume(drawn, "metal"), vMetal * 1e-9);
        Assert.Equal(vSub, Volume(drawn, "sub"), vSub * 1e-9);
        Assert.Contains("as drawn", File.ReadAllText(drawn.Path), StringComparison.Ordinal);
    }

    // ── 4. exclusions ───────────────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate4_PortsPolylinesAndMateriallessObjectsAreAbsent_TheAirBoxOnlyWhenAsked_AndAHiddenObjectIsPresent()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string path = WriteC3d(ws, "Part", Mixed());

        var plain = StepExport.Plan(path, new StepExportOptions(), kernel);
        Assert.Equal(["base", "lid", "rounded", "cap", "trace"], plain.PartNames);
        Assert.Equal((4, 1, 1, 1), (plain.Solids, plain.Faces, plain.Sheets, plain.Excluded));
        Assert.Contains(plain.Notes, n => n.Contains("no material", StringComparison.Ordinal) && n.Contains("'loose'", StringComparison.Ordinal));

        var withBox = StepExport.Export(path, Path.Combine(_root, "box.step"), new StepExportOptions { IncludeAirBox = true }, kernel);
        Assert.Equal(["base", "lid", "rounded", "cap", "trace", "airbox"], withBox.Plan.PartNames);
        Assert.Contains("PRODUCT('airbox'", File.ReadAllText(withBox.Path), StringComparison.Ordinal);
        foreach (string absent in (string[])["loose", "guide", "P1"])
            Assert.DoesNotContain($"'{absent}'", File.ReadAllText(withBox.Path), StringComparison.Ordinal);

        // Thickened, the sheet is a solid of its stated 5 µm, and a solid part again.
        var thick = StepExport.Export(path, Path.Combine(_root, "thick.step"), new StepExportOptions { ThickenSheets = true }, kernel);
        Assert.Equal((5, 0), (thick.Plan.Solids, thick.Plan.Faces));
        Assert.Equal(500.0 * 50 * 5, Volume(thick, "trace"), 1e-3);
    }

    // ── 5. units ────────────────────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate5_AMilDocumentWritesInches_AMicronOneMillimetres_AndTheExtentsReadBackToTheDbu()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        foreach (var (unit, expected) in ((LayoutUnit, double)[])[(LayoutUnit.Mil, 25400), (LayoutUnit.Um, 1000)])
        {
            var box = Box("blk", "Copper", 0, 0, 0, 254, 127, 51);
            string path = WriteC3d(ws, "U" + unit, new C3dDocument { DisplayUnit = unit, Objects = [box] });
            string step = Path.Combine(_root, $"blk-{unit}.step");
            var r = StepExport.Export(path, step, new StepExportOptions(), kernel);
            Assert.Equal(expected == 25400 ? "in" : "mm", r.Plan.Units);
            Assert.Equal([expected], kernel.ImportStep(File.ReadAllBytes(step)).UnitMicrons);
            var back = Reimport(kernel, step).Single();
            Assert.Equal([0L, 0, 0, 254 * Um, 127 * Um, 51 * Um], back.Box.Select(v => (long)Math.Round(v * Um)));
        }
    }

    // ── 6. the header ───────────────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate6_TheHeaderCarriesTheFileNameAndCircuitRFsVersion_NeverAUserNameOrADirectory()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string path = WriteC3d(ws, "Part", Mixed());
        string step = Path.Combine(_root, "deep", "er", "export.step");
        StepExport.Export(path, step, new StepExportOptions(), kernel);
        string text = File.ReadAllText(step);

        string header = text[..text.IndexOf("ENDSEC;", StringComparison.Ordinal)];
        Assert.Contains("FILE_NAME('export.step'", header, StringComparison.Ordinal);
        Assert.Contains($"'{StepExport.OriginatingSystem}'", header, StringComparison.Ordinal);
        Assert.StartsWith("circuitRF ", StepExport.OriginatingSystem, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetDirectoryName(step)!, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(_root), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), text, StringComparison.Ordinal);
        if (Environment.UserName is { Length: >= 3 } user) Assert.DoesNotContain(user, text, StringComparison.OrdinalIgnoreCase);
    }

    // ── 7. cancel ───────────────────────────────────────────────────────────────────────────

    [KernelFact]
    public async Task Gate7_ACancelledExportLeavesNoFileAtTheTarget()
    {
        using var kernel = new GeometryKernel(new GeometryKernelOptions
        {
            DiskCache = false,
            Start = p => new GeometryKernelProcessWorker(p, new Dictionary<string, string>(KernelForTests.TestOps)
            {
                ["CRF_GEOMETRY_WORKER_TEST_WRITE_SECONDS"] = "30",
            }),
        });
        string ws = Workspace();
        string path = WriteC3d(ws, "Part", Mixed());
        string dir = Path.Combine(_root, "cancel");
        string step = Path.Combine(dir, "x.step");
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => StepExport.Export(path, step, new StepExportOptions(), kernel, new RunControl { Token = cts.Token }));
        await Task.Delay(500);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(step));
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0);
    }

    // ── the dialog and the menus (R-em3d69-4) ───────────────────────────────────────────────

    [Fact]
    public async Task TheDialog_StatesTheCountsBeforeWriting_WritesOnExport_AndRemembersItsChoicesForTheSession()
    {
        int writes = 0;
        StepExportPlan Plan(StepExportOptions o) => new()
        {
            SourcePath = "a.c3d", IsLayout = false, Options = o, Units = "mm", PartNames = ["a"], Notes = [], Solids = 3, Faces = 1, Sheets = 1,
        };
        StepExportDialogViewModel Dialog() => new("out.step", new StepExportOptions(), Plan, (p, _) => { writes++; return new StepExportResult("out.step", p, [], []); });

        var vm = Dialog();
        await vm.PlanAsync();
        Assert.Equal("3 solids, 1 face, 1 sheet — in millimetres, AP214.", vm.Summary);
        Assert.Equal(0, writes);
        vm.Assembly = true;
        vm.ThickenSheets = true;
        vm.SchemaIndex = 1;
        await vm.PlanAsync();
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(1, writes);
        Assert.True(vm.Result?.Plan.Options is { Assembly: true, ThickenSheets: true, Schema: StepExportSchema.Ap242 });

        var next = Dialog();
        Assert.Equal((true, false, true, false, 1), (next.Assembly, next.AsDrawn, next.ThickenSheets, next.IncludeAirBox, next.SchemaIndex));
        next.Assembly = false;
        next.ThickenSheets = false;
        next.SchemaIndex = 0;
        await next.PlanAsync();
        await next.ExportCommand.ExecuteAsync(null);                  // leave the session's choices at the defaults
    }

    [Fact]
    public void ExportStep_IsInAllThreeFileMenus_OnOneCommandAndOneTooltip()
    {
        string window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml"));
        string torn = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Shared", "TornOffFileMenuView.axaml"));
        // 3D menu cleanup — File ▸ Export ▸ STEP… only: the 3D menu's copy was the same command a second time.
        Assert.Equal(2, Regex.Matches(window, @"ExportStepCommand").Count);          // native File, in-window File
        Assert.Equal(2, Regex.Matches(window, @"ThreeDExportStepTip").Count);
        Assert.Single(Regex.Matches(torn, "ExportStepCommand"));
        Assert.Contains("ThreeDExportStepTip", torn, StringComparison.Ordinal);
    }

    // ── the dialog's alpha is the 3D view's ─────────────────────────────────────────────────

    [Fact]
    public void ADielectricIsWrittenWithTheAlphaThe3DViewDrawsItWith()
        => Assert.Equal(CircuitRF.Render.Scene3D.Scene3DBuilder.DielectricAlpha, StepExport.DielectricAlpha);

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A base, a boolean, a fillet, a hidden box, a sheet — and a material-less box, a polyline and a port, none of
    /// which is in the model.</summary>
    private static C3dDocument Mixed() => new()
    {
        Objects =
        [
            Box("base", "Alumina", 0, 0, 0, 1000, 800, 200),
            new C3dBoolean
            {
                Name = "lid", Op = C3dBooleanOp.Subtract, Blank = Box("", "Copper", 0, 0, 300, 400, 400, 100),
                Tools = [new C3dCylinder { Name = "bore", Base = new C3dPoint3(200 * Um, 200 * Um, 250 * Um), Length = 200 * Um, Radius = 50 * Um }],
            },
            new C3dFillet { Name = "rounded", Radius = 20 * Um, Edges = ["xmax|zmax"], Target = Box("", "Copper", 600, 0, 300, 300, 300, 100) },
            Hidden(Box("cap", "Copper", 0, 500, 300, 200, 200, 50)),
            new C3dSheet
            {
                Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 200 * Um, ThicknessUm = 5,
                Rect = new C3dRect { Min = new C3dPoint2(100 * Um, 600 * Um), Size = new C3dPoint2(500 * Um, 50 * Um) },
            },
            Box("loose", null, 2000, 0, 0, 100, 100, 100),
            new C3dPolyline { Name = "guide", Plane = C3dPlane.XY, Points = [new C3dPoint2(0, 0), new C3dPoint2(100 * Um, 0)] },
        ],
        Ports =
        [
            new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 200 * Um,
                          Rect = new C3dRect { Min = new C3dPoint2(0, 600 * Um), Size = new C3dPoint2(100 * Um, 50 * Um) } },
        ],
    };

    private static C3dBox Hidden(C3dBox b) { b.Hidden = true; return b; }

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static double Volume(StepExportResult r, string name) => r.Parts.Single(p => p.Name == name).VolumeUm3;

    /// <summary>Every closed part of a STEP file as brief 68 reads it: its name, its volume rebuilt from the file (µm³), and
    /// its bound (µm).</summary>
    private static List<(string Name, double Volume, double[] Box)> Reimport(GeometryKernel kernel, string step)
    {
        byte[] bytes = File.ReadAllBytes(step);
        string hash = StepImport.HashOf(bytes);
        var list = new List<(string, double, double[])>();
        foreach (var p in kernel.ImportStep(bytes).Parts.Where(p => p.Closed))
        {
            var tree = GeometryKernelTree.From(new C3dStep { Name = "p", File = step, Hash = hash, Part = p.Path }, 1000);
            var faces = kernel.Faces(tree);
            double[] box = [faces.Min(f => f.Box[0]), faces.Min(f => f.Box[1]), faces.Min(f => f.Box[2]),
                            faces.Max(f => f.Box[3]), faces.Max(f => f.Box[4]), faces.Max(f => f.Box[5])];
            list.Add((p.Name, kernel.Build(tree).VolumeUm3, box));
        }
        return list;
    }

    /// <summary>R-em3d69 gate 2: the file's closed parts are the written solids, by name, each within 1e-6 of its volume.</summary>
    private static void AssertRoundTrip(GeometryKernel kernel, StepExportResult r, string step)
    {
        var written = r.Parts.Where(p => !p.Empty && p.Solids > 0).ToDictionary(p => p.Name, p => p.VolumeUm3, StringComparer.Ordinal);
        var back = Reimport(kernel, step);
        Assert.Equal(written.Keys.Order(StringComparer.Ordinal), back.Select(p => p.Name).Distinct().Order(StringComparer.Ordinal));
        foreach (var (name, volume, _) in back)
            Assert.True(Math.Abs(volume / written[name] - 1) < 1e-6, $"'{name}': {volume} µm³ read back, {written[name]} written");
        if (!r.Plan.Options.Assembly) Assert.Equal(written.Count, back.Count);
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
                new TechMaterial { Name = "Alumina", Epsr = 9.8, Color = "#e0e0d0" },
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Color = "#b87333" },
            ],
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

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) return d.FullName;
        throw new InvalidOperationException("no source tree above the test assembly");
    }
}
