// brief-silkscreen-stroke-font.md §5, R-ssf-6 — a stroke label reaches a Gerber legend as what a plotter draws: D01
// strokes through one round aperture of its pen width, no regions. A label switched to Sans is converted exactly as
// every label was before the stroke font: TrueType glyph outlines, flattened and filled.

using System.Text;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Text;

namespace CircuitRF.Ui.Tests.Layout.Text;

public sealed class GerberStrokeLabelTests : IDisposable
{
    internal static readonly LayerKey Silk = new(21, 0);
    private static readonly DateTime Fixed = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("gerber-stroke-label-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    internal static Technology SilkTech() => new()
    {
        Name = "Test",
        Layers =
        [
            new LayerDef
            {
                Key = Silk, Name = "Top Silkscreen", Purpose = "silkscreen", Color = new Rgba(0xF0, 0xF0, 0xF0),
                Interchange = new InterchangeMapping(null, null, null, "GTO", "Legend,Top"),
            },
        ],
    };

    /// <summary>The silkscreen file a layout of <paramref name="labels"/> exports as, and the plan behind it.</summary>
    internal static (string Gerber, GerberExport.ExportPlan Plan) Export(string dir, Technology tech, params LabelShape[] labels)
    {
        var cellDir = CellFolder.CreateCellFolder(dir, "TOP");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.AddRange(labels);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"), view);

        var plan = GerberExport.Analyze(cellDir, tech, 1000, view, null);
        return (Write(plan.Shapes, plan.Format, tech), plan);
    }

    private static string Write(IReadOnlyList<LayoutShape> shapes, GerberFormat format, Technology tech)
    {
        using var ms = new MemoryStream();
        GerberWriter.Write(ms, tech.Layers[0], shapes, format, tech, Fixed);
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    [Fact]
    public void AStrokeLabelIsD01StrokesThroughOneApertureOfItsPenWidth()
    {
        var tech = SilkTech();
        var label = new LabelShape { Layer = Silk, Text = "R12", Height = 1_000_000, X = 0, Y = 0 };

        var (gerber, plan) = Export(_dir, tech, label);

        Assert.Equal(1, plan.StrokeLabelsWritten);
        Assert.Equal(0, plan.SansLabelsConverted);
        Assert.DoesNotContain("G36", gerber);
        string pen = (Math.Round(1_000_000 / StrokeText.PenDivisor) / 1_000_000).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal([$"%ADD10C,{pen}*%"], gerber.Split('\n').Where(l => l.StartsWith("%ADD", StringComparison.Ordinal)));
        Assert.Equal(StrokeText.For(label).Strokes.Count, gerber.Split('\n').Count(l => l.EndsWith("D02*", StringComparison.Ordinal)));
        Assert.Contains(gerber.Split('\n'), l => l.EndsWith("D01*", StringComparison.Ordinal));
    }

    [Fact]
    public void ALabelSwitchedToSansExportsAsTheOutlinePipelineAlwaysDid()
    {
        var tech = SilkTech();
        var label = new LabelShape { Layer = Silk, Text = "C7", Height = 1_000_000, X = 0, Y = 0, Font = LabelFont.Sans };

        var (gerber, plan) = Export(_dir, tech, label);

        // The pre-stroke-font conversion, called directly: glyph contours, flattened, nested, filled.
        var polygons = LayoutTextFlatten.FlattenContoursToPolygons(
            LayoutTextOutline.BuildGlyphContours(label), LayoutFlattener.ResolveTolDbu(label, tech), label.Layer, label.Net);
        Assert.Equal(1, plan.SansLabelsConverted);
        Assert.Equal(0, plan.StrokeLabelsWritten);
        Assert.Equal(Write(polygons, plan.Format, tech), gerber);
    }
}
