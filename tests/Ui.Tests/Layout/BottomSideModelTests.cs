using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Text;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-bsp-1 — the side model: <see cref="LayoutInstance.Side"/>, surface roles on both faces, the
/// side-aware resolution every reader of a placed part goes through, and text that keeps its reading
/// direction under a mirror. One test per claim.
/// </summary>
public sealed class BottomSideModelTests : IDisposable
{
    private readonly string _ws;

    public BottomSideModelTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), "crfBottomSide_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_ws);
        CellLayoutResolver.InvalidateUnder(_ws);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_ws);
        if (Directory.Exists(_ws)) Directory.Delete(_ws, recursive: true);
    }

    private const string TwoLayer  = "pcb-2layer_RO4350B_20mil_1oz";
    private const string FourLayer = "pcb-4layer_FR-4_62mil_1oz";

    private static LayerKey Key(Technology t, string name) => t.Layers.Single(l => l.Name == name).Key;

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_ws, name);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1 };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private static RectShape Rect(LayerKey layer, long x1, long x2) => new() { Layer = layer, X1 = x1, Y1 = -500, X2 = x2, Y2 = 500 };

    // ── R-bsp1-3: each role found on both surfaces ────────────────────────────────────────────────

    [Theory]
    [InlineData(TwoLayer)]
    [InlineData(FourLayer)]
    public void ShippedBoardTechnology_EveryDrawnRoleResolvesOnBothSurfaces(string id)
    {
        var t = ShippedTechnologies.Load(id);
        var r = SurfaceRoles.For(t);

        Assert.Equal(Key(t, "Top Copper"), r.Top.Copper);
        Assert.Equal(Key(t, "Bottom Copper"), r.Bottom.Copper);
        Assert.Equal(Key(t, "Soldermask Top"), r.Top.Soldermask);
        Assert.Equal(Key(t, "Soldermask Bottom"), r.Bottom.Soldermask);
        Assert.Equal(Key(t, "Silk Top"), r.Top.Silkscreen);
        Assert.Equal(Key(t, "Silk Bottom"), r.Bottom.Silkscreen);
        Assert.Equal(Key(t, "Courtyard Top"), r.Top.Courtyard);
        Assert.Equal(Key(t, "Courtyard Bottom"), r.Bottom.Courtyard);
    }

    [Fact]
    public void ATechnologyNamedOnlyBySideWords_ResolvesBothSurfaces()
    {
        var t = new Technology
        {
            Name = "Words",
            Layers =
            [
                new LayerDef { Key = new(1, 0), Name = "Top Metal" },
                new LayerDef { Key = new(2, 0), Name = "Bottom Metal" },
                new LayerDef { Key = new(3, 0), Name = "Solder Mask Top" },
                new LayerDef { Key = new(4, 0), Name = "Solder Mask Bot" },
                new LayerDef { Key = new(5, 0), Name = "Silkscreen Front" },
                new LayerDef { Key = new(6, 0), Name = "Silkscreen Back" },
            ],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "M1", DrawingLayers = [new(1, 0)] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", Epsr = 4.0 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "M2", DrawingLayers = [new(2, 0)] },
                ],
            },
        };

        var r = SurfaceRoles.For(t);
        Assert.Equal(new LayerKey(1, 0), r.Top.Copper);
        Assert.Equal(new LayerKey(2, 0), r.Bottom.Copper);
        Assert.Equal(new LayerKey(3, 0), r.Top.Soldermask);
        Assert.Equal(new LayerKey(4, 0), r.Bottom.Soldermask);
        Assert.Equal(new LayerKey(5, 0), r.Top.Silkscreen);
        Assert.Equal(new LayerKey(6, 0), r.Bottom.Silkscreen);
    }

    // ── R-bsp1-5: the shipped boards are complete ─────────────────────────────────────────────────

    [Fact]
    public void EveryShippedBoardTechnology_HasEveryTopRoleOnTheBottomToo()
    {
        foreach (var entry in ShippedTechnologies.All.Where(e => e.Id.StartsWith("pcb-", StringComparison.Ordinal)))
        {
            var r = SurfaceRoles.For(ShippedTechnologies.Load(entry));
            foreach (var role in Enum.GetValues<SurfaceRole>())
                if (r.Top[role] is not null)
                    Assert.True(r.Bottom[role] is not null, $"{entry.Id} has a top {role} layer and no bottom one");
        }
    }

    // ── R-bsp1-6/10: a bottom land pattern flattens onto the bottom roles ─────────────────────────

    [Fact]
    public void BottomPlacement_FlattensOntoTheBottomRoles_MirroredInX_InnerAndThroughHoleCorrect()
    {
        var t = ShippedTechnologies.Load(FourLayer);
        CreateCell("Chip", v =>
        {
            v.Shapes.Add(Rect(Key(t, "Top Copper"), 1000, 2000));
            v.Shapes.Add(Rect(Key(t, "Soldermask Top"), 1000, 2000));
            v.Shapes.Add(Rect(Key(t, "Silk Top"), 1000, 2000));
            v.Shapes.Add(Rect(Key(t, "Courtyard Top"), 1000, 2000));
            v.Shapes.Add(Rect(Key(t, "Inner 1"), 1000, 2000));
            v.Shapes.Add(Rect(Key(t, "Bottom Copper"), 3000, 4000));   // the other land of a through-hole pad
        });
        var inst = new LayoutInstance { CellRef = "Chip", MirrorX = true, Side = PlacementSide.Bottom };

        var result = LayoutFlatten.FlattenOneLevel(inst, _ws, t)!;
        var layers = result.Shapes.Select(s => s.Layer).ToList();

        Assert.Equal(
            new[] { Key(t, "Bottom Copper"), Key(t, "Soldermask Bottom"), Key(t, "Silk Bottom"),
                    Key(t, "Courtyard Bottom"), Key(t, "Inner 1"), Key(t, "Top Copper") },
            layers);
        var land = Assert.IsType<RectShape>(result.Shapes[0]);
        Assert.Equal((-2000L, -1000L), (Math.Min(land.X1, land.X2), Math.Max(land.X1, land.X2)));
        Assert.Null(result.Notes);   // nothing omitted on a complete technology
    }

    // ── R-bsp1-4: an omitted role is reported; no bottom copper is the refusal ────────────────────

    [Fact]
    public void ARoleWithNoBottomLayer_IsOmittedAndReported()
    {
        var t = TechPersistence.Clone(ShippedTechnologies.Load(TwoLayer));
        t.Layers.RemoveAll(l => l.Name == "Silk Bottom");
        CreateCell("Chip", v =>
        {
            v.Shapes.Add(Rect(Key(t, "Top Copper"), 0, 1000));
            v.Shapes.Add(Rect(Key(t, "Silk Top"), 0, 1000));
        });
        var inst = new LayoutInstance { CellRef = "Chip", MirrorX = true, Side = PlacementSide.Bottom, RefDes = "R12" };

        var result = LayoutFlatten.FlattenOneLevel(inst, _ws, t)!;

        Assert.Equal(new[] { Key(t, "Bottom Copper") }, result.Shapes.Select(s => s.Layer));
        var note = Assert.Single(result.Notes!);
        Assert.Contains("R12 is on the bottom", note);
        Assert.Contains("silkscreen was omitted", note);
    }

    [Fact]
    public void ATechnologyWithNoBottomCopper_RefusesABottomPlacement()
    {
        var t = new Technology
        {
            Name = "Single-sided",
            Layers = [new LayerDef { Key = new(1, 0), Name = "Copper", Interchange = new(null, null, null, null, null, "F.Cu") }],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Cu", DrawingLayers = [new(1, 0)] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "FR-4", Epsr = 4.4 },
                ],
            },
        };
        CreateCell("Chip", v => v.Shapes.Add(Rect(new(1, 0), 0, 1000)));
        var inst = new LayoutInstance { CellRef = "Chip", MirrorX = true, Side = PlacementSide.Bottom };

        var res = CellLayoutResolver.Resolve(inst, _ws, t);

        Assert.Equal(CellLayoutState.SideRefused, res.State);
        Assert.Contains("no bottom copper", res.Refusal);
        Assert.Null(LayoutFlatten.FlattenOneLevel(inst, _ws, t));
    }

    // ── R-bsp1-2: sides compose ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SidesCompose_ABottomPartInsideABottomModuleIsOnTheTop()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        CreateCell("Chip", v => v.Shapes.Add(Rect(Key(t, "Top Copper"), 0, 1000)));
        CreateCell("Module", v => v.Instances.Add(new LayoutInstance { CellRef = "../../Chip", Side = PlacementSide.Bottom, MirrorX = true }));

        var bottomModule = new LayoutInstance { CellRef = "Module", Side = PlacementSide.Bottom, MirrorX = true };
        var topModule    = new LayoutInstance { CellRef = "Module" };

        Assert.Equal(new[] { Key(t, "Top Copper") }, LayoutFlatten.FlattenAllLevels(bottomModule, _ws, t).Shapes.Select(s => s.Layer));
        Assert.Equal(new[] { Key(t, "Bottom Copper") }, LayoutFlatten.FlattenAllLevels(topModule, _ws, t).Shapes.Select(s => s.Layer));
        Assert.Equal(PlacementSide.Top, Assert.Single(LayoutFlatten.FlattenOneLevel(bottomModule, _ws, t)!.Instances).Side);
    }

    // ── R-bsp1-1: an array of bottom parts is bottom parts ────────────────────────────────────────

    [Fact]
    public void AnArrayOfBottomParts_ExpandsToBottomParts()
    {
        var inst = new LayoutInstance { CellRef = "Chip", Side = PlacementSide.Bottom, MirrorX = true, Rows = 2, Cols = 3, PitchX = 10, PitchY = 10 };

        var result = LayoutFlatten.FlattenOneLevel(inst, _ws)!;

        Assert.Equal(6, result.Instances.Count);
        Assert.All(result.Instances, i => Assert.Equal(PlacementSide.Bottom, i.Side));
    }

    // ── R-bsp1-9: text keeps its reading direction ────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(30.0)]
    public void ALabelInABottomPlacement_ReadsLeftToRight_CentredWhereTheMirrorCarriesItsCentre(double instDeg)
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        var local = new LabelShape { Layer = Key(t, "Silk Top"), Text = "R12", X = 200, Y = 300, Height = 1000 };
        CreateCell("Chip", v => v.Shapes.Add(local));
        var inst = new LayoutInstance { CellRef = "Chip", X = 50_000, Side = PlacementSide.Bottom, MirrorX = true, RotationDegrees = instDeg };

        var label = Assert.IsType<LabelShape>(Assert.Single(LayoutFlatten.FlattenOneLevel(inst, _ws, t)!.Shapes));

        // Centred over the spot the mirror carries the local centre to.
        var (dx, dy) = StrokeText.CentreOffset(local);
        var (cx, cy) = LayoutInstanceTransform.TransformPoint(local.X + (long)Math.Round(dx), local.Y + (long)Math.Round(dy), inst, 0, 0);
        Assert.Equal((cx, cy), (label.X, label.Y));
        Assert.Equal((LabelHAlign.Center, LabelVAlign.Middle), (label.HAlign, label.VAlign));
        Assert.Equal(Key(t, "Silk Bottom"), label.Layer);

        // Never upside down, and it runs the way it reads: its first glyph starts before its last ends,
        // measured along its own baseline direction.
        double a = label.RotationDegrees > 180 ? label.RotationDegrees - 360 : label.RotationDegrees;
        Assert.InRange(a, -89.999, 90.0);
        var strokes = StrokeText.For(label).Strokes;
        var (c, s) = LayoutAngle.CosSin(label.RotationDegrees);
        double Along(double[] st) => st[0] * c + st[1] * s;
        Assert.True(Along(strokes[0]) < Along(strokes[^1]));
    }

    // ── R-bsp1-12: persistence ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Persistence_BottomRoundTrips_TopIsByteStableAgainstAFileWrittenBefore()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "A", Side = PlacementSide.Bottom });
        view.Instances.Add(new LayoutInstance { CellRef = "B" });
        var back = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        Assert.Equal(new[] { PlacementSide.Bottom, PlacementSide.Top }, back.Instances.Select(i => i.Side));

        // A .clay committed before the field existed, placing parts: it re-serializes byte for byte.
        string before = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "Hierarchy", "Board", "layout", "Board.clay"));
        Assert.Equal(before, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(before)));
        Assert.DoesNotContain("\"Side\"", before);
    }

    // ── R-bsp1-11: picked, and its placed pin reports bottom copper ───────────────────────────────

    [Fact]
    public void ABottomPart_IsPickedOnItsLand_AndItsPlacedPinReportsBottomCopper()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        CreateCell("Chip", v =>
        {
            v.Shapes.Add(Rect(Key(t, "Top Copper"), 1000, 2000));
            v.Pins.Add(new LayoutPin { Name = "1", X = 1500, Y = 0, Layer = Key(t, "Top Copper") });
        });
        string boardDir = CreateCell("Board", v =>
            v.Instances.Add(new LayoutInstance { CellRef = "../../Chip", Side = PlacementSide.Bottom, MirrorX = true, RefDes = "R1" }));
        string clay = Path.Combine(CellFolder.SubFolderPath(boardDir, ViewType.Layout), "main.clay");
        var board = LayoutPersistence.LoadFromFile(clay);
        string baseDir = CellHierarchy.BaseDirOfDocument(clay);

        Assert.Equal(new[] { 0 }, LayoutHitTest.HitInstanceStack(board, t, baseDir, -1500, 0, 5));

        var origins = new List<PlacedPinOrigin>();
        PlacedPins.Of(board, clay, t, PinNaming.ArtworkOnly, origins: origins);
        Assert.Equal(Key(t, "Bottom Copper"), Assert.Single(origins).Layer);
    }

    // ── R-bsp1-8: no placed part is resolved by its bare reference ────────────────────────────────

    [Fact]
    public void SourceScan_NoResolveOfAnInstancesBareCellRef()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string code = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", "", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\n]*", "");
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(code, @"CellLayoutResolver\.Resolve\(\s*[\w\.\[\]!]+\.CellRef\s*,"))
                offenders.Add($"{Path.GetRelativePath(src, file)}: {m.Value}");
        }
        Assert.Empty(offenders);
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
