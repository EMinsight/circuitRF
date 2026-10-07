using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Interchange.Gdstk;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Interchange;

/// <summary>
/// brief-oasis-gdstk.md §7c (R-oas-3c) — circuitRF's own GDSII reader and writer held against gdstk's, which
/// is the independent implementation the second route exists to provide. One test per claim, each under
/// §8b's equality (<see cref="InterchangeEquality"/>), each skipping with a reason when this build has no
/// worker. The writes go through <see cref="StreamInterchange.Write"/>, the call File ▸ Export and
/// <c>convert --engine</c> make.
/// </summary>
public sealed class GdstkGdsiiComparisonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gdstk-compare-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Cases => GdstkCorpus.Names();

    /// <summary>1 — the same file, two readers. Each §7c simple case as gdstk wrote it in G0 (the committed
    /// <c>*.gdstk.gds</c>: the <c>*.circuitrf.gds</c> twins predate the native-writer fixes, as the
    /// corpus README says), read by circuitRF's reader and by gdstk's, gives equal cells.</summary>
    [GdstkTheory, MemberData(nameof(Cases))]
    public void SameFile_BothReaders_AgreeOnTheCells(string fixture)
    {
        string file = Path.Combine(CorpusDir(), $"{fixture}.gdstk.gds");

        var (ours, ourDbu) = ReadNative(file);
        var (theirs, theirDbu, _) = GdstkImport.Read(file, GdstkFormat.Gdsii);

        InterchangeEquality.AssertEqual(ours, ourDbu, theirs, theirDbu);
    }

    /// <summary>2 — our writer, their reader: a layout written by circuitRF's own GDSII export reads back
    /// through gdstk as the layout it was.</summary>
    [GdstkTheory, MemberData(nameof(Cases))]
    public void OurWriter_TheirReader_GivesBackTheLayout(string name)
    {
        var c = GdstkCorpus.Named(name);
        string file = Write(StreamRoute.Gdsii, c);

        var (read, dbu, _) = GdstkImport.Read(file, GdstkFormat.Gdsii);

        InterchangeEquality.AssertEqual(c.Structures, c.DbuPerMicron, read, dbu);
    }

    /// <summary>3 — their writer, our reader: the same layout exported through gdstk reads back through
    /// circuitRF's own reader as the layout it was.</summary>
    [GdstkTheory, MemberData(nameof(Cases))]
    public void TheirWriter_OurReader_GivesBackTheLayout(string name)
    {
        var c = GdstkCorpus.Named(name);
        string file = Write(StreamRoute.GdsiiGdstk, c);

        var (read, dbu) = ReadNative(file);

        InterchangeEquality.AssertEqual(c.Structures, c.DbuPerMicron, read, dbu);
    }

    /// <summary>4 — the two writers agree on content: one layout written both ways and read by the same
    /// reader (circuitRF's) gives equal cells. Byte identity is never asserted: timestamps, record order,
    /// optional records and cell order legitimately differ (§7c).</summary>
    [GdstkTheory, MemberData(nameof(Cases))]
    public void BothWriters_ReadBySameReader_AgreeOnTheCells(string name)
    {
        var c = GdstkCorpus.Named(name);

        var (native, nativeDbu) = ReadNative(Write(StreamRoute.Gdsii, c));
        var (gdstk, gdstkDbu) = ReadNative(Write(StreamRoute.GdsiiGdstk, c));

        InterchangeEquality.AssertEqual(native, nativeDbu, gdstk, gdstkDbu);
    }

    /// <summary>5 — the fidelity counts agree: on a layout with a curve, a hole, a bitmap and a via, each
    /// route's write reports the counts the export dialog showed, because both start from one lowering.</summary>
    [GdstkFact]
    public void BothRoutes_ReportThePlansFidelityCounts()
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, "TOP");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 50_000 });
        view.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(2, 0), Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000], Holes = [[400, 600, 600, 600, 600, 400, 400, 400]],
        });
        view.Shapes.Add(new BitmapShape { Layer = new LayerKey(3, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 10, H = 10 });
        view.Shapes.Add(new ViaShape { Layer = new LayerKey(4, 0), X = 0, Y = 0, PadSize = 500, DrillSize = 300 });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"), view);
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        var counts = new[] { StreamRoute.Gdsii, StreamRoute.GdsiiGdstk }
            .Select(route => StreamInterchange.Write(route, Path.Combine(_dir, $"{route}.gds"), plan))
            .Select(s => (s.CurvedShapesFlattened, s.HolesKeyholed, s.BitmapsSkipped, s.ViaPadsSkipped))
            .ToList();

        // The circle and the via's round pad are both flattened, so curves count 2.
        var planned = (plan.CurvedShapesFlattened, plan.HolesKeyholed, plan.BitmapsSkipped, plan.ViaPadsSkipped);
        Assert.Equal((2, 1, 1, 1), planned);
        Assert.All(counts, c => Assert.Equal(planned, c));
    }

    private string Write(StreamRoute route, GdstkCorpus.Case c)
    {
        string file = Path.Combine(_dir, $"{c.Name}.{route}.gds");
        StreamInterchange.Write(route, file, GdstkCorpus.Plan(c));
        return file;
    }

    private static (List<InterchangeStructure> Structures, double DbuPerMicron) ReadNative(string file)
    {
        using var f = File.OpenRead(file);
        var reader = GdsiiReader.Open(f);
        var structures = reader.ReadStructures().ToList();
        return (structures, reader.Units.SourceDbuPerMicron);
    }

    private static string CorpusDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "testdata", "interchange", "gdstk", "8a");
    }
}
