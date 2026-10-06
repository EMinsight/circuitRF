// ================================================================
//  WavePortsByHandTests.cs — the gate for brief-em3d-121: a single wave port infers a coax's voltage path along a ray from
//  the outer to the inner, and Make Port ▸ Wave on a face whose strips lie between the air box's PEC faces writes a
//  terminal per strip, referenced to the box. The coax is brief 116's fixture; the pair and the launch are the shipped
//  3D Wave Ports cells, opened in the editor (nothing here saves them, except AuthorTheExamplesPorts, below, which
//  re-makes Pair's ports).
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class WavePortsByHandTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3d121-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly Queue<Action> _posted = new();
    private CircuitRF.Design.ThreeD.Occ.GeometryKernel? _kernel;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        _kernel?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private const long Um = 1000;                 // DBU per µm

    // ── 1. the coax ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_ACoaxWithNothingStated_RunsFromTheShieldToThePinAlongARay_AndATouchingPinIsStillRefused()
    {
        var doc = OpenEmsWavePortTests.Coax();
        var p = doc.Ports[0];
        (p.VoltagePath, p.Positive, p.Negative) = (null, null, null);
        var r = Resolve(doc);
        Assert.Null(r.Refusal);
        Assert.Equal(("pin", "shield"), (r.Resolved!.PositiveObject, r.Resolved.NegativeObject));
        Assert.Contains("the path runs from 'shield' to 'pin' along a ray, because 'shield' encloses 'pin' on the face", r.Reason);
        var v = r.Resolved.VoltagePath!.Value;
        output.WriteLine($"({v.From.X * 1e6:F1}, {v.From.Y * 1e6:F1}) → ({v.To.X * 1e6:F1}, {v.To.Y * 1e6:F1}) µm: {r.Reason}");
        // through the centre along x or y, from the hole's edge (670 µm) inward to the pin's surface (200 µm)
        bool alongX = Math.Abs(v.From.Y) < 1e-9 && Math.Abs(v.To.Y) < 1e-9, alongY = Math.Abs(v.From.X) < 1e-9 && Math.Abs(v.To.X) < 1e-9;
        Assert.True(alongX != alongY);
        double from = alongX ? v.From.X : v.From.Y, to = alongX ? v.To.X : v.To.Y;
        Assert.Equal(Math.Sign(from), Math.Sign(to));
        Assert.Equal(670e-6, Math.Abs(from), 1e-9);
        Assert.InRange(Math.Abs(to), 195e-6, 200e-6 + 1e-9);

        p.Flip = true;
        var flipped = Resolve(doc);
        Assert.Equal(("shield", "pin"), (flipped.Resolved!.PositiveObject, flipped.Resolved.NegativeObject));
        Assert.Equal((v.To, v.From), (flipped.Resolved.VoltagePath!.Value.From, flipped.Resolved.VoltagePath.Value.To));

        // the pin moved onto the hole's edge: shorted, not enclosed
        p.Flip = false;
        ((C3dCylinder)doc.Objects[2]).Base = new C3dPoint3(470 * Um, 0, 0);
        Assert.Contains("'shield' and 'pin' overlap across its region of the zmin face, so no straight path runs between them; " +
                        "state its VoltagePath.", Resolve(doc).Refusal);
    }

    // ── 2. the pair, by the gesture ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_MakePortWave_OnPairsEndFaces_WritesATerminalPerStrip_ReferencedToTheBox()
    {
        var vm = OpenPair();
        var fill = vm.SceneObject("fill")!;
        int entries = vm.UndoEntries;

        Assert.Null(vm.MakePortFromFace(fill.Id, Face(fill, "xmin"), Em3dPortKind.Wave));
        var xmin = Assert.Single(vm.Document.Ports);
        Assert.Equal(("xmin", 0, "airbox/zmin"), (xmin.Name, xmin.Number, xmin.Reference));
        Assert.Equal([(1, "P1", "strip_a"), (2, "P2", "strip_b")], xmin.Terminals!.Select(t => (t.Number, t.Name, t.Conductor)));
        output.WriteLine(vm.StatusMessage);
        Assert.Contains("'airbox/zmin' is the reference: the air box's PEC faces are one ground; 'airbox/zmin' names it.", vm.StatusMessage);
        Assert.Equal(entries + 1, vm.UndoEntries);

        Settle(vm);
        Assert.Null(vm.MakePortFromFace(fill.Id, Face(fill, "xmax"), Em3dPortKind.Wave));
        var xmax = vm.Document.Ports[1];
        Assert.Equal(("xmax", "airbox/zmin"), (xmax.Name, xmax.Reference));
        Assert.Equal([(3, "P3", "strip_a"), (4, "P4", "strip_b")], xmax.Terminals!.Select(t => (t.Number, t.Name, t.Conductor)));
        Assert.Equal(entries + 2, vm.UndoEntries);
    }

    // ── 3. the same problem as shipped ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_TheGestureMadePair_LowersToTheShippedPorts()
    {
        var vm = OpenPair();
        var fill = vm.SceneObject("fill")!;
        Assert.Null(vm.MakePortFromFace(fill.Id, Face(fill, "xmin"), Em3dPortKind.Wave));
        Settle(vm);
        Assert.Null(vm.MakePortFromFace(fill.Id, Face(fill, "xmax"), Em3dPortKind.Wave));

        var made = Ports(Lower(vm.Document, PairPath(), "openEMS"));
        var shipped = Ports(Lower(C3dPersistence.LoadFromFile(PairPath()), PairPath(), "openEMS"));
        Assert.Equal(shipped.Select(Key), made.Select(Key));
        foreach (var (a, b) in shipped.Zip(made)) AssertPath(a.VoltagePath!.Value, b.VoltagePath!.Value);
    }

    /// <summary>
    /// The Launch's P1 states neither end nor a path (the owner's choice: drawn with the Port tool, 2 × 2 mm round the axis),
    /// and lowers to the polarity and path brief 117 stated and its runs were recorded with: from the bore's wall (axis_z +
    /// 0.67 mm) to the pin's surface (axis_z + 0.2 mm). Make Port ▸ Wave on the bore's end face, P1 deleted, infers the same,
    /// on the 1.34 mm square round the disc.
    /// </summary>
    [KernelFact]
    public void Gate3_TheLaunchsP1_InfersThePathItsRunsWereRecordedWith_AndMakePortOnTheBoreFaceAgrees()
    {
        var stored = C3dPersistence.LoadFromFile(LaunchPath()).Ports.Single(p => p.Number == 1);
        Assert.True(stored.Positive is null && stored.Negative is null && stored.VoltagePath is null);
        var vm = OpenLaunch();
        var p1 = Ports(Lower(vm.Document, LaunchPath(), "openEMS")).Single(p => p.Number == 1);
        Assert.Equal(("pin", "housing"), (p1.PositiveObject, p1.NegativeObject));
        AssertPath(new Em3dSegment(new Point3(-5e-3, 0, 1.3955e-3), new Point3(-5e-3, 0, 0.9255e-3)), p1.VoltagePath!.Value);

        var made = MakeLaunchPort(vm);
        output.WriteLine(vm.StatusMessage);
        Assert.Contains("because 'housing' encloses 'pin' on the face", vm.StatusMessage);
        Assert.True(made.VoltagePath is null && made.Positive is null);
        Assert.Equal((-670_000L, 55_500L, 1_340_000L, 1_340_000L), (made.Rect.Min.U, made.Rect.Min.V, made.Rect.Size.U, made.Rect.Size.V));
        var q1 = Ports(Lower(vm.Document, LaunchPath(), "openEMS")).Single(p => p.Number == 1);
        Assert.Equal((p1.PositiveObject, p1.NegativeObject), (q1.PositiveObject, q1.NegativeObject));
        AssertPath(p1.VoltagePath.Value, q1.VoltagePath!.Value);
    }

    // ── 4. what stays as it is ──────────────────────────────────────────────────────────────────

    /// <summary>D1: a STORED bare wave port on Pair's xmin face keeps its strip-to-strip reading, and gains the note.</summary>
    [Fact]
    public void Gate4_AStoredBarePortOnPairsFace_ReadsStripToStrip_WithTheNote()
    {
        var doc = C3dPersistence.LoadFromFile(PairPath());
        var p = doc.Ports[0];
        (p.Terminals, p.Reference, p.Number) = (null, null, 1);
        doc.Ports.RemoveAt(1);
        var e = C3dElaborator.ElaborateOnce(doc, PairPath(), Cws());
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var r = Assert.Single(C3dPortReports.For(doc, e)).Result;
        output.WriteLine(C3dPortReports.Describe(r));
        Assert.Equal(["strip_a", "strip_b"], new[] { r.Resolved!.PositiveObject, r.Resolved.NegativeObject }.Order());
        const string Note = "xmin meets 'strip_a', 'strip_b' and the air box's PEC faces on the xmin face; it is read as one port " +
                            "between the two strips. Make Port ▸ Wave on that face writes a terminal per strip, referenced to the box.";
        Assert.Equal(Note, r.Note);
        Assert.EndsWith(Note, C3dPortReports.Describe(r));
    }

    // ── the example's ports, re-authored by the gesture ─────────────────────────────────────────

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_3D_WAVE_PORTS") != "1")
                Skip = "re-makes the ports of examples/3D Wave Ports/Pair only with CRF_AUTHOR_3D_WAVE_PORTS=1";
        }
    }

    /// <summary>R-em3d121-3 — WRITES the shipped Pair.c3d: its two wave ports deleted and remade by Make Port ▸ Wave on the
    /// fill's xmin face, then its xmax face, and saved. The Launch's P1 is not remade here: Make Port ▸ Wave on the bore's
    /// face gives the 1.34 mm square round the disc, on which openEMS's grid leaves the current probe too little room round
    /// the pin and the run is refused, so the shipped P1 keeps its Port-tool rectangle (2 × 2 mm) and states only that.</summary>
    [AuthorFact]
    public void AuthorTheExamplesPorts()
    {
        var pair = OpenPair();
        var fill = pair.SceneObject("fill")!;
        Assert.Null(pair.MakePortFromFace(fill.Id, Face(fill, "xmin"), Em3dPortKind.Wave));
        Settle(pair);
        Assert.Null(pair.MakePortFromFace(fill.Id, Face(fill, "xmax"), Em3dPortKind.Wave));
        Assert.Null(pair.Save());
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string ExampleRoot()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(PalaceBackendTests.RepoRoot()) ?? throw new InvalidOperationException("no examples/"),
                        "3D Wave Ports");

    private static string PairPath() => Path.Combine(ExampleRoot(), "Pair", "3d", "Pair.c3d");

    private static string LaunchPath() => Path.Combine(ExampleRoot(), "Launch", "3d", "Launch.c3d");

    private static string Cws() => Path.Combine(ExampleRoot(), ".cws");

    /// <summary>The shipped Pair in the editor, its active setup openEMS, its two ports deleted.</summary>
    private C3dEditorViewModel OpenPair()
    {
        var vm = Open(PairPath(), kernel: false, setup: "openEMS");
        vm.DeletePorts([.. vm.Document.Ports]);
        Settle(vm);
        Assert.Empty(vm.Document.Ports);
        return vm;
    }

    /// <summary>The shipped Launch in the editor (it needs the geometry kernel), on the setup it opens on: both of its setups
    /// put the box's xmin face on the housing's back, and selecting one would save it into the file.</summary>
    private C3dEditorViewModel OpenLaunch() => Open(LaunchPath(), kernel: true, setup: null);

    /// <summary>Delete P1, then Make Port ▸ Wave on the bore's end face at x = −5 mm; the port it made.</summary>
    private C3dPort MakeLaunchPort(C3dEditorViewModel vm)
    {
        vm.DeletePorts([.. vm.Document.Ports.Where(p => p.Number == 1)]);
        Settle(vm);
        var bore = vm.SceneObject("bore");
        if (bore is null)
            output.WriteLine("scene objects: " + string.Join(", ", vm.Viewer.Scene.Objects.Select(o => o.Name)));
        Assert.NotNull(bore);
        int face = Face(bore!, "bottom");              // the cylinder's base, at x = −5 mm
        Assert.Null(vm.MakePortFromFace(bore!.Id, face, Em3dPortKind.Wave));
        Settle(vm);
        return vm.Document.Ports.Single(p => p.Kind == Em3dPortKind.Wave);
    }

    private C3dEditorViewModel Open(string path, bool kernel, string? setup)
    {
        var fake = new PatchRecordingBackend();
        if (kernel && _kernel is null)
        {
            _kernel = KernelForTests.New();
            _kernel.Probe();
        }
        var vm = kernel
            ? new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, Cws, _posted.Enqueue, kernel: _kernel)
            : new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, Cws, a => a());
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        if (setup is null) vm.RestoreActiveSetup(null);
        else vm.SetActiveSetup(setup);
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(120)), "the scene never settled");

    private int Face(Scene3DObject o, string name)
    {
        if (!o.FaceNames.Contains(name)) output.WriteLine($"{o.Name}'s faces: {string.Join(", ", o.FaceNames)}");
        return Enumerable.Range(0, o.FaceNames.Count).Single(i => o.FaceNames[i] == name);
    }

    private static Em3dProblem Lower(C3dDocument doc, string path, string setupName)
    {
        var (embedded, why) = C3dSetups.Select(doc, setupName);
        Assert.True(embedded is not null, why);
        var g = C3dProblemAssembly.Assemble(C3dSetups.ForRun(embedded!, path), doc, path, Cws(), new C3dElaborator());
        Assert.True(g.Ok, g.Refusal);
        return g.Problem!;
    }

    private static List<Em3dPort> Ports(Em3dProblem p) => [.. p.Ports.OrderBy(q => q.Number)];

    private static (int, string, string, int?) Key(Em3dPort p) => (p.Number, p.PositiveObject, p.NegativeObject, p.FaceGroup);

    /// <summary>Two voltage paths equal to 1 DBU (1 nm).</summary>
    private static void AssertPath(Em3dSegment a, Em3dSegment b)
    {
        foreach (var (x, y) in new[] { (a.From, b.From), (a.To, b.To) })
            Assert.True(Math.Abs(x.X - y.X) <= 1e-9 && Math.Abs(x.Y - y.Y) <= 1e-9 && Math.Abs(x.Z - y.Z) <= 1e-9, $"{x} against {y}");
    }

    /// <summary>The coax's one port, resolved through brief 116's setup and a workspace of its own.</summary>
    private C3dPortResult Resolve(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        string dir = Path.Combine(ws, "Coax", "3d");
        Directory.CreateDirectory(dir);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "PTFE", Epsr = 2.1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string path = Path.Combine(dir, "Coax.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var setup = OpenEmsWavePortTests.CoaxSetup(cellsPerWavelength: 400);
        var ctx = C3dProblemAssembly.PortContext(setup, doc, e, C3dProblemAssembly.AirBox(setup, e, out _));
        return C3dPorts.Resolve(doc, ctx).Single();
    }
}
