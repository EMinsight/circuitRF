// brief-em3d-89 — `render --field` draws a Surfaces or Faces temperature plot, the verb run as a PROCESS on a synthetic thermal
// run: a 20 µm gold block at T = 25 + x + 2z (°C, x and z in µm), written by brief 74's own writer, in a document with a
// symmetry plane at x = 0.
//
//    1  All Faces from the top: the top face is what is drawn (a pixel reads its T, not the bottom's 40 °C less), the mirrored
//       half repeats it, the picture is framed on both halves and the range is the true minimum and maximum; --no-mirror
//       draws the modelled half alone
//    2  a Faces plot paints its face in front of the shaded block it lies on, ranged on that face alone
//
// The EM side (a Surfaces plot of |E| on the committed cavity) and the refusals are FieldRenderCliTests' gates 6, 7 and 11.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

public sealed class SurfaceFieldRenderTests(ITestOutputHelper output) : IDisposable
{
    private const long Um = 1000;                                           // DBU per micron
    private const double UmM = 1e-6;
    private const int W = 800, H = 600;
    private const double Margin = 0.05;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em89-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static double T(double xUm, double zUm) => 25 + xUm + 2 * zUm;

    // ── 1. All Faces, in depth, mirrored ────────────────────────────────────────────────────

    [Fact]
    public void Gate1_AllFacesFromTheTop_DrawsTheTopFace_MirroredAndRangedOnWhatIsDrawn_AndNoMirrorDrawsTheModelledHalf()
    {
        string c3d = Workspace();
        string png = Path.Combine(_root, "all.png");
        var render = Render(c3d, png, "--field", "All", "--view-dir", "top");
        var field = render.GetProperty("em3d").GetProperty("field");
        var viewport = render.GetProperty("viewport");
        double lo = field.GetProperty("range").GetProperty("lo").GetDouble(), hi = field.GetProperty("range").GetProperty("hi").GetDouble();
        output.WriteLine($"range {lo} … {hi}; frame x {viewport.GetProperty("x0").GetDouble() / UmM:G6} … {viewport.GetProperty("x1").GetDouble() / UmM:G6} µm");
        Assert.Equal(1, field.GetProperty("mirrored").GetInt32());
        Assert.Equal(T(0, 0), lo, 1e-6);                                   // the bottom's cold corner
        Assert.Equal(T(20, 20), hi, 1e-6);                                 // the top's hot edge
        Assert.Equal(-20 * UmM, viewport.GetProperty("x0").GetDouble(), 1e-9);
        Assert.Equal(20 * UmM, viewport.GetProperty("x1").GetDouble(), 1e-9);

        // from the top the picture is (x, y): a point of the top face reads the top's T — the bottom under it is 40 °C colder —
        // and its reflection across x = 0 reads the same
        using (var bmp = SKBitmap.Decode(png))
        {
            var page = Page(-20, 0, 20, 20);
            foreach (double x in (double[])[15, -15])
            {
                double got = Read(bmp, page, x, 10, lo, hi);
                output.WriteLine($"x = {x} µm: pixel {got:F2} °C, the top face {T(15, 20):F2}, the bottom {T(15, 0):F2}");
                Assert.Equal(T(15, 20), got, (hi - lo) / 50);
            }
        }

        var alone = Render(c3d, Path.Combine(_root, "half.png"), "--field", "All", "--view-dir", "top", "--no-mirror");
        Assert.Equal(0, alone.GetProperty("em3d").GetProperty("field").GetProperty("mirrored").GetInt32());
        Assert.Equal(0, alone.GetProperty("viewport").GetProperty("x0").GetDouble(), 1e-9);
    }

    // ── 2. a Faces plot ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_AFacesPlot_PaintsItsFaceInFrontOfTheShadedBlock_RangedOnThatFace()
    {
        string c3d = Workspace();
        string png = Path.Combine(_root, "top.png");
        var field = Render(c3d, png, "--field", "Top", "--view-dir", "top", "--no-mirror").GetProperty("em3d").GetProperty("field");
        double lo = field.GetProperty("range").GetProperty("lo").GetDouble(), hi = field.GetProperty("range").GetProperty("hi").GetDouble();
        Assert.Equal(T(0, 20), lo, 1e-6);
        Assert.Equal(T(20, 20), hi, 1e-6);
        using var bmp = SKBitmap.Decode(png);
        double got = Read(bmp, Page(0, 0, 20, 20), 15, 10, lo, hi);
        output.WriteLine($"painted top face at x = 15 µm: {got:F2} °C (T there {T(15, 20):F2})");
        Assert.Equal(T(15, 20), got, (hi - lo) / 50);
    }

