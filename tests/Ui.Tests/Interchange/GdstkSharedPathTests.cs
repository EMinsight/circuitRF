using System.Text.Json.Nodes;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Interchange.Gdstk;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Interchange;

/// <summary>Skips with a reason when this build has no gdstk worker (brief-oasis-gdstk.md D6).</summary>
public sealed class GdstkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        GdstkWorker.Locate() is { Found: false } miss ? $"needs the gdstk worker: {miss.Reason}" : null);

    public GdstkFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }

    internal static string? SkipReason => Reason.Value;
}

/// <summary><see cref="GdstkFactAttribute"/> for a theory.</summary>
public sealed class GdstkTheoryAttribute : TheoryAttribute
{
    public GdstkTheoryAttribute()
    {
        if (GdstkFactAttribute.SkipReason is { } why) Skip = why;
    }
}

/// <summary>brief-oasis-gdstk.md G2 (R-oas-2): the gdstk route reads and writes through the SAME
/// functions as circuitRF's own GDSII route — <see cref="StreamLayoutImport"/> after the read,
/// <see cref="StreamLowering"/> before the write — and every worker request is bounded.</summary>
public sealed class GdstkSharedPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gdstk-shared-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly GdsiiUnits Nm = new(1e-6, 1e-9);

    /// <summary>A native GDSII file imported through gdstk lands as byte-identical cell folders to the
    /// same file imported natively: one function does everything after the read.</summary>
    [GdstkFact]
    public void GdstkImport_OfANativeGdsiiFile_CreatesTheSameCellFoldersAsGdsiiImport()
    {
        var leaf = new InterchangeStructure("LEAF",
        [
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 300, Y2 = 400 },
            new PolygonShape { Layer = new LayerKey(2, 3), Xy = [0, 0, 700, 0, 900, 300, 600, 800, 200, 900, -100, 500, -50, 100] },
            new PathShape { Layer = new LayerKey(4, 0), Xy = [0, 0, 1000, 0, 1000, 2000], Width = 200, End = PathEndStyle.Flush },
            new PathShape { Layer = new LayerKey(4, 1), Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Round },
            new PathShape { Layer = new LayerKey(4, 2), Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Square },
            new PathShape { Layer = new LayerKey(4, 3), Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Extended },
            new LabelShape { Layer = new LayerKey(5, 2), X = 10, Y = 20, Text = "OUT", Height = 1000, Rotation = LayoutRotation.R90 },
        ], []);
        var top = new InterchangeStructure("TOP", [],
        [
            new LayoutInstance { CellRef = "LEAF", X = 500, Y = -300, RotationDegrees = 90, MirrorX = true, Mag = 2 },
            new LayoutInstance { CellRef = "LEAF", X = 0, Y = 0, Rows = 2, Cols = 3, PitchX = 1000, PitchY = 2000 },
        ]);
        string gds = Path.Combine(_dir, "in.gds");
        using (var f = File.Create(gds)) GdsiiWriter.Write(f, [leaf, top], Nm, null);

        string nativeDir = Directory.CreateDirectory(Path.Combine(_dir, "native")).FullName;
        string gdstkDir = Directory.CreateDirectory(Path.Combine(_dir, "gdstk")).FullName;
        GdsiiImport.ImportResult native;
        using (var f = File.OpenRead(gds)) native = GdsiiImport.Import(f, nativeDir, null, 1000, preferSourceResolution: true);
        var viaGdstk = GdstkImport.Import(gds, GdstkFormat.Gdsii, gdstkDir, null, 1000, preferSourceResolution: true);

        Assert.Equal(native.Messages, viaGdstk.Messages);
        Assert.Equal(Relative(nativeDir, native.TopLevelCellDirs), Relative(gdstkDir, viaGdstk.TopLevelCellDirs));
        var files = Directory.GetFiles(nativeDir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(nativeDir, p)).Order().ToList();
        Assert.Equal(files, Directory.GetFiles(gdstkDir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(gdstkDir, p)).Order());
        Assert.Contains(files, p => p.EndsWith(".clay", StringComparison.Ordinal));
        Assert.All(files, rel => Assert.Equal(File.ReadAllText(Path.Combine(nativeDir, rel)), File.ReadAllText(Path.Combine(gdstkDir, rel))));
    }

    private static IEnumerable<string> Relative(string root, IEnumerable<string> dirs) => dirs.Select(d => Path.GetRelativePath(root, d));

    /// <summary>The gdstk export reports exactly the plan's counts — one lowering produced both — and the
    /// file it writes reads back, through circuitRF's own reader, as the native writer's file does.</summary>
    [GdstkFact]
    public void GdstkExport_ReportsThePlansCounts_AndWritesWhatTheNativeWriterWrites()
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, "TOP");
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 50_000 });
        view.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(2, 0), Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000], Holes = [[400, 600, 600, 600, 600, 400, 400, 400]],
        });
        view.Shapes.Add(new BitmapShape { Layer = new LayerKey(3, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 10, H = 10 });
        view.Shapes.Add(new ViaShape { Layer = new LayerKey(4, 0), X = 0, Y = 0, PadSize = 500, DrillSize = 300 });
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(5, 0), X = 1, Y = 2, Text = "N", Height = 2500, RotationDegrees = 45 });
        view.Shapes.Add(new PathShape { Layer = new LayerKey(6, 0), Xy = [0, 0, 500, 0], Width = 100, End = PathEndStyle.Extended });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "TOP.clay"), view);

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        string nativePath = Path.Combine(_dir, "native.gds"), gdstkPath = Path.Combine(_dir, "gdstk.gds");
        GdsiiExport.Write(nativePath, plan);
        var summary = GdstkExport.Write(gdstkPath, plan);

        Assert.Equal(
            (plan.CurvedShapesFlattened, plan.HolesKeyholed, plan.BitmapsSkipped, plan.LabelRecordsWritten, plan.ViaPadsSkipped),
            (summary.CurvedShapesFlattened, summary.HolesKeyholed, summary.BitmapsSkipped, summary.LabelRecordsWritten, summary.ViaPadsSkipped));
        Assert.Equal((2, 1, 1, 1, 1), (plan.CurvedShapesFlattened, plan.HolesKeyholed, plan.BitmapsSkipped, plan.LabelRecordsWritten, plan.ViaPadsSkipped));
        Assert.Equal(Canonical(nativePath), Canonical(gdstkPath));
    }

    /// <summary>What circuitRF's own reader makes of a file, as one string, so a difference shows whole.</summary>
    private static string Canonical(string gds)
    {
        using var f = File.OpenRead(gds);
        var reader = GdsiiReader.Open(f);
        var lines = new List<string> { $"units {reader.Units}" };
        foreach (var s in reader.ReadStructures())
        {
            lines.Add($"structure {s.Name}");
            lines.AddRange(s.Shapes.Select(sh => sh switch
            {
                PolygonShape p => $"  polygon {p.Layer} {string.Join(",", p.Xy)}",
                PathShape p => $"  path {p.Layer} w={p.Width} {p.End} {string.Join(",", p.Xy)}",
                LabelShape l => $"  label {l.Layer} \"{l.Text}\" {l.X},{l.Y} h={l.Height} r={l.RotationDegrees}",
                _ => $"  {sh.GetType().Name}",
            }).Order(StringComparer.Ordinal)); // §8b: a multiset — gdstk writes polygons, then paths, then labels
            lines.AddRange(s.Instances.Select(i =>
                $"  ref {i.CellRef} {i.X},{i.Y} r={i.RotationDegrees} m={i.MirrorX} mag={i.Mag} {i.Cols}x{i.Rows} {i.PitchX},{i.PitchY}"));
        }
        lines.AddRange(reader.Diagnostics.Select(d => $"note {d}"));
        return string.Join("\n", lines);
    }

    /// <summary>A file gdstk cannot read is refused — by the worker, or by its ending — and the import
    /// creates nothing, because every cell is read before any is created.</summary>
    [GdstkFact]
    public void GdstkImport_OfAFileItCannotRead_FailsWithASentence_AndCreatesNothing()
    {
        string bad = Path.Combine(_dir, "bad.gds");
        File.WriteAllBytes(bad, [0x00, 0x06, 0x00, 0x02, 0x02, 0x58, 0x00, 0x1C, 0x01, 0x02, 0xFF, 0xFF]);
        string parent = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

        var ex = Assert.Throws<GdstkException>(() => GdstkImport.Import(bad, GdstkFormat.Gdsii, parent, null, 1000, true));

        Assert.Contains(bad, ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    /// <summary>tools/gdstk-worker/RESOLVED.md: a hang must reach the caller as a failure, so a request
    /// that misses its deadline kills the worker and says so.</summary>
    [GdstkFact]
    public void AMissedDeadline_KillsTheWorker_AndSaysSo()
    {
        using var worker = GdstkWorker.Start(TestWorker() with { BaseDeadline = TimeSpan.FromMilliseconds(500) });
        var (deadline, cap) = worker.BoundsFor(0);

        var ex = Assert.Throws<GdstkException>(() => worker.Send(
            new GeometryKernelMessage(new JsonObject { ["op"] = "sleep", ["seconds"] = 60 }), deadline, cap, "reading x.gds", default));

        Assert.Equal(GdstkFailure.TimedOut, ex.Failure);
        Assert.Equal("gdstk.worker.timed-out", ex.Diagnostic.Id);
        Assert.Equal("reading x.gds", ex.Diagnostic.Arguments["doing"]);
    }

    /// <summary>A worker that dies mid-request is reported as having stopped while doing it.</summary>
    [GdstkFact]
    public void AWorkerThatDies_IsReportedAsStoppedWhileReading()
    {
        using var worker = GdstkWorker.Start(TestWorker());
        var (deadline, cap) = worker.BoundsFor(0);

        var ex = Assert.Throws<GdstkException>(() => worker.Send(
            new GeometryKernelMessage(new JsonObject { ["op"] = "crash" }), deadline, cap, "reading x.gds", default));

        Assert.Equal(GdstkFailure.Crashed, ex.Failure);
        Assert.Equal("gdstk.worker.stopped", ex.Diagnostic.Id);
        Assert.StartsWith("The gdstk worker stopped while reading x.gds", ex.Message);
    }

    private static GdstkWorkerOptions TestWorker() =>
        new() { Environment = new Dictionary<string, string> { ["CRF_GDSTK_WORKER_TEST"] = "1" } };

    /// <summary>D6: with no worker, discovery says the sentence the disabled commands show.</summary>
    [Fact]
    public void Locate_WithNoWorkerAnywhere_SaysItIsNotInstalled()
    {
        var where = GdstkWorker.Locate(null, _dir, "osx-arm64", windows: false);

        Assert.False(where.Found);
        Assert.Equal(GdstkWorker.NotInstalledSentence, where.Reason);
    }

    /// <summary>gdstk cannot write a reference to a cell the file does not hold, so the export refuses it
    /// by name before starting the worker, and writes nothing.</summary>
    [Fact]
    public void GdstkExport_OfADanglingReference_IsRefusedByName_AndWritesNothing()
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, "TOP");
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "../Gone", X = 0, Y = 0, Mag = 1 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "TOP.clay"), view);
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        string outPath = Path.Combine(_dir, "out.gds");

        var ex = Assert.Throws<GdstkException>(() => GdstkExport.Write(outPath, plan));

        Assert.Contains("\"../Gone\"", ex.Message);
        Assert.False(File.Exists(outPath));
    }
}
