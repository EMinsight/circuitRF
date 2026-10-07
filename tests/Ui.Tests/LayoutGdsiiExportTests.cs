using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate 8 (coordinate overflow reported by name, nothing written) and R-L4a-3's fidelity
/// plan (curve/hole/bitmap counts, structure-name mapping) computed by the SAME write path the real
/// export uses (a dry run into <see cref="Stream.Null"/>).</summary>
public class LayoutGdsiiExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gdsii-export-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        populate(view);
        var layoutPath = Path.Combine(layoutDir, $"{name}.clay");
        LayoutPersistence.SaveToFile(layoutPath, view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    [Fact]
    public void Analyze_CoordinateOverflow_ReportsOffenderByName_CanWriteFalse()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = (long)int.MaxValue + 1000, Y2 = 100 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.False(plan.CanWrite);
        Assert.NotEmpty(plan.CoordinateOverflowOffenders);
        Assert.Contains(plan.CoordinateOverflowOffenders, o => o.Contains("RectShape"));
    }

    [Fact]
    public void Write_WhenPlanCannotWrite_Throws_NoFileWritten()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = (long)int.MaxValue + 1000, Y2 = 100 }));
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        var outPath = Path.Combine(_dir, "out.gds");

        Assert.Throws<GdsiiExportException>(() => GdsiiExport.Write(outPath, plan));
        Assert.False(File.Exists(outPath));
    }

    /// <summary>brief-gdsii-native-fixes.md F3, read at record level: UNITS holds the database unit
    /// in user units, then in metres. Within 1e-15 relative, because <c>1e-9 / 1e-6</c> is not exactly
    /// 0.001 in double precision.</summary>
    [Theory]
    [InlineData(1000, 0.001, 1e-9)]
    [InlineData(4000, 0.00025, 2.5e-10)]
    public void Write_UnitsRecord_HoldsTheDatabaseUnitInUserUnitsThenInMetres(int dbuPerMicron, double inUserUnits, double inMetres)
    {
        var cellDir = CreateCell("TOP", v => { });
        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, GdsiiExport.Analyze(cellDir, null, dbuPerMicron));

        using var stream = File.OpenRead(outPath);
        var records = new GdsiiRecordReader(stream);
        GdsiiRecord units;
        do Assert.True(records.TryReadNext(out units)); while (units.Type != GdsiiRecordType.Units);

        var v = units.AsReal8Array();
        Assert.True(Math.Abs(v[0] / inUserUnits - 1) < 1e-15, $"first real {v[0]:R}");
        Assert.True(Math.Abs(v[1] / inMetres - 1) < 1e-15, $"second real {v[1]:R}");
    }

    /// <summary>F3/D7: a layer or datatype outside 0–65535 is refused, by name, before a byte is
    /// written — a wrapped value would be a different layer.</summary>
    [Theory]
    [InlineData(70000, 0)]
    [InlineData(1, -1)]
    public void Write_LayerOrDatatypeOutsideSixteenBits_IsRefusedBeforeAnyByte(int layer, int datatype)
    {
        var rect = new RectShape { Layer = new LayerKey(layer, datatype), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 };
        using var ms = new MemoryStream();
        var ex = Assert.Throws<GdsiiExportException>(() =>
            GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [rect], [])], new GdsiiUnits(1e-6, 1e-9), null));
        Assert.Contains($"{layer}/{datatype}", Assert.Single(ex.Offenders));
        Assert.Equal(0, ms.Length);
    }

    /// <summary>D7 (owner decision while landing the brief): a DXF or board import gives a layer the
    /// technology had no number for a negative placeholder key. GDSII is written with the lowest layer
    /// number the export does not already use, datatype kept, and says so by name.</summary>
    [Fact]
    public void Write_PlaceholderLayer_IsWrittenAsTheLowestFreeLayerNumber_AndReported()
    {
        var tech = new Technology { Name = "T", Layers = { new LayerDef { Key = new LayerKey(-1, 0), Name = "Top Copper" } } };
        List<LayoutShape> shapes =
        [
            new RectShape { Layer = new LayerKey(0, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 },
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 },
            new RectShape { Layer = new LayerKey(-1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 },
            new LabelShape { Layer = new LayerKey(-2, 0), X = 0, Y = 0, Text = "x", Height = 10 },
        ];
        using var ms = new MemoryStream();
        var summary = GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", shapes, [])], new GdsiiUnits(1e-6, 1e-9), tech);

        ms.Position = 0;
        var layers = GdsiiReader.Open(ms).ReadStructures().Single().Shapes.Select(sh => sh.Layer.Layer);
        Assert.Equal([0, 1, 2, 3], layers);
        Assert.Equal(
            ["Layer \"Top Copper\" has no GDSII number; written as GDSII layer 2.",
             "Layer -2 has no GDSII number; written as GDSII layer 3."],
            summary.LayersRenumbered);
    }

    /// <summary>F3/D7: an array count outside COLROW's 1–32767 is refused the same way.</summary>
    [Fact]
    public void Write_ArrayCountOutsideColRow_IsRefusedBeforeAnyByte()
    {
        var inst = new LayoutInstance { CellRef = "LEAF", Cols = 40000, Rows = 1, PitchX = 1 };
        using var ms = new MemoryStream();
        var ex = Assert.Throws<GdsiiExportException>(() =>
            GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [], [inst])], new GdsiiUnits(1e-6, 1e-9), null));
        Assert.Contains("40000 × 1", Assert.Single(ex.Offenders));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void Analyze_ReportsCurveHoleAndBitmapCounts_MatchingWhatWriteActuallyDoes()
    {
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 50_000 });
            v.Shapes.Add(new PolygonShape
            {
                Layer = new LayerKey(2, 0),
                Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
                Holes = [[400, 600, 600, 600, 600, 400, 400, 400]],
            });
            v.Shapes.Add(new BitmapShape { Layer = new LayerKey(3, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 10, H = 10 });
        });

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.True(plan.CanWrite);
        Assert.Equal(1, plan.CurvedShapesFlattened);
        Assert.Equal(1, plan.HolesKeyholed);
        Assert.Equal(1, plan.BitmapsSkipped);

        // The preview must not disagree with the actual write — same counts, produced by GdsiiWriter.Write itself.
        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, plan);
        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 0);
    }

    [Fact]
    public void Analyze_ReportsStructureNameMapping_ForNonTrivialCellName()
    {
        var cellDir = CreateCell("My Amp!", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.True(plan.StructureNameByCellName.ContainsKey("My Amp!"));
        Assert.NotEqual("My Amp!", plan.StructureNameByCellName["My Amp!"]);
    }

    [Fact]
    public void Analyze_Hierarchy_CollectsEveryReachableCell()
    {
        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "TOP.clay"), topView);

        var plan = GdsiiExport.Analyze(topDir, null, 1000);
        Assert.Equal(2, plan.Structures.Count);
        Assert.Empty(plan.UnresolvedInstanceReferences);
    }

    [Fact]
    public void HasNothingToReport_PlainGeometry_IsTrue()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.True(plan.HasNothingToReport);
    }

    [Fact]
    public void HasNothingToReport_CurvedShape_IsFalse()
    {
        var cellDir = CreateCell("TOP", v =>
            v.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 50_000 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.False(plan.HasNothingToReport);
    }

    [Fact]
    public void HasNothingToReport_UnresolvedInstanceReference_IsFalse()
    {
        var cellDir = CreateCell("TOP", v => v.Instances.Add(
            new LayoutInstance { CellRef = "../DoesNotExist", X = 0, Y = 0, Mag = 1.0 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.False(plan.HasNothingToReport);
    }

    [Fact]
    public void HasNothingToReport_CoordinateOverflow_IsFalse()
    {
        // A blocking overflow always still needs the dialog, since it must stop the write and explain why.
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = (long)int.MaxValue + 1000, Y2 = 100 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.False(plan.CanWrite);
        Assert.False(plan.HasNothingToReport);
    }

    [Fact]
    public void Analyze_RootViewSupplied_UsesLiveInMemoryShapes_NotTheLastSavedFile()
    {
        // brief-layout-testing-fixes.md item 5/R-fix-4: an unsaved edit in the open editor must export
        // exactly what's on screen. The on-disk .clay has ONE rect; the caller's live LayoutView (the
        // editor's own in-memory Model, unsaved) has a DIFFERENT rect on a different layer — Analyze
        // must reflect the live one, never re-read the disk copy for the root cell.
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));

        var liveUnsavedView = new LayoutView { DbuPerMicron = 1000 };
        liveUnsavedView.Shapes.Add(new RectShape { Layer = new LayerKey(9, 0), X1 = 0, Y1 = 0, X2 = 99, Y2 = 99 });

        var plan = GdsiiExport.Analyze(cellDir, null, 1000, liveUnsavedView);

        var rootStructure = Assert.Single(plan.Structures, s => s.Name == plan.StructureNameByCellName["TOP"]);
        var shape = Assert.Single(rootStructure.Shapes);
        var rect = Assert.IsType<RectShape>(shape);
        Assert.Equal(new LayerKey(9, 0), rect.Layer);
        Assert.Equal(99, rect.X2);
    }

    [Fact]
    public void Analyze_NoRootViewSupplied_ReadsFromDisk_UnchangedBehavior()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        var rootStructure = Assert.Single(plan.Structures, s => s.Name == plan.StructureNameByCellName["TOP"]);
        var shape = Assert.Single(rootStructure.Shapes);
        Assert.Equal(new LayerKey(1, 0), Assert.IsType<RectShape>(shape).Layer);
    }

    [Fact]
    public void Analyze_UnresolvedInstanceCellRef_Reported_NotSilent()
    {
        // Regression for the exact mistake a hand-typed relative path fell into (see
        // LayoutGdsiiTransformTests's own history): an instance whose CellRef does not resolve to any
        // reachable cell must be reported, not silently exported as a dangling reference a GDSII
        // viewer would show with no explanation.
        var topDir = CreateCell("TOP", v => v.Instances.Add(
            new LayoutInstance { CellRef = "../DoesNotExist", X = 0, Y = 0, Mag = 1.0 }));

        var plan = GdsiiExport.Analyze(topDir, null, 1000);

        Assert.Single(plan.UnresolvedInstanceReferences);
        Assert.Contains("DoesNotExist", plan.UnresolvedInstanceReferences[0]);
        Assert.True(plan.CanWrite); // a dangling reference doesn't block export — it's the source
                                    // design's own pre-existing state, reported rather than refused
    }
}
