using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Theming;
using CircuitRF.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-bsp-3 — what a bottom-side part does to every output: the flat exports, the placement table,
/// the hierarchical exports' bottom variants, the board-format round trip, EM extraction, LVS and
/// <c>check</c>. One test per claim; every fixture authors <see cref="LayoutInstance.Side"/> directly.
/// </summary>
public sealed class BottomSideOutputTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crfBottomOut_").FullName;

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_ws);
        try { Directory.Delete(_ws, recursive: true); } catch { /* best effort */ }
    }

    private const string TwoLayer = "pcb-2layer_RO4350B_20mil_1oz";
    private const int Dbu = 1000;

    private static LayerKey Key(Technology t, string name) => t.Layers.Single(l => l.Name == name).Key;
    private static long Um(double um) => (long)Math.Round(um * Dbu);

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_ws, name);
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 1 };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private static string Clay(string cellDir) => Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay");

    /// <summary>A two-land chip whose lands are NOT symmetric about its origin, with pins and a silk line,
    /// so a land in the wrong place or on the wrong face cannot pass by symmetry.</summary>
    private string CreateChip(Technology t) => CreateCell("Chip", v =>
    {
        v.Shapes.Add(new RectShape { Layer = Key(t, "Top Copper"), X1 = Um(-900), Y1 = Um(-100), X2 = Um(-500), Y2 = Um(300), Pin = "1" });
        v.Shapes.Add(new RectShape { Layer = Key(t, "Top Copper"), X1 = Um(400),  Y1 = Um(-300), X2 = Um(800),  Y2 = Um(100), Pin = "2" });
        v.Shapes.Add(new RectShape { Layer = Key(t, "Silk Top"),   X1 = Um(-900), Y1 = Um(500),  X2 = Um(800),  Y2 = Um(600) });
        v.Pins.Add(new LayoutPin { Name = "1", X = Um(-700), Y = Um(100),  WidthDbu = Um(400), Layer = Key(t, "Top Copper") });
        v.Pins.Add(new LayoutPin { Name = "2", X = Um(600),  Y = Um(-100), WidthDbu = Um(400), Layer = Key(t, "Top Copper") });
    });

    private static LayoutInstance Bottom(double deg, long x, long y, string refdes) =>
        new() { CellRef = "../../Chip", X = x, Y = y, RotationDegrees = deg, MirrorX = true, Side = PlacementSide.Bottom, RefDes = refdes };

    private static LayoutInstance TopPart(double deg, long x, long y, string refdes, bool mirror = false) =>
        new() { CellRef = "../../Chip", X = x, Y = y, RotationDegrees = deg, MirrorX = mirror, RefDes = refdes };

    // ── R-bsp3-1: Gerber ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gerber_BottomFilesCarryTheBottomPartsLands_AndItsDesignatorReadsLeftToRight()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        CreateChip(t);
        string board = CreateCell("Board", v => v.Instances.Add(Bottom(0, Um(5000), Um(5000), "R12")));

        var plan = GerberExport.Analyze(board, t, Dbu, LayoutPersistence.LoadFromFile(Clay(board)), resolveTechAt: null);
        Assert.True(plan.CanWrite);

        // The lands on bottom copper, mirrored in X about the part's origin; nothing of the part on top copper.
        var lands = plan.Shapes.OfType<RectShape>().Where(s => s.Layer == Key(t, "Bottom Copper"))
                        .Select(r => (Math.Min(r.X1, r.X2), Math.Max(r.X1, r.X2))).OrderBy(p => p.Item1).ToList();
        Assert.Equal(new[] { (Um(4200), Um(4600)), (Um(5500), Um(5900)) }, lands);
        Assert.DoesNotContain(plan.Shapes, s => s.Layer == Key(t, "Top Copper"));

        // The designator's strokes, in glyph order, run left to right on the bottom silkscreen as seen from above.
        var strokes = plan.Shapes.OfType<PathShape>().Where(s => s.Layer == Key(t, "Silk Bottom")).ToList();
        Assert.NotEmpty(strokes);
        static double MeanX(PathShape p) => Enumerable.Range(0, p.Xy.Length / 2).Average(i => (double)p.Xy[2 * i]);
        Assert.True(MeanX(strokes[0]) < MeanX(strokes[^1]), "the first glyph must be left of the last");
        Assert.DoesNotContain(plan.Shapes, s => s.Layer == Key(t, "Silk Top"));
    }

    // ── R-bsp3-2: placement table ────────────────────────────────────────────────────────────────

    [Fact]
    public void Placement_SideComesFromSide_NotMirrorX_AndRoundTripsThroughTheReader()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Instances.Add(Bottom(30, Um(5000), Um(2000), "R1"));
        view.Instances.Add(TopPart(45, Um(9000), Um(2000), "R2", mirror: true));   // mirrored, on the TOP

        string text = PlacementWriter.Write(view, Dbu);
        Assert.Contains("# Rotation:", text);

        var table = PlacementFile.Read("Board.placement.csv", text, Dbu);
        Assert.Null(table.Refusal);
        var r1 = Assert.Single(table.Rows, r => r.Refdes == "R1");
        var r2 = Assert.Single(table.Rows, r => r.Refdes == "R2");
        Assert.True(r1.Mirror);
        Assert.False(r2.Mirror);
        Assert.Equal(30.0, r1.RotationDegrees, 6);
        Assert.Equal((Um(5000), Um(2000)), (r1.X, r1.Y));
    }

    // ── R-bsp3-4: GDSII bottom variant ───────────────────────────────────────────────────────────

    [Fact]
    public void Gdsii_WritesOneBottomVariantOnlyWhenABottomPlacementUsesIt_WithSwappedLayers()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        CreateChip(t);
        string twoSided = CreateCell("Board", v =>
        {
            v.Instances.Add(TopPart(0, 0, 0, "R1"));
            v.Instances.Add(Bottom(0, Um(3000), 0, "R2"));
            v.Instances.Add(Bottom(90, Um(6000), 0, "R3"));
        });
        string topOnly = CreateCell("TopBoard", v => v.Instances.Add(TopPart(0, 0, 0, "R1")));

        var plan = GdsiiExport.Analyze(twoSided, t, Dbu);
        var variant = Assert.Single(plan.Structures, s => s.Name == "Chip_B");
        Assert.Single(plan.Structures, s => s.Name == "Chip");
        Assert.Contains(variant.Shapes, s => s.Layer == Key(t, "Bottom Copper"));
        Assert.DoesNotContain(variant.Shapes, s => s.Layer == Key(t, "Top Copper"));
        var root = Assert.Single(plan.Structures, s => s.Name == "Board");
        Assert.Equal(new[] { "Chip", "Chip_B", "Chip_B" }, root.Instances.Select(i => i.CellRef));
        Assert.Contains(plan.SideNotes, n => n.Contains("1 variant structure"));

        var plain = GdsiiExport.Analyze(topOnly, t, Dbu);
        Assert.DoesNotContain(plain.Structures, s => s.Name.EndsWith("_B", StringComparison.Ordinal));
        Assert.Empty(plain.SideNotes);
    }

    // ── R-bsp3-5: the board format, export → import ──────────────────────────────────────────────

    [Fact]
    public void BoardFormat_ATwoSidedBoardRoundTrips_LandPositionsAndLayers()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        CreateChip(t);
        string board = CreateCell("Board", v =>
        {
            v.Instances.Add(TopPart(30, Um(4000), Um(3000), "R1"));
            v.Instances.Add(Bottom(30, Um(9000), Um(3000), "R2"));
            v.Instances.Add(Bottom(0, Um(14000), Um(3000), "R3"));
        });

        // What circuitRF draws: every land's centre and its board layer name.
        var naming = PcbLayerNaming.Assign(t);
        var flat = LayoutDesignFlatten.Flatten(LayoutPersistence.LoadFromFile(Clay(board)), board, t, null, null);
        var copper = new[] { Key(t, "Top Copper"), Key(t, "Bottom Copper") };
        var drawn = flat.Shapes.Where(s => copper.Contains(s.Layer))
                        .Select(s => Land(s, naming.RowByKey[s.Layer].Name)).OrderBy(l => l).ToList();

        var plan = PcbExport.Analyze(board, t, Dbu);
        Assert.True(plan.CanWrite, plan.Refusal);
        string path = Path.Combine(_ws, "out.kicad_pcb");
        PcbExport.Write(path, plan);
        var read = PcbReader.Read(File.ReadAllText(path), Dbu).Board!;

        // What the import places: each footprint's stored (already flipped) pads, turned and moved by its placement.
        var back = new List<(long, long, string)>();
        foreach (var p in read.Placements)
            foreach (var s in read.FootprintCells[p.ContentKey].Shapes.Where(s => s.LayerName.EndsWith(".Cu", StringComparison.Ordinal)))
            {
                var b = LayoutGeometry.BboxOf(s.Shape);
                var inst = new LayoutInstance { X = p.X, Y = p.Y, RotationDegrees = p.RotationDegrees };
                var (cx, cy) = LayoutInstanceTransform.TransformPoint((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, inst, 0, 0);
                back.Add(Near(cx, cy, s.LayerName));
            }
        back.Sort();

        Assert.Equal(6, drawn.Count);
        Assert.Equal(drawn, back);
        Assert.Equal(4, back.Count(l => l.Item3 == "B.Cu"));
    }

    private static (long, long, string) Land(LayoutShape s, string layer)
    {
        var b = LayoutGeometry.BboxOf(s);
        return Near((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, layer);
    }

    /// <summary>To the nearest 10 DBU: a DBU or two of rounding through millimetres and a turn is not a
    /// misplaced land.</summary>
    private static (long, long, string) Near(long x, long y, string layer) =>
        ((long)Math.Round(x / 10.0) * 10, (long)Math.Round(y / 10.0) * 10, layer);

    // ── R-bsp3-7: EM extraction ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Em_ABottomPartsLandsAreOnTheBottomCopper_WhichAMicrostripsPlanarProblemTreatsAsGround()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        CreateCell("Chip", v =>
        {
            v.Shapes.Add(new RectShape { Layer = Key(t, "Top Copper"), X1 = Um(-500), Y1 = Um(-250), X2 = Um(-150), Y2 = Um(250), Pin = "1" });
            v.Shapes.Add(new RectShape { Layer = Key(t, "Top Copper"), X1 = Um(150),  Y1 = Um(-250), X2 = Um(500),  Y2 = Um(250), Pin = "2" });
        });
        string board = CreateCell("Board", v =>
        {
            v.Shapes.Add(new RectShape { Layer = Key(t, "Top Copper"), X1 = 0, Y1 = Um(-560), X2 = Um(10000), Y2 = Um(560) });
            v.Shapes.Add(new LabelShape { Layer = Key(t, "Top Copper"), Text = "1", X = Um(100), Y = 0, Height = Um(100), IsPort = true });
            v.Shapes.Add(new LabelShape { Layer = Key(t, "Bottom Copper"), Text = "2", X = Um(4675), Y = Um(3000), Height = Um(100),
                                          IsPort = true, PortLayer = Key(t, "Bottom Copper") });
            v.Instances.Add(Bottom(0, Um(5000), Um(3000), "R1"));
        });

        var flat = EmGeometry.Flatten(LayoutPersistence.LoadFromFile(Clay(board)), Clay(board), t);
        var lands = flat.Shapes.OfType<RectShape>().Where(s => s.Pin is not null).ToList();
        Assert.Equal(2, lands.Count);
        Assert.All(lands, l => Assert.Equal(Key(t, "Bottom Copper"), l.Layer));

        // The planar problem meshes the top conductor alone, and on it only the trace: the bottom face of a
        // two-layer microstrip IS its ground reference, so a part soldered there is ground artwork, and a
        // port drawn on its land is refused as on no signal conductor rather than silently meshed.
        var extraction = PlanarExtractor.Extract(flat.Shapes, t, Dbu, 10e9);
        Assert.True(extraction.Ok, extraction.Refusal);
        var level = Assert.Single(extraction.Problem!.Layers);
        Assert.Equal(t.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name, level.Name);
        var trace = Assert.Single(level.Polygons);
        Assert.True(trace.Bounds().MaxY < Um(1000) * 1e-9);

        var ports = EmPortExtraction.Extract(flat.Shapes, extraction.Problem!, Dbu);
        Assert.Contains("Port 2", ports.Refusal);
        Assert.Contains("not on any conductor", ports.Refusal);
    }

    // ── R-bsp3-9: LVS ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lvs_ABottomPartWiredByViasComparesClean()
    {
        string lvs = CopyDirectory(Path.Combine(RepoRoot(), "examples", "LVS"), Path.Combine(_ws, "LVS"));
        string cell = Path.Combine(lvs, "Attenuator");
        string clay = Path.Combine(cell, "layout", "Attenuator.clay");
        var view = LayoutPersistence.LoadFromFile(clay);
        var top = new LayerKey(1, 0); var bottom = new LayerKey(2, 0); var drill = new LayerKey(5, 0);

        // R2, the series arm at (10, 6) mm, moves to the bottom. At R180 + MirrorX its lands are exactly
        // where they were and its pins at the same ends, so the top traces still reach them — through vias.
        var r2 = view.Instances.Single(i => i.SchematicId == "R2");
        r2.RotationDegrees = 180; r2.MirrorX = true; r2.Side = PlacementSide.Bottom;
        foreach (long x in new[] { Um(9600), Um(10400) })
        {
            view.Shapes.Add(new RectShape { Layer = top, X1 = x - Um(275), Y1 = Um(5725), X2 = x + Um(275), Y2 = Um(6275) });
            view.Shapes.Add(new ViaShape { Layer = drill, X = x, Y = Um(6000), PadSize = Um(300), DrillSize = Um(150), LandingLayer = top });
        }
        // The bottom ground plane opened around it, or both lands would sit on ground.
        var plane = view.Shapes.OfType<RectShape>().Single(s => s.Layer == bottom);
        view.Shapes.Remove(plane);
        foreach (var (x1, y1, x2, y2) in new[] { (200, 200, 8800, 11800), (11200, 200, 19800, 11800), (8800, 200, 11200, 5400), (8800, 6600, 11200, 11800) })
            view.Shapes.Add(new RectShape { Layer = bottom, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2), Net = plane.Net });
        LayoutPersistence.SaveToFile(clay, view);
        CellLayoutResolver.InvalidateUnder(lvs);

        var result = LvsRun.Run(cell);
        foreach (var f in result.Findings) output.WriteLine($"{f.Severity}: {f.Render()}");
        Assert.DoesNotContain(result.Findings, f => f.Severity == DiagnosticSeverity.Error);
        Assert.Equal(4, result.Comparison.Devices.Count);
    }

    // ── R-bsp3-10: check ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Check_AnOmittedBottomRoleIsAWarningNamingThePart_NoBottomCopperIsAnError()
    {
        var withoutSilkBottom = new Technology
        {
            Name = "NoBottomSilk",
            Layers =
            [
                new LayerDef { Key = new(1, 0), Name = "Top Copper", Color = new Rgba(0xC8, 0x7A, 0x3E), Interchange = new(null, null, null, null, null, "F.Cu") },
                new LayerDef { Key = new(2, 0), Name = "Bottom Copper", Color = new Rgba(0x8A, 0x50, 0x28), Interchange = new(null, null, null, null, null, "B.Cu") },
                new LayerDef { Key = new(3, 0), Name = "Silk Top", Color = new Rgba(0xF2, 0xF2, 0xF2), Interchange = new(null, null, null, null, null, "F.SilkS") },
            ],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35_000, DrawingLayers = [new(1, 0)] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 500_000, Epsr = 4.4 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 35_000, DrawingLayers = [new(2, 0)] },
                ],
            },
        };
        var singleSided = new Technology
        {
            Name = "SingleSided",
            Layers = [new LayerDef { Key = new(1, 0), Name = "Copper", Color = new Rgba(0xC8, 0x7A, 0x3E), Interchange = new(null, null, null, null, null, "F.Cu") }],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Cu", ThicknessDbu = 35_000, DrawingLayers = [new(1, 0)] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "FR-4", ThicknessDbu = 500_000, Epsr = 4.4 },
                ],
            },
        };
        TechPersistence.SaveToFile(Path.Combine(_ws, "nosilk.ctech"), withoutSilkBottom);
        TechPersistence.SaveToFile(Path.Combine(_ws, "single.ctech"), singleSided);

        CreateCell("Chip", v =>
        {
            v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = Um(500), Y2 = Um(500) });
            v.Shapes.Add(new RectShape { Layer = new(3, 0), X1 = 0, Y1 = Um(600), X2 = Um(500), Y2 = Um(700) });
        });
        string warned = CreateCell("Warned", v => { v.TechRef = "../../nosilk.ctech"; v.Instances.Add(Bottom(0, 0, 0, "R7")); });
        string refused = CreateCell("Refused", v => { v.TechRef = "../../single.ctech"; v.Instances.Add(Bottom(0, 0, 0, "R8")); });

        var w = Diagnostics(RunCli("check", Clay(warned), "--json"));
        var omitted = Assert.Single(w, d => d.Id == "check.layout.side-role-omitted");
        Assert.Equal("warning", omitted.Severity);
        Assert.Contains("R7", omitted.Message);
        Assert.Contains("silkscreen", omitted.Message);

        var r = Diagnostics(RunCli("check", Clay(refused), "--json"));
        var refusal = Assert.Single(r, d => d.Id == "check.layout.side-refused");
        Assert.Equal("error", refusal.Severity);
        Assert.Contains("R8", refusal.Message);
    }

    private sealed record Found(string Id, string Severity, string Message);

    private List<Found> Diagnostics(string json)
    {
        output.WriteLine(json);
        return [.. JsonDocument.Parse(json).RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => new Found(
            d.GetProperty("id").GetString()!, d.GetProperty("severity").GetString()!, d.GetProperty("message").GetString()!))];
    }

    private static string RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(BottomSideOutputTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return stdout.GetAwaiter().GetResult();
    }

    private static string CopyDirectory(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        return to;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
