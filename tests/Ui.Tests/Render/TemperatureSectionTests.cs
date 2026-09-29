// brief-em3d-88 §3 — `render --field` draws a temperature section:
//
//    1  a ClipPlane temperature plot of a committed thermal example, as a PROCESS, writes a PNG whose range is the slice's true
//       minimum and maximum (checked against the field file's own values on that plane) and the wires drawn in it   [Benchmark]
//    2  a wire crossing the plane is painted T(s) interpolated at the crossing: the pieces and crossings (pure), and a pixel
//       read back on a wire 50 K or more hotter than the mould around it (in-process page, and the process run)
//    3  a conductor in a temperature section is not filled with its material colour; an EM section's still is
//    4  a thermal page draws no port and no PEC/absorbing label, and no air-box caption
//    5  a Surfaces temperature plot asks which way to look (brief-em3d-89 draws it: SurfaceFieldRenderTests)
//
// Gate 1 solves the Output Wires example's RfHarmonics setup (~25 s in Debug, Gmsh required), so it is tagged Benchmark; the
// rest draw a synthetic page in-process or ask the committed Eight Fingers plot, and run in the routine gate.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

public sealed class TemperatureSectionTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6;
    private const int W = 800, H = 600;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em88-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 2. the wire's pieces ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ACrossingIsItsInterpolatedT_AndAWireInThePlaneIsPaintedAlongItsChain()
    {
        // a slant through x = 0: T 100 → 300 over the segment, so 200 where it crosses, halfway along
        var slant = new ThermalWireChain("slant", 25 * Um, [new(-10 * Um, 0, 0), new(10 * Um, 0, 10 * Um)], [0, 22.36 * Um], [100, 300]);
        var (pieces, crossings) = Em3dSectionThermal.Wires([slant], axis: 0, at: 0);
        var c = Assert.Single(crossings);
        Assert.Equal(200, c.T, 9);
        Assert.Equal(11.18 * Um, c.S, 12);
        Assert.Equal(new Uv(0, 5 * Um), c.At);
        Assert.NotEmpty(pieces);
        Assert.All(pieces.SelectMany(p => p.T), t => Assert.InRange(t, 100, 300));

        // lying in y = 0: a strip per segment whose chords carry the nodes' own T at its ends, and a disc per node under them
        var flat = new ThermalWireChain("flat", 25 * Um, [new(0, 0, 0), new(50 * Um, 0, 0), new(100 * Um, 0, 20 * Um)], [0, 50 * Um, 104 * Um], [100, 200, 300]);
        (pieces, crossings) = Em3dSectionThermal.Wires([flat], axis: 1, at: 0);
        Assert.Empty(crossings);
        Assert.Equal(5, pieces.Count);
        var strips = pieces.Where(p => p.T.Distinct().Count() > 1).ToList();
        Assert.Equal(2, strips.Count);
        Assert.Equal((100.0, 200.0), (strips[0].T[0], strips[0].T[^1]));
        Assert.Equal((200.0, 300.0), (strips[1].T[0], strips[1].T[^1]));
        Assert.Equal(25 * Um, Math.Abs(strips[0].Left[0].V - strips[0].Right[0].V), 12);       // the full diameter, in the plane
        Assert.True(pieces.IndexOf(strips[0]) > 2, "the node discs are drawn under the strips");
    }

    // ── 2, 3, 4. the thermal page, in-process ───────────────────────────────────────────────

    [Fact]
    public void Gate2_3_4_TheThermalPage_PaintsTheWire_OutlinesTheMetal_AndDrawsNoPortOrAirBox()
    {
        var (scene, style, flangeColour) = Page();
        var q = new FieldQuantity(new FieldArrayInfo(FieldNames.TemperatureArray, 1, false, false), false, FieldMode.Value);
        // the slice: 100 °C at x = −1 mm to 200 °C at x = +1 mm, over the whole frame
        double x0 = -1e-3, x1 = 1e-3, z0 = 0, z1 = 0.4e-3;
        double[] xyz = [x0, 0, z0, x1, 0, z0, x1, 0, z1, x0, 0, z0, x1, 0, z1, x0, 0, z1];
        double T(double x) => 100 + 100 * (x - x0) / (x1 - x0);
        var slice = new FieldSurface { Channels = 1, Xyz = xyz, Values = [.. Enumerable.Range(0, 6).Select(i => T(xyz[3 * i]))] };
        var cut = new FieldSectionCut(q, slice, FieldColorScale.MinMax(q, [slice]), ColorMap3D.For(q), (0, 0, 0), 1);
        // a wire at 300 °C crossing y = 0 at (0.5 mm, 0.25 mm)
        var wire = new ThermalWireChain("w", 25 * Um, [new(0.5e-3, -20 * Um, 0.25e-3), new(0.5e-3, 20 * Um, 0.25e-3)], [0, 40 * Um], [300, 300]);
        var (pieces, crossings) = Em3dSectionThermal.Wires([wire], 1, 0);
        cut = Em3dSectionThermal.WithWires(cut, pieces);
        Assert.Equal((100.0, 300.0), (cut.Scale.Lo, cut.Scale.Hi));
        var marks = new List<Em3dBoundaryMark> { new("flange/zmin", ThermalBoundaryKind.FixedT, "flange/zmin: held at 85 °C", [(new Uv(x0, 0), new Uv(x1, 0))]) };
        var page = new Em3dThermalPage(pieces, crossings, marks, Em3dSectionThermal.Caption(scene, "Hot", "the only point", marks, 1));

        var layout = Em3dSectionRenderer.Layout(W, H, scene, 0.05);
        using var bmp = Draw(scene, style, Em3dSectionField.Build(cut, 0, raster: true, null, ["T"], page));
        double Read(Uv at) { var p = layout.Map(at); return cut.Scale.Lo + Invert(cut.Map, bmp.GetPixel((int)p.X, (int)p.Y)) * (cut.Scale.Hi - cut.Scale.Lo); }

        // 2 — the wire's pixel is its own T, and the mould four diameters above it is the field's, more than 50 K cooler
        double onWire = Read(crossings.Single().At), mould = Read(new Uv(0.5e-3, 0.35e-3));
        output.WriteLine($"wire {onWire:F1} °C, mould {mould:F1} °C");
        Assert.Equal(300, onWire, 2.0);
        Assert.Equal(T(0.5e-3), mould, 2.0);
        Assert.True(onWire - mould >= 50);
        // 3 — inside the flange: the field's colour, not the flange's
        var inFlange = new Uv(-0.5e-3, 0.05e-3);
        Assert.Equal(T(-0.5e-3), Read(inFlange), 2.0);
        var pf = layout.Map(inFlange);
        Assert.NotEqual(flangeColour, bmp.GetPixel((int)pf.X, (int)pf.Y).WithAlpha(0xFF));
        // … and an EM field's page still fills it
        var em = new FieldQuantity(new FieldArrayInfo("V", 1, false, false), false, FieldMode.Value);
        using (var emBmp = Draw(scene, style, Em3dSectionField.Build(cut with { Quantity = em, Map = ColorMap3D.For(em) }, 0, raster: true, null, ["V"])))
            Assert.Equal(flangeColour, emBmp.GetPixel((int)pf.X, (int)pf.Y).WithAlpha(0xFF));

        // 4 — no port, no air-box face or caption
        string svg = Svg(scene, style, Em3dSectionField.Build(cut, 0, raster: false, null, ["T"], page));
        foreach (string gone in (string[])[">P1<", "PEC", "absorbing", "Air box", "Ports are drawn"])
            Assert.DoesNotContain(gone, svg);
        Assert.Contains("flange/zmin: held at 85 °C", svg);
        Assert.Contains("Temperature, XZ section", svg);

        // --axes and --scale-bar: the 3D view's axis indicator (X right, Z up on an XZ section) and a round bar in µm
        svg = Svg(scene, style with { Axes = true, ScaleBar = (CircuitRF.Design.Layout.LayoutUnit.Um, 1000) },
                  Em3dSectionField.Build(cut, 0, raster: false, null, ["T"], page));
        Assert.Matches(@">\s*X\s*</text>", svg);
        Assert.Matches(@">\s*Z\s*</text>", svg);
        Assert.Matches(@">\s*(1|2|5)0* µm\s*</text>", svg);
    }

    // ── 5. a direction ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_ASurfacesTemperaturePlot_AsksForADirection_AndAnIsometricScaleBarIsRefused()
    {
        string c3d = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Thermal Channel vs Surface", "Eight Fingers", "3d", "Eight Fingers.c3d");
        string png = Path.Combine(_root, "never.png");
        var (exit, stdout, stderr) = Cli("render", c3d, "-o", png, "--field", "Surface", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"render.field.direction-required\"", stdout);
        Assert.Contains("is drawn on every exposed face, and the 3D view's camera is not saved with it", stderr);
        Assert.False(File.Exists(png));

        (exit, stdout, _) = Cli("render", c3d, "-o", png, "--iso", "--scale-bar", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"render.em3d.scale-bar-iso\"", stdout);
        Assert.False(File.Exists(png));
    }

    // ── 1. the example, as a process ────────────────────────────────────────────────────────

    [GmshFact]
    [Trait("Category", "Benchmark")]
    public void Gate1_TheExamplesTemperaturePlot_RendersWithTheSlicesTrueRange_AndItsWires()
    {
        string ws = Path.Combine(_root, "Thermal Output Wires");
        CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Thermal Output Wires"), ws);
        string c3d = Path.Combine(ws, "Output", "3d", "Output.c3d");
        var run = Cli("em", c3d, "--setup", "RfHarmonics");
        Assert.True(run.Exit == 0, run.StdErr);

        const string Plot = "RF 8 A peak — across the six wires";
        string png = Path.Combine(_root, "across.png");
        var (exit, stdout, stderr) = Cli("render", c3d, "-o", png, "--field", Plot, "--size", $"{W}x{H}", "--json");
        Assert.True(exit == 0, stderr + stdout);
        Assert.True(File.Exists(png));
        var field = JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("render").GetProperty("em3d").GetProperty("field");
        double lo = field.GetProperty("range").GetProperty("lo").GetDouble(), hi = field.GetProperty("range").GetProperty("hi").GetDouble();
        var wires = field.GetProperty("wires").EnumerateArray().ToList();

        // the slice, cut from the field file here, on the plot's own plane at the view's own origin
        var doc = C3dPersistence.LoadFromFile(c3d);
        var e = new C3dElaborator().Elaborate(doc, c3d, Path.Combine(ws, ".cws"));
        var origin = FieldPlotResolver.SceneOrigin(e.DisplayExtent());
        var plot = doc.FieldPlots.Single(p => p.Name == Plot);
        var thermal = FieldRun.OpenPalace(Path.Combine(ws, "results", "Output RfHarmonics.thermal"), GmshGeoWriter.LengthUnitM)!;
        var step = FieldStep.Open(thermal.Solutions[plot.Solution!.Point!.Value - 1].VolumePvtu!, thermal.ToMetres);
        var slice = FieldSection.Slice(step.Mesh, step.Load(FieldNames.TemperatureArray)!, origin, FieldPlotResolver.ScenePlane(plot, doc.DbuPerMicron, origin));
        double sliceLo = slice.Values.Min(), sliceHi = slice.Values.Max();
        double wiresLo = wires.Min(w => w.GetProperty("lo").GetDouble()), wiresHi = wires.Max(w => w.GetProperty("hi").GetDouble());
        output.WriteLine($"slice {sliceLo:F2} … {sliceHi:F2}, wires {wiresLo:F2} … {wiresHi:F2}, range {lo:F2} … {hi:F2}");
        Assert.Equal(Math.Min(sliceLo, wiresLo), lo, 1e-9);
        Assert.Equal(Math.Max(sliceHi, wiresHi), hi, 1e-9);

        // six wires, each crossing once; the hottest crossing's pixel is its T, and it stands 50 K or more above the mould
        Assert.Equal(6, wires.Count);
        Assert.All(wires, w => Assert.Single(w.GetProperty("crossings").EnumerateArray()));
        var hot = wires.Select(w => w.GetProperty("crossings")[0]).MaxBy(x => x.GetProperty("t").GetDouble());
        var problem = C3dProblemAssembly.ViewProblem(e.Solids, e.Sheets, e.Materials, [], C3dProblemAssembly.ExtentBox(e.Extent()!.Value));
        var scene = Em3dSectionScene.Build(problem, new Em3dView(Em3dViewKind.SectionX, FieldPlotResolver.PlaneMetres(plot, doc.DbuPerMicron)));
        var layout = Em3dSectionRenderer.Layout(W, H, scene, DocumentExtents.DefaultMargin);
        using var bmp = SKBitmap.Decode(png);
        var map = ColorMap3D.Inferno;
        double Read(double u, double v) { var p = layout.Map(new Uv(u, v)); return lo + Invert(map, bmp.GetPixel((int)p.X, (int)p.Y)) * (hi - lo); }
        double u0 = hot.GetProperty("u").GetDouble(), v0 = hot.GetProperty("v").GetDouble(), t0 = hot.GetProperty("t").GetDouble();
        double onWire = Read(u0, v0), mould = Read(u0, v0 + 100 * Um);
        output.WriteLine($"hottest crossing T(s) {t0:F1} °C, its pixel {onWire:F1} °C, the mould 100 µm above {mould:F1} °C");
        Assert.Equal(t0, onWire, (hi - lo) / 100);
        Assert.True(onWire - mould >= 50, $"{onWire:F1} vs {mould:F1}");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A flange (a conductor) under a mould, with a die and a port: a section at y = 0, and its style. Neither the flange
    /// nor the mould spans the box, so the frame is framed on them and is the whole box.</summary>
    private static (Em3dScene Scene, Em3dRenderStyle Style, SKColor Flange) Page()
    {
        const double mm = 1e-3;
        var solids = new List<Em3dSolid>
        {
            new("flange", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.9 * mm, -0.9 * mm, 0), new(0.9 * mm, 0.9 * mm, 0.1 * mm)), 1),
            new("mould", "Mold", Em3dRole.Dielectric, new Em3dBox(new(-0.9 * mm, -0.9 * mm, 0.1 * mm), new(0.9 * mm, 0.9 * mm, 0.4 * mm)), 2),
            new("die", "SiC", Em3dRole.Dielectric, new Em3dBox(new(-0.3 * mm, -0.3 * mm, 0.1 * mm), new(0.3 * mm, 0.3 * mm, 0.2 * mm)), 3),
        };
        var port = new Em3dPort(1, "port/1", "flange", "die", new(-0.3 * mm, -0.1 * mm, 0.1 * mm), new(-0.3 * mm, 0.1 * mm, 0.2 * mm),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(-0.3 * mm, 0, 0.1 * mm), new(0, 0, 1), 0));
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 0.4 * mm),
            new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                          Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));
        var problem = C3dProblemAssembly.ViewProblem(solids, [],
            [new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7), new Em3dMaterial("Mold", 4, null, 0, 1, 0), new Em3dMaterial("SiC", 9.7, null, 0, 1, 0)], [port], box);
        var scene = Em3dSectionScene.Build(problem, new Em3dView(Em3dViewKind.SectionY, 0));
        var flange = new SKColor(200, 120, 40);
        return (scene, new Em3dRenderStyle(new Dictionary<string, SKColor> { ["flange"] = flange }, ColorTheme.BuiltIn, ColorVariant.Light, 0.05, false), flange);
    }

    private static SKBitmap Draw(Em3dScene scene, Em3dRenderStyle style, Em3dFieldLayer layer)
    {
        var bmp = new SKBitmap(W, H);
        using var canvas = new SKCanvas(bmp);
        Em3dSectionRenderer.Draw(canvas, W, H, scene, style, layer);
        return bmp;
    }

    private static string Svg(Em3dScene scene, Em3dRenderStyle style, Em3dFieldLayer layer)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, W, H), stream)) Em3dSectionRenderer.Draw(canvas, W, H, scene, style, layer);
        return Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
    }

    /// <summary>Where <paramref name="c"/> falls on <paramref name="map"/>, 0..1: the nearest of 4,096 samples.</summary>
    private static double Invert(ColorMap3D map, SKColor c)
    {
        double best = double.MaxValue, at = 0;
        for (int i = 0; i <= 4096; i++)
        {
            var (r, g, b) = map.Sample(i / 4096f);
            double d = (r - c.Red) * (r - c.Red) + (g - c.Green) * (g - c.Green) + (b - c.Blue) * (b - c.Blue);
            if (d < best) { best = d; at = i / 4096.0; }
        }
        return at;
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

    private static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = typeof(TemperatureSectionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
