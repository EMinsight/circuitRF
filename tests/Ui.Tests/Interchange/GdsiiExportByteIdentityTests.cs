using System.Security.Cryptography;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Interchange;

/// <summary>brief-oasis-gdstk.md R-oas-2b: the per-shape lowering moved out of <see cref="GdsiiWriter"/>
/// into <see cref="StreamLowering"/>, and native GDSII export must be byte-identical across that move.
/// Each hash below was recorded from the writer BEFORE the refactor (commit 75770824), over a corpus that
/// reaches every branch of the lowering: each shape kind, holes, curves, each path end, port and rotated
/// labels, a via with and without a pad, placeholder layers, plain and arrayed references. The two
/// BGNLIB/BGNSTR timestamps are zeroed before hashing, since they are the clock and nothing else.</summary>
public sealed class GdsiiExportByteIdentityTests(ITestOutputHelper output)
{
    private static readonly GdsiiUnits Nm = new(1e-6, 1e-9);
    private static readonly GdsiiUnits QuarterNm = new(1e-6, 2.5e-10);

    private static readonly LayerKey Drill = new(1, 0);
    private static readonly LayerKey Copper = new(2, 0);

    private static Technology ViaTech() => new()
    {
        Name = "T",
        DefaultFlattenTolDbu = 200,
        Layers =
        [
            new LayerDef { Key = Drill, Name = "Drill", Color = new Rgba(0, 0, 0) },
            new LayerDef { Key = Copper, Name = "Copper", Color = new Rgba(0xC0, 0x80, 0x20) },
            new LayerDef { Key = new LayerKey(-1, 0), Name = "Top Copper" },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "M1", DrawingLayers = [Copper] },
                new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Drill] },
            ],
        },
    };

    private static List<LayoutEdge> Arcs(int n) =>
        Enumerable.Range(0, n).Select(_ => new LayoutEdge { Kind = EdgeKind.Arc, Bulge = Math.Tan(Math.PI / 8.0) }).ToList();

    private static (string Name, IReadOnlyList<InterchangeStructure> Structures, GdsiiUnits Units, Technology? Tech)[] Corpus()
    {
        const long r = 50_000;
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = new LayerKey(1, 0), X1 = -10, Y1 = -20, X2 = 300, Y2 = 400 },
            new PolygonShape { Layer = new LayerKey(2, 3), Xy = [0, 0, 700, 0, 900, 300, 600, 800, 200, 900, -100, 500, -50, 100] },
            new PolygonShape
            {
                Layer = new LayerKey(2, 0), Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000],
                Holes = [[1000, 1000, 1000, 3000, 3000, 3000, 3000, 1000], [6000, 6000, 6000, 8000, 8000, 8000, 8000, 6000]],
            },
            new CircleShape { Layer = new LayerKey(3, 0), Cx = 5, Cy = -7, R = r },
            new CircleShape { Layer = new LayerKey(3, 1), Cx = 0, Cy = 0, R = r, FlattenTolDbu = 50 },
            new RoundedRectShape { Layer = new LayerKey(3, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 60_000, CornerRadius = 10_000 },
            new CurveShape { Layer = new LayerKey(4, 0), Xy = [r, 0, 0, r, -r, 0, 0, -r], Edges = Arcs(4) },
            new CurveShape
            {
                Layer = new LayerKey(4, 2), Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
                Holes = [[40_000, 40_000, 60_000, 40_000, 60_000, 60_000, 40_000, 60_000]],
            },
            new PathShape { Layer = new LayerKey(5, 0), Xy = [0, 0, 1000, 0, 1000, 2000], Width = 200, End = PathEndStyle.Flush },
            new PathShape { Layer = new LayerKey(5, 1), Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Round },
            new PathShape { Layer = new LayerKey(5, 2), Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Square },
            new PathShape { Layer = new LayerKey(5, 3), Xy = [0, 0, 1000, 0], Width = 201, End = PathEndStyle.Extended },
            new PathShape
            {
                Layer = new LayerKey(5, 4), Xy = [0, 0, r, r, 2 * r, 0], Width = 300,
                Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4 }, new LayoutEdge { Kind = EdgeKind.Line }],
            },
            new LabelShape { Layer = new LayerKey(6, 0), X = 100, Y = 200, Text = "OUT", Height = 300, Rotation = LayoutRotation.R90 },
            new LabelShape { Layer = new LayerKey(6, 5), X = -3, Y = 9, Text = "gate", Height = 100, IsPort = true },
            new LabelShape { Layer = new LayerKey(6, 0), X = 0, Y = 0, Text = "tilted", Height = 50, RotationDegrees = 45 },
            new BitmapShape { Layer = new LayerKey(7, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 10, H = 10 },
            new RectShape { Layer = new LayerKey(40000, 40001), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 },
        };
        var instances = new List<LayoutInstance>
        {
            new() { CellRef = "LEAF", X = 500, Y = -300, Rot = LayoutRotation.R90, Mag = 1.5 },
            new() { CellRef = "LEAF", X = 7, Y = 8, RotationDegrees = 270, MirrorX = true, Mag = 2 },
            new() { CellRef = "LEAF", X = 1, Y = 2, RotationDegrees = 30 },
            new() { CellRef = "LEAF", X = 0, Y = 0, Rows = 2, Cols = 3, PitchX = 1000, PitchY = -2000 },
            new() { CellRef = "MISSING", X = 0, Y = 0, Rows = 5, Cols = 1, PitchY = 100 },
        };
        var leaf = new InterchangeStructure("LEAF", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }], []);
        var top = new InterchangeStructure("TOP", shapes, instances);

        var vias = new InterchangeStructure("VIAS",
        [
            new ViaShape { Layer = Drill, LandingLayer = Copper, X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 },
            new ViaShape { Layer = Drill, X = 1_000_000, Y = 0, PadSize = 500_000, DrillSize = 300_000 },
            new ViaShape { Layer = new LayerKey(9, 0), X = 2_000_000, Y = 0, PadSize = 1, DrillSize = 1 },
            new RectShape { Layer = new LayerKey(-1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 },
            new LabelShape { Layer = new LayerKey(-2, 0), X = 0, Y = 0, Text = "x", Height = 10 },
        ], []);

        return
        [
            ("empty", [new InterchangeStructure("EMPTY", [], [])], Nm, null),
            ("every-shape-and-ref", [leaf, top], Nm, null),
            ("every-shape-and-ref-quarter-nm", [top, leaf], QuarterNm, null),
            ("every-shape-with-tech", [leaf, top], Nm, ViaTech()),
            ("vias-and-placeholders", [vias], Nm, ViaTech()),
            ("vias-no-tech", [new InterchangeStructure("VIAS", vias.Shapes.Where(s => s.Layer.Layer >= 0).ToList(), [])], Nm, null),
        ];
    }

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["empty"] = "A8007533F1DD30CBFD5FDE761F00946FE0E34326D610D5CA6B5BC407FB7B612C",
        ["every-shape-and-ref"] = "3776D75728248DF2F6A44D8DDF7AD0D2B84FE70F1037135D2A6C3D07F4B49437",
        ["every-shape-and-ref-quarter-nm"] = "578CB22DA61A02F08FC2ECF926D9FC11689F8072D1100AAEE6AE134D5A9859BF",
        ["every-shape-with-tech"] = "C0F4FE9D345FB1D0BC195206F6F8ED9651E6957FFE75E29F6321BFB2AE3FC67A",
        ["vias-and-placeholders"] = "E7A82DE6DE0F7A8D19231F44C1FD04296FB7B7173AC97186DF6A8F56F0C3BE49",
        ["vias-no-tech"] = "2B1BD5E75660ACD9783B460786319DCC3C924FB9C3032A3C50B63FC1B2D1DF06",
    };

    [Fact]
    public void NativeGdsiiExport_IsByteIdentical_ToThePreRefactorWriter()
    {
        var differences = new List<string>();
        foreach (var (name, structures, units, tech) in Corpus())
        {
            using var ms = new MemoryStream();
            GdsiiWriter.Write(ms, structures, units, tech);
            string hash = HashWithoutTimestamps(ms.ToArray());
            output.WriteLine($"[\"{name}\"] = \"{hash}\",");
            if (!Expected.TryGetValue(name, out var want) || want != hash)
                differences.Add($"{name}: {hash} (expected {want ?? "none"})");
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static string HashWithoutTimestamps(byte[] gds)
    {
        using var canonical = new MemoryStream();
        var records = new GdsiiRecordReader(new MemoryStream(gds));
        var w = new GdsiiRecordWriter(canonical);
        while (records.TryReadNext(out var rec))
        {
            bool clock = rec.Type is GdsiiRecordType.BgnLib or GdsiiRecordType.BgnStr;
            w.WriteRecord(rec.Type, rec.DataType, clock ? new byte[rec.Payload.Length] : rec.Payload);
        }
        Assert.Equal(gds.Length, canonical.Length); // the reader saw every byte
        return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
    }
}
