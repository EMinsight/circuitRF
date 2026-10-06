// ================================================================
//  TerminalWavePortTests.cs — the gate for brief-em3d-114: a .c3d wave port with N terminals, drawn, saved,
//  inferred, refused, lowered, made by the editor, refused by both solvers, and explained. No solver runs.
//  The fixture is an air stripline pair: two strips inside one shield (two ground planes joined by side walls,
//  one prism with a hole), both ending on the air box's xmin face.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
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

public sealed class TerminalWavePortTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3d114-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private const long Um = 1000;                 // DBU per µm

    // ── 1. round trip ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_ATwoTerminalPort_RoundTrips_AndWritesNoPortLevelNumberOrZ0()
    {
        var doc = Pair();
        doc.Ports.Add(PairPort());
        string text = C3dPersistence.Serialize(doc);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));

        var port = JsonDocument.Parse(text).RootElement.GetProperty("Ports")[0];
        Assert.False(port.TryGetProperty("Number", out _));
        Assert.False(port.TryGetProperty("Z0", out _));
        Assert.Equal("gnd", port.GetProperty("Reference").GetString());
        Assert.Equal(["strip_a", "strip_b"], port.GetProperty("Terminals").EnumerateArray().Select(t => t.GetProperty("Conductor").GetString()));
    }

    // ── 2. inference ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_StriplinePair_TheGroundIsTheReference_AndEachPathRunsFromItToItsStrip()
    {
        var doc = Pair();
        var p = PairPort();
        p.Reference = null;
        doc.Ports.Add(p);
        var setup = Setup();
        setup.Ground3D = "gnd";
        var (r, _) = Resolve(doc, setup);
        Assert.Null(r.Refusal);
        Assert.Contains("only one in the ground set", r.Reason);
        var ts = r.Terminals!;
        Assert.Equal([1, 2], ts.Select(t => t.Number));
        Assert.All(ts, t => Assert.Equal(("gnd", 1), (t.NegativeObject, t.FaceGroup)));
        foreach (var (t, strip) in ts.Zip(["strip_a", "strip_b"]))
        {
            Assert.Equal(strip, t.PositiveObject);
            var v = t.VoltagePath!.Value;
            output.WriteLine($"{strip}: ({v.From.Y * 1e6:F1}, {v.From.Z * 1e6:F1}) → ({v.To.Y * 1e6:F1}, {v.To.Z * 1e6:F1}) µm");
            double centre = strip == "strip_a" ? 2100e-6 : 2900e-6;
            Assert.Equal(centre, v.From.Y, 9);                                     // straight across, at the strip's centre
            Assert.Equal(centre, v.To.Y, 9);
            Assert.Contains(Math.Round(v.From.Z * 1e6, 3), new[] { 20.0, 2020.0 });  // on a ground plane's inner face
            Assert.Contains(Math.Round(v.To.Z * 1e6, 3), new[] { 1010.0, 1030.0 });  // on the strip's facing side
        }
    }

    [Fact]
    public void Gate2_ThreeConductorsAndNoGround_TheLargestIsTheReference_AndATieIsRefused()
    {
        var doc = ThreeBars(big: 300);
        doc.Ports.Add(BarsPort(["a", "b"]));
        var (r, _) = Resolve(doc, Setup());
        Assert.Null(r.Refusal);
        Assert.Equal("big", r.Resolved!.NegativeObject);
        Assert.Contains("'big' has the largest surface", r.Reason);

        // Four bars, two of them the terminals: the other two have equal surfaces, and neither is grounded.
        var tied = ThreeBars(big: 100);
        tied.Objects.Add(Box("d", 0, 900, 0, 1000, 100, 100));
        tied.Ports.Add(BarsPort(["a", "b"]));
        tied.Ports[0].Rect = Rect(0, 0, 1000, 1000);
        (r, _) = Resolve(tied, Setup());
        output.WriteLine(r.Refusal);
        Assert.Contains("'big' and 'd' have equal surfaces", r.Refusal);
        Assert.Contains("State its Reference", r.Refusal);
    }

    // ── 3. refusals ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("positive", "states both Terminals and Positive")]
    [InlineData("one", "has one terminal")]
    [InlineData("lumped", "is a lumped port with Terminals")]
    [InlineData("on-reference", "which is the port's reference")]
    [InlineData("same-conductor", "all name 'strip_a'")]
    [InlineData("clash", "are both port 2; each port has its own number, and a terminal is a port.")]
    [InlineData("no-terminals", "Add Terminals to the port, one per signal conductor, or use Make Port ▸ Wave on that face")]
    public void Gate3_EachRefusal_NamesWhatIsWrong(string which, string expected)
    {
        var doc = Pair();
        var p = PairPort();
        switch (which)
        {
            case "positive": p.Positive = "strip_a"; break;
            case "one": p.Terminals!.RemoveAt(1); break;
            case "lumped": p.Kind = Em3dPortKind.Lumped; break;
            case "on-reference": p.Reference = "strip_a"; break;
            case "same-conductor": p.Terminals![1].Conductor = "strip_a"; break;
            case "clash": doc.Ports.Add(new C3dPort { Number = 2, Name = "Other", Plane = C3dPlane.XY, Rect = Rect(0, 0, 10, 10) }); break;   // before p
            case "no-terminals": p.Terminals = null; p.Reference = null; p.Number = 1; break;
        }
        doc.Ports.Add(p);
        var r = Results(doc, Setup()).Single(x => x.Port == p);
        output.WriteLine(r.Refusal);
        Assert.Contains(expected, r.Refusal);
    }

    [Fact]
    public void Gate3_AFileStatingZ0BesideTerminals_IsRefusedNamingIt_AndKeepsTheKey()
    {
        var doc = Pair();
        doc.Ports.Add(PairPort());
        string text = C3dPersistence.Serialize(doc).Replace("\"Reference\": \"gnd\",", "\"Z0\": \"50\",\n\t\t\t\"Reference\": \"gnd\",");
        var read = C3dPersistence.Deserialize(text);
        Assert.Contains("states both Terminals and Z0", Results(read, Setup())[0].Refusal);
        Assert.Contains("\"Z0\": \"50\",\n\t\t\t\"Reference\"", C3dPersistence.Serialize(read));     // the refusal stays visible
    }

    // ── 4. the problem ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_TheLowering_IsTwoWavePortsSharingAFaceGroup_AndValidateRefusesEachGroupViolation()
    {
        var doc = Pair();
        doc.Ports.Add(PairPort());
        string path = WriteC3d(Workspace(), "Pair", doc);
        var g = C3dProblemAssembly.Assemble(Setup(), doc, path, null, new C3dElaborator(), false);
        Assert.True(g.Ok, g.Refusal);
        var ps = g.Problem!.Ports;
        Assert.Equal(2, ps.Count);
        Assert.All(ps, q => Assert.Equal((Em3dPortKind.Wave, (int?)1, "gnd", ps[0].Min, ps[0].Max), (q.Kind, q.FaceGroup, q.NegativeObject, q.Min, q.Max)));
        Assert.Empty(g.Problem.Validate());

        string Refusal(Func<Em3dPort, Em3dPort> second)
            => string.Join(" ", (g.Problem with { Ports = [ps[0], second(ps[1])] }).Validate());
        Assert.Contains("do not share one reference", Refusal(q => q with { NegativeObject = "strip_a" }));
        Assert.Contains("are all on 'strip_a'", Refusal(q => q with { PositiveObject = "strip_a" }));
        Assert.Contains("has no voltage path", Refusal(q => q with { VoltagePath = null }));
        Assert.Contains("is not a wave port", Refusal(q => q with { Kind = Em3dPortKind.Lumped }));
        Assert.Contains("do not share one rectangle", Refusal(q => q with { Max = q.Max with { Z = q.Max.Z - 1e-6 } }));
    }

    // ── 5. byte identity ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_EveryShippedAndFixtureC3d_ReSavesUnchanged()
    {
        string repo = PalaceBackendTests.RepoRoot();
        var files = new[] { "examples", "testdata" }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(repo, d), "*.c3d", SearchOption.AllDirectories)).ToList();
        Assert.NotEmpty(files);
        int written = 0;
        foreach (string f in files)
        {
            // A file the writer wrote (tab-indented) re-saves byte for byte; a hand-written one re-saves to the writer's form,
            // and that form re-saves byte for byte. Neither gains a key this brief added.
            string text = File.ReadAllText(f).ReplaceLineEndings("\n");
            string saved = C3dPersistence.Serialize(C3dPersistence.Deserialize(text));
            string name = Path.GetRelativePath(repo, f);
            if (text.Contains("\n\t\"Objects\"", StringComparison.Ordinal)) { Assert.True(text == saved, name); written++; }
            Assert.True(saved == C3dPersistence.Serialize(C3dPersistence.Deserialize(saved)), name);
            Assert.DoesNotContain("\"Terminals\"", saved);
            Assert.DoesNotContain("\"Reference\"", saved);
        }
        output.WriteLine($"{written} of {files.Count} files are the writer's own and re-save byte for byte.");
        Assert.True(written > 0);
    }

    // ── 6. Make Port ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_MakePortWave_OnThePairsFace_WritesOnePortWithTwoTerminals_AndOneUndoRemovesIt()
    {
        var doc = Pair();
        doc.Objects.Insert(0, new C3dBox { Name = "fill", Material = "Air", Min = new C3dPoint3(0, 20 * Um, 20 * Um), Size = new C3dPoint3(2000 * Um, 4960 * Um, 2000 * Um) });
        doc.Setups = [EmSetupPersistence.ToEmbedded(Setup())];
        var vm = Open(doc);
        var fill = vm.SceneObject("fill")!;
        int xmin = Enumerable.Range(0, 6).Single(i => fill.FaceName(i) == "xmin");
        int entries = vm.UndoEntries;

        Assert.Null(vm.MakePortFromFace(fill.Id, xmin, Em3dPortKind.Wave));
        var port = Assert.Single(vm.Document.Ports);
        Assert.Equal(("gnd", 0), (port.Reference, port.Number));
        Assert.Equal([(1, "P1", "strip_a"), (2, "P2", "strip_b")], port.Terminals!.Select(t => (t.Number, t.Name, t.Conductor)));
        Assert.Contains("2 terminals", vm.StatusMessage);
        Assert.Equal(entries + 1, vm.UndoEntries);

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.Ports);
    }

    // ── 7. refused to run ───────────────────────────────────────────────────────────────────────

    // brief-em3d-116 lifted openEMS's refusal (it builds terminal ports); Palace's stands (D14).
    [Theory]
    [InlineData(Em3dSolver.Palace, "Port 'Left' has two terminals; terminal wave ports run on openEMS only in this version. Set the setup's solver to openEMS.")]
    public void Gate7_ATwoTerminalSetup_IsRefusedBeforeGmsh(Em3dSolver solver, string sentence)
    {
        long gmsh = PalaceRun.GmshInvocations;
        var doc = Pair();
        doc.Ports.Add(PairPort());
        string ws = Workspace();
        string path = WriteC3d(ws, "Pair", doc);
        var setup = Setup();
        setup.Solver3D = solver;
        var run = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), Path.Combine(_root, "results"));
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains(sentence, run.Error);
        Assert.Equal(gmsh, PalaceRun.GmshInvocations);
    }

    // ── 8. explain ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_Explain_PrintsTheReference_EachTerminal_AndTheTerminalSLine()
    {
        var doc = Pair();
        doc.Ports.Add(PairPort());
        doc.Setups = [EmSetupPersistence.ToEmbedded(Setup())];
        string path = WriteC3d(Workspace(), "Pair", doc);
        var (_, stdout, stderr) = RunCli("explain", path);
        string all = stdout + stderr;
        Assert.Contains("A wave port with 2 terminals on the air box's xmin face; the reference is 'gnd'", all);
        Assert.Contains("conductor 'strip_a', Z0 50 Ω, voltage path (", all);
        Assert.Contains("conductor 'strip_b', Z0 50 Ω, voltage path (", all);
        Assert.Contains("terminal S: each terminal is a port of the result", all);
    }

    // ── the fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>The air stripline pair: a 5000 × 2040 µm shield with a 4960 × 2000 µm hole (one conductor), and two
    /// 600 × 20 µm strips at the hole's mid-height, 200 µm apart; everything 2000 µm long in x, from x = 0.</summary>
    private static C3dDocument Pair() => new()
    {
        Objects =
        [
            new C3dPrism
            {
                Name = "gnd", Material = "Copper", Plane = C3dPlane.YZ, Offset = 0, Height = 2000 * Um,
                Outline = [P(0, 0), P(5000, 0), P(5000, 2040), P(0, 2040)],
                Holes = [[P(20, 20), P(4980, 20), P(4980, 2020), P(20, 2020)]],
            },
            Box("strip_a", 0, 1800, 1010, 2000, 600, 20),
            Box("strip_b", 0, 2600, 1010, 2000, 600, 20),
        ],
    };

    private static C3dPort PairPort() => new()
    {
        Name = "Left", Kind = Em3dPortKind.Wave, Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(0, 0, 5000, 2040), Reference = "gnd",
        Terminals =
        [
            new C3dTerminal { Number = 1, Name = "P1", Conductor = "strip_a", Z0 = "50" },
            new C3dTerminal { Number = 2, Name = "P2", Conductor = "strip_b", Z0 = "50" },
        ],
    };

    /// <summary>Three bars ending on xmin, none grounded: a and b 100 µm square, the third <paramref name="big"/> µm square.</summary>
    private static C3dDocument ThreeBars(long big) => new()
    {
        Objects = [Box("a", 0, 0, 0, 1000, 100, 100), Box("b", 0, 300, 0, 1000, 100, 100), Box("big", 0, 600, 0, 1000, big, big)],
    };

    private static C3dPort BarsPort(string[]? terminals) => new()
    {
        Number = terminals is null ? 1 : 0, Name = "Bars", Kind = Em3dPortKind.Wave, Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(0, 0, 1000, 400),
        Terminals = terminals?.Select((c, k) => new C3dTerminal { Number = k + 1, Name = $"P{k + 1}", Conductor = c }).ToList(),
    };

    private static C3dPoint2 P(long u, long v) => new(u * Um, v * Um);

    private static C3dRect Rect(long u, long v, long du, long dv) => new() { Min = P(u, v), Size = P(du, dv) };

    private static C3dBox Box(string name, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    /// <summary>A Palace setup whose air box's xmin face sits on the content (padding 0), where the ports are.</summary>
    private static EmSetup Setup() => new()
    {
        Name = "S1", Solver3D = Em3dSolver.Palace,
        AirBox = new EmAirBox(XMin: new EmAirBoxFace(0, Em3dBoundaryKind.Absorbing)),
    };

    private C3dElaboration Elaborate(C3dDocument doc)
    {
        var e = C3dElaborator.ElaborateOnce(doc, WriteC3d(Workspace(), "Cell", doc), null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        return e;
    }

    private static C3dPortContext Context(C3dDocument doc, C3dElaboration e, EmSetup setup)
        => C3dProblemAssembly.PortContext(setup, doc, e, C3dProblemAssembly.AirBox(setup, e, out _));

    private IReadOnlyList<C3dPortResult> Results(C3dDocument doc, EmSetup setup)
        => C3dPorts.Resolve(doc, Context(doc, Elaborate(doc), setup));

    private (C3dPortResult Result, C3dPortContext Context) Resolve(C3dDocument doc, EmSetup setup)
    {
        var ctx = Context(doc, Elaborate(doc), setup);
        return (C3dPorts.Resolve(doc, ctx).Single(), ctx);
    }

    /// <summary>A workspace whose default technology holds Copper and Air.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Air", Epsr = 1 }],
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

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Workspace();
        string path = WriteC3d(ws, "Pair", doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        vm.SetActiveSetup("S1");
        Settle(vm);
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(TerminalWavePortTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = outTask.GetAwaiter().GetResult(), stderr = errTask.GetAwaiter().GetResult();
        output.WriteLine($"$ {string.Join(' ', args)}\n{stdout}{stderr}");
        return (proc.ExitCode, stdout, stderr);
    }
}