    // ── the fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>The block, its thermal setup, a symmetry plane at x = 0, an All Faces plot and a Faces plot of its top — and the
    /// setup's finished run: the field files (brief 74's writer), the group table naming the block, and the solved document.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws");
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
        C3dPersistence.SaveToFile(c3d, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "block", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) }],
            SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 0 }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
            FieldPlots =
            [
                new C3dFieldPlot { Name = "All", Setup = "Hot", Solution = point, Quantity = C3dFieldPlot.TemperatureQuantity, On = C3dFieldPlotOn.Surfaces },
                new C3dFieldPlot
                {
                    Name = "Top", Setup = "Hot", Solution = point, Quantity = C3dFieldPlot.TemperatureQuantity, On = C3dFieldPlotOn.Faces,
                    Faces = [new C3dFieldPlotFace { Face = "block/zmax" }],
                },
            ],
        });

        var doc = C3dPersistence.LoadFromFile(c3d);
        string run = ThermalRunService.RunDirectory(Path.Combine(ws, "results"), C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, c3d));
        var (mesh, _) = SyntheticThermal.Block(2, 10, 1, (_, _, _) => 0);
        var t = Enumerable.Range(0, mesh.NodeCount).Select(n => T(mesh.Nodes[3 * n] * 1e6, mesh.Nodes[3 * n + 2] * 1e6)).ToArray();
        ThermalFieldFiles.Write(run, mesh, [t], [1, 1]);
        File.WriteAllText(Path.Combine(run, GmshGeoWriter.GroupsFile),
            JsonSerializer.Serialize(new { Groups = new[] { new { Name = "block", Attribute = 1, Dimension = 3, Kind = "Conductor" } } }));
        C3dRunInputs.Take(doc, c3d, C3dElaborator.ElaborateOnce(doc, c3d, null).FilesRead).KeepIn(run);
        return c3d;
    }

    /// <summary>The page a top view framed on x0..x1, y0..y1 (µm) is laid out on — Em3dSectionRenderer.Layout, as the picture was.</summary>
    private static Em3dPageLayout Page(double x0, double y0, double x1, double y1)
        => Em3dSectionRenderer.Layout(W, H, new Em3dScene(new Em3dView(Em3dViewKind.Projection, 0), 0, 0, [], [], [], [],
                                                          new Uv(x0 * UmM, y0 * UmM), new Uv(x1 * UmM, y1 * UmM), [], []), Margin);

    /// <summary>The temperature a pixel at (x, y) µm reads, inverted through the temperature's map and the range.</summary>
    private static double Read(SKBitmap bmp, Em3dPageLayout page, double x, double y, double lo, double hi)
    {
        var p = page.Map(new Uv(x * UmM, y * UmM));
        var c = bmp.GetPixel((int)p.X, (int)p.Y);
        double best = double.MaxValue, at = 0;
        for (int i = 0; i <= 4096; i++)
        {
            var (r, g, b) = ColorMap3D.Inferno.Sample(i / 4096f);
            double d = (r - c.Red) * (r - c.Red) + (g - c.Green) * (g - c.Green) + (b - c.Blue) * (b - c.Blue);
            if (d < best) { best = d; at = i / 4096.0; }
        }
        return lo + at * (hi - lo);
    }

    private JsonElement Render(string c3d, string png, params string[] extra)
    {
        var (exit, stdout, stderr) = Cli([.. new[] { "render", c3d, "-o", png, "--size", $"{W}x{H}", "--margin", "0.05" }, .. extra, "--json"]);
        Assert.True(exit == 0, stderr + stdout);
        return JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("render");
    }

    private static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = typeof(SurfaceFieldRenderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
