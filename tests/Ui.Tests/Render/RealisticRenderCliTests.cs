// brief-em3d-110 — `render x.c3d --look realistic`: the realistic view's picture with no window, drawn on the CPU by RealisticPicture.
//
//    1  plain unchanged: with no --look, the verb's bytes are the hashes recorded before the brief began (macOS, where they were
//       recorded — the caption's text rasterises differently on another platform)
//    2  the Look is the file's: Exposure changes the picture, and --look-set Exposure=… is byte-identical to editing the file
//    3  refusals: an SVG, a .cem, an out-of-range --look-set — each its sentence, exit 1
//    4  deterministic: the same command twice, and on one thread, gives the same bytes
//    6  cancelled: exit 130, nothing written
//    7  as a process: the verb run as a process is byte-identical to the in-process call (a Lit field plot on it)
//    8  explain --look: every slot with its provenance, and a missing .hdr named
//   D1  a perspective Look.Camera is the picture's camera; --iso overrides it; with neither, a camera is required
//
// Gate 5 (GPU = CPU) and D1's perspective camera are RealisticPictureTests', beside the Metal backend.

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

// Gate 7 compares rendered TEXT bytes against a process: SkiaFontsTypefaceCollection's membership rule. It is also the group
// RenderCliVerbTests and Em3dRenderExplainTests drive the verb in process in, whose RunHost and JsonRun this class shares.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class RealisticRenderCliTests(ITestOutputHelper output) : IDisposable
{
    private const long Um = 1000;                                           // DBU per micron
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em110-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        // CRF_REALISTIC_PNG=<dir> keeps the pictures, to look at
        if (Environment.GetEnvironmentVariable("CRF_REALISTIC_PNG") is { Length: > 0 } keep && Directory.Exists(_root))
            foreach (string png in Directory.GetFiles(_root, "*.png")) File.Copy(png, Path.Combine(keep, Path.GetFileName(png)), true);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. plain unchanged ──────────────────────────────────────────────────────────────────

    /// <summary>The verb's bytes for the two plain pictures of the fixture, recorded by running the verb on 2026-10-04 before any of
    /// brief 110's changes (macOS arm64): the isometric outline, and a Faces plot drawn by Em3dSurfaceField — whose rasteriser this
    /// brief factored out, so its bytes are the refactor's check.</summary>
    private const string IsoHash = "5B4496AC252C42E73B7B8E15F8BF2652FCE675EBED734F4044CE449F60C36896",
                         SurfaceHash = "5D234DE94494EC0DED4D6D0ACF87980408EDA35E81972C8C68DFFBA69238A345";

    [RecordedOnMacOSFact]
    public void Gate1_WithNoLook_TheVerbsBytesAreTheOnesRecordedBeforeTheBrief()
    {
        string c3d = Workspace();
        string iso = Path.Combine(_root, "iso.png"), top = Path.Combine(_root, "top.png");
        Assert.Equal(0, InProcess("render", c3d, "-o", iso, "--iso", "--size", "400x300"));
        Assert.Equal(0, InProcess("render", c3d, "-o", top, "--field", "Top", "--view-dir", "top", "--no-mirror", "--size", "400x300"));
        string isoHash = Hash(iso), topHash = Hash(top);
        output.WriteLine($"iso {isoHash}\ntop {topHash}");
        Assert.Equal(IsoHash, isoHash);
        Assert.Equal(SurfaceHash, topHash);
    }

    // ── 2. the Look is the file's ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The picture is drawn with the .c3d's own Look: a file stating Exposure 1.5 draws a different picture from one stating
    /// none, and the first file with <c>--look-set Exposure=1.5</c> is byte-identical to the second.</summary>
    [Fact]
    public void Gate2_TheFilesExposureChangesThePicture_AndLookSetIsEditingTheFile()
    {
        string plain = Workspace(name: "a"), edited = Workspace(d => d.Look = new C3dLook { Exposure = 1.5 }, name: "b");
        string a = Out("a.png"), b = Out("b.png"), c = Out("c.png");
        Assert.Equal(0, InProcess(Realistic(plain, a)));
        Assert.Equal(0, InProcess(Realistic(edited, b)));
        Assert.Equal(0, InProcess([.. Realistic(plain, c), "--look-set", "Exposure=1.5"]));
        Assert.NotEqual(Hash(a), Hash(b));
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(c));
    }

    // ── 3. refusals ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AnSvg_ACem_AndAnOutOfRangeLookSet_EachRefuseWithTheirSentence()
    {
        string c3d = Workspace();
        string cem = Path.Combine(_root, "setup.cem");
        File.WriteAllText(cem, "{}");
        (string[] Args, string Says)[] cases =
        [
            ([.. Realistic(c3d, Out("x.svg"))], "a realistic picture is pixels; use .png"),
            ([.. Realistic(cem, Out("y.png"))], "has no appearances and no Look"),
            ([.. Realistic(c3d, Out("z.png")), "--look-set", "Exposure=20"], "The Look's Exposure is 20; it is -10 to 10."),
        ];
        foreach (var (args, says) in cases)
        {
            var (exit, _, err) = Captured(args);
            output.WriteLine(err.Trim());
            Assert.Equal(1, exit);
            Assert.Contains(says, err);
        }
        Assert.False(File.Exists(Out("z.png")));
    }

    // ── 4. deterministic ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_TheSameCommandTwice_AndOnOneThread_GivesTheSameBytes()
    {
        string c3d = Workspace();
        string a = Out("a.png"), b = Out("b.png"), one = Out("one.png");
        Assert.Equal(0, InProcess(Realistic(c3d, a)));
        Assert.Equal(0, InProcess(Realistic(c3d, b)));
        Environment.SetEnvironmentVariable(RealisticPicture.ThreadsVariable, "1");
        try { Assert.Equal(0, InProcess(Realistic(c3d, one))); }
        finally { Environment.SetEnvironmentVariable(RealisticPicture.ThreadsVariable, null); }
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(one));
    }

    // ── 6. cancelled ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cancelled mid-picture — as the occlusion pass begins, its bands checking the token (a stage only the realistic picture
    /// reports, so no other in-process render is the trigger) — the verb exits 130 and the output path does not exist.</summary>
    [Fact]
    public void Gate6_ACancelledRealisticRender_Exits130AndWritesNothing()
    {
        string c3d = Workspace();
        string png = Out("cancelled.png");
        using var cts = new CancellationTokenSource();
        var stages = new List<string>();
        using (RunHost.Install(cts.Token, p =>
        {
            lock (stages) stages.Add(p.Stage);
            if (p.Stage == "occlusion") cts.Cancel();
        }))
            Assert.Equal(130, InProcess(Realistic(c3d, png)));
        Assert.False(File.Exists(png), "a cancelled render published a partial result");
        Assert.Contains("shadow", stages);
    }

    // ── 7. as a process ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The verb run as a process writes the bytes the in-process call writes — a field plot on it, drawn Lit, so the indicator
    /// and the legend are in both.</summary>
    [Fact]
    public void Gate7_TheVerbAsAProcess_IsByteIdenticalToTheInProcessCall()
    {
        string c3d = Workspace();
        string[] extra = ["--field", "Top", "--no-mirror", "--look-set", "FieldStyle=Lit"];
        string inProcess = Out("in.png"), process = Out("process.png");
        Assert.Equal(0, InProcess([.. Realistic(c3d, inProcess), .. extra]));
        var (exit, stdout, stderr) = Process([.. Realistic(c3d, process), .. extra]);
        Assert.True(exit == 0, stderr + stdout);
        Assert.Equal(File.ReadAllBytes(inProcess), File.ReadAllBytes(process));
    }

    // ── 8. explain --look ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_ExplainLook_ListsEverySlotWithItsProvenance_AndNamesAMissingHdr()
    {
        string c3d = Workspace(d => d.Look = new C3dLook { Environment = "studio.hdr", Exposure = 0.5 });
        var (exit, stdout, stderr) = Captured("explain", c3d, "--look", "--json");
        Assert.True(exit == 0, stderr + stdout);
        var steps = JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("explain").GetProperty("walks").EnumerateArray()
                                .ToDictionary(w => w.GetProperty("step").GetString()!, w => w.Clone());
        foreach (string key in C3dLook.Keys) Assert.True(steps.ContainsKey("look " + key), $"no step for {key}");
        Assert.Equal("the file", steps["look Exposure"].GetProperty("from").GetString());
        Assert.Equal("the default", steps["look Rotation"].GetProperty("from").GetString());
        string environment = steps["look environment"].GetProperty("resolved").GetString()!;
        Assert.Contains(Path.Combine(Path.GetDirectoryName(c3d)!, "studio.hdr"), environment);
        Assert.Contains("does not exist", environment);
        var slots = steps.Where(kv => kv.Key.StartsWith("appearance slot ", StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
        Assert.NotEmpty(slots);
        foreach (var slot in slots)
        {
            string resolved = slot.GetProperty("resolved").GetString()!;
            output.WriteLine(resolved);
            Assert.Contains("used by block", resolved);
            Assert.Contains("BaseColor (", resolved);
            Assert.Contains("Metallic", resolved);
            Assert.Matches(@"Roughness [0-9.]+ \(", resolved);              // every field names the statement that decided it
        }
    }

    // ── D1. the Look's camera ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A .c3d holding a perspective Look.Camera is drawn from it with no direction typed; --iso overrides it; and with no
    /// camera at all, a direction is required.</summary>
    [Fact]
    public void D1_APerspectiveLookCamera_IsThePicturesCamera_AndIsoOverridesIt()
    {
        var camera = new C3dLookCamera { Direction = [1, -2, 1.5], Target = [10 * Um, 10 * Um, 10 * Um], Distance = 90 * Um, FovY = 30 };
        string c3d = Workspace(d => d.Look = new C3dLook { Camera = camera });
        var own = Report(c3d, Out("own.png"));
        Assert.Equal(("Look.Camera", "perspective"), (own.GetProperty("camera").GetString(), own.GetProperty("projection").GetString()));
        var iso = Report(c3d, Out("iso.png"), "--iso");
        Assert.Equal(("--iso", "orthographic"), (iso.GetProperty("camera").GetString(), iso.GetProperty("projection").GetString()));
        Assert.NotEqual(Hash(Out("own.png")), Hash(Out("iso.png")));

        string bare = Workspace(name: "bare");
        var (exit, _, err) = Captured("render", bare, "-o", Out("none.png"), "--look", "realistic");
        Assert.Equal(1, exit);
        Assert.Contains("needs a camera", err);
    }

    private JsonElement Report(string c3d, string png, params string[] extra)
    {
        var (exit, stdout, stderr) = Captured(["render", c3d, "-o", png, "--look", "realistic", "--size", "160x120", "--supersample", "1", .. extra, "--json"]);
        Assert.True(exit == 0, stderr + stdout);
        return JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("render").GetProperty("em3d").GetProperty("look").Clone();
    }

    /// <summary>A small realistic picture of <paramref name="c3d"/> from the isometric view.</summary>
    private static string[] Realistic(string c3d, string png) => ["render", c3d, "-o", png, "--look", "realistic", "--iso", "--size", "160x120"];

    private string Out(string name) => Path.Combine(_root, name);

    // ── the fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>A 20 µm gold block with a thermal setup, a symmetry plane at x = 0, a Faces plot of its top, and the setup's finished
    /// run (brief 74's writer, T = 25 + x + 2z °C) — SurfaceFieldRenderTests' fixture.</summary>
    private string Workspace(Action<C3dDocument>? edit = null, string name = "ws")
    {
        string ws = Path.Combine(_root, name);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string c3d = Path.Combine(dir, "cell.c3d");
        var setup = new EmSetup
        {
            Name = "Hot", Solver3D = Em3dSolver.None, Problem3D = Em3dProblemType.Thermal,
            Thermal = new CemThermal { Boundaries = [new CemThermalBoundary { Face = "block/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }] },
        };
        var point = new C3dFieldSolution { Point = 1 };
        var document = new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "block", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) }],
            SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 0 }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
            FieldPlots =
            [
                new C3dFieldPlot
                {
                    Name = "Top", Setup = "Hot", Solution = point, Quantity = C3dFieldPlot.TemperatureQuantity, On = C3dFieldPlotOn.Faces,
                    Faces = [new C3dFieldPlotFace { Face = "block/zmax" }],
                },
            ],
        };
        edit?.Invoke(document);
        C3dPersistence.SaveToFile(c3d, document);

        var doc = C3dPersistence.LoadFromFile(c3d);
        string run = ThermalRunService.RunDirectory(Path.Combine(ws, "results"), C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, c3d));
        var (mesh, _) = SyntheticThermal.Block(2, 10, 1, (_, _, _) => 0);
        var t = Enumerable.Range(0, mesh.NodeCount).Select(n => 25 + mesh.Nodes[3 * n] * 1e6 + 2 * mesh.Nodes[3 * n + 2] * 1e6).ToArray();
        ThermalFieldFiles.Write(run, mesh, [t], [1, 1]);
        File.WriteAllText(Path.Combine(run, GmshGeoWriter.GroupsFile),
            JsonSerializer.Serialize(new { Groups = new[] { new { Name = "block", Attribute = 1, Dimension = 3, Kind = "Conductor" } } }));
        C3dRunInputs.Take(doc, c3d, C3dElaborator.ElaborateOnce(doc, c3d, null).FilesRead).KeepIn(run);
        return c3d;
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    // ── driving the verb ─────────────────────────────────────────────────────────────────────

    private static int InProcess(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    /// <summary>The verb's exit, stdout and stderr — a process, since Console is one writer for every class running in parallel.</summary>
    private static (int Exit, string StdOut, string StdErr) Captured(params string[] args) => Process(args);

    private static (int Exit, string StdOut, string StdErr) Process(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = typeof(RealisticRenderCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}

/// <summary>Gate 1's hashes were recorded on macOS: the caption's text rasterises differently elsewhere, so there it is skipped.</summary>
internal sealed class RecordedOnMacOSFactAttribute : FactAttribute
{
    public RecordedOnMacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "the plain pictures' hashes were recorded on macOS, and their text rasterises differently elsewhere";
    }
}
